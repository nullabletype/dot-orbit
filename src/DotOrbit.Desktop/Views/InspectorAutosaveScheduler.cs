using Avalonia.Threading;

namespace DotOrbit.Desktop.Views;

internal interface IInspectorAutosaveScheduler : IDisposable
{
    void Schedule(TimeSpan delay, long revision, Action<long> callback);
    void Cancel();
}

internal sealed class DispatcherInspectorAutosaveScheduler : IInspectorAutosaveScheduler
{
    private readonly DispatcherTimer _timer = new();
    private Action<long>? _callback;
    private long _revision;

    public DispatcherInspectorAutosaveScheduler()
    {
        _timer.Tick += OnTick;
    }

    public void Schedule(TimeSpan delay, long revision, Action<long> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _timer.Stop();
        _timer.Interval = delay;
        _revision = revision;
        _callback = callback;
        _timer.Start();
    }

    public void Cancel() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _callback = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        _callback?.Invoke(_revision);
    }
}
