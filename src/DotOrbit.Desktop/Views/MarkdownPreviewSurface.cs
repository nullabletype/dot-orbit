using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DotOrbit.Desktop.Views;

public sealed class MarkdownPreviewSurface : ContentControl
{
    private bool _pointerPressed;

    public static readonly RoutedEvent<RoutedEventArgs> ActivatedEvent = RoutedEvent.Register<MarkdownPreviewSurface, RoutedEventArgs>(
        nameof(Activated),
        RoutingStrategies.Bubble);

    public MarkdownPreviewSurface()
    {
        Focusable = true;
    }

    public event EventHandler<RoutedEventArgs> Activated
    {
        add => AddHandler(ActivatedEvent, value);
        remove => RemoveHandler(ActivatedEvent, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownPreviewSurfaceAutomationPeer(this);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsFromHyperlink(e.Source)) return;
        _pointerPressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pointerPressed || e.InitialPressMouseButton != MouseButton.Left) return;
        var shouldActivate = new Rect(Bounds.Size).Contains(e.GetPosition(this));
        _pointerPressed = false;
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
        if (shouldActivate) Activate();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _pointerPressed = false;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Source != this || e.Key is not (Key.Enter or Key.Space)) return;
        Activate();
        e.Handled = true;
    }

    internal void Activate()
    {
        Focus();
        RaiseEvent(new RoutedEventArgs(ActivatedEvent, this));
    }

    private static bool IsFromHyperlink(object? source) => source is HyperlinkButton
        || source is Control control && control.GetVisualAncestors().OfType<HyperlinkButton>().Any();

    private sealed class MarkdownPreviewSurfaceAutomationPeer(MarkdownPreviewSurface owner)
        : ControlAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(MarkdownPreviewSurface);

        public void Invoke() => owner.Activate();
    }
}
