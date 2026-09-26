namespace DotOrbit.Storage.Sqlite;

internal interface IWorkspaceFileOperations
{
    string ResolvePath(string path);

    bool Exists(string path);

    void EnsureParentDirectory(string path);

    string GetCandidatePath(string targetPath, string identifier);

    void Publish(string candidatePath, string targetPath);

    void DeleteCandidate(string candidatePath);
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
}
