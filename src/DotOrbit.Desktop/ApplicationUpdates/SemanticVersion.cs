using System.Reflection;

namespace DotOrbit.Desktop.ApplicationUpdates;

internal sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(
        string major,
        string minor,
        string patch,
        IReadOnlyList<string> prerelease,
        IReadOnlyList<string> build,
        string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
        Text = text;
    }

    public string Major { get; }
    public string Minor { get; }
    public string Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public IReadOnlyList<string> Build { get; }
    public string Text { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 200 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0 && value.IndexOf('+', plus + 1) >= 0)
        {
            return false;
        }

        var precedence = plus < 0 ? value : value[..plus];
        var buildText = plus < 0 ? null : value[(plus + 1)..];
        var dash = precedence.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? precedence : precedence[..dash];
        var prereleaseText = dash < 0 ? null : precedence[(dash + 1)..];
        var coreParts = core.Split('.');
        if (coreParts.Length != 3
            || !IsCanonicalNumber(coreParts[0])
            || !IsCanonicalNumber(coreParts[1])
            || !IsCanonicalNumber(coreParts[2])
            || !TryParseIdentifiers(prereleaseText, numericMustBeCanonical: true, out var prerelease)
            || !TryParseIdentifiers(buildText, numericMustBeCanonical: false, out var build))
        {
            return false;
        }

        version = new(coreParts[0], coreParts[1], coreParts[2], prerelease, build, value);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var comparison = CompareNumber(Major, other.Major);
        if (comparison == 0) comparison = CompareNumber(Minor, other.Minor);
        if (comparison == 0) comparison = CompareNumber(Patch, other.Patch);
        if (comparison != 0) return comparison;
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;

        for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            comparison = leftNumeric && rightNumeric
                ? CompareNumber(left, right)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemanticVersion? other) =>
        other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;

    private static bool IsCanonicalNumber(string value) =>
        value.Length > 0
        && value.All(char.IsAsciiDigit)
        && (value.Length == 1 || value[0] != '0');

    private static bool TryParseIdentifiers(
        string? value,
        bool numericMustBeCanonical,
        out IReadOnlyList<string> identifiers)
    {
        identifiers = [];
        if (value is null)
        {
            return true;
        }

        var parts = value.Split('.');
        if (parts.Any(part =>
                part.Length == 0
                || part.Length > 64
                || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
                || (numericMustBeCanonical && part.All(char.IsAsciiDigit) && !IsCanonicalNumber(part))))
        {
            return false;
        }

        identifiers = parts;
        return true;
    }

    private static int CompareNumber(string left, string right) =>
        left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right);
}

internal enum ApplicationReleaseChannel
{
    Development,
    Stable,
    Prerelease,
}

internal sealed record InstalledApplicationBuild(
    string DisplayVersion,
    SemanticVersion? Version,
    ApplicationReleaseChannel Channel)
{
    private const string ReleaseTagMetadataName = "DotOrbitReleaseTag";

    public bool IsOfficialRelease => Channel is not ApplicationReleaseChannel.Development;

    public static InstalledApplicationBuild FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var releaseTags = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute =>
                string.Equals(attribute.Key, ReleaseTagMetadataName, StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .ToArray();
        var releaseTag = releaseTags.Length == 1 ? releaseTags[0] : null;

        if (!SemanticVersion.TryParse(informational, out var version)
            || !string.Equals(releaseTag, $"v{version.Text}", StringComparison.Ordinal))
        {
            return new(informational ?? "Development build", null, ApplicationReleaseChannel.Development);
        }

        return new(
            version.Text,
            version,
            version.IsPrerelease ? ApplicationReleaseChannel.Prerelease : ApplicationReleaseChannel.Stable);
    }

    internal static InstalledApplicationBuild CreateForTests(
        string displayVersion,
        SemanticVersion? version,
        ApplicationReleaseChannel channel) => new(displayVersion, version, channel);
}
