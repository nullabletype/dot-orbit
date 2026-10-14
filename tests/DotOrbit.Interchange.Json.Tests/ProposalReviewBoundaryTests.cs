using System.Text;
using System.Text.Json;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Interchange.Json.Tests;

public sealed class ProposalReviewBoundaryTests
{
    [Fact]
    public void UnchangedStoredMarkdownMayExceedProposalFieldNodeLimit()
    {
        var source = Source(string.Join("\n\n", Enumerable.Repeat("synthetic", 2_049)));
        var result = Review(source, new { title = "Changed synthetic task" });
        Assert.NotNull(result.Plan);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public void ProposedMarkdownOverNodeLimitIsRejectedCompletely()
    {
        var result = Review(Source(""), new { description = string.Join("\n\n", Enumerable.Repeat("synthetic", 2_049)) });
        Assert.Null(result.Plan);
        Assert.Equal("markdown-limit", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void ExcessiveParserNestingReturnsSafeFailure()
    {
        var result = Review(Source(""), new { description = new string('>', 1_000) + " synthetic" });
        Assert.Null(result.Plan);
        Assert.Equal("markdown-limit", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void SyntaxRejectionDoesNotReadWorkspace()
    {
        var source = Source("");
        var result = new ProposalReviewBoundary(source, TimeProvider.System).Review(Encoding.UTF8.GetBytes("{} {}"));
        Assert.Null(result.Plan);
        Assert.Equal(0, source.ReadCount);
    }

    private static ProposalPlanResult Review(CountingSource source, object changes)
    {
        var content = PlanningSnapshotProjection.Create(source.State.Revision, source.State.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        var input = JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = "1",
            operations = new[] { new { operation = "editTask", operationId = "synthetic-edit", task = new { existingId = "t" }, changes } },
        });
        return new ProposalReviewBoundary(source, TimeProvider.System).Review(Encoding.UTF8.GetBytes(input));
    }

    private static CountingSource Source(string description) => new(new(new(1),
        new([new("c", "Synthetic category", 0)], [],
            [new("t", null, "Synthetic task", description, "c", null, 0, null)], []), [], []));

    private sealed class CountingSource(ProposalWorkspaceState state) : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState State { get; } = state;
        public int ReadCount { get; private set; }
        public ProposalWorkspaceState Read() { ReadCount++; return State; }
    }
}
