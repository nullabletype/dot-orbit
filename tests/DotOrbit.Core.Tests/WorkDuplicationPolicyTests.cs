using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class WorkDuplicationPolicyTests
{
    [Fact]
    public void ActiveProjectTaskRetainsProjectAndExplicitCategorySemantics()
    {
        var project = Project("project", "home");
        var source = Task("task", project.Id, explicitCategoryId: null);

        var destination = WorkDuplicationPolicy.TaskDestination(
            new([Category("home")], [project], [source]), source);

        Assert.Equal(project.Id, destination.ProjectId);
        Assert.Null(destination.ExplicitCategoryId);
    }

    [Fact]
    public void ArchivedProjectTaskBecomesStandaloneWithItsEffectiveCategory()
    {
        var project = Project("project", "home") with
        {
            ArchivedAt = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero),
            ArchiveDate = new DateOnly(2026, 10, 11),
        };
        var inherited = Task("inherited", project.Id, explicitCategoryId: null);
        var overridden = Task("overridden", project.Id, explicitCategoryId: "work");
        var snapshot = new WorkspaceWorkSnapshot(
            [Category("home"), Category("work")],
            [project],
            [inherited, overridden]);

        Assert.Equal((null, "home"), WorkDuplicationPolicy.TaskDestination(snapshot, inherited));
        Assert.Equal((null, "work"), WorkDuplicationPolicy.TaskDestination(snapshot, overridden));
    }

    [Fact]
    public void SuggestedTitleNormalizesTheSourceTitle()
    {
        Assert.Equal("Copy of Plan the café", WorkDuplicationPolicy.SuggestedTitle("  Plan the café\n"));
    }

    private static WorkspaceCategory Category(string id) => new(id, id, 0);

    private static ProjectRecord Project(string id, string categoryId) =>
        new(id, "Project", "", categoryId, null, 0);

    private static TaskRecord Task(string id, string? projectId, string? explicitCategoryId) =>
        new(id, projectId, "Task", "", explicitCategoryId, null, 0, projectId is null ? null : 0);
}
