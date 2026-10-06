using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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

    void CreateHardLink(string existingPath, string linkPath);

    string ComputeSha256(string path);

    void Flush(string path);

    void Replace(string candidatePath, string targetPath);

    void DeleteCandidate(string candidatePath);

    IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    void PublishOrReplace(string candidatePath, string targetPath);

    void DeleteFile(string path);
}

internal sealed partial class WorkspaceFileOperations : IWorkspaceFileOperations
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

    public void CreateHardLink(string existingPath, string linkPath)
    {
        var succeeded = OperatingSystem.IsWindows()
            ? NativeMethods.CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero) != 0
            : NativeMethods.CreateHardLinkUnix(existingPath, linkPath) == 0;
        if (succeeded)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        throw new IOException(
            "The encrypted workspace name could not be linked safely.",
            new Win32Exception(error));
    }

    public string ComputeSha256(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        var descriptor = NativeMethods.OpenUnix(path, 0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "The encrypted workspace could not be fingerprinted safely.",
                new Win32Exception(error));
        }

        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        using var nativeStream = new FileStream(handle, FileAccess.Read);
        return Convert.ToHexString(SHA256.HashData(nativeStream));
    }

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

    private static partial class NativeMethods
    {
        [LibraryImport(
            "kernel32",
            EntryPoint = "CreateHardLinkW",
            StringMarshalling = StringMarshalling.Utf16,
            SetLastError = true)]
        internal static partial int CreateHardLinkWindows(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [LibraryImport(
            "libc",
            EntryPoint = "link",
            StringMarshalling = StringMarshalling.Utf8,
            SetLastError = true)]
        internal static partial int CreateHardLinkUnix(
            string existingPath,
            string newPath);

        [LibraryImport(
            "libc",
            EntryPoint = "open",
            StringMarshalling = StringMarshalling.Utf8,
            SetLastError = true)]
        internal static partial int OpenUnix(string path, int flags);
    }
}
