using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Export.Json.Tests;

public sealed class PlaintextWorkspaceExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "orbit-export-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExportWritesTheDocumentedVersionTwoContractWithoutLosingRelationshipsOrState()
    {
        var completedAt = new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.FromHours(1));
        var archivedAt = new DateTimeOffset(2026, 10, 6, 11, 45, 0, TimeSpan.Zero);
        var snapshot = new WorkspaceWorkSnapshot(
            [
                new("work", "Work", 8, "rose"),
                new("home", "Home", 2, "teal"),
            ],
            [
                new("archived-project", "Old plan", "History", "work", null, 7, archivedAt, new(2026, 10, 6)),
                new("project", "Garden", "Bulbs", "home", new(2026, 10, 12), 1),
            ],
            [
                new("archived-task", "archived-project", "Finished", "Done", null, null, 9, 0,
                    completedAt, new(2026, 10, 5), ["zoe", "alex"], null, archivedAt, new(2026, 10, 6)),
                new("task", "project", "Plant", "Near the fence", "work", new(2026, 10, 8), 3, 4,
                    null, null, ["alex"], TodayLane.InProgress),
                new("standalone", null, "Call", "", "home", null, 5, null),
                new("archived-project-child", "archived-project", "Retained child", "", null, null, 6, 2),
            ],
            [
                new("zoe", "Zoe"),
                new("alex", "Alex"),
            ]);

        using var document = JsonDocument.Parse(PlaintextWorkspaceExporter.CreateDocument(snapshot));
        var root = document.RootElement;

        Assert.Equal(["format", "schemaVersion", "categories", "participants", "projects", "tasks"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(PlaintextWorkspaceExporter.Format, root.GetProperty("format").GetString());
        Assert.Equal(PlaintextWorkspaceExporter.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["home", "work"], root.GetProperty("categories").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()));
        Assert.Equal([2L, 8L], root.GetProperty("categories").EnumerateArray()
            .Select(item => item.GetProperty("position").GetInt64()));
        Assert.Equal(["teal", "rose"], root.GetProperty("categories").EnumerateArray()
            .Select(item => item.GetProperty("colourKey").GetString()));
        Assert.All(root.GetProperty("categories").EnumerateArray(), category =>
            Assert.Equal(["id", "name", "position", "colourKey"], category.EnumerateObject().Select(property => property.Name)));
        Assert.Equal(["alex", "zoe"], root.GetProperty("participants").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()));
        Assert.All(root.GetProperty("participants").EnumerateArray(), participant =>
            Assert.Equal(["id", "label"], participant.EnumerateObject().Select(property => property.Name)));

        var activeProject = Assert.Single(root.GetProperty("projects").GetProperty("active").EnumerateArray());
        Assert.Equal("project", activeProject.GetProperty("id").GetString());
        Assert.Equal(
            ["id", "title", "description", "categoryId", "targetDate", "position", "isArchived", "archiveInstant", "archiveDate"],
            activeProject.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Garden", activeProject.GetProperty("title").GetString());
        Assert.Equal("Bulbs", activeProject.GetProperty("description").GetString());
        Assert.Equal("home", activeProject.GetProperty("categoryId").GetString());
        Assert.Equal("2026-10-12", activeProject.GetProperty("targetDate").GetString());
        Assert.False(activeProject.GetProperty("isArchived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, activeProject.GetProperty("archiveInstant").ValueKind);

        var archivedProject = Assert.Single(root.GetProperty("projects").GetProperty("archived").EnumerateArray());
        Assert.True(archivedProject.GetProperty("isArchived").GetBoolean());
        Assert.Equal("2026-10-06T11:45:00.0000000+00:00", archivedProject.GetProperty("archiveInstant").GetString());
        Assert.Equal("2026-10-06", archivedProject.GetProperty("archiveDate").GetString());

        var activeTasks = root.GetProperty("tasks").GetProperty("active").EnumerateArray().ToArray();
        Assert.Equal(["task", "standalone", "archived-project-child"],
            activeTasks.Select(item => item.GetProperty("id").GetString()));
        var attached = activeTasks[0];
        Assert.Equal(
            ["id", "projectId", "title", "description", "explicitCategoryId", "dueDate", "sharedPosition", "projectPosition", "isComplete", "completionInstant", "completionDate", "participantIds", "todayLane", "isArchived", "archiveInstant", "archiveDate"],
            attached.EnumerateObject().Select(property => property.Name));
        Assert.Equal("project", attached.GetProperty("projectId").GetString());
        Assert.Equal("work", attached.GetProperty("explicitCategoryId").GetString());
        Assert.Equal(3, attached.GetProperty("sharedPosition").GetInt64());
        Assert.Equal(4, attached.GetProperty("projectPosition").GetInt64());
        Assert.Equal("inProgress", attached.GetProperty("todayLane").GetString());
        Assert.Equal(["alex"], attached.GetProperty("participantIds").EnumerateArray().Select(item => item.GetString()));
        Assert.False(attached.GetProperty("isComplete").GetBoolean());
        Assert.False(attached.GetProperty("isArchived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, activeTasks[1].GetProperty("projectId").ValueKind);
        Assert.Equal(JsonValueKind.Null, activeTasks[1].GetProperty("projectPosition").ValueKind);
        Assert.Equal("archived-project", activeTasks[2].GetProperty("projectId").GetString());
        Assert.Equal(2, activeTasks[2].GetProperty("projectPosition").GetInt64());

        var archivedTask = Assert.Single(root.GetProperty("tasks").GetProperty("archived").EnumerateArray());
        Assert.True(archivedTask.GetProperty("isComplete").GetBoolean());
        Assert.True(archivedTask.GetProperty("isArchived").GetBoolean());
        Assert.Equal("2026-10-05T08:30:00.0000000+00:00", archivedTask.GetProperty("completionInstant").GetString());
        Assert.Equal("2026-10-05", archivedTask.GetProperty("completionDate").GetString());
        Assert.Equal(["zoe", "alex"], archivedTask.GetProperty("participantIds").EnumerateArray()
            .Select(item => item.GetString()));
    }

    [Fact]
    public void ExportIsByteDeterministicAndUnicodeContentRoundTripsThroughValidJson()
    {
        var category = new WorkspaceCategory("cat", "Café ☕", 0, "indigo");
        var otherCategory = new WorkspaceCategory("other", "Other", 1, "lime");
        var project = new ProjectRecord("project", "Plan \"A\" 🪴", "Line one\nLine two\\end", "cat", null, 0);
        var otherProject = new ProjectRecord("other-project", "Other", "", "other", null, 1);
        var task = new TaskRecord("task", "project", "日本語", "Emoji 👩🏽‍💻", null, null, 0, 0);
        var otherTask = new TaskRecord("other-task", "other-project", "Other", "", null, null, 1, 0);
        var participant = new ParticipantRecord("person", "Zoë");
        var otherParticipant = new ParticipantRecord("other-person", "Alex");
        var first = new WorkspaceWorkSnapshot(
            [category, otherCategory],
            [project, otherProject],
            [task, otherTask],
            [participant, otherParticipant]);
        var shuffled = new WorkspaceWorkSnapshot(
            [otherCategory, category],
            [otherProject, project],
            [otherTask, task],
            [otherParticipant, participant]);
        var firstBytes = PlaintextWorkspaceExporter.CreateDocument(first);
        var secondBytes = PlaintextWorkspaceExporter.CreateDocument(shuffled);

        Assert.Equal(firstBytes, secondBytes);
        using var document = JsonDocument.Parse(firstBytes);
        Assert.Equal("Café ☕", document.RootElement.GetProperty("categories")[0].GetProperty("name").GetString());
        Assert.Equal("indigo", document.RootElement.GetProperty("categories")[0].GetProperty("colourKey").GetString());
        Assert.Equal("Plan \"A\" 🪴", document.RootElement.GetProperty("projects").GetProperty("active")[0]
            .GetProperty("title").GetString());
        Assert.Equal("Line one\nLine two\\end", document.RootElement.GetProperty("projects").GetProperty("active")[0]
            .GetProperty("description").GetString());
        Assert.Equal("日本語", document.RootElement.GetProperty("tasks").GetProperty("active")[0]
            .GetProperty("title").GetString());
        Assert.DoesNotContain("Line one\nLine two", Encoding.UTF8.GetString(firstBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivedCollectionsUsePersistedOrderWithIdentifierTieBreaks()
    {
        var archivedAt = new DateTimeOffset(2026, 10, 6, 11, 45, 0, TimeSpan.Zero);
        var snapshot = new WorkspaceWorkSnapshot(
            [],
            [
                new("project-z", "Z", "", "home", null, 3, archivedAt, new(2026, 10, 6)),
                new("project-b", "B", "", "home", null, 1, archivedAt, new(2026, 10, 6)),
                new("project-a", "A", "", "home", null, 1, archivedAt, new(2026, 10, 6)),
            ],
            [
                new("task-z", "project-z", "Z", "", null, null, 3, 0,
                    null, null, [], null, archivedAt, new(2026, 10, 6)),
                new("task-b", "project-b", "B", "", null, null, 1, 0,
                    null, null, [], null, archivedAt, new(2026, 10, 6)),
                new("task-a", "project-a", "A", "", null, null, 1, 0,
                    null, null, [], null, archivedAt, new(2026, 10, 6)),
            ],
            []);

        using var document = JsonDocument.Parse(PlaintextWorkspaceExporter.CreateDocument(snapshot));

        Assert.Equal(["project-a", "project-b", "project-z"],
            document.RootElement.GetProperty("projects").GetProperty("archived").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(["task-a", "task-b", "task-z"],
            document.RootElement.GetProperty("tasks").GetProperty("archived").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public void RealEncryptedSnapshotIncludesArchiveAndExcludesTaskAndProjectBinContents()
    {
        Directory.CreateDirectory(_directory);
        var workspacePath = Path.Combine(_directory, "workspace.db");
        var passphrase = WorkspacePassphrase.Create(
            "correct horse battery",
            "correct horse battery").Passphrase!;
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), time);
        using var session = store.Create(
            workspacePath,
            passphrase,
            CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var participant = session.Work.CreateParticipant("SD");
        var activeProject = session.Work.CreateProject("Visible project", "Visible description", category.Id, null);
        var archivedTask = session.Work.CreateStandaloneTask(
            "Archived task",
            "Archived marker",
            category.Id,
            null,
            new([participant.Id], []));
        session.Work.CompleteTask(archivedTask.Id);
        session.Work.ArchiveTask(archivedTask.Id);
        var binnedTask = session.Work.CreateStandaloneTask("Binned task marker", "Binned task secret", category.Id, null);
        session.Work.MoveTaskToBin(binnedTask.Id);
        var binnedProject = session.Work.CreateProject("Binned project marker", "Binned project secret", category.Id, null);
        session.Work.CreateTask(binnedProject.Id, "Binned aggregate child marker");
        session.Work.MoveProjectToBin(binnedProject.Id);

        var content = Encoding.UTF8.GetString(PlaintextWorkspaceExporter.CreateDocument(session.Work.Read()));

        Assert.Contains(activeProject.Id, content, StringComparison.Ordinal);
        Assert.Contains("Archived marker", content, StringComparison.Ordinal);
        Assert.Contains(participant.Id, content, StringComparison.Ordinal);
        Assert.DoesNotContain("Binned task marker", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Binned task secret", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Binned project marker", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Binned project secret", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Binned aggregate child marker", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportPublishesAtomicallyAndPreservesAnExistingDestinationWhenTemporaryCreationFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        await File.WriteAllTextAsync(destinationPath, "existing export", cancellationToken);
        var identifier = new FixedIdentifierGenerator("collision");
        var temporaryPath = Path.Combine(_directory, ".workspace.json.collision.tmp");
        await File.WriteAllTextAsync(temporaryPath, "occupied", cancellationToken);
        var exporter = new PlaintextWorkspaceExporter(identifier);
        var snapshot = new WorkspaceWorkSnapshot([], [], [], []);

        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(snapshot, destinationPath, cancellationToken));

        Assert.Equal("existing export", await File.ReadAllTextAsync(destinationPath, cancellationToken));
        Assert.Equal("occupied", await File.ReadAllTextAsync(temporaryPath, cancellationToken));
    }

    [Fact]
    public async Task ExportReplacesTheDestinationOnlyAfterACompleteDocumentIsWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        await File.WriteAllTextAsync(destinationPath, "old export", cancellationToken);
        var exporter = new PlaintextWorkspaceExporter(new FixedIdentifierGenerator("new"));

        await exporter.ExportAsync(new WorkspaceWorkSnapshot([], [], [], []), destinationPath, cancellationToken);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(destinationPath, cancellationToken));
        Assert.Equal(PlaintextWorkspaceExporter.Format, document.RootElement.GetProperty("format").GetString());
        Assert.False(File.Exists(Path.Combine(_directory, ".workspace.json.new.tmp")));
    }

    [Fact]
    public async Task CancelledExportRemovesItsTemporaryFileAndPreservesTheDestination()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        await File.WriteAllTextAsync(destinationPath, "existing export", cancellationToken);
        var temporaryPath = Path.Combine(_directory, ".workspace.json.cancelled.tmp");
        var exporter = new PlaintextWorkspaceExporter(new FixedIdentifierGenerator("cancelled"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(new WorkspaceWorkSnapshot([], [], [], []), destinationPath, cancellation.Token));

        Assert.Equal("existing export", await File.ReadAllTextAsync(destinationPath, cancellationToken));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task ExportPreservesAnExistingUnixMode()
    {
        if (OperatingSystem.IsWindows()) return;

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        await File.WriteAllTextAsync(destinationPath, "existing export", cancellationToken);
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(destinationPath, expectedMode);
        var exporter = new PlaintextWorkspaceExporter(new FixedIdentifierGenerator("permissions"));

        await exporter.ExportAsync(new WorkspaceWorkSnapshot([], [], [], []), destinationPath, cancellationToken);

        Assert.Equal(expectedMode, File.GetUnixFileMode(destinationPath));
    }

    [Fact]
    public async Task NewUnixExportIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        var exporter = new PlaintextWorkspaceExporter(new FixedIdentifierGenerator("permissions"));

        await exporter.ExportAsync(new WorkspaceWorkSnapshot([], [], [], []), destinationPath, cancellationToken);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(destinationPath));
    }

    [Fact]
    public async Task ExportPreservesAnExistingWindowsAcl()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        var expectedSecurity = CreateOwnerOnlySecurity();
        using (var stream = new FileInfo(destinationPath).Create(
            FileMode.CreateNew,
            FileSystemRights.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            expectedSecurity))
        {
            await stream.WriteAsync("existing export"u8.ToArray(), cancellationToken);
        }
        var exporter = new PlaintextWorkspaceExporter(new FixedIdentifierGenerator("permissions"));

        await exporter.ExportAsync(new WorkspaceWorkSnapshot([], [], [], []), destinationPath, cancellationToken);

        AssertCurrentUserOnlyAcl(destinationPath);
    }

    [Fact]
    public async Task NewWindowsExportIsOwnerOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, "workspace.json");
        var exporter = new PlaintextWorkspaceExporter(new FixedIdentifierGenerator("permissions"));

        await exporter.ExportAsync(new WorkspaceWorkSnapshot([], [], [], []), destinationPath, cancellationToken);

        AssertCurrentUserOnlyAcl(destinationPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedIdentifierGenerator(string identifier) : IIdentifierGenerator
    {
        public string NewIdentifier() => identifier;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateOwnerOnlySecurity()
    {
        var owner = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserOnlyAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var rule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }
}
