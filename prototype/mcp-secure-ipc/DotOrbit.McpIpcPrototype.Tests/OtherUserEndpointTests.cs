using System.Diagnostics;

namespace DotOrbit.McpIpcPrototype.Tests;

public sealed class OtherUserEndpointTests
{
    [Fact]
    public async Task LinuxOtherUserCannotConnectEvenWithExactSocketPath()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("DOTORBIT_REQUIRE_OTHER_USER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "r");
        await using var listener = await LocalEndpoint.CreateSessionAsync(runtimeRoot);
        var start = new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-n", "-u", "nobody",
            PrototypeProcess.PrototypeExecutablePath,
            "probe", "--transport", "unix", "--address", listener.Address.Address,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var probe = Process.Start(start) ?? throw new InvalidOperationException("other-user-probe-did-not-start");
        await probe.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(0, probe.ExitCode);
        Assert.Empty(await probe.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await probe.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));

        var accepting = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();
        await using var sameUser = await LocalEndpoint.ConnectAsync(
            listener.Address,
            TestContext.Current.CancellationToken);
        await using var accepted = await accepting;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ou-{Guid.NewGuid():N}"[..11]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
