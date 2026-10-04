using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecoveryViewModelTests
{
    [Fact]
    public void ConstructionLoadsThePersistedAutomaticRecoveryDirectory()
    {
        var recovery = new StubWorkspaceRecovery
        {
            AutomaticRecoveryDirectoryPath = "/persisted/recovery",
        };

        var viewModel = CreateViewModel(recovery, new StubRecoveryPathPicker());

        Assert.Equal("/persisted/recovery", viewModel.RecoveryDirectoryPath);
        Assert.True(viewModel.HasRecoveryDirectory);
    }

    [Fact]
    public async Task SelectionUsesFilesystemPathsAndCancellationPreservesTheExistingChoice()
    {
        var picker = new StubRecoveryPathPicker
        {
            DirectoryResults = new Queue<string?>(["/sync-folder/recovery", null]),
            RecoveryPointResults = new Queue<string?>(["/portable/selected.dotorbit-recovery", null]),
        };
        var viewModel = CreateViewModel(new StubWorkspaceRecovery(), picker);

        await viewModel.SelectRecoveryDirectoryAsync();
        await viewModel.SelectRecoveryPointAsync();
        await viewModel.SelectRecoveryDirectoryAsync();
        await viewModel.SelectRecoveryPointAsync();

        Assert.Equal("/sync-folder/recovery", viewModel.RecoveryDirectoryPath);
        Assert.Equal("/portable/selected.dotorbit-recovery", viewModel.RecoveryPointPath);
        Assert.Equal("selected.dotorbit-recovery", viewModel.RecoveryPointName);
    }

    [Fact]
    public async Task CreateRecoveryPointUsesSelectedDirectoryAndAnnouncesValidation()
    {
        var recovery = new StubWorkspaceRecovery
        {
            CreationResult = RecoveryPointCreationResult.Created(
                "/sync-folder/recovery/point.dotorbit-recovery"),
        };
        var picker = new StubRecoveryPathPicker
        {
            DirectoryResults = new Queue<string?>(["/sync-folder/recovery"]),
        };
        var viewModel = CreateViewModel(recovery, picker);
        await viewModel.SelectRecoveryDirectoryAsync();

        viewModel.CreateRecoveryPointCommand.Execute(null);

        Assert.Equal("/sync-folder/recovery", recovery.LastCreationDirectory);
        Assert.Equal("Encrypted recovery point created and validated.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedAutomaticDirectoryConfigurationPreservesPreviousSelection()
    {
        var recovery = new StubWorkspaceRecovery
        {
            AutomaticRecoveryDirectoryPath = "/existing/recovery",
            ConfigurationResult = RecoveryDirectoryConfigurationResult.Failed(),
        };
        var picker = new StubRecoveryPathPicker
        {
            DirectoryResults = new Queue<string?>(["/unavailable/recovery"]),
        };
        var viewModel = CreateViewModel(recovery, picker);

        await viewModel.SelectRecoveryDirectoryAsync();

        Assert.Equal("/existing/recovery", viewModel.RecoveryDirectoryPath);
        Assert.Equal(
            "The automatic recovery directory could not be saved. The previous directory is unchanged.",
            viewModel.StatusMessage);
        Assert.DoesNotContain("/unavailable/recovery", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateWithoutDirectoryDoesNotCallRecoveryModule()
    {
        var recovery = new StubWorkspaceRecovery();
        var viewModel = CreateViewModel(recovery, new StubRecoveryPathPicker());

        viewModel.CreateRecoveryPointCommand.Execute(null);

        Assert.Equal(0, recovery.CreateCallCount);
        Assert.Equal("Choose a recovery directory first.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CancelRestoreKeepsCurrentWorkspaceAndReturnsToReviewState()
    {
        var recovery = new StubWorkspaceRecovery();
        var viewModel = await CreateSelectedViewModelAsync(recovery);
        var cancelled = 0;
        viewModel.RestoreCancelled += (_, _) => cancelled++;
        viewModel.RequestRestoreCommand.Execute(null);
        Assert.True(viewModel.IsRestoreConfirmationVisible);

        viewModel.CancelRestoreCommand.Execute(null);

        Assert.False(viewModel.IsRestoreConfirmationVisible);
        Assert.Equal(0, recovery.RestoreCallCount);
        Assert.Equal(1, cancelled);
        Assert.Equal(
            "Restore cancelled. The current workspace was not changed.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task ConfirmRestorePassesBothSelectionsAndReplacesTheConsumedSession()
    {
        var replacementRecovery = new StubWorkspaceRecovery();
        using var replacementSession = new StubWorkspaceSession(replacementRecovery);
        var recovery = new StubWorkspaceRecovery
        {
            RestoreResult = WorkspaceRestoreResult.Restored(replacementSession),
        };
        IWorkspaceSession? replaced = null;
        var viewModel = await CreateSelectedViewModelAsync(
            recovery,
            session => replaced = session);
        viewModel.RequestRestoreCommand.Execute(null);

        viewModel.ConfirmRestoreCommand.Execute(null);

        Assert.Equal("/portable/selected.dotorbit-recovery", recovery.LastRestorePath);
        Assert.Equal("/sync-folder/recovery", recovery.LastPreRestoreDirectory);
        Assert.Same(replacementSession, replaced);
        Assert.Equal(
            "Recovery point restored and the workspace reopened.",
            viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(
        WorkspaceRestoreStatus.InvalidRecoveryPoint,
        "The recovery point could not be validated with this workspace passphrase.")]
    [InlineData(
        WorkspaceRestoreStatus.UnsupportedSchema,
        "This recovery point was created by a newer version of dot-orbit.")]
    [InlineData(
        WorkspaceRestoreStatus.PreRestoreRecoveryFailed,
        "Restore stopped because the current workspace recovery point could not be created.")]
    [InlineData(
        WorkspaceRestoreStatus.Failed,
        "The workspace could not be restored. Known-valid files were kept.")]
    public async Task RestoreFailureUsesBoundedNonSensitiveFeedback(
        WorkspaceRestoreStatus status,
        string expectedMessage)
    {
        var recovery = new StubWorkspaceRecovery
        {
            RestoreResult = status switch
            {
                WorkspaceRestoreStatus.InvalidRecoveryPoint =>
                    WorkspaceRestoreResult.InvalidRecoveryPoint(),
                WorkspaceRestoreStatus.UnsupportedSchema =>
                    WorkspaceRestoreResult.UnsupportedSchema(),
                WorkspaceRestoreStatus.PreRestoreRecoveryFailed =>
                    WorkspaceRestoreResult.PreRestoreRecoveryFailed(),
                _ => WorkspaceRestoreResult.Failed(),
            },
        };
        var viewModel = await CreateSelectedViewModelAsync(recovery);
        viewModel.RequestRestoreCommand.Execute(null);

        viewModel.ConfirmRestoreCommand.Execute(null);

        Assert.Equal(expectedMessage, viewModel.StatusMessage);
        Assert.DoesNotContain("selected.dotorbit-recovery", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsRestoreConfirmationVisible);
    }

    private static RecoveryViewModel CreateViewModel(
        IWorkspaceRecovery recovery,
        IRecoveryPathPicker picker,
        Action<IWorkspaceSession>? sessionReplaced = null) =>
        new(recovery, picker, sessionReplaced ?? (_ => { }));

    private static async Task<RecoveryViewModel> CreateSelectedViewModelAsync(
        IWorkspaceRecovery recovery,
        Action<IWorkspaceSession>? sessionReplaced = null)
    {
        var picker = new StubRecoveryPathPicker
        {
            DirectoryResults = new Queue<string?>(["/sync-folder/recovery"]),
            RecoveryPointResults = new Queue<string?>(["/portable/selected.dotorbit-recovery"]),
        };
        var viewModel = CreateViewModel(recovery, picker, sessionReplaced);
        await viewModel.SelectRecoveryDirectoryAsync();
        await viewModel.SelectRecoveryPointAsync();
        return viewModel;
    }

    internal sealed class StubRecoveryPathPicker : IRecoveryPathPicker
    {
        public Queue<string?> DirectoryResults { get; init; } = new([null]);

        public Queue<string?> RecoveryPointResults { get; init; } = new([null]);

        public Task<string?> SelectRecoveryDirectoryAsync() =>
            Task.FromResult(DirectoryResults.Dequeue());

        public Task<string?> SelectRecoveryPointAsync() =>
            Task.FromResult(RecoveryPointResults.Dequeue());
    }

    internal sealed class StubWorkspaceRecovery : IWorkspaceRecovery
    {
        public string? AutomaticRecoveryDirectoryPath { get; set; }

        public RecoveryDirectoryConfigurationResult ConfigurationResult { get; set; } =
            RecoveryDirectoryConfigurationResult.Configured("/sync-folder/recovery");

        public int CreateCallCount { get; private set; }

        public string? LastCreationDirectory { get; private set; }

        public string? LastPreRestoreDirectory { get; private set; }

        public string? LastRestorePath { get; private set; }

        public int RestoreCallCount { get; private set; }

        public RecoveryPointCreationResult CreationResult { get; init; } =
            RecoveryPointCreationResult.Failed();

        public WorkspaceRestoreResult RestoreResult { get; init; } =
            WorkspaceRestoreResult.Failed();

        public RecoveryDirectoryConfigurationResult ConfigureAutomaticRecoveryDirectory(
            string directoryPath)
        {
            if (ConfigurationResult.Status == RecoveryDirectoryConfigurationStatus.Configured)
            {
                AutomaticRecoveryDirectoryPath = ConfigurationResult.DirectoryPath;
            }

            return ConfigurationResult;
        }

        public RecoveryPointCreationResult CreateRecoveryPoint(string directoryPath)
        {
            CreateCallCount++;
            LastCreationDirectory = directoryPath;
            return CreationResult;
        }

        public WorkspaceRestoreResult Restore(
            string recoveryPointPath,
            string preRestoreRecoveryDirectoryPath)
        {
            RestoreCallCount++;
            LastRestorePath = recoveryPointPath;
            LastPreRestoreDirectory = preRestoreRecoveryDirectoryPath;
            return RestoreResult;
        }
    }

    internal sealed class StubWorkspaceSession(IWorkspaceRecovery recovery) : IWorkspaceSession
    {
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();

        public int SchemaVersion => 1;

        public string FirstCategoryName => "Home";

        public IWorkspaceRecovery Recovery { get; } = recovery;

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) =>
            PassphraseRotationResult.Failed();

        public void Dispose()
        {
        }
    }
}
