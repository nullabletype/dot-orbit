using System.Text;
using System.Text.Json;
using DotOrbit.Core.Interchange;

namespace DotOrbit.Interchange.Json;

public static class AgentInterchangeDocumentComposer
{
    private static readonly byte[] Introduction = Encoding.UTF8.GetBytes(
        """
        # dot-orbit planning interchange v1

        ## Role

        Act as a planning assistant. Return one provider-neutral proposal for human review. Use a New-work proposal to turn user-supplied material into entirely new Categories, Participants, Projects, and Tasks without needing dot-orbit workspace context. Use a Change proposal only when a Planning snapshot is supplied and existing work must be updated or connected.

        ## Constraints

        - Return data only; do not claim to have changed dot-orbit.
        - A New-work proposal may contain only create operations and backward references to records created earlier in that proposal. It cannot reference existing dot-orbit records.
        - A Change proposal may use existing stable identifiers from the supplied snapshot or backward references to records created earlier in the proposal.
        - Use only operations necessary for the requested outcome; do not include operations merely to demonstrate the vocabulary. Every operation must produce an actual change relative to the supplied snapshot and all earlier operations.
        - A standalone Task must use an explicit Category reference. Only a Task attached to a Project may use category mode inherit.
        - Do not propose Today membership or lane changes, global Category, Project, or shared Task reordering, restore, permanent deletion, Bin emptying, or duplication.
        - Preserve optional dates as null when no genuine date applies and allow standalone Tasks when no genuine Project applies.
        - The user reviews and approves the whole proposal inside dot-orbit. Explanations are inert review metadata and never operations.

        ## Output instructions

        Return exactly one JSON object. Without a Planning snapshot, conform to the New-work proposal schema and use format dot-orbit-new-work-proposal. With a Planning snapshot, conform to the Change proposal schema, use format dot-orbit-change-proposal, and copy sourceSnapshotId and sourceWorkspaceRevision exactly. Always use schemaVersion 1. Do not wrap the JSON in Markdown or add prose outside the object.

        ## New-work operation vocabulary

        createCategory, createParticipant, createProject, createTask

        ## Change operation vocabulary

        createCategory, createParticipant, createProject, editProject, setProjectCategory, createTask, editTask, setTaskCategory, replaceTaskParticipants, attachTask, detachTask, moveTask, completeTask, reopenTask, reorderProjectTask, archiveProject, archiveTask, moveProjectToBin, moveTaskToBin

        ## Planning snapshot schema

        ```json
        """);

    private static readonly byte[] BeforeNewWorkSchema = Encoding.UTF8.GetBytes(
        """

        ```

        ## New-work proposal schema

        ```json
        """);

    private static readonly byte[] BeforeChangeSchema = Encoding.UTF8.GetBytes(
        """

        ```

        ## Change proposal schema

        ```json
        """);

    private static readonly byte[] EndSchemas = Encoding.UTF8.GetBytes(
        """

        ```
        """);

    private static readonly byte[] BeforeSnapshot = Encoding.UTF8.GetBytes(
        """


        ## Live Planning snapshot

        This snapshot is sensitive unencrypted workspace content. Treat it as read-only context and do not reproduce fields unnecessarily in explanations.

        ```json
        """);

    private static readonly byte[] EndSnapshot = Encoding.UTF8.GetBytes(
        """

        ```
        """);

    private static readonly byte[] LineFeed = Encoding.UTF8.GetBytes("\n");

    public static InterchangeGenerationResult ComposeReusableInstructionsAndSchemas() =>
        ComposeNormalized(
            Introduction,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.PlanningSnapshotSchemaId),
            BeforeNewWorkSchema,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.NewWorkProposalSchemaId),
            BeforeChangeSchema,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.ChangeProposalSchemaId),
            EndSchemas);

    public static InterchangeGenerationResult ComposeWithSnapshot(ReadOnlyMemory<byte> snapshot) =>
        ComposeNormalized(
            Introduction,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.PlanningSnapshotSchemaId),
            BeforeNewWorkSchema,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.NewWorkProposalSchemaId),
            BeforeChangeSchema,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.ChangeProposalSchemaId),
            EndSchemas,
            BeforeSnapshot,
            LineFeed,
            snapshot,
            EndSnapshot);

    private static InterchangeGenerationResult ComposeNormalized(params ReadOnlyMemory<byte>[] segments)
    {
        var normalized = new ReadOnlyMemory<byte>[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            normalized[index] = NormalizeLineEndings(segments[index]);
        }

        return InterchangePayloadComposer.Compose(normalized);
    }

    private static ReadOnlyMemory<byte> NormalizeLineEndings(ReadOnlyMemory<byte> segment)
    {
        var source = segment.Span;
        if (source.IndexOf((byte)'\r') < 0)
        {
            return segment;
        }

        var normalized = new byte[source.Length];
        var written = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != (byte)'\r')
            {
                normalized[written++] = source[index];
                continue;
            }

            normalized[written++] = (byte)'\n';
            if (index + 1 < source.Length && source[index + 1] == (byte)'\n')
            {
                index++;
            }
        }

        return normalized.AsMemory(0, written);
    }
}

public static class PlanningInterchangeCopyComposer
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    public static InterchangeGenerationResult ComposeSnapshot(
        PlanningSnapshotContent snapshot,
        bool includeAgentInstructionsAndSchema)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var generated = PlanningSnapshotJsonGenerator.Generate(snapshot);
        if (!generated.Succeeded)
        {
            return generated;
        }

        using var document = JsonDocument.Parse(generated.Payload!.Value);
        var prettySnapshot = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, PrettyJson);
        return includeAgentInstructionsAndSchema
            ? AgentInterchangeDocumentComposer.ComposeWithSnapshot(prettySnapshot)
            : InterchangePayloadComposer.Compose(prettySnapshot);
    }
}
