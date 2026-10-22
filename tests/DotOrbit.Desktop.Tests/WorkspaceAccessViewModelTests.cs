using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspaceAccessViewModelTests
{
    [Fact]
    public void DefaultWorkspacePathUsesTheLocalDotOrbitDirectory()
    {
        var workspacePath = new SystemWorkspacePathProvider().GetDefaultWorkspacePath();

        Assert.True(Path.IsPathFullyQualified(workspacePath));
        Assert.Equal("workspace.orb", Path.GetFileName(workspacePath));
        Assert.Equal("dot-orbit", Path.GetFileName(Path.GetDirectoryName(workspacePath)));
    }

    [Fact]
    public void SampleWorkspaceLaunchUsesTheRepositoryArtifact()
    {
        using var repository = TemporaryDirectory.CreateRepository();

        var workspacePath = SystemWorkspacePathProvider.FromArguments(
            ["--sample-workspace"],
            Path.Combine(repository.Path, "src", "DotOrbit.Desktop"))
            .GetDefaultWorkspacePath();

        Assert.Equal(
            Path.Combine(repository.Path, "artifacts", "sample-workspace", "workspace.orb"),
            workspacePath);
    }

    [Fact]
    public void LocalRepositoryLaunchDefaultsToTheSampleWorkspace()
    {
        using var repository = TemporaryDirectory.CreateRepository();
        using var unrelated = TemporaryDirectory.CreateRepository(createSolution: false);

        var workspacePath = SystemWorkspacePathProvider.FromArguments(
            ["--unrelated"],
            unrelated.Path,
            Path.Combine(repository.Path, "src", "DotOrbit.Desktop", "bin", "Debug", "net10.0"))
            .GetDefaultWorkspacePath();

        Assert.Equal(
            Path.Combine(repository.Path, "artifacts", "sample-workspace", "workspace.orb"),
            workspacePath);
    }

    [Fact]
    public void PackagedLaunchKeepsThePlatformWorkspace()
    {
        using var repository = TemporaryDirectory.CreateRepository();
        using var unrelated = TemporaryDirectory.CreateRepository(createSolution: false);

        var workspacePath = SystemWorkspacePathProvider.FromArguments(
            [],
            repository.Path,
            unrelated.Path)
            .GetDefaultWorkspacePath();

        Assert.Equal(WorkspacePathDefaults.GetDefaultWorkspacePath(), workspacePath);
    }

    [Fact]
    public void ConflictingWorkspaceArgumentsAreRejected()
    {
        using var repository = TemporaryDirectory.CreateRepository();

        var error = Assert.Throws<InvalidOperationException>(() =>
            SystemWorkspacePathProvider.FromArguments(
                ["--sample-workspace", "--default-workspace"],
                repository.Path,
                repository.Path));

        Assert.Contains("cannot be used together", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalLaunchCanExplicitlyUseThePlatformWorkspace()
    {
        using var repository = TemporaryDirectory.CreateRepository();

        var workspacePath = SystemWorkspacePathProvider.FromArguments(
            ["--default-workspace"],
            repository.Path)
            .GetDefaultWorkspacePath();

        Assert.Equal(WorkspacePathDefaults.GetDefaultWorkspacePath(), workspacePath);
    }

    [Fact]
    public void SampleWorkspaceLaunchFallsBackToTheApplicationDirectory()
    {
        using var repository = TemporaryDirectory.CreateRepository();
        using var unrelated = TemporaryDirectory.CreateRepository(createSolution: false);

        var workspacePath = SystemWorkspacePathProvider.FromArguments(
            [],
            unrelated.Path,
            Path.Combine(repository.Path, "src", "DotOrbit.Desktop", "bin", "Debug", "net10.0"))
            .GetDefaultWorkspacePath();

        Assert.Equal(
            Path.Combine(repository.Path, "artifacts", "sample-workspace", "workspace.orb"),
            workspacePath);
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

    [Theory]
    [InlineData(DefaultWorkspaceResolutionStatus.Conflict)]
    [InlineData(DefaultWorkspaceResolutionStatus.Failed)]
    public void BlockedStartupDoesNotInspectOrMutateStorageAndDisablesEveryAction(
        DefaultWorkspaceResolutionStatus status)
    {
        var store = new StubWorkspaceStore(exists: true);
        var viewModel = new WorkspaceAccessViewModel(
            store,
            new DefaultWorkspaceResolution(status, "/data/workspace.orb"),
            _ => { });

        Assert.True(viewModel.IsBlocked);
        Assert.False(viewModel.IsCreateMode);
        Assert.False(viewModel.IsUnlockMode);
        Assert.False(viewModel.IsWorkspaceAccessAvailable);
        Assert.Equal("Workspace needs attention", viewModel.Heading);
        Assert.DoesNotContain("/data", viewModel.BlockingMessage, StringComparison.Ordinal);
        Assert.Equal(0, store.ExistsCallCount);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        Assert.False(viewModel.RestoreMigrationRecoveryCommand.CanExecute(null));
        Assert.False(viewModel.TogglePassphraseVisibilityCommand.CanExecute(null));

        viewModel.SubmitCommand.Execute(null);
        viewModel.RestoreMigrationRecoveryCommand.Execute(null);
        viewModel.TogglePassphraseVisibilityCommand.Execute(null);

        Assert.Equal(0, store.CreateCallCount);
        Assert.Equal(0, store.OpenCallCount);
        Assert.Equal(0, store.MigrationRestoreCallCount);
        Assert.False(viewModel.IsPassphraseVisible);
    }

    [Theory]
    [InlineData(WorkspaceOpenStatus.AdoptionConflict)]
    [InlineData(WorkspaceOpenStatus.AdoptionFailed)]
    public void AdoptionProblemBecomesANonSensitiveBlockingState(WorkspaceOpenStatus status)
    {
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = status == WorkspaceOpenStatus.AdoptionConflict
                ? WorkspaceOpenResult.AdoptionConflict()
                : WorkspaceOpenResult.AdoptionFailed(),
        };
        var viewModel = new WorkspaceAccessViewModel(store, "/secret/user/workspace.db", _ => { })
        {
            Passphrase = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.True(viewModel.IsBlocked);
        Assert.Empty(viewModel.Passphrase);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        Assert.DoesNotContain("/secret", viewModel.BlockingMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery", viewModel.BlockingMessage, StringComparison.Ordinal);
        Assert.Equal(1, store.OpenCallCount);

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(1, store.OpenCallCount);
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
            "The workspace upgrade could not be completed. The original remains usable and a validated recovery point is available.",
            viewModel.ValidationMessage);
        Assert.DoesNotContain("/safe", viewModel.ValidationMessage, StringComparison.Ordinal);
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

    [Fact]
    public void MissingRememberedWorkspaceNeverFallsBackToCreation()
    {
        var store = new StubWorkspaceStore(exists: false);
        var preferences = new RecordingWorkspacePathPreferenceStore();
        var path = Path.GetFullPath("/missing/remembered-workspace.custom");
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Existing(path, wasRemembered: true),
            preferences,
            _ => { });

        Assert.True(viewModel.IsWorkspaceUnavailable);
        Assert.True(viewModel.IsBlocked);
        Assert.False(viewModel.IsCreateMode);
        Assert.True(viewModel.CanRetryWorkspace);
        Assert.True(viewModel.CanReturnToDefault);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(0, store.CreateCallCount);
        Assert.Equal(0, store.OpenCallCount);
        Assert.Null(preferences.SavedPath);
    }

    [Fact]
    public void ChosenExistingWorkspaceThatDisappearsMovesToUnavailableWithoutCreation()
    {
        var store = new StubWorkspaceStore(exists: true);
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Existing(Path.GetFullPath("/chosen/workspace.custom")),
            new RecordingWorkspacePathPreferenceStore(),
            _ => { })
        {
            Passphrase = "correct horse battery",
        };
        store.ExistsValue = false;

        viewModel.SubmitCommand.Execute(null);

        Assert.True(viewModel.IsWorkspaceUnavailable);
        Assert.True(viewModel.CanRetryWorkspace);
        Assert.Empty(viewModel.Passphrase);
        Assert.Equal(1, store.OpenCallCount);
        Assert.Equal(0, store.CreateCallCount);
    }

    [Fact]
    public void ChosenNewLocationIsCreatedWithoutInferringIntentFromExistenceAndRememberedOnSuccess()
    {
        using var session = new StubWorkspaceSession("Home");
        var store = new StubWorkspaceStore(exists: true)
        {
            CreateResult = WorkspaceCreationResult.Created(session),
        };
        var preferences = new RecordingWorkspacePathPreferenceStore();
        var path = Path.GetFullPath("/chosen/new-workspace.any-name");
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Existing(Path.GetFullPath("/old/workspace.orb")),
            preferences,
            _ => { });

        viewModel.SelectNewWorkspace(path);
        viewModel.FirstCategoryName = "Home";
        viewModel.Passphrase = "correct horse battery";
        viewModel.Confirmation = "correct horse battery";
        viewModel.SubmitCommand.Execute(null);

        Assert.True(viewModel.IsCreateMode);
        Assert.Equal(path, store.LastCreatePath);
        Assert.Equal(path, preferences.SavedPath);
    }

    [Fact]
    public void FailedChosenWorkspaceDoesNotReplaceRememberedPath()
    {
        var preferences = new RecordingWorkspacePathPreferenceStore();
        var viewModel = new WorkspaceAccessViewModel(
            new StubWorkspaceStore(exists: false),
            WorkspacePathSelection.New(Path.GetFullPath("/chosen/workspace.orb")),
            preferences,
            _ => { })
        {
            FirstCategoryName = "Home",
            Passphrase = "correct horse battery",
            Confirmation = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Null(preferences.SavedPath);
    }

    [Fact]
    public void SuccessfulLegacyAdoptionRemembersTheCurrentDefaultName()
    {
        using var session = new StubWorkspaceSession("Home");
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = WorkspaceOpenResult.Opened(session),
        };
        var preferences = new RecordingWorkspacePathPreferenceStore();
        var legacy = Path.GetFullPath("/platform/dot-orbit/workspace.db");
        var current = Path.GetFullPath("/platform/dot-orbit/workspace.orb");
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Default(DefaultWorkspaceResolution.Ready(legacy), current),
            preferences,
            _ => { })
        {
            Passphrase = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(legacy, store.LastOpenPath);
        Assert.Equal(current, preferences.SavedPath);
    }

    [Fact]
    public void SuccessfulChosenExistingWorkspaceRemembersItsCanonicalPath()
    {
        using var session = new StubWorkspaceSession("Home");
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = WorkspaceOpenResult.Opened(session),
        };
        var preferences = new RecordingWorkspacePathPreferenceStore();
        var path = Path.GetFullPath("/chosen/../chosen/workspace.custom");
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Existing(path),
            preferences,
            _ => { })
        {
            Passphrase = "correct horse battery",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(Path.GetFullPath(path), preferences.SavedPath);
    }

    [Theory]
    [InlineData(WorkspaceOpenStatus.InvalidPassphraseOrStore)]
    [InlineData(WorkspaceOpenStatus.UnsupportedSchema)]
    [InlineData(WorkspaceOpenStatus.Failed)]
    public void FailedExistingOpenNeverReplacesTheRememberedPath(WorkspaceOpenStatus status)
    {
        var store = new StubWorkspaceStore(exists: true)
        {
            OpenResult = status switch
            {
                WorkspaceOpenStatus.UnsupportedSchema => WorkspaceOpenResult.UnsupportedSchema(),
                WorkspaceOpenStatus.Failed => WorkspaceOpenResult.Failed(),
                _ => WorkspaceOpenResult.InvalidPassphraseOrStore(),
            },
        };
        var preferences = new RecordingWorkspacePathPreferenceStore();
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Existing(Path.GetFullPath("/chosen/workspace.custom")),
            preferences,
            _ => { })
        {
            Passphrase = "incorrect or invalid",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Null(preferences.SavedPath);
    }

    [Fact]
    public void ProductionStartupUsesRememberedPathWhileDevelopmentOverrideStaysExplicit()
    {
        var remembered = Path.GetFullPath("/remembered/custom.workspace");
        var fallback = Path.GetFullPath("/default/workspace.orb");
        var preferences = new RecordingWorkspacePathPreferenceStore(remembered);

        var production = WorkspacePathSelectionFactory.CreateInitial(
            new StubWorkspacePathProvider(fallback, usesRememberedWorkspace: true),
            preferences);
        var development = WorkspacePathSelectionFactory.CreateInitial(
            new StubWorkspacePathProvider(fallback, usesRememberedWorkspace: false),
            preferences);

        Assert.Equal(remembered, production.StoragePath);
        Assert.True(production.WasRemembered);
        Assert.Equal(WorkspacePathIntent.Existing, production.Intent);
        Assert.Equal(fallback, development.StoragePath);
        Assert.Equal(WorkspacePathIntent.Development, development.Intent);
    }

    [Fact]
    public void SystemAvailabilityAcceptsReadableContentAndRejectsDirectoriesAndEmptyFiles()
    {
        using var directory = TemporaryDirectory.CreateRepository(createSolution: false);
        var emptyPath = Path.Combine(directory.Path, "empty.workspace");
        var contentPath = Path.Combine(directory.Path, "content.workspace");
        File.WriteAllBytes(emptyPath, []);
        File.WriteAllBytes(contentPath, [1]);
        var availability = new SystemWorkspacePathAvailability();

        Assert.False(availability.IsAvailableRegularFile(directory.Path));
        Assert.False(availability.IsAvailableRegularFile(emptyPath));
        Assert.True(availability.IsAvailableRegularFile(contentPath));
    }

    [Fact]
    public void PresentButUnavailableRememberedWorkspaceNeverShowsPassphraseOrCreation()
    {
        var store = new StubWorkspaceStore(exists: true);
        var viewModel = new WorkspaceAccessViewModel(
            store,
            WorkspacePathSelection.Existing(
                Path.GetFullPath("/present/but-unreadable.workspace"),
                wasRemembered: true),
            new RecordingWorkspacePathPreferenceStore(),
            _ => { },
            new FixedWorkspacePathAvailability(isAvailable: false));

        Assert.True(viewModel.IsWorkspaceUnavailable);
        Assert.False(viewModel.IsWorkspaceAccessAvailable);
        Assert.False(viewModel.IsCreateMode);
        Assert.Equal(0, store.OpenCallCount);
        Assert.Equal(0, store.CreateCallCount);
    }

    private sealed class StubWorkspaceStore(bool exists) : IWorkspaceStore
    {
        public bool ExistsValue { get; set; } = exists;

        public int ExistsCallCount { get; private set; }

        public int CreateCallCount { get; private set; }

        public int OpenCallCount { get; private set; }

        public int MigrationRestoreCallCount { get; private set; }

        public string? LastCategoryName { get; private set; }

        public string? LastCreatePath { get; private set; }

        public string? LastOpenPath { get; private set; }

        public WorkspaceCreationResult CreateResult { get; init; } = WorkspaceCreationResult.Failed();

        public WorkspaceOpenResult OpenResult { get; init; } = WorkspaceOpenResult.Failed();

        public MigrationRecoveryRestoreResult MigrationRestoreResult { get; init; } =
            MigrationRecoveryRestoreResult.Failed();

        public bool Exists(string path)
        {
            ExistsCallCount++;
            return ExistsValue;
        }

        public WorkspaceCreationResult Create(
            string path,
            WorkspacePassphrase passphrase,
            CategoryName firstCategory)
        {
            CreateCallCount++;
            LastCreatePath = path;
            LastCategoryName = firstCategory.Value;
            return CreateResult;
        }

        public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
        {
            OpenCallCount++;
            LastOpenPath = path;
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

    private sealed class RecordingWorkspacePathPreferenceStore(string? loadedPath = null)
        : IWorkspacePathPreferenceStore
    {
        public string? SavedPath { get; private set; }

        public string? LoadWorkspacePath() => loadedPath;

        public bool TrySaveWorkspacePath(string path)
        {
            SavedPath = path;
            return true;
        }
    }

    private sealed class StubWorkspacePathProvider(
        string path,
        bool usesRememberedWorkspace) : IWorkspacePathProvider
    {
        public bool UsesRememberedWorkspace => usesRememberedWorkspace;

        public string GetDefaultWorkspacePath() => path;

        public DefaultWorkspaceResolution ResolveDefaultWorkspace() =>
            DefaultWorkspaceResolution.Ready(path);
    }

    private sealed class FixedWorkspacePathAvailability(bool isAvailable)
        : IWorkspacePathAvailability
    {
        public bool IsAvailableRegularFile(string path) => isAvailable;
    }

    private sealed class StubWorkspaceSession(string firstCategoryName) : IWorkspaceSession
    {
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();

        public int SchemaVersion => 1;

        public string FirstCategoryName { get; } = firstCategoryName;

        public IWorkspaceRecovery Recovery => throw new NotSupportedException();

        public DotOrbit.Core.Interchange.IProposalWorkspaceSource Proposals => throw new NotSupportedException();

        public DotOrbit.Core.Interchange.IPlanningSnapshotSource Planning => throw new NotSupportedException();

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) =>
            PassphraseRotationResult.Failed();

        public void Dispose()
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory CreateRepository(bool createSolution = true)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dot-orbit-workspace-path-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            if (createSolution)
            {
                File.WriteAllText(System.IO.Path.Combine(path, "DotOrbit.slnx"), "<Solution />");
            }
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
