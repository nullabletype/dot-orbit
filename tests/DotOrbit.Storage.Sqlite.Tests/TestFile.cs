using System.Security.Cryptography;

namespace DotOrbit.Storage.Sqlite.Tests;

internal static class TestFile
{
    public static byte[] ReadAllBytesWithSharedAccess(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var contents = new MemoryStream();
        stream.CopyTo(contents);
        return contents.ToArray();
    }

    public static string ComputeSha256WithSharedAccess(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
