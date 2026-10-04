using Avalonia.Threading;

namespace DotOrbit.Desktop.Views;

internal interface ITransientMessageScheduler : IDisposable
{
    void Schedule(TimeSpan delay, long revision, Action<long> callback);
    void Cancel();
}

internal sealed class DispatcherTransientMessageScheduler : ITransientMessageScheduler
{
    private readonly DispatcherTimer _timer = new();
    private Action<long>? _callback;
    private long _revision;

    public DispatcherTransientMessageScheduler()
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

    public void Cancel()
    {
        _timer.Stop();
        _callback = null;
    }

    public void Dispose()
    {
        Cancel();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var callback = _callback;
        _callback = null;
        callback?.Invoke(_revision);
    }
}
