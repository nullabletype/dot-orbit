using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Export.Json;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PlaintextExportViewModelTests
{
    [Fact]
    public void PreviewReportsActiveAndArchivedScopeBeforeAnyFileIsWritten()
    {
        var work = CreatePopulatedWork();
        var destination = Destination("workspace-export.json");
        var exporter = new RecordingExporter();
        var viewModel = new PlaintextExportViewModel(
            work,
            new StubDestinationPicker(destination),
            exporter);

        Assert.Equal("Categories: 2", viewModel.CategoryScopeText);
        Assert.Equal("Participant labels: 1", viewModel.ParticipantScopeText);
        Assert.Equal("Projects: 1 active, 1 archived", viewModel.ProjectScopeText);
        Assert.Equal("Tasks: 1 active, 1 archived", viewModel.TaskScopeText);
        Assert.Equal(0, exporter.CallCount);
    }

    [Fact]
    public async Task DestinationSelectionCancellationAndReviewGuardNeverWrite()
    {
        var destination = Destination("workspace-export.json");
        var picker = new StubDestinationPicker(null, destination);
        var exporter = new RecordingExporter();
        var viewModel = new PlaintextExportViewModel(CreatePopulatedWork(), picker, exporter);

        await viewModel.SelectDestinationAsync();
        viewModel.RequestExportCommand.Execute(null);

        Assert.False(viewModel.HasDestination);
        Assert.False(viewModel.IsConfirmationVisible);
        Assert.Equal("Choose an export destination first.", viewModel.StatusMessage);

        await viewModel.SelectDestinationAsync();

        Assert.True(viewModel.HasDestination);
        Assert.Equal("workspace-export.json", viewModel.DestinationName);
    }

    [Fact]
    public async Task ConfirmationCancellationWritesNothingAndSuccessfulConfirmationWritesOnce()
    {
        var destination = Destination("workspace-export.json");
        var exporter = new RecordingExporter();
        var viewModel = new PlaintextExportViewModel(
            CreatePopulatedWork(),
            new StubDestinationPicker(destination),
            exporter);
        await viewModel.SelectDestinationAsync();

        viewModel.RequestExportCommand.Execute(null);
        Assert.True(viewModel.IsConfirmationVisible);
        viewModel.CancelExportCommand.Execute(null);

        Assert.False(viewModel.IsConfirmationVisible);
        Assert.Equal("Export cancelled. No plaintext file was written.", viewModel.StatusMessage);
        Assert.Equal(0, exporter.CallCount);

        viewModel.RequestExportCommand.Execute(null);
        await viewModel.ConfirmExportAsync();

        Assert.Equal(1, exporter.CallCount);
        Assert.Equal("/exports/workspace-export.json", exporter.DestinationPath);
        Assert.Equal("Unencrypted workspace JSON exported.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveFailureUsesBoundedFeedbackWithoutContentOrDestinationDisclosure()
    {
        var destination = Destination("private-customer-file.json");
        var viewModel = new PlaintextExportViewModel(
            CreatePopulatedWork("private customer title"),
            new StubDestinationPicker(destination),
            new RecordingExporter(new IOException("/secret/private-customer-file.json failed")));
        await viewModel.SelectDestinationAsync();
        viewModel.RequestExportCommand.Execute(null);

        await viewModel.ConfirmExportAsync();

        Assert.Equal(
            "The unencrypted export could not be saved. Choose a writable destination and try again.",
            viewModel.StatusMessage);
        Assert.DoesNotContain("private customer", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.HasDestination);
    }

    [Fact]
    public async Task UnexpectedPickerAndExporterFailuresUseBoundedFeedback()
    {
        var pickerFailure = new PlaintextExportViewModel(
            CreatePopulatedWork("private customer title"),
            new ThrowingDestinationPicker(new InvalidOperationException("/secret/picker failed")),
            new RecordingExporter());

        await pickerFailure.SelectDestinationAsync();

        Assert.Equal("The export destination could not be selected. Try again.", pickerFailure.StatusMessage);
        Assert.False(pickerFailure.HasDestination);

        var exporterFailure = new PlaintextExportViewModel(
            CreatePopulatedWork("private customer title"),
            new StubDestinationPicker(Destination("private-customer-file.json")),
            new RecordingExporter(new InvalidOperationException("private customer content failed")));
        await exporterFailure.SelectDestinationAsync();
        exporterFailure.RequestExportCommand.Execute(null);

        await exporterFailure.ConfirmExportAsync();

        Assert.Equal(
            "The unencrypted export could not be saved. Choose a writable destination and try again.",
            exporterFailure.StatusMessage);
        Assert.DoesNotContain("private customer", exporterFailure.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static MemoryWorkspaceWork CreatePopulatedWork(string activeTaskTitle = "Active task")
    {
        var work = new MemoryWorkspaceWork();
        work.CreateParticipant("SD");
        var activeProject = work.CreateProject("Active project", "", "home", null);
        work.CreateTask(activeProject.Id, activeTaskTitle);
        var archivedProject = work.CreateProject("Archived project", "", "home", null);
        work.ArchiveProject(archivedProject.Id);
        var archivedTask = work.CreateStandaloneTask("Archived task", "", "home", null);
        work.CompleteTask(archivedTask.Id);
        work.ArchiveTask(archivedTask.Id);
        return work;
    }

    private static PlaintextExportDestination Destination(string name) => new(name, $"/exports/{name}");

    internal sealed class StubDestinationPicker(params PlaintextExportDestination?[] destinations)
        : IPlaintextExportDestinationPicker
    {
        private readonly Queue<PlaintextExportDestination?> _destinations = new(destinations);

        public Task<PlaintextExportDestination?> SelectAsync() =>
            Task.FromResult(_destinations.Count == 0 ? null : _destinations.Dequeue());
    }

    private sealed class ThrowingDestinationPicker(Exception failure) : IPlaintextExportDestinationPicker
    {
        public Task<PlaintextExportDestination?> SelectAsync() => Task.FromException<PlaintextExportDestination?>(failure);
    }

    internal sealed class RecordingExporter(Exception? failure = null) : IPlaintextWorkspaceExporter
    {
        public int CallCount { get; private set; }
        public WorkspaceWorkSnapshot? Snapshot { get; private set; }
        public string? DestinationPath { get; private set; }

        public Task ExportAsync(
            WorkspaceWorkSnapshot snapshot,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Snapshot = snapshot;
            DestinationPath = destinationPath;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }
}
