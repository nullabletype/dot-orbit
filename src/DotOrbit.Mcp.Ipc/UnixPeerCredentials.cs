using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotOrbit.Mcp.Ipc;

internal readonly record struct VerifiedUnixPeer(uint UserId, int? ProcessId);

internal static partial class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    public static VerifiedUnixPeer VerifySameUser(Socket socket)
    {
        if (OperatingSystem.IsLinux())
        {
            var credentials = default(LinuxCredentials);
            uint length = (uint)Marshal.SizeOf<LinuxCredentials>();
            if (GetSocketOption(socket.SafeHandle, SolSocket, SoPeerCred, out credentials, ref length) != 0
                || length != Marshal.SizeOf<LinuxCredentials>()
                || credentials.UserId != GetEffectiveUserId())
            {
                throw new UnauthorizedAccessException("peer-user-mismatch");
            }

            return new(credentials.UserId, credentials.ProcessId);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (GetPeerEffectiveId(socket.SafeHandle, out var userId, out _) != 0
                || userId != GetEffectiveUserId())
            {
                throw new UnauthorizedAccessException("peer-user-mismatch");
            }

            return new(userId, null);
        }

        throw new PlatformNotSupportedException("unix-peer-credentials-unavailable");
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSocketOption(
        SafeSocketHandle socket,
        int level,
        int optionName,
        out LinuxCredentials optionValue,
        ref uint optionLength);

    [LibraryImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    private static partial int GetPeerEffectiveId(
        SafeSocketHandle socket,
        out uint effectiveUserId,
        out uint effectiveGroupId);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxCredentials
    {
        public int ProcessId;
        public uint UserId;
        public uint GroupId;
    }
}
