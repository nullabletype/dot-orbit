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
        var reviewed = ReviewCore(strictUtf8, queuedBasis: null, allowRefresh: false);
        return new(reviewed.Plan, reviewed.Failures);
    }

    public QueuedProposalAdmissionResult PrepareQueued(ReadOnlyMemory<byte> strictUtf8)
    {
        var reviewed = ReviewCore(strictUtf8, queuedBasis: null, allowRefresh: false);
        if (reviewed.Plan is not { } plan || reviewed.ParsedProposal is not { } proposal)
        {
            return new(null, reviewed.Failures, ReadOnlyMemory<byte>.Empty);
        }

        try
        {
            var basis = PendingProposalReviewBasis.Create(
                proposal,
                reviewed.Current!,
                PayloadDigest(strictUtf8.Span));
            return new(plan, [], basis);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or JsonException)
        {
            return new(
                null,
                Array.AsReadOnly(new[] { new ProposalValidationFailure("review-basis-limit") }),
                ReadOnlyMemory<byte>.Empty);
        }
    }

    public QueuedProposalReviewResult ReviewQueued(
        ReadOnlyMemory<byte> strictUtf8,
        ReadOnlyMemory<byte> reviewBasis)
    {
        var reviewed = ReviewCore(strictUtf8, reviewBasis, allowRefresh: true);
        return new(reviewed.Plan, reviewed.Failures, reviewed.WasRefreshed);
    }

    private ReviewCoreResult ReviewCore(
        ReadOnlyMemory<byte> strictUtf8,
        ReadOnlyMemory<byte>? queuedBasis,
        bool allowRefresh)
    {
        var parsed = ChangeProposalJsonParser.Parse(strictUtf8);
        if (!parsed.IsValid)
            return Failed(parsed.ErrorCode!);

        ProposalWorkspaceState current;
        try { current = source.Read(); }
        catch (WorkspaceWorkException) { return Unavailable(); }
        catch (ObjectDisposedException) { return Unavailable(); }
        string? identity = null;
        IPlanningSnapshotIdentityVerifier? verifier = null;
        if (parsed.Proposal!.Kind == ProposalKind.WorkspaceChange)
        {
            var snapshot = PlanningSnapshotJsonGenerator.Generate(
                PlanningSnapshotProjection.Create(current.Revision, current.Workspace),
                parsed.Proposal.SchemaVersion);
            if (!snapshot.Succeeded)
                return Failed(snapshot.ErrorCode!);
            using var document = JsonDocument.Parse(snapshot.Payload!.Value);
            identity = document.RootElement.GetProperty("snapshotId").GetString()!;
            verifier = new PlanningSnapshotIdentityVerifier(parsed.Proposal.SchemaVersion);
        }
        var proposal = parsed.Proposal;
        var wasRefreshed = false;
        if (allowRefresh
            && proposal.Kind == ProposalKind.WorkspaceChange
            && (proposal.SourceWorkspaceRevision != current.Revision
                || !string.Equals(proposal.SourceSnapshotId, identity, StringComparison.Ordinal)))
        {
            var payloadDigest = PayloadDigest(strictUtf8.Span);
            if (queuedBasis is not { } basis || basis.IsEmpty)
            {
                return Failed("missing-review-basis", proposal, current);
            }

            if (!PendingProposalReviewBasis.Matches(
                    basis.Span,
                    proposal,
                    current,
                    payloadDigest))
            {
                return Failed("source-conflict", proposal, current);
            }

            proposal = proposal with
            {
                SourceWorkspaceRevision = current.Revision,
                SourceSnapshotId = identity,
            };
            wasRefreshed = true;
        }
        var planner = new ProposalSemanticPlanner(timeProvider, new MarkdownValidator());
        var result = planner.ValidateForApplication(proposal, source, current, identity,
            Convert.ToHexString(parsed.CanonicalDigest.Span).ToLowerInvariant(),
            PayloadDigest(strictUtf8.Span),
            verifier);
        return new(result.Plan, result.Failures, proposal, current, wasRefreshed);
    }

    private static string PayloadDigest(ReadOnlySpan<byte> strictUtf8) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(strictUtf8));

    private static ReviewCoreResult Failed(
        string code,
        ChangeProposal? proposal = null,
        ProposalWorkspaceState? current = null) => new(
            null,
            Array.AsReadOnly(new[] { new ProposalValidationFailure(code) }),
            proposal,
            current,
            false);

    private static ReviewCoreResult Unavailable() => Failed("workspace-unavailable");

    private sealed class MarkdownValidator : IProposalMarkdownValidator
    {
        public ProposalMarkdownValidation Validate(string markdown, bool proposed)
        {
            var parsed = SanitisedMarkdown.MeasureBounded(markdown, maxNodes: proposed ? 4_096 : 65_536, maxDepth: proposed ? 32 : 65_536);
            return new(parsed.Error == MarkdownBudgetError.None, parsed.NodeCount, parsed.ContainerDepth);
        }
    }

    private sealed record ReviewCoreResult(
        ProposalExecutionPlan? Plan,
        IReadOnlyList<ProposalValidationFailure> Failures,
        ChangeProposal? ParsedProposal,
        ProposalWorkspaceState? Current,
        bool WasRefreshed);
}

public sealed record QueuedProposalAdmissionResult(
    ProposalExecutionPlan? Plan,
    IReadOnlyList<ProposalValidationFailure> Failures,
    ReadOnlyMemory<byte> ReviewBasis);

public sealed record QueuedProposalReviewResult(
    ProposalExecutionPlan? Plan,
    IReadOnlyList<ProposalValidationFailure> Failures,
    bool WasRefreshed);
