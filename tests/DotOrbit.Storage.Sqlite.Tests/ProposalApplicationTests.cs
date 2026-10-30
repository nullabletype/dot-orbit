using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Interchange.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class ProposalApplicationTests : IDisposable
{
    private const string PassphraseText = "private synthetic password";
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "orbit-proposal-apply-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspacePassphrase passphrase = WorkspacePassphrase.Create(
        PassphraseText,
        PassphraseText).Passphrase!;

    private string WorkspacePath => Path.Combine(directory, "workspace.db");

    [Fact]
    public void NewWorkUsesFreshIdsRecoveryAndCurrentRevisionWithoutAProvidedSnapshot()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var beforeRevision = Revision(session);
        var stalePlan = ReviewNewWork(session,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "category",
                ["name"] = "Must be reviewed again",
            });
        var staleApproval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(stalePlan));
        session.Work.CreateParticipant("Workspace changed");

        Assert.Equal(
            ProposalApplyStatus.StaleSource,
            Apply(session, staleApproval, new Host()).Status);
        Assert.DoesNotContain(session.Work.Read().Categories, item => item.Name == "Must be reviewed again");

        var reviewedRevision = Revision(session);
        var plan = ReviewNewWork(session,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "category",
                ["name"] = "Transcript work",
            },
            new JsonObject
            {
                ["operation"] = "createTask",
                ["operationId"] = "2",
                ["localId"] = "task",
                ["title"] = "Follow up from transcript",
                ["description"] = "",
                ["project"] = null,
                ["category"] = new JsonObject
                {
                    ["mode"] = "explicit",
                    ["category"] = Local("category"),
                },
                ["dueDate"] = null,
                ["participants"] = new JsonArray(),
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        Assert.Equal(ProposalApplyStatus.Committed, Apply(session, approval, new Host()).Status);
        Assert.Equal(reviewedRevision + 1, Revision(session));
        var category = Assert.Single(session.Work.Read().Categories, item => item.Name == "Transcript work");
        var task = Assert.Single(session.Work.Read().Tasks, item => item.Title == "Follow up from transcript");
        Assert.Equal(category.Id, task.ExplicitCategoryId);
        Assert.DoesNotContain("category", category.Id, StringComparison.Ordinal);
        var recoveryPath = Assert.Single(Directory.GetFiles(
            recoveryDirectory,
            "dot-orbit-pre-import-*.dotorbit-recovery"));
        using var recovered = Assert.IsAssignableFrom<IWorkspaceSession>(store.Open(recoveryPath, passphrase).Session);
        Assert.DoesNotContain(recovered.Work.Read().Categories, item => item.Name == "Transcript work");
        Assert.DoesNotContain(recovered.Work.Read().Tasks, item => item.Title == "Follow up from transcript");
        Assert.True(beforeRevision < reviewedRevision);
    }

    [Fact]
    public void ApprovedLocalCreatesCommitOnceWithPortablePreImportRecoveryAndReviewedClock()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var storageClock = new ManualTimeProvider(new(2030, 4, 7, 9, 0, 0, TimeSpan.Zero));
        var reviewClock = new ManualTimeProvider(new(2030, 4, 5, 23, 30, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            storageClock);
        using var session = CreateWorkspace(store);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var beforeRevision = Revision(session);
        var plan = Review(session, reviewClock,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new-category",
                ["name"] = "Agent category",
            },
            new JsonObject
            {
                ["operation"] = "createParticipant",
                ["operationId"] = "2",
                ["localId"] = "new-participant",
                ["label"] = "Agent person",
            },
            new JsonObject
            {
                ["operation"] = "createProject",
                ["operationId"] = "3",
                ["localId"] = "new-project",
                ["title"] = "Agent project",
                ["description"] = "Created together",
                ["category"] = Local("new-category"),
                ["targetDate"] = "2030-05-01",
            },
            new JsonObject
            {
                ["operation"] = "createTask",
                ["operationId"] = "4",
                ["localId"] = "new-task",
                ["title"] = "Agent task",
                ["description"] = "Created together",
                ["project"] = Local("new-project"),
                ["category"] = new JsonObject { ["mode"] = "inherit" },
                ["dueDate"] = null,
                ["participants"] = new JsonArray(Local("new-participant")),
            },
            new JsonObject
            {
                ["operation"] = "completeTask",
                ["operationId"] = "5",
                ["task"] = Local("new-task"),
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        var result = Apply(session, approval, new Host());

        Assert.Equal(ProposalApplyStatus.Committed, result.Status);
        Assert.Equal(beforeRevision + 1, Revision(session));
        var current = session.Work.Read();
        var category = Assert.Single(current.Categories, item => item.Name == "Agent category");
        var participant = Assert.Single(current.Participants, item => item.Label == "Agent person");
        var project = Assert.Single(current.Projects, item => item.Title == "Agent project");
        var task = Assert.Single(current.Tasks, item => item.Title == "Agent task");
        Assert.Equal(category.Id, project.CategoryId);
        Assert.Equal(project.Id, task.ProjectId);
        Assert.Equal([participant.Id], task.Participants);
        Assert.True(task.IsComplete);
        Assert.Equal(new DateOnly(2030, 4, 5), task.CompletionDate);
        Assert.DoesNotContain("new-", category.Id, StringComparison.Ordinal);
        Assert.Null(session.ProposalApplication.Approve(plan));
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);
        Assert.Equal(beforeRevision + 1, Revision(session));

        var recoveryPath = Assert.Single(Directory.GetFiles(
            recoveryDirectory,
            "dot-orbit-pre-import-*.dotorbit-recovery"));
        Assert.DoesNotContain(':', Path.GetFileName(recoveryPath));
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(recoveryPath), 0, 16));
        using var recovered = Assert.IsAssignableFrom<IWorkspaceSession>(store.Open(recoveryPath, passphrase).Session);
        Assert.Empty(recovered.Work.Read().Projects);
        Assert.Empty(recovered.Work.Read().Tasks);
        Assert.DoesNotContain(recovered.Work.Read().Categories, item => item.Name == "Agent category");
        var wrong = WorkspacePassphrase.Create("another private password", "another private password").Passphrase!;
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, store.Open(recoveryPath, wrong).Status);
    }

    [Fact]
    public void BlockedStaleAndCancelledAttemptsConsumeApprovalWithoutProposalMutation()
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Keep incomplete", "", category.Id, null);

        var blockedPlan = Review(session, TimeProvider.System, Complete(task.Id, "blocked"));
        var blockedApproval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(blockedPlan));
        var blockedHost = new Host(flushResult: false);
        Assert.Equal(ProposalApplyStatus.PendingInspectorWork,
            Apply(session, blockedApproval, blockedHost).Status);
        Assert.Equal(1, blockedHost.FlushCount);
        Assert.Equal(0, blockedHost.RefreshCount);
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, blockedApproval, new Host()).Status);

        var stalePlan = Review(session, TimeProvider.System, Complete(task.Id, "stale"));
        var staleApproval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(stalePlan));
        var hidden = session.Work.CreateStandaloneTask("Hidden change", "", category.Id, null);
        session.Work.MoveTaskToBin(hidden.Id);
        var staleRevision = Revision(session);
        Assert.Equal(ProposalApplyStatus.StaleSource,
            Apply(session, staleApproval, new Host()).Status);
        Assert.Equal(staleRevision, Revision(session));
        Assert.False(session.Work.Read().Tasks.Single(item => item.Id == task.Id).IsComplete);

        var cancelledPlan = Review(session, TimeProvider.System, Complete(task.Id, "cancelled"));
        var cancelledApproval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(cancelledPlan));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledHost = new Host();
        Assert.Equal(ProposalApplyStatus.Cancelled,
            session.ProposalApplication.Apply(
                cancelledApproval,
                cancelledHost,
                cancellation.Token).Status);
        Assert.Equal(0, cancelledHost.FlushCount);
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, cancelledApproval, new Host()).Status);
    }

    [Fact]
    public async Task ConcurrentAndDisconnectedUseCannotReplayAnApproval()
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var beforeRevision = Revision(session);
        var concurrentPlan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new",
                ["name"] = "Created once",
            });
        var concurrentApproval = Assert.IsType<ProposalApproval>(
            session.ProposalApplication.Approve(concurrentPlan));

        var attempts = await Task.WhenAll(
            Task.Run(() => Apply(session, concurrentApproval, new Host()),
                TestContext.Current.CancellationToken),
            Task.Run(() => Apply(session, concurrentApproval, new Host()),
                TestContext.Current.CancellationToken));

        Assert.Contains(attempts, result => result.Status == ProposalApplyStatus.Committed);
        Assert.Contains(attempts, result => result.Status == ProposalApplyStatus.ApprovalUnavailable);
        Assert.Equal(beforeRevision + 1, Revision(session));
        Assert.Single(session.Work.Read().Categories, item => item.Name == "Created once");

        var disconnectedPlan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "2",
                ["localId"] = "other",
                ["name"] = "Must not be created",
            });
        using var otherSession = Assert.IsAssignableFrom<IWorkspaceSession>(
            store.Open(WorkspacePath, passphrase).Session);
        Assert.Null(otherSession.ProposalApplication.Approve(disconnectedPlan));
        var disconnectedApproval = Assert.IsType<ProposalApproval>(
            session.ProposalApplication.Approve(disconnectedPlan));

        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(otherSession, disconnectedApproval, new Host()).Status);
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, disconnectedApproval, new Host()).Status);
        Assert.DoesNotContain(session.Work.Read().Categories,
            item => item.Name == "Must not be created");
    }

    [Fact]
    public void SessionCloseInvalidatesApprovalWithoutExposingSensitiveState()
    {
        var store = new EncryptedWorkspaceStore();
        var session = CreateWorkspace(store);
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "private-operation",
                ["localId"] = "private-local-id",
                ["name"] = "Private value",
            });
        var application = session.ProposalApplication;
        var approval = Assert.IsType<ProposalApproval>(application.Approve(plan));

        session.Dispose();

        Assert.Equal("Proposal approval", approval.ToString());
        Assert.Equal("Proposal execution plan", plan.ToString());
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            application.Apply(approval, new Host(), TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void CallerBuiltSemanticPlanCannotBeApproved()
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var source = session.Proposals.Read();
        var snapshot = PlanningSnapshotJsonGenerator.Generate(session.Planning.Read(), 1);
        using var document = JsonDocument.Parse(snapshot.Payload!.Value);
        var identity = document.RootElement.GetProperty("snapshotId").GetString()!;
        var result = new ProposalSemanticPlanner(TimeProvider.System, new AcceptMarkdown()).Validate(
            new ChangeProposal(
                ProposalKind.WorkspaceChange,
                AgentInterchangeV1.ChangeProposalFormat,
                1,
                identity,
                source.Revision,
                [new CreateCategoryOperation("1", "local", "Must not be authorized")]),
            source,
            identity,
            "caller-supplied-digest");

        var plan = Assert.IsType<ProposalExecutionPlan>(result.Plan);
        Assert.Null(session.ProposalApplication.Approve(plan));
        Assert.DoesNotContain(session.Work.Read().Categories,
            item => item.Name == "Must not be authorized");
    }

    [Fact]
    public void RecoveryFailureLeavesWorkspaceAndRevisionUnchanged()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var fileOperations = new FailingProposalRecoveryFileOperations();
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            fileOperations);
        using var session = CreateWorkspace(store);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var recoveryStatePath = WorkspacePath + ".recovery-state.json";
        var recoveryStateBefore = File.ReadAllText(recoveryStatePath);
        var before = session.Proposals.Read();
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new",
                ["name"] = "Must not be created",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        fileOperations.FailProposalRecoveryPublish = true;

        var result = Apply(session, approval, new Host());

        Assert.Equal(ProposalApplyStatus.RecoveryPointCreationFailed, result.Status);
        var after = session.Proposals.Read();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Workspace.Categories.ToArray(), after.Workspace.Categories.ToArray());
        Assert.Equal(before.Workspace.Projects.ToArray(), after.Workspace.Projects.ToArray());
        Assert.Equal(before.Workspace.Tasks.ToArray(), after.Workspace.Tasks.ToArray());
        Assert.Equal(before.Workspace.Participants.ToArray(), after.Workspace.Participants.ToArray());
        Assert.Equal(before.TaskBin.ToArray(), after.TaskBin.ToArray());
        Assert.Equal(before.ProjectBin.ToArray(), after.ProjectBin.ToArray());
        Assert.Equal(recoveryStateBefore, File.ReadAllText(recoveryStatePath));
        Assert.Empty(Directory.GetFiles(recoveryDirectory, "dot-orbit-pre-import-*.dotorbit-recovery"));
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);
    }

    [Fact]
    public void OperationConnectionFailureReturnsFixedOutcomeAndConsumesApproval()
    {
        var failConnection = false;
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            connectionOpener: (path, key, mode) => failConnection
                ? throw new IOException("Synthetic path-bearing connection failure: " + path)
                : EncryptedWorkspaceStore.OpenConnection(path, key, mode));
        using var session = CreateWorkspace(store);
        var beforeRevision = Revision(session);
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new",
                ["name"] = "Must not be created",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        failConnection = true;

        var result = Apply(session, approval, new Host());

        Assert.Equal(ProposalApplyStatus.Failed, result.Status);
        Assert.Equal("Proposal apply: Failed.", result.ToString());
        Assert.DoesNotContain(WorkspacePath, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(beforeRevision, Revision(session));
        Assert.DoesNotContain(session.Work.Read().Categories,
            item => item.Name == "Must not be created");
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);
    }

    [Theory]
    [InlineData((int)ProposalApplyCheckpoint.OperationCompleted, 0)]
    [InlineData((int)ProposalApplyCheckpoint.OperationCompleted, 1)]
    [InlineData((int)ProposalApplyCheckpoint.OperationCompleted, 2)]
    [InlineData((int)ProposalApplyCheckpoint.BeforeCommit, -1)]
    public void FailureBeforeCommitRollsBackDataIndexesAndRevisionButConsumesApproval(
        int checkpointValue,
        int operationIndex)
    {
        var injectedCheckpoint = (ProposalApplyCheckpoint)checkpointValue;
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            proposalApplyCheckpoint: (checkpoint, _, index) =>
            {
                if (checkpoint == injectedCheckpoint
                    && (operationIndex < 0 || index == operationIndex))
                {
                    throw new InvalidOperationException("Synthetic fixed failure.");
                }
            });
        using var session = CreateWorkspace(store);
        var home = Assert.Single(session.Work.Read().Categories);
        var indexedTask = session.Work.CreateStandaloneTask(
            "Rollback indexed task",
            "",
            home.Id,
            null);
        session.Work.CompleteTask(indexedTask.Id);
        var before = session.Proposals.Read();
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "archiveTask",
                ["operationId"] = "1",
                ["task"] = Existing(indexedTask.Id),
            },
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "2",
                ["localId"] = "first",
                ["name"] = "Must roll back",
            },
            new JsonObject
            {
                ["operation"] = "createParticipant",
                ["operationId"] = "3",
                ["localId"] = "second",
                ["label"] = "Must roll back",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        var result = Apply(session, approval, new Host());

        Assert.Equal(ProposalApplyStatus.Failed, result.Status);
        var after = session.Proposals.Read();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Workspace.Categories.ToArray(), after.Workspace.Categories.ToArray());
        Assert.Equal(before.Workspace.Projects.ToArray(), after.Workspace.Projects.ToArray());
        Assert.Equal(before.Workspace.Tasks.ToArray(), after.Workspace.Tasks.ToArray());
        Assert.Equal(before.Workspace.Participants.ToArray(), after.Workspace.Participants.ToArray());
        Assert.Equal(before.TaskBin.ToArray(), after.TaskBin.ToArray());
        Assert.Equal(before.ProjectBin.ToArray(), after.ProjectBin.ToArray());
        Assert.False(session.Work.Read().Tasks.Single(item => item.Id == indexedTask.Id).IsArchived);
        Assert.Empty(session.Work.SearchArchive("Rollback indexed task"));
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);
        using var competing = store.Open(WorkspacePath, passphrase).Session;
        Assert.NotNull(competing);
    }

    [Fact]
    public void CancellationAfterMutationRollsBackTransactionAndConsumesApproval()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            proposalApplyCheckpoint: (checkpoint, _, index) =>
            {
                if (checkpoint == ProposalApplyCheckpoint.OperationCompleted && index == 0)
                {
                    cancellation.Cancel();
                }
            });
        using var session = CreateWorkspace(store);
        var home = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Cancellation rollback", "", home.Id, null);
        session.Work.CompleteTask(task.Id);
        var beforeRevision = Revision(session);
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "archiveTask",
                ["operationId"] = "1",
                ["task"] = Existing(task.Id),
            },
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "2",
                ["localId"] = "new",
                ["name"] = "Must not be created",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        var result = session.ProposalApplication.Apply(approval, new Host(), cancellation.Token);

        Assert.Equal(ProposalApplyStatus.Cancelled, result.Status);
        Assert.Equal(beforeRevision, Revision(session));
        Assert.False(session.Work.Read().Tasks.Single(item => item.Id == task.Id).IsArchived);
        Assert.Empty(session.Work.SearchArchive("Cancellation rollback"));
        Assert.DoesNotContain(session.Work.Read().Categories,
            item => item.Name == "Must not be created");
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);
    }

    [Theory]
    [InlineData((int)ProposalApplyCheckpoint.FirstSourceValidated)]
    [InlineData((int)ProposalApplyCheckpoint.ExclusiveLockRetainedForRecovery)]
    [InlineData((int)ProposalApplyCheckpoint.RecoveryPointValidated)]
    [InlineData((int)ProposalApplyCheckpoint.SecondSourceValidated)]
    [InlineData((int)ProposalApplyCheckpoint.BeforeCommit)]
    public void ExclusiveDatabaseLockRejectsASecondConnectionAtEveryApplyPhase(
        int checkpointValue)
    {
        var observedCheckpoint = (ProposalApplyCheckpoint)checkpointValue;
        SqliteConnection? competing = null;
        var checkpointObserved = false;
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            proposalApplyCheckpoint: (checkpoint, _, _) =>
            {
                if (checkpoint != observedCheckpoint)
                {
                    return;
                }

                checkpointObserved = true;
                using var command = competing!.CreateCommand();
                command.CommandTimeout = 1;
                command.CommandText = "UPDATE categories SET name='Concurrent' WHERE position=0;";
                var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
                Assert.Equal(5, error.SqliteErrorCode);
            });
        using var session = CreateWorkspace(store);
        using (competing = EncryptedWorkspaceStore.OpenConnection(
                   WorkspacePath,
                   passphrase,
                   SqliteOpenMode.ReadWrite))
        {
            EncryptedWorkspaceStore.ConfigureConnection(competing);
            using var timeout = competing.CreateCommand();
            timeout.CommandText = "PRAGMA busy_timeout=1;";
            timeout.ExecuteNonQuery();
            var plan = Review(session, TimeProvider.System,
                new JsonObject
                {
                    ["operation"] = "createCategory",
                    ["operationId"] = "1",
                    ["localId"] = "new",
                    ["name"] = "Committed",
                });
            var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

            Assert.Equal(ProposalApplyStatus.Committed,
                Apply(session, approval, new Host()).Status);
        }

        Assert.True(checkpointObserved);
    }

    [Fact]
    public void SecondSourceCheckRejectsInterveningIdentityChangeAfterRecovery()
    {
        var changed = false;
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            proposalApplyCheckpoint: (checkpoint, connection, _) =>
            {
                if (checkpoint != ProposalApplyCheckpoint.RecoveryPointValidated)
                {
                    return;
                }

                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE categories SET name='Intervening';";
                command.ExecuteNonQuery();
                changed = true;
            });
        using var session = CreateWorkspace(store);
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new",
                ["name"] = "Must not apply",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        var result = Apply(session, approval, new Host());

        Assert.True(changed);
        Assert.Equal(ProposalApplyStatus.StaleSource, result.Status);
        Assert.Equal("Intervening", Assert.Single(session.Work.Read().Categories).Name);
        Assert.DoesNotContain(session.Work.Read().Categories, item => item.Name == "Must not apply");
    }

    [Fact]
    public void RefreshFailureReturnsCommittedMaintenanceAndRetryNeverReplaysOperations()
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var beforeRevision = Revision(session);
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new",
                ["name"] = "Created once",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        var host = new Host(refreshError: new IOException("Synthetic refresh failure."));

        var result = Apply(session, approval, host);

        Assert.Equal(ProposalApplyStatus.CommittedMaintenanceRequired, result.Status);
        Assert.Equal(beforeRevision + 1, Revision(session));
        Assert.Single(session.Work.Read().Categories, item => item.Name == "Created once");
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);
        host.RefreshError = null;
        var maintenance = Assert.IsAssignableFrom<IProposalMaintenance>(result.Maintenance);
        Assert.Equal(ProposalMaintenanceStatus.Completed,
            maintenance.Retry(host, TestContext.Current.CancellationToken).Status);
        Assert.Equal(beforeRevision + 1, Revision(session));
        Assert.Single(session.Work.Read().Categories, item => item.Name == "Created once");
        Assert.Equal(2, host.RefreshCount);
    }

    [Fact]
    public void AutomaticRecoveryFailureReturnsCommittedMaintenanceAndRetryNeverReplaysOperations()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var fileOperations = new FailingProposalRecoveryFileOperations();
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            fileOperations);
        using var session = CreateWorkspace(store);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var beforeRevision = Revision(session);
        var plan = Review(session, TimeProvider.System,
            new JsonObject
            {
                ["operation"] = "createCategory",
                ["operationId"] = "1",
                ["localId"] = "new",
                ["name"] = "Committed before maintenance",
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        fileOperations.FailAutomaticRecoveryMaintenance = true;

        var result = Apply(session, approval, new Host());

        Assert.Equal(ProposalApplyStatus.CommittedMaintenanceRequired, result.Status);
        Assert.Equal(beforeRevision + 1, Revision(session));
        Assert.Single(session.Work.Read().Categories,
            item => item.Name == "Committed before maintenance");
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable,
            Apply(session, approval, new Host()).Status);

        fileOperations.FailAutomaticRecoveryMaintenance = false;
        var maintenance = Assert.IsAssignableFrom<IProposalMaintenance>(result.Maintenance);
        Assert.Equal(ProposalMaintenanceStatus.Completed,
            maintenance.Retry(new Host(), TestContext.Current.CancellationToken).Status);
        Assert.Equal(beforeRevision + 1, Revision(session));
        Assert.Single(session.Work.Read().Categories,
            item => item.Name == "Committed before maintenance");
        using var state = JsonDocument.Parse(
            File.ReadAllText(WorkspacePath + ".recovery-state.json"));
        Assert.Equal(
            JsonValueKind.Null,
            state.RootElement.GetProperty("PendingChangeGeneration").ValueKind);
        Assert.Single(Directory.GetFiles(
            recoveryDirectory,
            "dot-orbit-auto-*.dotorbit-recovery"));
    }

    [Fact]
    public void InboundProposalIsConsumedInTheSameCommitBeforePostCommitFailure()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            proposalApplyCheckpoint: (checkpoint, _, _) =>
            {
                if (checkpoint == ProposalApplyCheckpoint.AfterCommit)
                {
                    throw new InvalidOperationException("Synthetic process-loss boundary after commit.");
                }
            });
        const string receipt = "receipt-atomic-apply";
        var identity = new string('d', 64);
        byte[] proposalBytes;
        using (var session = CreateWorkspace(store))
        {
            Assert.Equal(
                RecoveryDirectoryConfigurationStatus.Configured,
                session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
            var proposal = new JsonObject
            {
                ["format"] = AgentInterchangeV1.NewWorkProposalFormat,
                ["schemaVersion"] = 1,
                ["operations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["operation"] = "createCategory",
                        ["operationId"] = "1",
                        ["localId"] = "new",
                        ["name"] = "Applied exactly once",
                    },
                },
            };
            proposalBytes = JsonSerializer.SerializeToUtf8Bytes(proposal);
            var reviewed = new ProposalReviewBoundary(session.Proposals, TimeProvider.System)
                .Review(proposalBytes);
            Assert.Empty(reviewed.Failures);
            var plan = Assert.IsType<ProposalExecutionPlan>(reviewed.Plan);
            var pending = new PendingAgentProposal(
                receipt,
                "key-atomic-apply",
                "authorization-1",
                identity,
                "Unverified client",
                new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero),
                plan.Kind,
                plan.Operations.Count,
                plan.Preview.Count,
                proposalBytes);
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(pending).Status);
            var other = new PendingAgentProposal(
                "receipt-other",
                "key-other",
                "authorization-1",
                identity,
                "Unverified client",
                new DateTimeOffset(2026, 10, 21, 12, 0, 30, TimeSpan.Zero),
                ProposalKind.NewWork,
                1,
                1,
                Encoding.UTF8.GetBytes("other pending proposal"));
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(other).Status);
            var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
            var application = Assert.IsAssignableFrom<IInboundProposalApplication>(
                session.ProposalApplication);

            var result = application.ApplyInbound(
                approval,
                receipt,
                new Host(),
                TestContext.Current.CancellationToken);

            Assert.Equal(ProposalApplyStatus.CommittedMaintenanceRequired, result.Status);
            Assert.Null(session.AgentIntegration.FindPendingProposal(receipt));
            Assert.Equal("receipt-other", Assert.Single(session.AgentIntegration.ReadPendingProposals()).Receipt);
            Assert.Single(session.Work.Read().Categories, item => item.Name == "Applied exactly once");
        }

        using var reopened = Assert.IsAssignableFrom<IWorkspaceSession>(
            store.Open(WorkspacePath, passphrase).Session);
        Assert.Null(reopened.AgentIntegration.FindPendingProposal(receipt));
        Assert.Equal("receipt-other", Assert.Single(reopened.AgentIntegration.ReadPendingProposals()).Receipt);
        Assert.Single(reopened.Work.Read().Categories, item => item.Name == "Applied exactly once");
        var replay = reopened.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
            "unused-receipt",
            "key-atomic-apply",
            "authorization-2",
            identity,
            "Unverified client",
            new DateTimeOffset(2026, 10, 21, 12, 1, 0, TimeSpan.Zero),
            ProposalKind.NewWork,
            1,
            1,
            proposalBytes));
        Assert.Equal(PendingAgentProposalStoreStatus.Duplicate, replay.Status);
        Assert.Equal(receipt, replay.Receipt);
        Assert.Null(reopened.AgentIntegration.FindPendingProposal(receipt));
        Assert.Equal("receipt-other", Assert.Single(reopened.AgentIntegration.ReadPendingProposals()).Receipt);
    }

    [Fact]
    public void InboundProposalAndItsSelectedReceiptBothRemainWhenTheApplyTransactionRollsBack()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            TimeProvider.System,
            proposalApplyCheckpoint: (checkpoint, _, _) =>
            {
                if (checkpoint == ProposalApplyCheckpoint.BeforeCommit)
                {
                    throw new IOException("Injected failure before commit.");
                }
            });
        using var session = CreateWorkspace(store);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var proposal = new JsonObject
        {
            ["format"] = AgentInterchangeV1.NewWorkProposalFormat,
            ["schemaVersion"] = 1,
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["operation"] = "createCategory",
                    ["operationId"] = "1",
                    ["localId"] = "new",
                    ["name"] = "Must roll back",
                },
            },
        };
        var proposalBytes = JsonSerializer.SerializeToUtf8Bytes(proposal);
        var reviewed = new ProposalReviewBoundary(session.Proposals, TimeProvider.System)
            .Review(proposalBytes);
        var plan = Assert.IsType<ProposalExecutionPlan>(reviewed.Plan);
        Assert.Empty(reviewed.Failures);
        var identity = new string('f', 64);
        var selected = new PendingAgentProposal(
            "receipt-selected",
            "key-selected",
            "authorization-1",
            identity,
            "Unverified client",
            new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero),
            plan.Kind,
            plan.Operations.Count,
            plan.Preview.Count,
            proposalBytes);
        var other = new PendingAgentProposal(
            "receipt-other",
            "key-other",
            "authorization-1",
            identity,
            "Unverified client",
            new DateTimeOffset(2026, 10, 21, 12, 1, 0, TimeSpan.Zero),
            ProposalKind.NewWork,
            1,
            1,
            Encoding.UTF8.GetBytes("other pending proposal"));
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(selected).Status);
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(other).Status);
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        var application = Assert.IsAssignableFrom<IInboundProposalApplication>(session.ProposalApplication);

        var result = application.ApplyInbound(
            approval,
            selected.Receipt,
            new Host(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProposalApplyStatus.Failed, result.Status);
        Assert.Equal(
            [selected.Receipt, other.Receipt],
            session.AgentIntegration.ReadPendingProposals().Select(item => item.Receipt));
        Assert.DoesNotContain(session.Work.Read().Categories, item => item.Name == "Must roll back");
    }

    [Fact]
    public void RemainingOperationFamiliesCommitTogetherAndPreserveArchivedOrderSlots()
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var home = Assert.Single(session.Work.Read().Categories);
        var other = session.Work.CreateCategory("Other");
        var participant = session.Work.CreateParticipant("Alex");
        var editProject = session.Work.CreateProject("Edit project", "", home.Id, null);
        var categoryProject = session.Work.CreateProject("Category project", "", home.Id, null);
        var attachmentProject = session.Work.CreateProject("Attachment project", "", home.Id, null);
        var destinationProject = session.Work.CreateProject("Destination project", "", other.Id, null);
        var archiveProject = session.Work.CreateProject("Archive project", "", home.Id, null);
        var binProject = session.Work.CreateProject("Bin project", "", home.Id, null);
        session.Work.CreateTask(binProject.Id, "Binned project child");
        var editTask = session.Work.CreateStandaloneTask("Edit task", "", home.Id, null);
        var categoryTask = session.Work.CreateStandaloneTask("Category task", "", home.Id, null);
        var participantTask = session.Work.CreateStandaloneTask("Participant task", "", home.Id, null);
        var attachTask = session.Work.CreateStandaloneTask("Attach task", "", other.Id, null);
        var detachTask = session.Work.CreateTask(attachmentProject.Id, "Detach task");
        var moveTask = session.Work.CreateTask(attachmentProject.Id, "Move task");
        var completeTask = session.Work.CreateStandaloneTask("Complete task", "", home.Id, null);
        var reopenTask = session.Work.CreateStandaloneTask("Reopen task", "", home.Id, null);
        session.Work.CompleteTask(reopenTask.Id);
        var orderA = session.Work.CreateTask(destinationProject.Id, "Order A");
        var archivedSlot = session.Work.CreateTask(destinationProject.Id, "Archived slot");
        session.Work.CompleteTask(archivedSlot.Id);
        session.Work.ArchiveTask(archivedSlot.Id);
        var orderB = session.Work.CreateTask(destinationProject.Id, "Order B");
        var archiveTask = session.Work.CreateStandaloneTask("Archive task", "", home.Id, null);
        session.Work.CompleteTask(archiveTask.Id);
        var binTask = session.Work.CreateStandaloneTask("Bin task", "", home.Id, null);
        var beforeRevision = Revision(session);
        var plan = Review(session, TimeProvider.System,
            new JsonObject { ["operation"] = "editProject", ["operationId"] = "1", ["project"] = Existing(editProject.Id), ["changes"] = new JsonObject { ["description"] = "Edited" } },
            new JsonObject { ["operation"] = "setProjectCategory", ["operationId"] = "2", ["project"] = Existing(categoryProject.Id), ["category"] = Existing(other.Id) },
            new JsonObject { ["operation"] = "editTask", ["operationId"] = "3", ["task"] = Existing(editTask.Id), ["changes"] = new JsonObject { ["title"] = "Edited task" } },
            new JsonObject { ["operation"] = "setTaskCategory", ["operationId"] = "4", ["task"] = Existing(categoryTask.Id), ["category"] = new JsonObject { ["mode"] = "explicit", ["category"] = Existing(other.Id) } },
            new JsonObject { ["operation"] = "replaceTaskParticipants", ["operationId"] = "5", ["task"] = Existing(participantTask.Id), ["participants"] = new JsonArray(Existing(participant.Id)) },
            new JsonObject { ["operation"] = "attachTask", ["operationId"] = "6", ["task"] = Existing(attachTask.Id), ["project"] = Existing(attachmentProject.Id), ["categoryChoice"] = "preserveAsOverride" },
            new JsonObject { ["operation"] = "detachTask", ["operationId"] = "7", ["task"] = Existing(detachTask.Id) },
            new JsonObject { ["operation"] = "moveTask", ["operationId"] = "8", ["task"] = Existing(moveTask.Id), ["project"] = Existing(destinationProject.Id), ["categoryChoice"] = "adoptProjectCategory" },
            Complete(completeTask.Id, "9"),
            new JsonObject { ["operation"] = "reopenTask", ["operationId"] = "10", ["task"] = Existing(reopenTask.Id) },
            new JsonObject { ["operation"] = "reorderProjectTask", ["operationId"] = "11", ["task"] = Existing(orderB.Id), ["placement"] = new JsonObject { ["before"] = Existing(orderA.Id) } },
            new JsonObject { ["operation"] = "archiveProject", ["operationId"] = "12", ["project"] = Existing(archiveProject.Id) },
            new JsonObject { ["operation"] = "archiveTask", ["operationId"] = "13", ["task"] = Existing(archiveTask.Id) },
            new JsonObject { ["operation"] = "moveProjectToBin", ["operationId"] = "14", ["project"] = Existing(binProject.Id) },
            new JsonObject { ["operation"] = "moveTaskToBin", ["operationId"] = "15", ["task"] = Existing(binTask.Id) });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        Assert.Equal(ProposalApplyStatus.Committed,
            Apply(session, approval, new Host()).Status);

        Assert.Equal(beforeRevision + 1, Revision(session));
        var workspace = session.Work.Read();
        Assert.Equal("Edited", workspace.Projects.Single(item => item.Id == editProject.Id).Description);
        Assert.Equal(other.Id, workspace.Projects.Single(item => item.Id == categoryProject.Id).CategoryId);
        Assert.Equal("Edited task", workspace.Tasks.Single(item => item.Id == editTask.Id).Title);
        Assert.Equal(other.Id, workspace.Tasks.Single(item => item.Id == categoryTask.Id).ExplicitCategoryId);
        Assert.Equal([participant.Id], workspace.Tasks.Single(item => item.Id == participantTask.Id).Participants);
        Assert.Equal(attachmentProject.Id, workspace.Tasks.Single(item => item.Id == attachTask.Id).ProjectId);
        Assert.Equal(other.Id, workspace.Tasks.Single(item => item.Id == attachTask.Id).ExplicitCategoryId);
        Assert.Null(workspace.Tasks.Single(item => item.Id == detachTask.Id).ProjectId);
        Assert.Equal(destinationProject.Id, workspace.Tasks.Single(item => item.Id == moveTask.Id).ProjectId);
        Assert.Null(workspace.Tasks.Single(item => item.Id == moveTask.Id).ExplicitCategoryId);
        Assert.True(workspace.Tasks.Single(item => item.Id == completeTask.Id).IsComplete);
        Assert.False(workspace.Tasks.Single(item => item.Id == reopenTask.Id).IsComplete);
        Assert.True(workspace.Projects.Single(item => item.Id == archiveProject.Id).IsArchived);
        Assert.True(workspace.Tasks.Single(item => item.Id == archiveTask.Id).IsArchived);
        Assert.Equal(
            [orderB.Id, archivedSlot.Id, orderA.Id],
            workspace.Tasks
                .Where(item => item.ProjectId == destinationProject.Id
                    && (item.Id == orderA.Id || item.Id == orderB.Id || item.Id == archivedSlot.Id))
                .OrderBy(item => item.ProjectPosition)
                .Select(item => item.Id));
        Assert.Contains(session.Work.ReadProjectBin(), item => item.Project.Id == binProject.Id);
        Assert.Contains(session.Work.ReadTaskBin(), item => item.Task.Id == binTask.Id);
    }

    [Theory]
    [InlineData(null, "planned", TodayLane.Planned)]
    [InlineData(null, "inProgress", TodayLane.InProgress)]
    [InlineData(TodayLane.Planned, "inProgress", TodayLane.InProgress)]
    [InlineData(TodayLane.InProgress, "planned", TodayLane.Planned)]
    [InlineData(TodayLane.Planned, "none", null)]
    [InlineData(TodayLane.InProgress, "none", null)]
    public void EveryVersionTwoTodayTransitionPreservesTaskSharedPosition(TodayLane? before, string state, TodayLane? expected)
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Transition", "", category.Id, null);
        session.Work.CreateStandaloneTask("Neighbour", "", category.Id, null);
        if (before is not null) session.Work.SetTaskTodayLane(task.Id, before);
        var original = session.Work.Read().Tasks.Select(item => (item.Id, item.SharedPosition)).ToArray();
        var revision = Revision(session);
        var plan = ReviewVersion(session, 2, SetToday("1", task.Id, state));
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        Assert.Equal(ProposalApplyStatus.Committed, Apply(session, approval, new Host()).Status);

        Assert.Equal(expected, session.Work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        Assert.Equal(original, session.Work.Read().Tasks.Select(item => (item.Id, item.SharedPosition)));
        Assert.Equal(revision + 1, Revision(session));
    }

    [Fact]
    public void VersionTwoTodayMutationsCommitOneRevisionPreserveOrderAndRecoverPriorMembership()
    {
        var recoveryDirectory = Path.Combine(directory, "recovery");
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var category = Assert.Single(session.Work.Read().Categories);
        var add = session.Work.CreateStandaloneTask("Add", "", category.Id, null);
        var move = session.Work.CreateStandaloneTask("Move", "", category.Id, null);
        var remove = session.Work.CreateStandaloneTask("Remove", "", category.Id, null);
        var reopen = session.Work.CreateStandaloneTask("Reopen", "", category.Id, null);
        session.Work.SetTaskTodayLane(move.Id, TodayLane.Planned);
        session.Work.SetTaskTodayLane(remove.Id, TodayLane.InProgress);
        session.Work.CompleteTask(reopen.Id);
        var before = session.Proposals.Read();
        var plan = ReviewVersion(session, 2,
            SetToday("1", add.Id, "planned"),
            SetToday("2", move.Id, "inProgress"),
            SetToday("3", remove.Id, "none"),
            new JsonObject { ["operation"] = "reopenTask", ["operationId"] = "4", ["task"] = Existing(reopen.Id) },
            SetToday("5", reopen.Id, "inProgress"));
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        Assert.Equal(ProposalApplyStatus.Committed, Apply(session, approval, new Host()).Status);

        var after = session.Proposals.Read();
        Assert.Equal(before.Revision.Value + 1, after.Revision.Value);
        Assert.Equal(before.Workspace.Tasks.Select(item => (item.Id, item.SharedPosition)),
            after.Workspace.Tasks.Select(item => (item.Id, item.SharedPosition)));
        Assert.Equal(TodayLane.Planned, after.Workspace.Tasks.Single(item => item.Id == add.Id).TodayLane);
        Assert.Equal(TodayLane.InProgress, after.Workspace.Tasks.Single(item => item.Id == move.Id).TodayLane);
        Assert.Null(after.Workspace.Tasks.Single(item => item.Id == remove.Id).TodayLane);
        var reopened = after.Workspace.Tasks.Single(item => item.Id == reopen.Id);
        Assert.False(reopened.IsComplete);
        Assert.Null(reopened.CompletionDate);
        Assert.Equal(TodayLane.InProgress, reopened.TodayLane);
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable, Apply(session, approval, new Host()).Status);
        Assert.Null(session.ProposalApplication.Approve(plan));
        Assert.Equal(after.Revision, session.Proposals.Read().Revision);
        var recoveryPath = Assert.Single(Directory.GetFiles(recoveryDirectory, "dot-orbit-pre-import-*.dotorbit-recovery"));
        using var recovered = Assert.IsAssignableFrom<IWorkspaceSession>(store.Open(recoveryPath, passphrase).Session);
        var recoveryTasks = recovered.Work.Read().Tasks;
        Assert.Null(recoveryTasks.Single(item => item.Id == add.Id).TodayLane);
        Assert.Equal(TodayLane.Planned, recoveryTasks.Single(item => item.Id == move.Id).TodayLane);
        Assert.Equal(TodayLane.InProgress, recoveryTasks.Single(item => item.Id == remove.Id).TodayLane);
        Assert.True(recoveryTasks.Single(item => item.Id == reopen.Id).IsComplete);
    }

    [Theory]
    [InlineData("planned", TodayLane.Planned)]
    [InlineData("inProgress", TodayLane.InProgress)]
    public void VersionTwoNewWorkCreatesTodayWithFreshIdentityInOneTransaction(string state, TodayLane lane)
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var before = Revision(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["format"] = AgentInterchangeV1.NewWorkProposalFormat,
            ["schemaVersion"] = 2,
            ["operations"] = new JsonArray(
                new JsonObject { ["operation"] = "createCategory", ["operationId"] = "1", ["localId"] = "category", ["name"] = "New category" },
                new JsonObject
                {
                    ["operation"] = "createTask",
                    ["operationId"] = "2",
                    ["localId"] = "task",
                    ["title"] = "Today creation",
                    ["description"] = "",
                    ["project"] = null,
                    ["category"] = new JsonObject { ["mode"] = "explicit", ["category"] = Local("category") },
                    ["dueDate"] = null,
                    ["participants"] = new JsonArray(),
                    ["todayLane"] = state,
                }),
        });
        var review = new ProposalReviewBoundary(session.Proposals, TimeProvider.System).Review(bytes);
        Assert.Empty(review.Failures);
        var plan = Assert.IsType<ProposalExecutionPlan>(review.Plan);
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        Assert.Equal(ProposalApplyStatus.Committed, Apply(session, approval, new Host()).Status);

        Assert.Equal(before + 1, Revision(session));
        var task = Assert.Single(session.Work.Read().Tasks);
        Assert.NotEqual("task", task.Id);
        Assert.Equal(lane, task.TodayLane);
        Assert.False(task.IsComplete);
        Assert.Equal(Assert.Single(session.Work.Read().Categories, item => item.Name == "New category").Id, task.ExplicitCategoryId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public void VersionTwoTodayFailureRollsBackAllOperationsAndConsumesApproval(int failIndex)
    {
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), TimeProvider.System,
            proposalApplyCheckpoint: (checkpoint, _, index) =>
            {
                if (failIndex == -1 ? checkpoint == ProposalApplyCheckpoint.BeforeCommit
                    : checkpoint == ProposalApplyCheckpoint.OperationCompleted && index == failIndex)
                    throw new IOException("Synthetic failure.");
            });
        using var session = CreateWorkspace(store);
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Keep", "", category.Id, null);
        var completed = session.Work.CreateStandaloneTask("Completed", "", category.Id, null);
        session.Work.CompleteTask(completed.Id);
        var before = session.Proposals.Read();
        var plan = ReviewVersion(session, 2,
            SetToday("1", task.Id, "inProgress"),
            new JsonObject { ["operation"] = "reopenTask", ["operationId"] = "2", ["task"] = Existing(completed.Id) },
            SetToday("3", completed.Id, "planned"),
            new JsonObject
            {
                ["operation"] = "createTask",
                ["operationId"] = "4",
                ["localId"] = "new",
                ["title"] = "Roll back creation",
                ["description"] = "",
                ["project"] = null,
                ["category"] = new JsonObject { ["mode"] = "explicit", ["category"] = Existing(category.Id) },
                ["dueDate"] = null,
                ["participants"] = new JsonArray(),
                ["todayLane"] = "inProgress"
            });
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));

        Assert.Equal(ProposalApplyStatus.Failed, Apply(session, approval, new Host()).Status);

        var after = session.Proposals.Read();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Workspace.Tasks.Select(item => (item.Id, item.SharedPosition, item.TodayLane, item.CompletedAt)),
            after.Workspace.Tasks.Select(item => (item.Id, item.SharedPosition, item.TodayLane, item.CompletedAt)));
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable, Apply(session, approval, new Host()).Status);
    }

    [Fact]
    public void StaleVersionTwoTodayApprovalCannotChangeMembership()
    {
        var store = new EncryptedWorkspaceStore();
        using var session = CreateWorkspace(store);
        var task = session.Work.CreateStandaloneTask("Keep", "", Assert.Single(session.Work.Read().Categories).Id, null);
        var plan = ReviewVersion(session, 2, SetToday("1", task.Id, "planned"));
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        session.Work.CreateParticipant("Unrelated revision");
        var before = Revision(session);

        Assert.Equal(ProposalApplyStatus.StaleSource, Apply(session, approval, new Host()).Status);

        Assert.Null(Assert.Single(session.Work.Read().Tasks).TodayLane);
        Assert.Equal(before, Revision(session));
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable, Apply(session, approval, new Host()).Status);
    }

    [Fact]
    public void VersionTwoTodayRecoveryFailureCannotMutateMembershipOrRevision()
    {
        var files = new FailingProposalRecoveryFileOperations();
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), files);
        using var session = CreateWorkspace(store);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(Path.Combine(directory, "recovery")).Status);
        var task = session.Work.CreateStandaloneTask("Keep", "", Assert.Single(session.Work.Read().Categories).Id, null);
        var plan = ReviewVersion(session, 2, SetToday("1", task.Id, "inProgress"));
        var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
        var before = Revision(session);
        files.FailProposalRecoveryPublish = true;

        Assert.Equal(ProposalApplyStatus.RecoveryPointCreationFailed, Apply(session, approval, new Host()).Status);

        Assert.Null(Assert.Single(session.Work.Read().Tasks).TodayLane);
        Assert.Equal(before, Revision(session));
        Assert.Equal(ProposalApplyStatus.ApprovalUnavailable, Apply(session, approval, new Host()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedVersionTwoTodaySurvivesRestartAndConsumesReceiptOnlyWithAtomicCommit(bool failAfterFirstOperation)
    {
        var injectFailure = false;
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), TimeProvider.System,
            proposalApplyCheckpoint: (checkpoint, _, index) =>
            {
                if (injectFailure && checkpoint == ProposalApplyCheckpoint.OperationCompleted && index == 0)
                    throw new IOException("Synthetic failure after Today mutation.");
            });
        const string receipt = "today-receipt";
        const string key = "today-idempotency-key";
        var clientIdentity = new string('a', 64);
        byte[] bytes;
        string taskId;
        string removedId;
        long initialRevision;
        using (var session = CreateWorkspace(store))
        {
            var category = Assert.Single(session.Work.Read().Categories);
            taskId = session.Work.CreateStandaloneTask("Queued add", "", category.Id, null).Id;
            removedId = session.Work.CreateStandaloneTask("Queued removal", "", category.Id, null).Id;
            session.Work.SetTaskTodayLane(removedId, TodayLane.Planned);
            initialRevision = Revision(session);
            var snapshot = PlanningSnapshotJsonGenerator.Generate(session.Planning.Read(), 2);
            using var document = JsonDocument.Parse(snapshot.Payload!.Value);
            bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["format"] = AgentInterchangeV1.ChangeProposalFormat,
                ["schemaVersion"] = 2,
                ["sourceSnapshotId"] = document.RootElement.GetProperty("snapshotId").GetString(),
                ["sourceWorkspaceRevision"] = initialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["operations"] = new JsonArray(SetToday("1", taskId, "inProgress"), SetToday("2", removedId, "none")),
            });
            var admitted = new ProposalReviewBoundary(session.Proposals, TimeProvider.System).PrepareQueued(bytes);
            Assert.Empty(admitted.Failures);
            var plan = Assert.IsType<ProposalExecutionPlan>(admitted.Plan);
            Assert.Equal(PendingAgentProposalStoreStatus.Stored, session.AgentIntegration.TryStorePendingProposal(new(
                receipt, key, "authorization", clientIdentity, "Synthetic client",
                DateTimeOffset.UnixEpoch, plan.Kind, plan.Operations.Count, plan.Preview.Count, reviewBasis: admitted.ReviewBasis.Span, strictUtf8: bytes)).Status);
            Assert.Null(session.Work.Read().Tasks.Single(item => item.Id == taskId).TodayLane);
            Assert.Equal(initialRevision, Revision(session));
        }

        using (var session = Assert.IsAssignableFrom<IWorkspaceSession>(store.Open(WorkspacePath, passphrase).Session))
        {
            var pending = Assert.Single(session.AgentIntegration.ReadPendingProposals());
            Assert.True(bytes.AsSpan().SequenceEqual(pending.StrictUtf8.Span));
            var reviewed = new ProposalReviewBoundary(session.Proposals, TimeProvider.System).ReviewQueued(pending.StrictUtf8, pending.ReviewBasis);
            Assert.Empty(reviewed.Failures);
            var plan = Assert.IsType<ProposalExecutionPlan>(reviewed.Plan);
            var approval = Assert.IsType<ProposalApproval>(session.ProposalApplication.Approve(plan));
            injectFailure = failAfterFirstOperation;
            var result = Assert.IsAssignableFrom<IInboundProposalApplication>(session.ProposalApplication)
                .ApplyInbound(approval, receipt, new Host(), TestContext.Current.CancellationToken);
            Assert.Equal(failAfterFirstOperation ? ProposalApplyStatus.Failed : ProposalApplyStatus.Committed, result.Status);
            Assert.Equal(ProposalApplyStatus.ApprovalUnavailable, Apply(session, approval, new Host()).Status);
        }

        using var finalSession = Assert.IsAssignableFrom<IWorkspaceSession>(store.Open(WorkspacePath, passphrase).Session);
        Assert.Equal(initialRevision + (failAfterFirstOperation ? 0 : 1), Revision(finalSession));
        Assert.Equal(failAfterFirstOperation ? null : TodayLane.InProgress,
            finalSession.Work.Read().Tasks.Single(item => item.Id == taskId).TodayLane);
        Assert.Equal(failAfterFirstOperation ? TodayLane.Planned : null,
            finalSession.Work.Read().Tasks.Single(item => item.Id == removedId).TodayLane);
        if (failAfterFirstOperation) Assert.Equal(receipt, Assert.Single(finalSession.AgentIntegration.ReadPendingProposals()).Receipt);
        else Assert.Empty(finalSession.AgentIntegration.ReadPendingProposals());
        var replay = finalSession.AgentIntegration.TryStorePendingProposal(new(
            "replayed-receipt", key, "new-authorization", clientIdentity, "Synthetic client", DateTimeOffset.UnixEpoch,
            ProposalKind.WorkspaceChange, 2, 2, bytes));
        Assert.Equal(PendingAgentProposalStoreStatus.Duplicate, replay.Status);
        Assert.Equal(receipt, replay.Receipt);
        Assert.Equal(failAfterFirstOperation ? 1 : 0, finalSession.AgentIntegration.ReadPendingProposals().Count);
    }

    private static JsonObject SetToday(string operationId, string task, string state) => new()
    {
        ["operation"] = "setTaskToday",
        ["operationId"] = operationId,
        ["task"] = Existing(task),
        ["todayLane"] = state,
    };

    private static ProposalExecutionPlan ReviewVersion(IWorkspaceSession session, int version, params JsonObject[] operations)
    {
        var snapshot = PlanningSnapshotJsonGenerator.Generate(session.Planning.Read(), version);
        using var document = JsonDocument.Parse(snapshot.Payload!.Value);
        var operationArray = new JsonArray();
        foreach (var operation in operations) operationArray.Add(operation);
        var proposal = new JsonObject
        {
            ["format"] = AgentInterchangeV1.ChangeProposalFormat,
            ["schemaVersion"] = version,
            ["sourceSnapshotId"] = document.RootElement.GetProperty("snapshotId").GetString(),
            ["sourceWorkspaceRevision"] = session.Planning.Read().WorkspaceRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["operations"] = operationArray,
        };
        var result = new ProposalReviewBoundary(session.Proposals, TimeProvider.System).Review(JsonSerializer.SerializeToUtf8Bytes(proposal));
        Assert.Empty(result.Failures);
        return Assert.IsType<ProposalExecutionPlan>(result.Plan);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private IWorkspaceSession CreateWorkspace(EncryptedWorkspaceStore store) =>
        Assert.IsAssignableFrom<IWorkspaceSession>(store.Create(
            WorkspacePath,
            passphrase,
            CategoryName.Create("Home").CategoryName!).Session);

    private static long Revision(IWorkspaceSession session) =>
        session.Planning.Read().WorkspaceRevision.Value;

    private static ProposalApplyResult Apply(
        IWorkspaceSession session,
        ProposalApproval approval,
        IProposalApplicationHost host) => session.ProposalApplication.Apply(
        approval,
        host,
        TestContext.Current.CancellationToken);

    private static ProposalExecutionPlan Review(
        IWorkspaceSession session,
        TimeProvider timeProvider,
        params JsonObject[] operations)
    {
        var snapshot = PlanningSnapshotJsonGenerator.Generate(session.Planning.Read(), 1);
        using var document = JsonDocument.Parse(snapshot.Payload!.Value);
        var operationArray = new JsonArray();
        foreach (var operation in operations)
        {
            operationArray.Add(operation);
        }

        var proposal = new JsonObject
        {
            ["format"] = AgentInterchangeV1.ChangeProposalFormat,
            ["schemaVersion"] = 1,
            ["sourceSnapshotId"] = document.RootElement.GetProperty("snapshotId").GetString(),
            ["sourceWorkspaceRevision"] = session.Planning.Read().WorkspaceRevision.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["operations"] = operationArray,
        };
        var result = new ProposalReviewBoundary(session.Proposals, timeProvider)
            .Review(JsonSerializer.SerializeToUtf8Bytes(proposal));
        Assert.Empty(result.Failures);
        return Assert.IsType<ProposalExecutionPlan>(result.Plan);
    }

    private static ProposalExecutionPlan ReviewNewWork(
        IWorkspaceSession session,
        params JsonObject[] operations)
    {
        var operationArray = new JsonArray();
        foreach (var operation in operations)
        {
            operationArray.Add(operation);
        }

        var proposal = new JsonObject
        {
            ["format"] = AgentInterchangeV1.NewWorkProposalFormat,
            ["schemaVersion"] = 1,
            ["operations"] = operationArray,
        };
        var result = new ProposalReviewBoundary(session.Proposals, TimeProvider.System)
            .Review(JsonSerializer.SerializeToUtf8Bytes(proposal));
        Assert.Empty(result.Failures);
        return Assert.IsType<ProposalExecutionPlan>(result.Plan);
    }

    private static JsonObject Existing(string id) => new() { ["existingId"] = id };

    private static JsonObject Local(string id) => new() { ["localId"] = id };

    private static JsonObject Complete(string taskId, string operationId) => new()
    {
        ["operation"] = "completeTask",
        ["operationId"] = operationId,
        ["task"] = Existing(taskId),
    };

    private sealed class Host(
        bool flushResult = true,
        bool refreshResult = true,
        Exception? refreshError = null) : IProposalApplicationHost
    {
        public bool RefreshResult { get; set; } = refreshResult;
        public Exception? RefreshError { get; set; } = refreshError;
        public int FlushCount { get; private set; }
        public int RefreshCount { get; private set; }

        public bool FlushPendingInspectorWork()
        {
            FlushCount++;
            return flushResult;
        }

        public bool RefreshProjections()
        {
            RefreshCount++;
            if (RefreshError is not null)
            {
                throw RefreshError;
            }

            return RefreshResult;
        }
    }

    private sealed class AcceptMarkdown : IProposalMarkdownValidator
    {
        public ProposalMarkdownValidation Validate(string markdown, bool proposed) =>
            new(true, string.IsNullOrEmpty(markdown) ? 0 : 1, 0);
    }

    private sealed class FailingProposalRecoveryFileOperations : IWorkspaceFileOperations
    {
        private readonly WorkspaceFileOperations inner = new();

        public bool FailProposalRecoveryPublish { get; set; }
        public bool FailAutomaticRecoveryMaintenance { get; set; }

        public string ResolvePath(string path) => inner.ResolvePath(path);
        public bool Exists(string path) => inner.Exists(path);
        public void EnsureParentDirectory(string path) => inner.EnsureParentDirectory(path);
        public void EnsureDirectory(string path) => inner.EnsureDirectory(path);
        public string GetCandidatePath(string targetPath, string identifier) =>
            inner.GetCandidatePath(targetPath, identifier);

        public void Publish(string candidatePath, string targetPath)
        {
            if (FailProposalRecoveryPublish
                && Path.GetFileName(targetPath).StartsWith(
                    "dot-orbit-pre-import-",
                    StringComparison.Ordinal))
            {
                throw new IOException("Synthetic proposal recovery failure.");
            }
            if (FailAutomaticRecoveryMaintenance
                && Path.GetFileName(targetPath).StartsWith(
                    "dot-orbit-auto-",
                    StringComparison.Ordinal))
            {
                throw new IOException("Synthetic automatic recovery failure.");
            }

            inner.Publish(candidatePath, targetPath);
        }

        public void Copy(string sourcePath, string candidatePath) => inner.Copy(sourcePath, candidatePath);
        public void CreateHardLink(string existingPath, string linkPath) =>
            inner.CreateHardLink(existingPath, linkPath);
        public string ComputeSha256(string path) => inner.ComputeSha256(path);
        public void Flush(string path) => inner.Flush(path);
        public void Replace(string candidatePath, string targetPath) => inner.Replace(candidatePath, targetPath);
        public void DeleteCandidate(string candidatePath) => inner.DeleteCandidate(candidatePath);
        public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern) =>
            inner.EnumerateFiles(directoryPath, searchPattern);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public void WriteAllText(string path, string contents)
        {
            if (FailAutomaticRecoveryMaintenance
                && path.Contains(".recovery-state.json.", StringComparison.Ordinal))
            {
                throw new IOException("Synthetic automatic recovery state failure.");
            }

            inner.WriteAllText(path, contents);
        }
        public void PublishOrReplace(string candidatePath, string targetPath) =>
            inner.PublishOrReplace(candidatePath, targetPath);
        public void DeleteFile(string path) => inner.DeleteFile(path);
    }
}
