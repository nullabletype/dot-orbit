namespace DotOrbit.Packaging;

using System.Text.RegularExpressions;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = PackageOptions.Parse(args);
            var archivePath = PackageArchive.Build(
                options.RepositoryRoot,
                options.RuntimeIdentifier,
                options.Version,
                options.ReleaseTag,
                options.NoRestore);
            Console.WriteLine(
                $"package: runtime={options.RuntimeIdentifier} version={options.Version} result=passed archive={archivePath}");
            return 0;
        }
        catch (PackageException exception)
        {
            Console.Error.WriteLine($"package: result=failed reason={exception.Message}");
            return 1;
        }
    }
}

internal sealed record PackageOptions(
    string RepositoryRoot,
    string RuntimeIdentifier,
    string Version,
    string? ReleaseTag,
    bool NoRestore)
{
    private static readonly Regex SemanticVersionPattern = new(
        @"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static PackageOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? repositoryRoot = null;
        string? runtimeIdentifier = null;
        string? version = null;
        string? releaseTag = null;
        var noRestore = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repository-root" when index + 1 < args.Length:
                    repositoryRoot = args[++index];
                    break;
                case "--runtime" when index + 1 < args.Length:
                    runtimeIdentifier = args[++index];
                    break;
                case "--version" when index + 1 < args.Length:
                    version = args[++index];
                    break;
                case "--release-tag" when index + 1 < args.Length:
                    releaseTag = args[++index];
                    break;
                case "--no-restore":
                    noRestore = true;
                    break;
                default:
                    throw new PackageException("invalid-arguments");
            }
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot)
            || string.IsNullOrWhiteSpace(runtimeIdentifier)
            || string.IsNullOrWhiteSpace(version))
        {
            throw new PackageException("missing-required-arguments");
        }
        if (!SemanticVersionPattern.IsMatch(version) || !IsBoundedSemanticVersion(version))
        {
            throw new PackageException("invalid-version");
        }
        if (releaseTag is { Length: > 0 }
            && !string.Equals(releaseTag, $"v{version}", StringComparison.Ordinal))
        {
            throw new PackageException("release-tag-version-mismatch");
        }

        return new(
            Path.GetFullPath(repositoryRoot),
            runtimeIdentifier,
            version,
            string.IsNullOrEmpty(releaseTag) ? null : releaseTag,
            noRestore);
    }

    private static bool IsBoundedSemanticVersion(string version)
    {
        if (version.Length > 200) return false;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var precedence = plus < 0 ? version : version[..plus];
        var build = plus < 0 ? null : version[(plus + 1)..];
        var dash = precedence.IndexOf('-', StringComparison.Ordinal);
        var prerelease = dash < 0 ? null : precedence[(dash + 1)..];
        return new[] { prerelease, build }
            .Where(value => value is not null)
            .SelectMany(value => value!.Split('.'))
            .All(identifier => identifier.Length <= 64);
    }
}

internal sealed class PackageException(string message) : Exception(message);
