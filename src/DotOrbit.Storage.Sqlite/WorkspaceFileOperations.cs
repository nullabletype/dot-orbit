namespace DotOrbit.Storage.Sqlite;

internal interface IWorkspaceFileOperations
{
    string ResolvePath(string path);

    bool Exists(string path);

    void EnsureParentDirectory(string path);

    void EnsureDirectory(string path);

    string GetCandidatePath(string targetPath, string identifier);

    void Publish(string candidatePath, string targetPath);

    void Copy(string sourcePath, string candidatePath);

    void Flush(string path);

    void Replace(string candidatePath, string targetPath);

    void DeleteCandidate(string candidatePath);

    IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    void PublishOrReplace(string candidatePath, string targetPath);

    void DeleteFile(string path);
}

internal sealed class WorkspaceFileOperations : IWorkspaceFileOperations
{
    public string ResolvePath(string path) => Path.GetFullPath(path);

    public bool Exists(string path) => File.Exists(path);

    public void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException();
        }

        Directory.CreateDirectory(directory);
    }

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public string GetCandidatePath(string targetPath, string identifier)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException();
        }

        return Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{identifier}.creating");
    }

    public void Publish(string candidatePath, string targetPath) =>
        File.Move(candidatePath, targetPath, overwrite: false);

    public void Copy(string sourcePath, string candidatePath) =>
        File.Copy(sourcePath, candidatePath, overwrite: false);

    public void Flush(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string candidatePath, string targetPath) =>
        File.Replace(
            candidatePath,
            targetPath,
            destinationBackupFileName: null,
            ignoreMetadataErrors: true);

    public void DeleteCandidate(string candidatePath)
    {
        try
        {
            File.Delete(candidatePath);
            File.Delete(candidatePath + "-journal");
            File.Delete(candidatePath + "-shm");
            File.Delete(candidatePath + "-wal");
        }
        catch (IOException)
        {
            // A failed candidate is never published; later startup can ignore it safely.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed candidate is never published; later startup can ignore it safely.
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern) =>
        Directory.Exists(directoryPath)
            ? Directory.GetFiles(directoryPath, searchPattern)
            : [];

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public void PublishOrReplace(string candidatePath, string targetPath) =>
        File.Move(candidatePath, targetPath, overwrite: true);

    public void DeleteFile(string path) => File.Delete(path);
}
