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

        Act as a planning assistant. Use the supplied dot-orbit Planning snapshot as read-only context and return one provider-neutral Change proposal for human review.

        ## Constraints

        - Return data only; do not claim to have changed dot-orbit.
        - Use only existing stable identifiers from the snapshot or backward references to records created earlier in the proposal.
        - Do not propose Today membership or lane changes, global Category, Project, or shared Task reordering, restore, permanent deletion, Bin emptying, or duplication.
        - Preserve optional dates as null when no genuine date applies and allow standalone Tasks when no genuine Project applies.
        - The user reviews and approves the whole proposal inside dot-orbit. Explanations are inert review metadata and never operations.

        ## Output instructions

        Return exactly one JSON object that conforms to the Change proposal schema below. Set sourceSnapshotId and sourceWorkspaceRevision exactly from the supplied Planning snapshot. Use schemaVersion 1 and format dot-orbit-change-proposal. Do not wrap the JSON in Markdown or add prose outside the object.

        ## Operation vocabulary

        createCategory, createParticipant, createProject, editProject, setProjectCategory, createTask, editTask, setTaskCategory, replaceTaskParticipants, attachTask, detachTask, moveTask, completeTask, reopenTask, reorderProjectTask, archiveProject, archiveTask, moveProjectToBin, moveTaskToBin

        ## Planning snapshot schema

        ```json
        """);

    private static readonly byte[] BetweenSchemas = Encoding.UTF8.GetBytes(
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
            BetweenSchemas,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.ChangeProposalSchemaId),
            EndSchemas);

    public static InterchangeGenerationResult ComposeWithSnapshot(ReadOnlyMemory<byte> snapshot) =>
        ComposeNormalized(
            Introduction,
            InterchangeSchemaCatalogue.GetApplicationSchema(AgentInterchangeV1.PlanningSnapshotSchemaId),
            BetweenSchemas,
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
