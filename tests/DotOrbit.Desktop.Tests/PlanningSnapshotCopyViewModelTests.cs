using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Desktop.ViewModels;
using System.Text;
using Avalonia.Input;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PlanningSnapshotCopyViewModelTests
{
    [Fact]
    public void PlainTextTransferPublishesStrictUtf8TextAndRejectsInvalidBytes()
    {
        using var transfer = PlainTextClipboardTransfer.Create("Synthetic café"u8);

        Assert.Equal("Synthetic café", transfer.TryGetText());
        Assert.Throws<DecoderFallbackException>(() =>
            PlainTextClipboardTransfer.Create(new byte[] { 0xff }));
    }

    [Fact]
    public void ReviewReportsIncludedRelationshipsExcludedChildrenAndHiddenCatalogueScope()
    {
        var clipboard = new RecordingPlainTextClipboard();
        var viewModel = new PlanningSnapshotCopyViewModel(
            CreateRepresentativeSnapshot(),
            includeAgentInstructionsAndSchema: true,
            clipboard);

        Assert.Equal("Reusable Categories: 2", viewModel.CategoryScopeText);
        Assert.Equal("Reusable Participant labels: 2", viewModel.ParticipantScopeText);
        Assert.Equal("Active Projects: 1", viewModel.ProjectScopeText);
        Assert.Equal("Included Tasks: 2, including 1 completed", viewModel.TaskScopeText);
        Assert.Equal(
            "Individually archived child Tasks excluded from included Projects: 1",
            viewModel.ExcludedChildScopeText);
        Assert.Contains("relationships", viewModel.OutputScopeText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CanConfirmCopy);
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task CancellationWritesNothingAndEachReviewCanConfirmOnlyOneCopy()
    {
        var clipboard = new RecordingPlainTextClipboard();
        var cancelled = new PlanningSnapshotCopyViewModel(
            CreateEmptySnapshot(),
            includeAgentInstructionsAndSchema: false,
            clipboard);

        cancelled.CancelCopy();

        Assert.Equal(0, clipboard.WriteCount);
        Assert.Equal(
            "Planning snapshot copy cancelled. Clipboard contents were not changed.",
            cancelled.StatusMessage);

        var confirmed = new PlanningSnapshotCopyViewModel(
            CreateRepresentativeSnapshot(),
            includeAgentInstructionsAndSchema: false,
            clipboard);
        await confirmed.ConfirmCopyAsync();
        await confirmed.ConfirmCopyAsync();

        Assert.Equal(1, clipboard.WriteCount);
        Assert.False(confirmed.CanConfirmCopy);
        Assert.Equal(
            "Planning snapshot copied as sensitive unencrypted plaintext.",
            confirmed.StatusMessage);
    }

    [Fact]
    public async Task ClipboardFailureUsesFixedStatusWithoutPayloadContent()
    {
        var viewModel = new PlanningSnapshotCopyViewModel(
            CreateRepresentativeSnapshot("private synthetic title"),
            includeAgentInstructionsAndSchema: true,
            new RecordingPlainTextClipboard(
                new InvalidOperationException("private synthetic title leaked from platform")));

        await viewModel.ConfirmCopyAsync();

        Assert.Equal(
            "The Planning snapshot could not be copied. Try again.",
            viewModel.StatusMessage);
        Assert.DoesNotContain("private synthetic", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CanConfirmCopy);
    }

    [Fact]
    public async Task CombinedOverLimitFailsBeforeClipboardAccessWithoutPartialPayload()
    {
        var clipboard = new RecordingPlainTextClipboard();
        var snapshot = CreateRepresentativeSnapshot() with
        {
            Tasks =
            [
                CreateRepresentativeSnapshot().Tasks[0] with
                {
                    Description = new string('x', AgentInterchangeV1.MaximumOutputBytes),
                },
            ],
        };
        var viewModel = new PlanningSnapshotCopyViewModel(
            snapshot,
            includeAgentInstructionsAndSchema: true,
            clipboard);

        await viewModel.ConfirmCopyAsync();

        Assert.False(viewModel.CanConfirmCopy);
        Assert.Equal(PlanningSnapshotCopyViewModel.TooLargeStatus, viewModel.StatusMessage);
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task RepresentativeLongSnapshotCanBeCopiedInSnapshotOnlyMode()
    {
        var clipboard = new RecordingPlainTextClipboard();
        var snapshot = CreateRepresentativeSnapshot() with
        {
            Tasks =
            [
                CreateRepresentativeSnapshot().Tasks[0] with
                {
                    Description = new string('x', 65_537),
                },
            ],
        };
        var viewModel = new PlanningSnapshotCopyViewModel(
            snapshot,
            includeAgentInstructionsAndSchema: false,
            clipboard);

        await viewModel.ConfirmCopyAsync();

        Assert.Equal(1, clipboard.WriteCount);
        Assert.True(clipboard.LastPayloadLength > 65_537);
    }

    private static PlanningSnapshotContent CreateEmptySnapshot() => new(
        new WorkspaceRevision(1),
        [],
        [],
        [],
        [],
        new([], [], []));

    internal static PlanningSnapshotContent CreateRepresentativeSnapshot(
        string taskTitle = "Current task") => new(
        new WorkspaceRevision(14),
        [new("category-current", "Current"), new("category-hidden", "Hidden catalogue label")],
        [new("participant-current", "Current participant"), new("participant-hidden", "Hidden catalogue participant")],
        [
            new(
                "project-current",
                "Current project",
                "",
                "category-current",
                null,
                PlanningProjectStatus.InProgress,
                new(1, 3),
                null,
                1,
                ["task-current", "task-complete"]),
        ],
        [
            new(
                "task-current",
                "project-current",
                taskTitle,
                "",
                new(PlanningTaskCategoryMode.Inherited, "category-current"),
                null,
                false,
                null,
                ["participant-current"],
                TodayLane.Planned),
            new(
                "task-complete",
                "project-current",
                "Completed task",
                "",
                new(PlanningTaskCategoryMode.Explicit, "category-current"),
                null,
                true,
                new DateOnly(2026, 10, 12),
                [],
                null),
        ],
        new(
            ["category-current", "category-hidden"],
            ["project-current"],
            ["task-current", "task-complete"]));

    internal sealed class RecordingPlainTextClipboard(Exception? failure = null) : IPlainTextClipboard
    {
        public int WriteCount { get; private set; }
        public int LastPayloadLength { get; private set; }

        public ValueTask WriteTextAsync(ReadOnlyMemory<byte> strictUtf8Text)
        {
            WriteCount++;
            LastPayloadLength = strictUtf8Text.Length;
            return failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(failure);
        }
    }
}
