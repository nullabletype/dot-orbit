namespace DotOrbit.Desktop.ApplicationUpdates;

internal enum ApplicationUpdateStatus
{
    NotChecked,
    Checking,
    UpToDate,
    UpdateAvailable,
    UnsupportedDevelopmentBuild,
    CheckUnavailable,
}

internal sealed record ApplicationUpdatePreferences(
    bool AutomaticEnabled = true,
    DateTimeOffset? LastAutomaticAttemptUtc = null,
    DateTimeOffset? LastSuccessfulCheckUtc = null,
    string? ETag = null,
    bool HasVerifiedCatalog = false,
    string? VerifiedStableVersion = null,
    string? VerifiedPrereleaseVersion = null,
    string? DismissedVersion = null);

internal interface IApplicationUpdatePreferenceStore
{
    ApplicationUpdatePreferences LoadApplicationUpdatePreferences();

    bool TrySaveApplicationUpdatePreferences(ApplicationUpdatePreferences preferences);
}

internal sealed record ApplicationUpdateSnapshot(
    ApplicationUpdateStatus Status,
    InstalledApplicationBuild InstalledBuild,
    bool AutomaticEnabled,
    ApplicationRelease? AvailableRelease,
    DateTimeOffset? LastSuccessfulCheckUtc,
    bool IsAvailableVersionDismissed)
{
    public bool IsKnownUpdateAvailable =>
        InstalledBuild.IsOfficialRelease
        && AvailableRelease is not null
        && InstalledBuild.Version is { } installedVersion
        && AvailableRelease.Version.CompareTo(installedVersion) > 0;

    public bool ShowNavigationAttention =>
        IsKnownUpdateAvailable
        && !IsAvailableVersionDismissed;
}

internal interface IApplicationUpdateService
{
    event EventHandler? StateChanged;

    ApplicationUpdateSnapshot Snapshot { get; }

    bool TrySetAutomaticEnabled(bool enabled);

    Task CheckAutomaticallyAsync(CancellationToken cancellationToken);

    Task CheckManuallyAsync(CancellationToken cancellationToken);

    bool TryDismissAvailableVersion();
}

internal sealed class ApplicationUpdateService : IApplicationUpdateService
{
    internal static TimeSpan AutomaticCheckInterval { get; } = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly InstalledApplicationBuild _installedBuild;
    private readonly IReleaseCatalogClient _catalogClient;
    private readonly IApplicationUpdatePreferenceStore _preferenceStore;
    private readonly TimeProvider _timeProvider;
    private ApplicationUpdatePreferences _preferences;
    private Task? _activeCheck;

    public ApplicationUpdateService(
        InstalledApplicationBuild installedBuild,
        IReleaseCatalogClient catalogClient,
        IApplicationUpdatePreferenceStore preferenceStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(installedBuild);
        ArgumentNullException.ThrowIfNull(catalogClient);
        ArgumentNullException.ThrowIfNull(preferenceStore);
        _installedBuild = installedBuild;
        _catalogClient = catalogClient;
        _preferenceStore = preferenceStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preferences = Validate(preferenceStore.LoadApplicationUpdatePreferences());
        Snapshot = CreateSnapshot(DefaultRestingStatus(), GetCachedRelease());
    }

    public event EventHandler? StateChanged;

    public ApplicationUpdateSnapshot Snapshot { get; private set; }

    public bool TrySetAutomaticEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _preferences.AutomaticEnabled) return true;
            var updated = _preferences with { AutomaticEnabled = enabled };
            if (!_preferenceStore.TrySaveApplicationUpdatePreferences(updated)) return false;
            _preferences = updated;
            Publish(Snapshot.Status, Snapshot.AvailableRelease);
            return true;
        }
    }

    public Task CheckAutomaticallyAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_installedBuild.IsOfficialRelease
                || !_preferences.AutomaticEnabled
                || !IsAutomaticCheckDue(_timeProvider.GetUtcNow()))
            {
                return Task.CompletedTask;
            }

            return StartOrJoinCheck(automatic: true, cancellationToken);
        }
    }

    public Task CheckManuallyAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return StartOrJoinCheck(automatic: false, cancellationToken);
        }
    }

    public bool TryDismissAvailableVersion()
    {
        lock (_gate)
        {
            if (Snapshot.AvailableRelease is not { } release) return false;
            var updated = _preferences with { DismissedVersion = release.Version.Text };
            if (!_preferenceStore.TrySaveApplicationUpdatePreferences(updated)) return false;
            _preferences = updated;
            Publish(Snapshot.Status, release);
            return true;
        }
    }

    private Task StartOrJoinCheck(bool automatic, CancellationToken cancellationToken)
    {
        if (_activeCheck is { IsCompleted: false }) return _activeCheck;
        if (automatic)
        {
            var updated = _preferences with
            {
                LastAutomaticAttemptUtc = _timeProvider.GetUtcNow(),
            };
            if (!_preferenceStore.TrySaveApplicationUpdatePreferences(updated))
            {
                Publish(ApplicationUpdateStatus.CheckUnavailable, GetCachedRelease());
                return Task.CompletedTask;
            }

            _preferences = updated;
        }

        Publish(ApplicationUpdateStatus.Checking, GetCachedRelease());
        _activeCheck = ExecuteCheckAsync(cancellationToken);
        return _activeCheck;
    }

    private async Task ExecuteCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _catalogClient
                .GetAsync(_preferences.ETag, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                ApplyResult(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                Publish(DefaultRestingStatus(), GetCachedRelease());
            }
        }
        finally
        {
            lock (_gate)
            {
                _activeCheck = null;
            }
        }
    }

    private void ApplyResult(ReleaseCatalogResult result)
    {
        if (result.Kind is ReleaseCatalogResultKind.Unavailable
            || (result.Kind is ReleaseCatalogResultKind.NotModified
                && !_preferences.HasVerifiedCatalog))
        {
            Publish(ApplicationUpdateStatus.CheckUnavailable, GetCachedRelease());
            return;
        }

        ApplicationRelease? selected;
        var stableVersion = _preferences.VerifiedStableVersion;
        var prereleaseVersion = _preferences.VerifiedPrereleaseVersion;
        var etag = _preferences.ETag;
        if (result.Kind is ReleaseCatalogResultKind.NotModified)
        {
            selected = GetCachedRelease();
        }
        else
        {
            selected = SelectRelease(result.Releases);
            stableVersion = MaximumVersion(result.Releases, prerelease: false)?.Text;
            prereleaseVersion = MaximumVersion(result.Releases, prerelease: true)?.Text;
            etag = IsSafeEtag(result.ETag) ? result.ETag : null;
        }

        var dismissed = _preferences.DismissedVersion;
        if (selected is not null
            && SemanticVersion.TryParse(dismissed, out var dismissedVersion)
            && selected.Version.CompareTo(dismissedVersion) > 0)
        {
            dismissed = null;
        }

        var updated = _preferences with
        {
            ETag = etag,
            HasVerifiedCatalog = true,
            VerifiedStableVersion = stableVersion,
            VerifiedPrereleaseVersion = prereleaseVersion,
            DismissedVersion = dismissed,
            LastSuccessfulCheckUtc = _timeProvider.GetUtcNow(),
        };
        if (!_preferenceStore.TrySaveApplicationUpdatePreferences(updated))
        {
            Publish(ApplicationUpdateStatus.CheckUnavailable, GetCachedRelease());
            return;
        }

        _preferences = updated;
        Publish(StatusFor(selected), selected);
    }

    private ApplicationRelease? SelectRelease(IReadOnlyList<ApplicationRelease> releases)
    {
        var eligible = _installedBuild.Channel is ApplicationReleaseChannel.Stable
            or ApplicationReleaseChannel.Development
            ? releases.Where(release => !release.Version.IsPrerelease)
            : releases;
        return eligible.MaxBy(release => release.Version, Comparer<SemanticVersion>.Default);
    }

    private static SemanticVersion? MaximumVersion(
        IReadOnlyList<ApplicationRelease> releases,
        bool prerelease)
    {
        SemanticVersion? maximum = null;
        foreach (var release in releases.Where(release =>
                     release.Version.IsPrerelease == prerelease))
        {
            if (maximum is null || release.Version.CompareTo(maximum) > 0)
            {
                maximum = release.Version;
            }
        }

        return maximum;
    }

    private ApplicationUpdateStatus StatusFor(ApplicationRelease? release)
    {
        if (!_installedBuild.IsOfficialRelease)
        {
            return ApplicationUpdateStatus.UnsupportedDevelopmentBuild;
        }

        return release is not null
               && release.Version.CompareTo(_installedBuild.Version) > 0
            ? ApplicationUpdateStatus.UpdateAvailable
            : ApplicationUpdateStatus.UpToDate;
    }

    private ApplicationUpdateStatus DefaultRestingStatus() =>
        _installedBuild.IsOfficialRelease
            ? _preferences.HasVerifiedCatalog
                ? StatusFor(GetCachedRelease())
                : ApplicationUpdateStatus.NotChecked
            : ApplicationUpdateStatus.UnsupportedDevelopmentBuild;

    private bool IsAutomaticCheckDue(DateTimeOffset now)
    {
        if (_preferences.LastAutomaticAttemptUtc is not { } attempted) return true;
        if (now < attempted) return false;
        return now - attempted >= AutomaticCheckInterval;
    }

    private ApplicationRelease? GetCachedRelease()
    {
        var stable = SemanticVersion.TryParse(
            _preferences.VerifiedStableVersion,
            out var parsedStable)
            ? parsedStable
            : null;
        if (_installedBuild.Channel is ApplicationReleaseChannel.Stable
            or ApplicationReleaseChannel.Development)
        {
            return stable is null ? null : new(stable);
        }

        var prerelease = SemanticVersion.TryParse(
            _preferences.VerifiedPrereleaseVersion,
            out var parsedPrerelease)
            ? parsedPrerelease
            : null;
        var selected = stable is null
            ? prerelease
            : prerelease is null || stable.CompareTo(prerelease) >= 0
                ? stable
                : prerelease;
        return selected is null ? null : new(selected);
    }

    private ApplicationUpdateSnapshot CreateSnapshot(
        ApplicationUpdateStatus status,
        ApplicationRelease? release) => new(
            status,
            _installedBuild,
            _preferences.AutomaticEnabled,
            release,
            _preferences.LastSuccessfulCheckUtc,
            release is not null
            && string.Equals(
                _preferences.DismissedVersion,
                release.Version.Text,
                StringComparison.Ordinal));

    private void Publish(ApplicationUpdateStatus status, ApplicationRelease? release)
    {
        Snapshot = CreateSnapshot(status, release);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ApplicationUpdatePreferences Validate(ApplicationUpdatePreferences preferences)
    {
        var stableVersionIsValid = preferences.VerifiedStableVersion is null
                                   || (SemanticVersion.TryParse(
                                           preferences.VerifiedStableVersion,
                                           out var stable)
                                       && !stable.IsPrerelease);
        var prereleaseVersionIsValid = preferences.VerifiedPrereleaseVersion is null
                                       || (SemanticVersion.TryParse(
                                               preferences.VerifiedPrereleaseVersion,
                                               out var prerelease)
                                           && prerelease.IsPrerelease);
        var dismissedVersion = SemanticVersion.TryParse(preferences.DismissedVersion, out _)
            ? preferences.DismissedVersion
            : null;
        var catalogIsValid = preferences.HasVerifiedCatalog
                             && stableVersionIsValid
                             && prereleaseVersionIsValid;
        var etag = catalogIsValid && IsSafeEtag(preferences.ETag)
            ? preferences.ETag
            : null;
        return preferences with
        {
            ETag = etag,
            HasVerifiedCatalog = catalogIsValid,
            VerifiedStableVersion = catalogIsValid ? preferences.VerifiedStableVersion : null,
            VerifiedPrereleaseVersion = catalogIsValid
                ? preferences.VerifiedPrereleaseVersion
                : null,
            DismissedVersion = dismissedVersion,
        };
    }

    private static bool IsSafeEtag(string? etag) =>
        etag is null
        || (etag.Length <= 256
            && etag.IndexOfAny('\r', '\n') < 0
            && System.Net.Http.Headers.EntityTagHeaderValue.TryParse(etag, out _));
}

internal sealed class TransientApplicationUpdateService : IApplicationUpdateService
{
    private readonly InstalledApplicationBuild _build =
        InstalledApplicationBuild.CreateForTests(
            "Development build",
            null,
            ApplicationReleaseChannel.Development);

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public ApplicationUpdateSnapshot Snapshot => new(
        ApplicationUpdateStatus.UnsupportedDevelopmentBuild,
        _build,
        true,
        null,
        null,
        false);

    public bool TrySetAutomaticEnabled(bool enabled) => true;

    public Task CheckAutomaticallyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CheckManuallyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public bool TryDismissAvailableVersion() => false;
}
