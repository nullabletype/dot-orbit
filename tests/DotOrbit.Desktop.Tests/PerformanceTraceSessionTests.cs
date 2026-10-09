using System.Text;
using System.Text.Json;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Desktop;
using Xunit;

namespace DotOrbit.Desktop.Tests;

[CollectionDefinition("Performance trace", DisableParallelization = true)]
public sealed class PerformanceTraceTestGroup;

[Collection("Performance trace")]
public sealed class PerformanceTraceSessionTests
{
    [Fact]
    public void OrdinaryLaunchDoesNotOpenAFileOrEnableTracing()
    {
        var opened = false;
        var session = PerformanceTraceSession.TryStart([], () =>
        {
            opened = true;
            return new MemoryStream();
        });

        Assert.Null(session);
        Assert.False(opened);
        Assert.False(PerformanceTraceSession.IsRequested(["--performance-trace=anything"]));
    }

    [Fact]
    public void OptInWritesOnlyAllowlistedMetadataOnClose()
    {
        using var output = new MemoryStream();
        var session = Assert.IsType<PerformanceTraceSession>(
            PerformanceTraceSession.TryStart(["--performance-trace"], () => output));
        using (PerformanceTrace.Measure(PerformanceStage.Command, "private task title and password")) { }
        Assert.Equal(0, output.Length);

        session.Dispose();
        session.Dispose();

        var json = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("private task", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(json);
        Assert.Equal("dot-orbit-performance-v1", report.RootElement.GetProperty("schema").GetString());
        var sample = Assert.Single(report.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal("Command", sample.GetProperty("stage").GetString());
        Assert.Equal("Other", sample.GetProperty("operation").GetString());
        Assert.Equal(7, sample.EnumerateObject().Count());
    }

    [Fact]
    public void RecordingIsBoundedAndReportsDroppedSamples()
    {
        using var output = new MemoryStream();
        using (Assert.IsType<PerformanceTraceSession>(
            PerformanceTraceSession.TryStart(["--performance-trace"], () => output)))
        {
            for (var index = 0; index < PerformanceTraceSession.MaximumSamples + 5; index++)
                using (PerformanceTrace.Measure(PerformanceStage.Command)) { }
        }

        var bytes = output.ToArray();
        Assert.True(bytes.Length < PerformanceTraceSession.MaximumOutputBytes);
        using var report = JsonDocument.Parse(bytes);
        Assert.Equal(PerformanceTraceSession.MaximumSamples, report.RootElement.GetProperty("samples").GetArrayLength());
        Assert.Equal(5, report.RootElement.GetProperty("droppedSamples").GetInt64());
    }

    [Fact]
    public void UnwritableOutputDoesNotPreventOrdinaryAppStartup()
    {
        Assert.Null(PerformanceTraceSession.TryStart(["--performance-trace"],
            () => throw new UnauthorizedAccessException("sensitive filesystem path")));
    }

    [Fact]
    public void OutputFailureDoesNotEscapeShutdownOrLeaveTracingEnabled()
    {
        using var output = new FailedOutputStream();
        var session = Assert.IsType<PerformanceTraceSession>(
            PerformanceTraceSession.TryStart(["--performance-trace"], () => output));
        using (PerformanceTrace.Measure(PerformanceStage.Command)) { }
        session.Dispose();
        using var next = PerformanceTrace.Start();
        Assert.Empty(next.Snapshot());
    }

    [Fact]
    public async Task ShutdownCancelsAHeldDispatcherProbeWithoutWaitingForTheUi()
    {
        using var output = new MemoryStream();
        var session = Assert.IsType<PerformanceTraceSession>(
            PerformanceTraceSession.TryStart(["--performance-trace"], () => output));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        session.StartUiProbe(_ =>
        {
            Interlocked.Increment(ref posts);
            posted.TrySetResult();
        }, 123);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        session.Dispose();

        Assert.Equal(1, posts);
        using var report = JsonDocument.Parse(output.ToArray());
        Assert.Equal(123, report.RootElement.GetProperty("uiThreadId").GetInt32());
        Assert.DoesNotContain(report.RootElement.GetProperty("samples").EnumerateArray(),
            sample => sample.GetProperty("stage").GetString() == "DispatchDelay");
    }

    [Theory]
    [InlineData("123-456-789", true)]
    [InlineData("../outside", false)]
    [InlineData("", false)]
    [InlineData("123456789012345678901234567890123", false)]
    public void LauncherIdentifierCannotSelectAnotherPath(string identifier, bool valid) =>
        Assert.Equal(valid, PerformanceTraceSession.IsValidIdentifier(identifier));

    [Fact]
    public void ExistingRecordingIsNeverOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dot-orbit-trace-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var args = new[] { "--performance-trace", "--performance-trace-id=123-456" };
            var path = Path.Combine(directory, "dot-orbit-performance-123-456.json");
            File.WriteAllText(path, "previous recording");
            Assert.Null(PerformanceTraceSession.TryStart(args, directory));
            Assert.Equal("previous recording", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DispatcherTimingEndsInCallbackBeforeWorkerContinuation()
    {
        using var output = new MemoryStream();
        var time = new TraceTimestampProvider();
        using var session = Assert.IsType<PerformanceTraceSession>(
            PerformanceTraceSession.TryStart(["--performance-trace"], () => output, time));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        session.StartUiProbe(callback =>
        {
            if (Interlocked.Increment(ref posts) != 1) return;
            time.Timestamp = 50;
            callback();
            time.Timestamp = 5000;
            posted.TrySetResult();
        }, 123);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        session.Dispose();

        using var report = JsonDocument.Parse(output.ToArray());
        var sample = Assert.Single(report.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal("DispatchDelay", sample.GetProperty("stage").GetString());
        Assert.Equal(50, sample.GetProperty("durationMs").GetDouble());
    }

    private sealed class TraceTimestampProvider : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    private sealed class FailedOutputStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("sensitive filesystem path");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("sensitive filesystem path");
    }
}
