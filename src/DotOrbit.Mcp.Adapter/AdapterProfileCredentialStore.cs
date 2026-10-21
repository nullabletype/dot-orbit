using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotOrbit.Mcp.Adapter;

internal interface IAdapterProfileCredentialStore
{
    Task<byte[]> GetOrCreateAsync(string profile, CancellationToken cancellationToken);
}

internal sealed partial class AdapterProfileCredentialStore : IAdapterProfileCredentialStore
{
    private const string Service = "dot-orbit-mcp";
    private const int MaximumSecretToolOutputBytes = 256;

    public async Task<byte[]> GetOrCreateAsync(string profile, CancellationToken cancellationToken)
    {
        ValidateProfile(profile);
        if (OperatingSystem.IsWindows())
        {
            return GetOrCreateWindows(profile);
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetOrCreateMacOS(profile);
        }

        if (OperatingSystem.IsLinux())
        {
            return await GetOrCreateLinuxAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        throw new PlatformNotSupportedException("adapter-credential-store-unavailable");
    }

    private static void ValidateProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)
            || profile.Length > 64
            || profile.Any(character => !char.IsAsciiLetterOrDigit(character)
                                        && character is not '-' and not '_'))
        {
            throw new ArgumentException("adapter-profile-invalid", nameof(profile));
        }
    }

    [SupportedOSPlatform("macos")]
    private static byte[] GetOrCreateMacOS(string profile)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(profile);
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            checked((uint)service.Length),
            service,
            checked((uint)account.Length),
            account,
            out var passwordLength,
            out var passwordData,
            out var item);
        if (status == 0)
        {
            try
            {
                if (passwordLength != 32)
                {
                    throw new InvalidOperationException("adapter-credential-invalid");
                }

                var credential = new byte[passwordLength];
                Marshal.Copy(passwordData, credential, 0, credential.Length);
                return credential;
            }
            finally
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                if (item != IntPtr.Zero)
                {
                    CfRelease(item);
                }
            }
        }

        const int itemNotFound = -25300;
        if (status != itemNotFound)
        {
            throw new InvalidOperationException("adapter-credential-read-failed");
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                checked((uint)service.Length),
                service,
                checked((uint)account.Length),
                account,
                checked((uint)generated.Length),
                generated,
                out item);
            if (item != IntPtr.Zero)
            {
                CfRelease(item);
            }

            if (status != 0)
            {
                throw new InvalidOperationException("adapter-credential-write-failed");
            }

            return generated.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] GetOrCreateWindows(string profile)
    {
        var target = $"{Service}/{profile}";
        if (CredRead(target, 1, 0, out var credentialPointer))
        {
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
                if (credential.CredentialBlobSize != 32 || credential.CredentialBlob == IntPtr.Zero)
                {
                    throw new InvalidOperationException("adapter-credential-invalid");
                }

                var value = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, value, 0, value.Length);
                return value;
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }

        const int notFound = 1168;
        if (Marshal.GetLastPInvokeError() != notFound)
        {
            throw new InvalidOperationException("adapter-credential-read-failed");
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        var blob = Marshal.AllocHGlobal(generated.Length);
        try
        {
            Marshal.Copy(generated, 0, blob, generated.Length);
            var credential = new Credential
            {
                Type = 1,
                TargetName = target,
                CredentialBlobSize = checked((uint)generated.Length),
                CredentialBlob = blob,
                Persist = 2,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException("adapter-credential-write-failed");
            }

            return generated.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
            var zero = new byte[32];
            Marshal.Copy(zero, 0, blob, zero.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<byte[]> GetOrCreateLinuxAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        var existing = await RunSecretToolAsync(
            ["lookup", "application", Service, "profile", profile],
            null,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            try
            {
                var decoded = Convert.FromBase64String(existing.Trim());
                return decoded.Length == 32
                    ? decoded
                    : throw new InvalidOperationException("adapter-credential-invalid");
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("adapter-credential-invalid");
            }
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            await RunSecretToolAsync(
                ["store", "--label=dot-orbit MCP adapter credential", "application", Service, "profile", profile],
                Convert.ToBase64String(generated),
                allowNotFound: false,
                cancellationToken).ConfigureAwait(false);
            return generated.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }
    }

    private static async Task<string?> RunSecretToolAsync(
        string[] arguments,
        string? standardInput,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "secret-tool",
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("adapter-credential-store-unavailable");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("adapter-credential-store-unavailable");
        }

        using (process)
        {
            var output = ReadBoundedOutputAsync(
                process.StandardOutput.BaseStream,
                MaximumSecretToolOutputBytes,
                cancellationToken);
            var error = DrainAsync(process.StandardError.BaseStream, cancellationToken);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            var result = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                return result;
            }

            if (allowNotFound && process.ExitCode == 1)
            {
                return null;
            }

            throw new InvalidOperationException(allowNotFound
                ? "adapter-credential-read-failed"
                : "adapter-credential-write-failed");
        }
    }

    internal static async Task<string> ReadBoundedOutputAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var captured = new byte[maximumBytes];
        var buffer = new byte[128];
        var length = 0;
        var exceeded = false;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var available = maximumBytes - length;
                var toCopy = Math.Min(available, count);
                if (toCopy > 0)
                {
                    buffer.AsSpan(0, toCopy).CopyTo(captured.AsSpan(length));
                    length += toCopy;
                }

                exceeded |= toCopy != count;
            }

            if (exceeded)
            {
                throw new InvalidOperationException("adapter-credential-output-too-large");
            }

            return Encoding.UTF8.GetString(captured, 0, length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(captured);
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
            {
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial void CredFree(IntPtr buffer);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    [SupportedOSPlatform("macos")]
    private static partial int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    [SupportedOSPlatform("macos")]
    private static partial int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    [SupportedOSPlatform("macos")]
    private static partial int SecKeychainItemFreeContent(IntPtr attributeList, IntPtr data);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    [SupportedOSPlatform("macos")]
    private static partial void CfRelease(IntPtr value);
}
