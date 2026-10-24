using DotOrbit.Desktop.ApplicationUpdates;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public async Task StableBuildSelectsLatestStableAndDismissalOnlyHidesAttention()
    {
        var store = new MemoryPreferenceStore();
        var client = new RecordingCatalogClient(Success("1.1.0-beta.1", "1.0.1"));
        var service = CreateService("1.0.0", ApplicationReleaseChannel.Stable, client, store);

        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationUpdateStatus.UpdateAvailable, service.Snapshot.Status);
        Assert.Equal("1.0.1", service.Snapshot.AvailableRelease!.Version.Text);
        Assert.True(service.Snapshot.ShowNavigationAttention);
        Assert.True(service.TryDismissAvailableVersion());
        Assert.Equal("1.0.1", service.Snapshot.AvailableRelease!.Version.Text);
        Assert.False(service.Snapshot.ShowNavigationAttention);
    }

    [Fact]
    public async Task PrereleaseBuildConsidersStableAndPrereleaseBySemVerPrecedence()
    {
        var service = CreateService(
            "2.0.0-beta.1",
            ApplicationReleaseChannel.Prerelease,
            new RecordingCatalogClient(Success("2.0.0-beta.2", "1.9.9")),
            new MemoryPreferenceStore());

        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2.0.0-beta.2", service.Snapshot.AvailableRelease!.Version.Text);
        Assert.Equal(ApplicationUpdateStatus.UpdateAvailable, service.Snapshot.Status);
    }

    [Fact]
    public async Task UnsupportedBuildNeverChecksAutomaticallyButManualShowsStableWithoutAttention()
    {
        var client = new RecordingCatalogClient(Success("2.0.0-beta.1", "1.5.0"));
        var service = new ApplicationUpdateService(
            InstalledApplicationBuild.CreateForTests(
                "Development build",
                null,
                ApplicationReleaseChannel.Development),
            client,
            new MemoryPreferenceStore(),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        await service.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, client.CallCount);

        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(ApplicationUpdateStatus.UnsupportedDevelopmentBuild, service.Snapshot.Status);
        Assert.Equal("1.5.0", service.Snapshot.AvailableRelease!.Version.Text);
        Assert.False(service.Snapshot.ShowNavigationAttention);
    }

    [Fact]
    public async Task AutomaticCheckRecordsAttemptBeforeRequestAndHonoursTwentyFourHourCadence()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var store = new MemoryPreferenceStore();
        var client = new RecordingCatalogClient(Success("1.0.0"));
        var service = CreateService("1.0.0", ApplicationReleaseChannel.Stable, client, store, time);

        await service.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);
        await service.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.Equal(now, store.Preferences.LastAutomaticAttemptUtc);
        Assert.Equal(now, store.Preferences.LastSuccessfulCheckUtc);

        time.Set(now.AddHours(24));
        await service.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task FailedRefreshKeepsVerifiedCandidateAndReportsUnavailable()
    {
        var store = new MemoryPreferenceStore
        {
            Preferences = new(
                ETag: "\"known\"",
                HasVerifiedCatalog: true,
                VerifiedStableVersion: "1.1.0"),
        };
        var service = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            new RecordingCatalogClient(ReleaseCatalogResult.Unavailable),
            store);

        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationUpdateStatus.CheckUnavailable, service.Snapshot.Status);
        Assert.Equal("1.1.0", service.Snapshot.AvailableRelease!.Version.Text);
        Assert.True(service.Snapshot.IsKnownUpdateAvailable);
        Assert.True(service.Snapshot.ShowNavigationAttention);
    }

    [Fact]
    public async Task NotModifiedRequiresValidatedCacheAndConcurrentChecksCoalesce()
    {
        var pending = new TaskCompletionSource<ReleaseCatalogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingCatalogClient(pending.Task);
        var store = new MemoryPreferenceStore();
        var service = CreateService("1.0.0", ApplicationReleaseChannel.Stable, client, store);

        var first = service.CheckManuallyAsync(TestContext.Current.CancellationToken);
        var second = service.CheckManuallyAsync(TestContext.Current.CancellationToken);
        pending.SetResult(new(ReleaseCatalogResultKind.NotModified, [], "\"unknown\""));
        await Task.WhenAll(first, second);

        Assert.Equal(1, client.CallCount);
        Assert.Equal(ApplicationUpdateStatus.CheckUnavailable, service.Snapshot.Status);
    }

    [Fact]
    public async Task AutomaticPreferenceFailureAndClockRollbackDoNotIssueRequestsButManualBypassesBothGates()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var store = new MemoryPreferenceStore
        {
            Preferences = new(LastAutomaticAttemptUtc: now.AddHours(1)),
        };
        var client = new RecordingCatalogClient(Success("1.0.1"));
        var service = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            client,
            store,
            new FixedTimeProvider(now));

        await service.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, client.CallCount);

        Assert.True(service.TrySetAutomaticEnabled(false));
        await service.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);
        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, client.CallCount);

        var failingStore = new MemoryPreferenceStore { AllowSave = false };
        var failingClient = new RecordingCatalogClient(Success("1.0.1"));
        var failingService = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            failingClient,
            failingStore,
            new FixedTimeProvider(now));
        await failingService.CheckAutomaticallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, failingClient.CallCount);
        Assert.Equal(ApplicationUpdateStatus.CheckUnavailable, failingService.Snapshot.Status);
    }

    [Fact]
    public async Task NewerVerifiedVersionClearsExactDismissalAndValidNotModifiedReusesCache()
    {
        var store = new MemoryPreferenceStore
        {
            Preferences = new(
                ETag: "W/\"known\"",
                HasVerifiedCatalog: true,
                VerifiedStableVersion: "1.1.0",
                DismissedVersion: "1.1.0"),
        };
        var unchanged = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            new RecordingCatalogClient(new ReleaseCatalogResult(
                ReleaseCatalogResultKind.NotModified,
                [],
                "W/\"known\"")),
            store);

        await unchanged.CheckManuallyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateStatus.UpdateAvailable, unchanged.Snapshot.Status);
        Assert.False(unchanged.Snapshot.ShowNavigationAttention);

        var newer = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            new RecordingCatalogClient(Success("1.2.0")),
            store);
        await newer.CheckManuallyAsync(TestContext.Current.CancellationToken);
        Assert.True(newer.Snapshot.ShowNavigationAttention);
        Assert.Null(store.Preferences.DismissedVersion);
    }

    [Fact]
    public async Task ManualSuccessRecordsSuccessTimeAndCachesBothReleaseChannels()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var store = new MemoryPreferenceStore();
        var service = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            new RecordingCatalogClient(Success("1.2.0-beta.1", "1.1.0")),
            store,
            new FixedTimeProvider(now));

        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(now, service.Snapshot.LastSuccessfulCheckUtc);
        Assert.Equal(now, store.Preferences.LastSuccessfulCheckUtc);
        Assert.Equal("1.1.0", store.Preferences.VerifiedStableVersion);
        Assert.Equal("1.2.0-beta.1", store.Preferences.VerifiedPrereleaseVersion);
    }

    [Fact]
    public void CachedCatalogIsSelectedForTheInstalledChannel()
    {
        var store = new MemoryPreferenceStore
        {
            Preferences = new(
                HasVerifiedCatalog: true,
                VerifiedStableVersion: "1.1.0",
                VerifiedPrereleaseVersion: "2.0.0-beta.1"),
        };

        var stable = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            new RecordingCatalogClient(ReleaseCatalogResult.Unavailable),
            store);
        var prerelease = CreateService(
            "1.0.0-beta.1",
            ApplicationReleaseChannel.Prerelease,
            new RecordingCatalogClient(ReleaseCatalogResult.Unavailable),
            store);

        Assert.Equal("1.1.0", stable.Snapshot.AvailableRelease!.Version.Text);
        Assert.Equal("2.0.0-beta.1", prerelease.Snapshot.AvailableRelease!.Version.Text);
    }

    [Fact]
    public async Task MalformedCachedCatalogIsInvalidatedBeforeConditionalRequest()
    {
        var store = new MemoryPreferenceStore
        {
            Preferences = new(
                ETag: "\"known\"",
                HasVerifiedCatalog: true,
                VerifiedStableVersion: "2.0.0-beta.1",
                VerifiedPrereleaseVersion: "2.1.0-beta.1"),
        };
        var client = new RecordingCatalogClient(new ReleaseCatalogResult(
            ReleaseCatalogResultKind.NotModified,
            [],
            "\"known\""));
        var service = CreateService(
            "1.0.0",
            ApplicationReleaseChannel.Stable,
            client,
            store);

        await service.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Null(client.LastETag);
        Assert.Equal(ApplicationUpdateStatus.CheckUnavailable, service.Snapshot.Status);
        Assert.Null(service.Snapshot.AvailableRelease);

        var prerelease = CreateService(
            "1.0.0-beta.1",
            ApplicationReleaseChannel.Prerelease,
            new RecordingCatalogClient(ReleaseCatalogResult.Unavailable),
            store);
        Assert.Null(prerelease.Snapshot.AvailableRelease);
    }

    private static ApplicationUpdateService CreateService(
        string versionText,
        ApplicationReleaseChannel channel,
        IReleaseCatalogClient client,
        IApplicationUpdatePreferenceStore store,
        TimeProvider? timeProvider = null)
    {
        Assert.True(SemanticVersion.TryParse(versionText, out var version));
        return new(
            InstalledApplicationBuild.CreateForTests(versionText, version, channel),
            client,
            store,
            timeProvider ?? new FixedTimeProvider(DateTimeOffset.UnixEpoch));
    }

    private static ReleaseCatalogResult Success(params string[] versions) => new(
        ReleaseCatalogResultKind.Success,
        versions.Select(version =>
        {
            Assert.True(SemanticVersion.TryParse(version, out var parsed));
            return new ApplicationRelease(parsed);
        }).ToArray(),
        "\"catalog\"");

    private sealed class MemoryPreferenceStore : IApplicationUpdatePreferenceStore
    {
        public ApplicationUpdatePreferences Preferences { get; set; } = new();
        public bool AllowSave { get; set; } = true;

        public ApplicationUpdatePreferences LoadApplicationUpdatePreferences() => Preferences;

        public bool TrySaveApplicationUpdatePreferences(ApplicationUpdatePreferences preferences)
        {
            if (!AllowSave) return false;
            Preferences = preferences;
            return true;
        }
    }

    private sealed class RecordingCatalogClient : IReleaseCatalogClient
    {
        private readonly Func<Task<ReleaseCatalogResult>> _result;

        public RecordingCatalogClient(ReleaseCatalogResult result) : this(Task.FromResult(result))
        {
        }

        public RecordingCatalogClient(Task<ReleaseCatalogResult> result) => _result = () => result;

        public int CallCount { get; private set; }
        public string? LastETag { get; private set; }

        public Task<ReleaseCatalogResult> GetAsync(string? etag, CancellationToken cancellationToken)
        {
            CallCount++;
            LastETag = etag;
            return _result();
        }
    }
}
