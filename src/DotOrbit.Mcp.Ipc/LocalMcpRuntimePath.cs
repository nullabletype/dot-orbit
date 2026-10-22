namespace DotOrbit.Mcp.Ipc;

public static class LocalMcpRuntimePath
{
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string GetDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetTempPath();
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData) || !Path.IsPathFullyQualified(localData))
        {
            throw new InvalidOperationException("local-application-data-unavailable");
        }

        var root = Path.Combine(localData, "dot-orbit");
        RejectLink(localData);
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root, PrivateDirectoryMode);
        }

        RejectLink(root);
        File.SetUnixFileMode(root, PrivateDirectoryMode);
        if (File.GetUnixFileMode(root) != PrivateDirectoryMode)
        {
            throw new UnauthorizedAccessException("runtime-parent-mode-invalid");
        }

        return Path.Combine(root, "mcp");
    }

    private static void RejectLink(string path)
    {
        var information = new DirectoryInfo(path);
        if (!information.Exists || information.LinkTarget is not null)
        {
            throw new UnauthorizedAccessException("runtime-parent-invalid");
        }
    }
}
