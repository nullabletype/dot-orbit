using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DotOrbit.Interchange.Json;
using Microsoft.Win32.SafeHandles;

namespace DotOrbit.Desktop;

internal enum ProposalFileIntakeStatus
{
    Loaded,
    Rejected,
    Cancelled,
}

internal sealed record ProposalFileIntakeResult(
    ProposalFileIntakeStatus Status,
    string? Text = null)
{
    public static ProposalFileIntakeResult Loaded(string text) =>
        new(ProposalFileIntakeStatus.Loaded, text);

    public static ProposalFileIntakeResult Rejected() =>
        new(ProposalFileIntakeStatus.Rejected);

    public static ProposalFileIntakeResult Cancelled() =>
        new(ProposalFileIntakeStatus.Cancelled);

    public override string ToString() => $"Proposal file intake: {Status}.";
}

internal interface IProposalFileIntake
{
    Task<ProposalFileIntakeResult> ReadAsync(
        string localPath,
        CancellationToken cancellationToken = default);
}

internal sealed class ProposalFileIntake : IProposalFileIntake
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xef, 0xbb, 0xbf];
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _timeProvider;

    public ProposalFileIntake(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProposalFileIntakeResult> ReadAsync(
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        using var deadline = new CancellationTokenSource(ReadDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await Task.Run(
                () => ReadCoreAsync(localPath, linked.Token),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return ProposalFileIntakeResult.Cancelled();
        }
        catch (Exception exception) when (exception is IOException
                                          or ArgumentException
                                          or UnauthorizedAccessException
                                          or DecoderFallbackException
                                          or NotSupportedException)
        {
            return ProposalFileIntakeResult.Rejected();
        }
    }

    private static async Task<ProposalFileIntakeResult> ReadCoreAsync(
        string localPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = NativeProposalFile.OpenRegularFile(localPath);
        var initialLength = RandomAccess.GetLength(handle);
        if (initialLength < 0 || initialLength > ChangeProposalJsonParser.MaximumInputBytes)
        {
            return ProposalFileIntakeResult.Rejected();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ChangeProposalJsonParser.MaximumInputBytes + 1);
        var length = 0;
        try
        {
            while (length <= ChangeProposalJsonParser.MaximumInputBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(
                        length,
                        ChangeProposalJsonParser.MaximumInputBytes + 1 - length),
                    length,
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > ChangeProposalJsonParser.MaximumInputBytes
                || buffer.AsSpan(0, length).StartsWith(Utf8Bom))
            {
                return ProposalFileIntakeResult.Rejected();
            }

            return ProposalFileIntakeResult.Loaded(
                StrictUtf8.GetString(buffer.AsSpan(0, length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static class NativeProposalFile
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileTypeDisk = 0x0001;
        private const int UnixFileTypeMask = 0xf000;
        private const int UnixRegularFile = 0x8000;

        public static SafeFileHandle OpenRegularFile(string localPath)
        {
            if (!Path.IsPathFullyQualified(localPath))
            {
                throw new IOException();
            }

            return OperatingSystem.IsWindows()
                ? OpenWindows(localPath)
                : OpenUnix(localPath);
        }

        private static SafeFileHandle OpenWindows(string localPath)
        {
            if (localPath.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)
                || localPath.StartsWith("\\\\?\\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException();
            }

            var handle = CreateFile(
                localPath,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOverlapped
                    | FileFlagOpenReparsePoint
                    | FileFlagBackupSemantics
                    | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException();
            }

            if (GetFileType(handle) != FileTypeDisk
                || !GetFileInformationByHandle(handle, out var information)
                || (information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
            {
                handle.Dispose();
                throw new IOException();
            }

            return handle;
        }

        private static SafeFileHandle OpenUnix(string localPath)
        {
            var flags = OperatingSystem.IsMacOS()
                ? 0x0004 | 0x0100 | 0x01000000
                : 0x0800 | 0x20000 | 0x80000;
            var descriptor = Open(localPath, flags, 0);
            if (descriptor < 0)
            {
                throw new IOException();
            }

            var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            var stat = Marshal.AllocHGlobal(256);
            try
            {
                if (FStat(descriptor, stat) != 0)
                {
                    handle.Dispose();
                    throw new IOException();
                }

                var mode = OperatingSystem.IsMacOS()
                    ? unchecked((ushort)Marshal.ReadInt16(
                        stat,
                        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 8 : 4))
                    : Marshal.ReadInt32(stat, 24);
                if ((mode & UnixFileTypeMask) != UnixRegularFile)
                {
                    handle.Dispose();
                    throw new IOException();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(stat);
            }

            return handle;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true,
            BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern int Open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            int mode);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStat(int descriptor, IntPtr buffer);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
