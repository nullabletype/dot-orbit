using Avalonia.Threading;
using DotOrbit.Core.Interchange;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop;

internal sealed class DesktopProposalApplicationHost(ProjectCaptureViewModel work)
    : IProposalApplicationHost
{
    public bool FlushPendingInspectorWork() => OnUiThread(work.CanApplyProposalNow);

    public bool RefreshProjections() => OnUiThread(work.RefreshAfterProposalApply);

    private static bool OnUiThread(Func<bool> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
    }
}
