using System.Security.Cryptography;
using System.Text;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.SampleWorkspace.Tests;

public sealed class SampleWorkspaceGeneratorTests
{
    private static readonly DateOnly AnchorDate = new(2030, 4, 5);

    [Fact]
    public void GenerateCreatesEncryptedCurrentSchemaRepresentativeWorkspace()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");

        var summary = SampleWorkspaceGenerator.Generate(path, AnchorDate);

        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, summary.SchemaVersion);
        Assert.Equal(6, summary.CategoryCount);
        Assert.Equal(6, summary.ProjectCount);
        Assert.Equal(33, summary.TaskCount);
        Assert.Equal(7, summary.ParticipantCount);
        Assert.Equal(21, summary.IncompleteTaskCount);
        Assert.Equal(12, summary.CompletedTaskCount);
        Assert.Equal(9, summary.TodayTaskCount);
        Assert.Equal(13, summary.UpcomingTaskCount);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(bytes, 0, 16));
        var decoded = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("Review Markdown showcase", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain(SampleWorkspaceGenerator.SamplePassphrase, decoded, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".recovery-state.json"));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".dot-orbit-sample-*"));

        var wrongPassphrase = Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.ForUnlock("wrong sample phrase"));
        var wrongOpen = new EncryptedWorkspaceStore().Open(path, wrongPassphrase);
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, wrongOpen.Status);

        using var session = Open(path);
        var snapshot = session.Work.Read();
        Assert.Equal(
            ["Work", "Home", "Health", "Learning & creative practice", "Errands", "Someday"],
            snapshot.Categories.Select(category => category.Name));
        Assert.Equal(
            ["AB", "Coach", "Garden pal", "MK", "SD", "Unused", "Zoë"],
            snapshot.Participants.Select(participant => participant.Label));
        Assert.Null(session.Recovery.AutomaticRecoveryDirectoryPath);

        var projects = snapshot.Projects.ToDictionary(project => project.Title, StringComparer.Ordinal);
        Assert.Equal(
            [
                "Launch the dot-orbit sample workspace", "Autumn garden", "Welsh foundations",
                "Kitchen refresh", "Health reset", "Prepare quarterly household accounts and paperwork",
            ],
            snapshot.Projects.Select(project => project.Title));
        Assert.Equal("Not started", ProjectWorkSummary.From(snapshot, projects["Launch the dot-orbit sample workspace"].Id).Status);
        Assert.Equal("In progress", ProjectWorkSummary.From(snapshot, projects["Autumn garden"].Id).Status);
        Assert.Equal("Complete", ProjectWorkSummary.From(snapshot, projects["Welsh foundations"].Id).Status);
        Assert.Equal("Not started", ProjectWorkSummary.From(snapshot, projects["Kitchen refresh"].Id).Status);
        Assert.Equal("In progress", ProjectWorkSummary.From(snapshot, projects["Health reset"].Id).Status);

        var orderBulbs = snapshot.Tasks.Single(task => task.Title == "Order spring bulbs");
        Assert.Equal(snapshot.Categories.Single(category => category.Name == "Errands").Id, orderBulbs.ExplicitCategoryId);
        var inherited = Task(snapshot, "Write release checklist");
        Assert.Equal(projects["Launch the dot-orbit sample workspace"].Id, inherited.ProjectId);
        Assert.Null(inherited.ExplicitCategoryId);
        Assert.Equal(
            snapshot.Categories.Single(category => category.Name == "Work").Id,
            projects["Launch the dot-orbit sample workspace"].CategoryId);
        var standalone = Task(snapshot, "Pick up parcel");
        Assert.Null(standalone.ProjectId);
        Assert.Null(standalone.ProjectPosition);
        Assert.Equal(snapshot.Categories.Single(category => category.Name == "Errands").Id, standalone.ExplicitCategoryId);
        var markdown = snapshot.Tasks.Single(task => task.Title == "Review Markdown showcase");
        Assert.Contains("Unicode: café, Zoë, Cymru", markdown.Description, StringComparison.Ordinal);
        Assert.Contains("javascript:alert(1)", markdown.Description, StringComparison.Ordinal);
        var unused = snapshot.Participants.Single(participant => participant.Label == "Unused");
        Assert.DoesNotContain(snapshot.Tasks, task => task.Participants.Contains(unused.Id, StringComparer.Ordinal));
        Assert.Equal(
            ["SD", "AB"],
            Task(snapshot, "Write release checklist").Participants.Select(id =>
                snapshot.Participants.Single(participant => participant.Id == id).Label));
    }

    [Fact]
    public void GenerateUsesAnchorRelativeDatesCompletionHistoryAndDeterministicIdentifiers()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        var firstPath = System.IO.Path.Combine(firstDirectory.Path, "workspace.db");
        var secondPath = System.IO.Path.Combine(secondDirectory.Path, "workspace.db");
        SampleWorkspaceGenerator.Generate(firstPath, AnchorDate);
        SampleWorkspaceGenerator.Generate(secondPath, AnchorDate);

        using var firstSession = Open(firstPath);
        using var secondSession = Open(secondPath);
        var first = firstSession.Work.Read();
        var second = secondSession.Work.Read();

        Assert.Equal(first.Categories.Select(item => item.Id), second.Categories.Select(item => item.Id));
        Assert.Equal(first.Projects.Select(item => item.Id), second.Projects.Select(item => item.Id));
        Assert.Equal(first.Tasks.Select(item => item.Id), second.Tasks.Select(item => item.Id));
        Assert.Equal(first.Participants.Select(item => item.Id), second.Participants.Select(item => item.Id));

        Assert.Equal(AnchorDate.AddDays(-7), Task(first, "Pick up parcel").DueDate);
        Assert.Equal(AnchorDate, Task(first, "Polish keyboard journey").DueDate);
        Assert.Equal(AnchorDate.AddDays(7), Task(first, "Schedule dentist").DueDate);
        Assert.Equal(AnchorDate.AddDays(8), Task(first, "Read migration ADR").DueDate);
        Assert.Null(Task(first, "Replace hallway bulb").DueDate);
        Assert.Equal(AnchorDate, Task(first, "Publish sample changelog").CompletionDate);
        Assert.Equal(AnchorDate.AddDays(-30), Task(first, "File appliance warranty").CompletionDate);
        Assert.Equal(AnchorDate.AddDays(-31), Task(first, "Close an old household loop").CompletionDate);
        Assert.All(first.Tasks.Where(task => task.IsComplete), task => Assert.NotNull(task.CompletedAt));

        var completeProject = first.Projects.Single(project => project.Title == "Welsh foundations");
        Assert.Equal(AnchorDate, ProjectWorkSummary.From(first, completeProject.Id).CompletionDate);
        Assert.False(WorkDatePresentation.IsOverdue(
            completeProject,
            ProjectWorkSummary.From(first, completeProject.Id),
            AnchorDate));
        var overdueProject = first.Projects.Single(project => project.Title == "Autumn garden");
        Assert.True(WorkDatePresentation.IsOverdue(
            overdueProject,
            ProjectWorkSummary.From(first, overdueProject.Id),
            AnchorDate));
    }

    [Fact]
    public void GeneratePreservesSharedProjectCategoryAndTodayOrdering()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");
        SampleWorkspaceGenerator.Generate(path, AnchorDate);
        using var session = Open(path);
        var snapshot = session.Work.Read();

        Assert.Equal(
            [
                "Polish keyboard journey", "Pick up parcel", "Replace hallway bulb", "Write release checklist",
                "Evening walk", "Renew library books", "Order spring bulbs", "Verify recovery wording",
                "Gather statements", "Plant bulbs", "Buy train tickets", "Prep questions", "Read migration ADR",
                "Schedule dentist", "Sketch winter layout", "Run platform smoke checks", "Match receipts",
                "Refill vitamins", "Send weekly update", "Write summary", "Review Markdown showcase",
            ],
            snapshot.Tasks.Where(task => !task.IsComplete)
                .OrderBy(task => task.SharedPosition)
                .Select(task => task.Title));

        var launch = snapshot.Projects.Single(project => project.Title == "Launch the dot-orbit sample workspace");
        Assert.Equal(
            ["Write release checklist", "Polish keyboard journey", "Verify recovery wording", "Run platform smoke checks", "Review Markdown showcase"],
            snapshot.Tasks.Where(task => task.ProjectId == launch.Id)
                .OrderBy(task => task.ProjectPosition)
                .Select(task => task.Title));
        Assert.Equal(
            ["Write release checklist", "Renew library books", "Verify recovery wording", "Plant bulbs", "Prep questions", "Schedule dentist"],
            snapshot.Tasks.Where(task => task.TodayLane == TodayLane.Planned)
                .OrderBy(task => task.SharedPosition)
                .Select(task => task.Title));
        Assert.Equal(
            ["Polish keyboard journey", "Pick up parcel", "Evening walk"],
            snapshot.Tasks.Where(task => task.TodayLane == TodayLane.InProgress)
                .OrderBy(task => task.SharedPosition)
                .Select(task => task.Title));
        Assert.DoesNotContain(snapshot.Tasks, task => task.IsComplete && task.IsInToday);
    }

    [Fact]
    public void GenerateRefusesExistingTargetWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");
        SampleWorkspaceGenerator.Generate(path, AnchorDate);
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var error = Assert.Throws<SampleWorkspaceException>(() =>
            SampleWorkspaceGenerator.Generate(path, AnchorDate.AddDays(1)));

        Assert.Equal("output-already-exists", error.Reason);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        using var session = Open(path);
        Assert.Equal(33, session.Work.Read().Tasks.Count);
    }

    [Fact]
    public void GenerateRefusesExistingRecoveryStateCompanionWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");
        var recoveryStatePath = path + ".recovery-state.json";
        const string originalState = "synthetic existing recovery state";
        File.WriteAllText(recoveryStatePath, originalState);

        var error = Assert.Throws<SampleWorkspaceException>(() =>
            SampleWorkspaceGenerator.Generate(path, AnchorDate));

        Assert.Equal("output-companion-exists", error.Reason);
        Assert.False(File.Exists(path));
        Assert.Equal(originalState, File.ReadAllText(recoveryStatePath));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".dot-orbit-sample-*"));
    }

    [Fact]
    public void GenerateRejectsOutOfRangeAnchorWithoutArtifacts()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");

        var error = Assert.Throws<SampleWorkspaceException>(() =>
            SampleWorkspaceGenerator.Generate(path, DateOnly.MaxValue));

        Assert.Equal("anchor-date-out-of-range", error.Reason);
        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetDirectories(directory.Path));
    }

    [Fact]
    public void ProgramReportsOnlyNonSensitiveSummary()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = Program.Main(["--output", path, "--anchor-date", "2030-04-05"]);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        Assert.Contains("result=passed", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("categories=6 projects=6 tasks=33 participants=7", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SampleWorkspaceGenerator.SamplePassphrase, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SampleWorkspaceGenerator.SamplePassphrase, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void ProgramRefusesExistingTargetWithNonSensitiveReason()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "workspace.db");
        SampleWorkspaceGenerator.Generate(path, AnchorDate);
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = Program.Main(["--output", path, "--anchor-date", "2030-04-06"]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("reason=output-already-exists", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SampleWorkspaceGenerator.SamplePassphrase, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StoreTimeProviderOverloadRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EncryptedWorkspaceStore(new DeterministicIdentifierGenerator(), null!));
    }

    private static IWorkspaceSession Open(string path)
    {
        var passphrase = Assert.IsType<WorkspacePassphrase>(
            WorkspacePassphrase.ForUnlock(SampleWorkspaceGenerator.SamplePassphrase));
        var opened = new EncryptedWorkspaceStore().Open(path, passphrase);
        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        return Assert.IsAssignableFrom<IWorkspaceSession>(opened.Session);
    }

    private static TaskRecord Task(WorkspaceWorkSnapshot snapshot, string title) =>
        snapshot.Tasks.Single(task => task.Title == title);
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dot-orbit-sample-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public static TemporaryDirectory CreateRepository()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "DotOrbit.slnx"), "<Solution />");
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
