namespace DotOrbit.Packaging;

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
                options.NoRestore);
            Console.WriteLine(
                $"package: runtime={options.RuntimeIdentifier} result=passed archive={archivePath}");
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
    bool NoRestore)
{
    public static PackageOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? repositoryRoot = null;
        string? runtimeIdentifier = null;
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
                case "--no-restore":
                    noRestore = true;
                    break;
                default:
                    throw new PackageException("invalid-arguments");
            }
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot)
            || string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            throw new PackageException("missing-required-arguments");
        }

        return new(
            Path.GetFullPath(repositoryRoot),
            runtimeIdentifier,
            noRestore);
    }
}

internal sealed class PackageException(string message) : Exception(message);
