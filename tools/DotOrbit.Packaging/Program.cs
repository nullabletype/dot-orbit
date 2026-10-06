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
        if (!SemanticVersionPattern.IsMatch(version))
        {
            throw new PackageException("invalid-version");
        }

        return new(
            Path.GetFullPath(repositoryRoot),
            runtimeIdentifier,
            version,
            noRestore);
    }
}

internal sealed class PackageException(string message) : Exception(message);
