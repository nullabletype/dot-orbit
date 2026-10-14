using System.Text.Json;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Markdown;

namespace DotOrbit.Interchange.Json;

/// <summary>Deliberate, read-only proposal review; this boundary has no workspace mutation capability.</summary>
public sealed class ProposalReviewBoundary(IProposalWorkspaceSource source, TimeProvider timeProvider)
{
    public ProposalPlanResult Review(ReadOnlyMemory<byte> strictUtf8)
    {
        var parsed = ChangeProposalJsonParser.Parse(strictUtf8);
        if (!parsed.IsValid)
            return new(null, Array.AsReadOnly(new[] { new ProposalValidationFailure(parsed.ErrorCode!) }));

        ProposalWorkspaceState current;
        try { current = source.Read(); }
        catch (WorkspaceWorkException) { return Unavailable(); }
        catch (ObjectDisposedException) { return Unavailable(); }
        var snapshot = PlanningSnapshotJsonGenerator.Generate(
            PlanningSnapshotProjection.Create(current.Revision, current.Workspace));
        if (!snapshot.Succeeded)
            return new(null, Array.AsReadOnly(new[] { new ProposalValidationFailure(snapshot.ErrorCode!) }));
        using var document = JsonDocument.Parse(snapshot.Payload!.Value);
        var identity = document.RootElement.GetProperty("snapshotId").GetString()!;
        var planner = new ProposalSemanticPlanner(timeProvider, new MarkdownValidator());
        return planner.Validate(parsed.Proposal!, current, identity,
            Convert.ToHexString(parsed.CanonicalDigest.Span).ToLowerInvariant());
    }

    private static ProposalPlanResult Unavailable() => new(null,
        Array.AsReadOnly(new[] { new ProposalValidationFailure("workspace-unavailable") }));

    private sealed class MarkdownValidator : IProposalMarkdownValidator
    {
        public ProposalMarkdownValidation Validate(string markdown, bool proposed)
        {
            var parsed = SanitisedMarkdown.MeasureBounded(markdown, maxNodes: proposed ? 4_096 : 65_536, maxDepth: proposed ? 32 : 65_536);
            return new(parsed.Error == MarkdownBudgetError.None, parsed.NodeCount, parsed.ContainerDepth);
        }
    }
}
