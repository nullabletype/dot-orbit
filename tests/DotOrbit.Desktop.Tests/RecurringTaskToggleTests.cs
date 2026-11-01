using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecurringTaskToggleTests
{
    private static FixedTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private static RecurringMemoryWorkspace Store()
    {
        var store = new RecurringMemoryWorkspace();
        store.CreateRecurringTask(new("Scheduled", "", null, "home", [], RecurrenceCadence.Daily, new(2026, 11, 2), null, true));
        return store;
    }
    private static RecurringTaskToggle Toggle(Window window) => Assert.Single(window.GetVisualDescendants().OfType<RecurringTaskToggle>(), toggle => toggle.IsEffectivelyVisible);
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }
    private static void AssertState(RecurringTaskToggle toggle, bool enabled)
    {
        Assert.Equal(enabled, toggle.IsChecked);
        Assert.Equal(enabled ? "Pause recurring Task Scheduled" : "Resume recurring Task Scheduled", AutomationProperties.GetName(toggle));
        Assert.Equal(AutomationProperties.GetName(toggle), ToolTip.GetTip(toggle));
        var provider = ControlAutomationPeer.CreatePeerForElement(toggle).GetProvider<IToggleProvider>();
        Assert.NotNull(provider);
        Assert.Equal(enabled ? ToggleState.On : ToggleState.Off, provider.ToggleState);
    }

    [AvaloniaTheory]
    [InlineData("Stay")]
    [InlineData("Mutation")]
    [InlineData("Refresh")]
    public void ToggleStateRemainsCommittedAcrossDirtyGuardAndFailures(string scenario)
    {
        var store = Store();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var toggle = Toggle(window);
            AssertState(toggle, true);
            if (scenario == "Stay")
            {
                shell.Work!.Recurring.Definitions.Single().EditCommand.Execute(null);
                shell.Work.Recurring.Title = "Unsaved title";
            }
            store.FailUpdate = scenario == "Mutation";
            store.FailReadAfterMutation = scenario == "Refresh";
            Click(window, toggle);
            Dispatcher.UIThread.RunJobs();
            AssertState(toggle, true);
            if (scenario == "Stay")
            {
                Assert.True(shell.Work!.Recurring.NeedsDecision);
                shell.Work.Recurring.StayCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                AssertState(toggle, true);
                Assert.True(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
                Assert.Equal("Unsaved title", shell.Work.Recurring.Title);
                Assert.Equal(0, store.UpdateCalls);
            }
            else if (scenario == "Mutation")
            {
                Assert.True(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
                Assert.False(shell.Work!.Recurring.HasEditor);
                Assert.Contains("Could not save", shell.Work.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
                Assert.True(shell.Work!.CanRetryWorkspaceRefresh);
                store.FailReads = false;
                store.FailReadAfterMutation = false;
                shell.Work.RetryWorkspaceRefreshCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                AssertState(Toggle(window), false);
                Assert.Equal(1, store.UpdateCalls);
            }
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaFact]
    public async Task HeldPersistenceKeepsCheckedStateThenPointerAndKeyboardCommitIndependentlyOfEdit()
    {
        var store = Store();
        using var release = new ManualResetEventSlim();
        var operation = new HeldToggleOperation(store, release);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, Clock());
        var shell = new ShellViewModel(store, Clock(), null, executor, executor, new AvaloniaUiDispatcher());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            await shell.Work!.WaitForWorkspaceActionsAsync();
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var toggle = Toggle(window);
            Click(window, toggle);
            await operation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            AssertState(toggle, true);
            Assert.True(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
            Assert.False(shell.Work.Recurring.HasEditor);
            release.Set();
            await shell.Work.WaitForWorkspaceActionsAsync();
            Dispatcher.UIThread.RunJobs();
            var resume = Toggle(window);
            AssertState(resume, false);
            Assert.True(resume.IsFocused);
            Assert.Equal(toggle.Bounds.Width, resume.Bounds.Width);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await shell.Work.WaitForWorkspaceActionsAsync();
            Dispatcher.UIThread.RunJobs();
            AssertState(Toggle(window), true);
            Assert.False(shell.Work.Recurring.HasEditor);
            Assert.True(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
            Assert.Equal(2, store.UpdateCalls);
        }
        finally { release.Set(); shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    private sealed class HeldToggleOperation(IWorkspaceWork work, ManualResetEventSlim release) : IWorkspacePersistenceOperation
    {
        private readonly WorkspacePersistenceOperation _inner = new(work);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InspectorSaveResult Execute(InspectorSaveRequest request) => _inner.Execute(request);
        public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
        {
            if (request.Kind == WorkspaceActionKind.SetRecurringEnabled)
            {
                Started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
            return _inner.ExecuteAction(request);
        }
    }
}
