using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotOrbit.Core.Diagnostics;

namespace DotOrbit.Desktop;

// Opt-in, local-only diagnostics. No content, entity identifiers, paths, SQL or exception text.
internal sealed class PerformanceTraceSession : IDisposable
{
    internal const int MaximumSamples = 30_000;
    internal const int MaximumOutputBytes = 16 * 1024 * 1024;
    private readonly Stream _output;
    private readonly PerformanceRecording _recording;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stop = new();
    private Task? _probe;
    private int _uiThreadId;
    private bool _disposed;

    private PerformanceTraceSession(Stream output, TimeProvider timeProvider)
    {
        _output = output;
        _timeProvider = timeProvider;
        _recording = PerformanceTrace.Start(timeProvider, MaximumSamples);
    }

    internal static bool IsRequested(string[] args) =>
        args.Contains("--performance-trace", StringComparer.Ordinal);

    internal static PerformanceTraceSession? TryStart(string[] args, string outputDirectory)
    {
        if (!IsRequested(args)) return null;
        var identifiers = args.Where(argument => argument.StartsWith("--performance-trace-id=", StringComparison.Ordinal))
            .Select(argument => argument["--performance-trace-id=".Length..]).ToArray();
        if (identifiers.Length > 1 || (identifiers.Length == 1 && !IsValidIdentifier(identifiers[0])))
            return null;
        var identifier = identifiers.Length == 1 ? identifiers[0] : Guid.NewGuid().ToString("N");
        return TryStart(args, () => new FileStream(
            Path.Combine(outputDirectory, $"dot-orbit-performance-{identifier}.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    }

    internal static bool IsValidIdentifier(string identifier) =>
        identifier.Length is >= 1 and <= 32
        && identifier.All(character => char.IsAsciiDigit(character) || character == '-');

    internal static PerformanceTraceSession? TryStart(
        string[] args,
        Func<Stream> openOutput,
        TimeProvider? timeProvider = null)
    {
        if (!IsRequested(args)) return null;
        Stream? stream = null;
        try
        {
            stream = openOutput();
            return new(stream, timeProvider ?? TimeProvider.System);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            stream?.Dispose();
            Console.Error.WriteLine("performance-trace: recording-unavailable");
            return null;
        }
    }

    internal void StartUiProbe(Action<Action> post, int uiThreadId)
    {
        if (_probe is not null || _disposed) return;
        _uiThreadId = uiThreadId;
        _probe = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, _stop.Token).ConfigureAwait(false);
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var timing = PerformanceTrace.Measure(PerformanceStage.DispatchDelay);
                    try
                    {
                        // End the measurement on the UI, excluding thread-pool continuation delay.
                        // One outstanding callback at most; a blocked UI cannot accumulate probes.
                        post(() =>
                        {
                            timing.Dispose();
                            completed.TrySetResult();
                        });
                        await completed.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Restore this execution context, or omit a callback never delivered.
                        timing.Cancel();
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Closing the application must not wait for another dispatcher callback.
            }
            catch (InvalidOperationException)
            {
                // The dispatcher may already be shutting down; diagnostics remain optional.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try
        {
            _probe?.GetAwaiter().GetResult();
            _recording.Dispose();
            var samples = _recording.Snapshot();
            var report = new
            {
                schema = "dot-orbit-performance-v1",
                appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                configuration = BuildConfiguration,
                os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                osVersion = Environment.OSVersion.Version.ToString(),
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = Environment.Version.ToString(),
                processorCount = Environment.ProcessorCount,
                uiThreadId = _uiThreadId,
                droppedSamples = _recording.DroppedSamples,
                sampleLimit = MaximumSamples,
                boundary = "Command and dispatcher timings; not presented-frame latency",
                samples = samples.Select(sample => new
                {
                    id = sample.Id,
                    parentId = sample.ParentId,
                    stage = sample.Stage.ToString(),
                    operation = sample.Operation.ToString(),
                    startMs = sample.StartMilliseconds,
                    durationMs = sample.DurationMilliseconds,
                    threadId = sample.ThreadId,
                }),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
            if (bytes.Length > MaximumOutputBytes)
            {
                Console.Error.WriteLine("performance-trace: output-limit-exceeded");
                return;
            }
            _output.Write(bytes);
            _output.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("performance-trace: output-unavailable");
        }
        finally
        {
            _recording.Dispose();
            try { _output.Dispose(); }
            catch (IOException) { /* Diagnostics must not prevent application shutdown. */ }
            _stop.Dispose();
        }
    }

    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
