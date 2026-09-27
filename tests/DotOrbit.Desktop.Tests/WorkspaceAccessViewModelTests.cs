using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspaceAccessViewModelTests
{
    [Fact]
    public void DefaultWorkspacePathUsesTheLocalDotOrbitDirectory()
    {
        var workspacePath = new SystemWorkspacePathProvider().GetDefaultWorkspacePath();

        Assert.True(Path.IsPathFullyQualified(workspacePath));
        Assert.Equal("workspace.db", Path.GetFileName(workspacePath));
        Assert.Equal("dot-orbit", Path.GetFileName(Path.GetDirectoryName(workspacePath)));
    }

    [Fact]
    public void MissingWorkspaceStartsFirstRunWithoutCreatingAnything()
    {
        var store = new StubWorkspaceStore(exists: false);
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { });

        Assert.True(viewModel.IsCreateMode);
        Assert.False(viewModel.IsUnlockMode);
        Assert.Equal("Create your workspace", viewModel.Heading);
        Assert.Equal(0, store.CreateCallCount);
        Assert.Equal(0, store.OpenCallCount);
    }

    [Fact]
    public void ExistingWorkspaceStartsUnlockWithoutExposingFirstRunFields()
    {
        var store = new StubWorkspaceStore(exists: true);
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { });

        Assert.False(viewModel.IsCreateMode);
        Assert.True(viewModel.IsUnlockMode);
        Assert.Equal("Unlock your workspace", viewModel.Heading);
    }

    [Fact]
    public void InvalidCreationDoesNotCallStorageAndExplainsTheFirstFailure()
    {
        var store = new StubWorkspaceStore(exists: false);
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { })
        {
            FirstCategoryName = "   ",
            Passphrase = "short",
            Confirmation = "different",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(0, store.CreateCallCount);
        Assert.Equal("Name your first Category.", viewModel.ValidationMessage);
        Assert.True(viewModel.HasValidationMessage);
    }

    [Fact]
    public void ValidCreationPassesTheNamedCategoryAndClearsTextSecrets()
    {
        using var session = new StubWorkspaceSession("Personal Admin");
        var store = new StubWorkspaceStore(exists: false)
        {
            CreateResult = WorkspaceCreationResult.Created(session),
        };
        IWorkspaceSession? opened = null;
        var viewModel = new WorkspaceAccessViewModel(
            store,
            "/data/workspace.db",
            sessionResult => opened = sessionResult)
        {
            FirstCategoryName = "  Personal Admin  ",
            Passphrase = "correct horse battery",
            Confirmation = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(1, store.CreateCallCount);
        Assert.Equal("Personal Admin", store.LastCategoryName);
        Assert.Same(session, opened);
        Assert.Empty(viewModel.Passphrase);
        Assert.Empty(viewModel.Confirmation);
    }

    [Theory]
    [InlineData(WorkspaceOpenStatus.InvalidPassphraseOrStore)]
    [InlineData(WorkspaceOpenStatus.Failed)]
    public void FailedUnlockUsesOneNonSensitiveMessageAndRequestsPassphraseFocus(
        WorkspaceOpenStatus status)
    {
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = status == WorkspaceOpenStatus.InvalidPassphraseOrStore
                ? WorkspaceOpenResult.InvalidPassphraseOrStore()
                : WorkspaceOpenResult.Failed(),
        };
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { })
        {
            Passphrase = "a wrong passphrase",
        };
        var focusRequests = 0;
        viewModel.PassphraseFocusRequested += (_, _) => focusRequests++;

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal("The workspace could not be unlocked. Check the passphrase and try again.", viewModel.ValidationMessage);
        Assert.DoesNotContain("a wrong passphrase", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.Equal(1, focusRequests);
    }

    [Fact]
    public void NewerSchemaHasAnActionableNonDestructiveMessage()
    {
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = WorkspaceOpenResult.UnsupportedSchema(
                "/safe/pre-migration.dotorbit-recovery"),
        };
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { })
        {
            Passphrase = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(
            "This workspace was created by a newer version of dot-orbit. Update the application to open it.",
            viewModel.ValidationMessage);
        Assert.False(viewModel.CanRestoreMigrationRecovery);

        viewModel.RestoreMigrationRecoveryCommand.Execute(null);

        Assert.Equal(0, store.MigrationRestoreCallCount);
    }

    [Fact]
    public void FailedMigrationIdentifiesTheValidatedRecoveryPoint()
    {
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = WorkspaceOpenResult.MigrationFailed("/safe/pre-migration.dotorbit-recovery"),
            MigrationRestoreResult = MigrationRecoveryRestoreResult.Restored(),
        };
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { })
        {
            Passphrase = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(
            "The workspace upgrade could not be completed. The original remains usable and a recovery point is available at /safe/pre-migration.dotorbit-recovery.",
            viewModel.ValidationMessage);
        Assert.Empty(viewModel.Passphrase);
        Assert.True(viewModel.CanRestoreMigrationRecovery);

        viewModel.RestoreMigrationRecoveryCommand.Execute(null);

        Assert.Equal(1, store.MigrationRestoreCallCount);
        Assert.False(viewModel.CanRestoreMigrationRecovery);
        Assert.Equal(
            "The pre-upgrade workspace was restored. Enter your passphrase to retry the upgrade.",
            viewModel.ValidationMessage);
    }

    [Fact]
    public void SuccessfulUnlockReturnsTheSessionAndClearsTheTextSecret()
    {
        using var session = new StubWorkspaceSession("Home");
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = WorkspaceOpenResult.Opened(session),
        };
        IWorkspaceSession? opened = null;
        var viewModel = new WorkspaceAccessViewModel(
            store,
            "/data/workspace.db",
            sessionResult => opened = sessionResult)
        {
            Passphrase = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(1, store.OpenCallCount);
        Assert.Same(session, opened);
        Assert.Empty(viewModel.Passphrase);
        Assert.False(viewModel.HasValidationMessage);
    }

    [Fact]
    public void ShowHideCommandChangesTheAccessibleActionAndPasswordMask()
    {
        var viewModel = new WorkspaceAccessViewModel(
            new StubWorkspaceStore(exists: false),
            "/data/workspace.db",
            _ => { });

        Assert.NotEqual('\0', viewModel.PasswordCharacter);
        Assert.Equal("Show passphrase", viewModel.PassphraseVisibilityActionName);

        viewModel.TogglePassphraseVisibilityCommand.Execute(null);

        Assert.Equal('\0', viewModel.PasswordCharacter);
        Assert.Equal("Hide passphrase", viewModel.PassphraseVisibilityActionName);
    }

    private sealed class StubWorkspaceStore(bool exists) : IWorkspaceStore
    {
        public int CreateCallCount { get; private set; }

        public int OpenCallCount { get; private set; }

        public int MigrationRestoreCallCount { get; private set; }

        public string? LastCategoryName { get; private set; }

        public WorkspaceCreationResult CreateResult { get; init; } = WorkspaceCreationResult.Failed();

        public WorkspaceOpenResult OpenResult { get; init; } = WorkspaceOpenResult.Failed();

        public MigrationRecoveryRestoreResult MigrationRestoreResult { get; init; } =
            MigrationRecoveryRestoreResult.Failed();

        public bool Exists(string path) => exists;

        public WorkspaceCreationResult Create(
            string path,
            WorkspacePassphrase passphrase,
            CategoryName firstCategory)
        {
            CreateCallCount++;
            LastCategoryName = firstCategory.Value;
            return CreateResult;
        }

        public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
        {
            OpenCallCount++;
            return OpenResult;
        }

        public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
            string workspacePath,
            WorkspacePassphrase passphrase,
            string recoveryPointPath)
        {
            MigrationRestoreCallCount++;
            return MigrationRestoreResult;
        }
    }

    private sealed class StubWorkspaceSession(string firstCategoryName) : IWorkspaceSession
    {
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();

        public int SchemaVersion => 1;

        public string FirstCategoryName { get; } = firstCategoryName;

        public IWorkspaceRecovery Recovery => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
