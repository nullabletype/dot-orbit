using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Interchange.Json;
using Xunit;

namespace DotOrbit.Interchange.Json.Tests;

public sealed class InterchangeContractTests
{
    private static readonly string[] OperationVocabulary =
    [
        "createCategory", "createParticipant", "createProject", "editProject",
        "setProjectCategory", "createTask", "editTask", "setTaskCategory",
        "replaceTaskParticipants", "attachTask", "detachTask", "moveTask",
        "completeTask", "reopenTask", "reorderProjectTask", "archiveProject",
        "archiveTask", "moveProjectToBin", "moveTaskToBin",
    ];

    [Fact]
    public void PublishedSchemasAreDraft202012ValidAndEmbedded()
    {
        Assert.Equal(
            [AgentInterchangeV1.PlanningSnapshotSchemaId, AgentInterchangeV1.ChangeProposalSchemaId],
            InterchangeSchemaCatalogue.ApplicationSchemaIds);

        foreach (var id in InterchangeSchemaCatalogue.ApplicationSchemaIds)
        {
            var bytes = InterchangeSchemaCatalogue.GetApplicationSchema(id);
            Assert.True(bytes.Length > 0);
            var validation = InterchangeSchemaCatalogue.ValidateApplicationSchema(id);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        }
    }

    [Fact]
    public void CheckedInExamplesConformAndProposalCoversEveryOperation()
    {
        using var snapshot = ReadExample("planning-snapshot.json");
        using var proposal = ReadExample("change-proposal.json");

        var snapshotValidation = InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            snapshot.RootElement);
        var proposalValidation = InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.ChangeProposalSchemaId,
            proposal.RootElement);

        Assert.True(snapshotValidation.IsValid, string.Join(Environment.NewLine, snapshotValidation.Errors));
        Assert.True(proposalValidation.IsValid, string.Join(Environment.NewLine, proposalValidation.Errors));
        var snapshotRoot = JsonNode.Parse(snapshot.RootElement.GetRawText())!.AsObject();
        var publishedId = Assert.IsType<string>(snapshotRoot["snapshotId"]!.GetValue<string>());
        Assert.True(snapshotRoot.Remove("snapshotId"));
        using var unsignedSnapshot = JsonDocument.Parse(snapshotRoot.ToJsonString());
        var canonical = Rfc8785Canonicalizer.Canonicalize(unsignedSnapshot.RootElement);
        Assert.Equal(
            "sha256:" + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(),
            publishedId);
        Assert.Equal(
            OperationVocabulary,
            proposal.RootElement.GetProperty("operations")
                .EnumerateArray()
                .Select(operation => operation.GetProperty("operation").GetString()));
    }

    [Fact]
    public void PlanningSnapshotGenerationIsCanonicalStableAndDigestVerified()
    {
        var content = CreateSnapshot();

        var first = PlanningSnapshotJsonGenerator.Generate(content);
        var second = PlanningSnapshotJsonGenerator.Generate(content);

        Assert.True(first.Succeeded);
        Assert.True(first.Payload!.Value.Span.SequenceEqual(second.Payload!.Value.Span));
        Assert.Equal(-1, first.Payload.Value.Span.IndexOf((byte)' '));
        using var document = JsonDocument.Parse(first.Payload.Value);
        var validation = InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            document.RootElement);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        var snapshotId = document.RootElement.GetProperty("snapshotId").GetString();
        var withoutId = JsonNode.Parse(first.Payload.Value.Span)!.AsObject();
        Assert.True(withoutId.Remove("snapshotId"));
        using var withoutIdDocument = JsonDocument.Parse(withoutId.ToJsonString());
        var canonical = Rfc8785Canonicalizer.Canonicalize(withoutIdDocument.RootElement);
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        Assert.Equal(expected, snapshotId);
    }

    [Fact]
    public void CanonicalizerUsesUtf16PropertyOrderAndRfc8785Escapes()
    {
        var json = "{\"😀\":2,\"\\r\":\"\\u000f\\n\\\"\\\\\",\"€\":1}"u8;

        var canonical = Rfc8785Canonicalizer.Canonicalize(json);

        Assert.Equal(
            "{\"\\r\":\"\\u000f\\n\\\"\\\\\",\"€\":1,\"😀\":2}",
            Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void ParticipantOrderingUsesUnicodeScalarValuesRatherThanUtf16CodeUnits()
    {
        Assert.True(UnicodeScalarOrdinalComparer.Instance.Compare("\uE000", "😀") < 0);
        Assert.True(StringComparer.Ordinal.Compare("\uE000", "😀") > 0);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("9007199254740992")]
    public void CanonicalizerRejectsNumbersOutsideTheV1IntegerProfile(string number)
    {
        Assert.Throws<InvalidDataException>(() =>
            Rfc8785Canonicalizer.Canonicalize(Encoding.UTF8.GetBytes(number)));
    }

    [Theory]
    [InlineData("9223372036854775807", true)]
    [InlineData("9223372036854775808", false)]
    [InlineData("9999999999999999999", false)]
    [InlineData("01", false)]
    public void RevisionSchemaEnforcesCanonicalSignedInt64Range(string revision, bool expected)
    {
        using var example = ReadExample("planning-snapshot.json");
        var root = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        root["workspaceRevision"] = revision;
        using var candidate = JsonDocument.Parse(root.ToJsonString());

        var validation = InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            candidate.RootElement);

        Assert.Equal(expected, validation.IsValid);
    }

    [Theory]
    [InlineData("planning-snapshot.json", "format", "unknown-format")]
    [InlineData("planning-snapshot.json", "schemaVersion", 2)]
    [InlineData("change-proposal.json", "format", "unknown-format")]
    [InlineData("change-proposal.json", "schemaVersion", 2)]
    public void SchemasRejectUnknownFormatsAndVersions(
        string fileName,
        string propertyName,
        object replacement)
    {
        using var example = ReadExample(fileName);
        var root = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        root[propertyName] = JsonValue.Create(replacement);
        using var candidate = JsonDocument.Parse(root.ToJsonString());

        var validation = InterchangeSchemaCatalogue.Validate(
            fileName == "planning-snapshot.json"
                ? AgentInterchangeV1.PlanningSnapshotSchemaId
                : AgentInterchangeV1.ChangeProposalSchemaId,
            candidate.RootElement);

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void SchemasRejectUnknownMembers()
    {
        using var snapshotExample = ReadExample("planning-snapshot.json");
        var snapshot = JsonNode.Parse(snapshotExample.RootElement.GetRawText())!.AsObject();
        snapshot["unknown"] = true;
        using var invalidSnapshot = JsonDocument.Parse(snapshot.ToJsonString());

        using var proposalExample = ReadExample("change-proposal.json");
        var proposal = JsonNode.Parse(proposalExample.RootElement.GetRawText())!.AsObject();
        var operations = proposal["operations"]!.AsArray();
        operations[0]!["unknown"] = true;
        using var invalidProposal = JsonDocument.Parse(proposal.ToJsonString());

        Assert.False(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            invalidSnapshot.RootElement).IsValid);
        Assert.False(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.ChangeProposalSchemaId,
            invalidProposal.RootElement).IsValid);
    }

    [Fact]
    public void ValidationDiagnosticsStayFixedAndBounded()
    {
        using var example = ReadExample("planning-snapshot.json");
        var snapshot = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        for (var index = 0; index < 250; index++)
            snapshot[$"unknown-{index:D3}"] = true;
        using var candidate = JsonDocument.Parse(snapshot.ToJsonString());

        var validation = InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            candidate.RootElement);

        Assert.False(validation.IsValid);
        Assert.Equal(["Schema validation failed."], validation.Errors);
    }

    [Fact]
    public void ProposalSchemaRejectsAmbiguousReferences()
    {
        using var example = ReadExample("change-proposal.json");
        var proposal = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        proposal["operations"]!.AsArray()[2]!["category"]!["existingId"] = "category-existing";
        using var candidate = JsonDocument.Parse(proposal.ToJsonString());

        Assert.False(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.ChangeProposalSchemaId,
            candidate.RootElement).IsValid);
    }

    [Fact]
    public void ProposalSchemaRejectsUnknownOperationsAndDuplicateParticipantReferences()
    {
        using var example = ReadExample("change-proposal.json");
        var unknownOperation = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        unknownOperation["operations"]!.AsArray()[0]!["operation"] = "inventTask";
        using var unknownCandidate = JsonDocument.Parse(unknownOperation.ToJsonString());

        var duplicateParticipant = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        var replacement = duplicateParticipant["operations"]!.AsArray()
            .Single(operation => operation!["operation"]!.GetValue<string>() == "replaceTaskParticipants")!;
        var participants = replacement["participants"]!.AsArray();
        participants.Add(JsonNode.Parse(participants[0]!.ToJsonString()));
        using var duplicateCandidate = JsonDocument.Parse(duplicateParticipant.ToJsonString());

        Assert.False(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.ChangeProposalSchemaId,
            unknownCandidate.RootElement).IsValid);
        Assert.False(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.ChangeProposalSchemaId,
            duplicateCandidate.RootElement).IsValid);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void SchemaStringLimitsCountUnicodeScalarValues(int scalarCount, bool expected)
    {
        using var example = ReadExample("change-proposal.json");
        var proposal = JsonNode.Parse(example.RootElement.GetRawText())!.AsObject();
        proposal["operations"]!.AsArray()[0]!["operationId"] = string.Concat(
            Enumerable.Repeat("😀", scalarCount));
        using var candidate = JsonDocument.Parse(proposal.ToJsonString());

        Assert.Equal(expected, InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.ChangeProposalSchemaId,
            candidate.RootElement).IsValid);
    }

    [Fact]
    public void OversizedGenerationAndCompositionReturnNoPartialPayload()
    {
        var content = CreateSnapshot() with
        {
            Tasks =
            [
                CreateSnapshot().Tasks[0] with
                {
                    Description = new string('x', AgentInterchangeV1.MaximumOutputBytes),
                },
            ],
        };

        var generated = PlanningSnapshotJsonGenerator.Generate(content);
        var composed = InterchangePayloadComposer.Compose(
            new byte[AgentInterchangeV1.MaximumOutputBytes],
            new byte[1]);

        Assert.False(generated.Succeeded);
        Assert.Null(generated.Payload);
        Assert.Equal("workspace-too-large-for-v1-interchange", generated.ErrorCode);
        Assert.False(composed.Succeeded);
        Assert.Null(composed.Payload);
    }

    [Fact]
    public void CompositionRejectsInvalidUtf8BeforeProducingOutput()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            InterchangePayloadComposer.Compose(new byte[] { 0xff }));

        Assert.DoesNotContain("ff", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReusableInstructionsContainProviderNeutralContractAndExactPackagedSchemas()
    {
        var result = AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas();

        Assert.True(result.Succeeded);
        var text = Encoding.UTF8.GetString(result.Payload!.Value.Span);
        Assert.Contains("Act as a planning assistant", text, StringComparison.Ordinal);
        Assert.Contains("Return exactly one JSON object", text, StringComparison.Ordinal);
        foreach (var operation in OperationVocabulary)
        {
            Assert.Contains(operation, text, StringComparison.Ordinal);
        }
        Assert.Contains(
            Encoding.UTF8.GetString(InterchangeSchemaCatalogue.GetApplicationSchema(
                AgentInterchangeV1.PlanningSnapshotSchemaId).Span).ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            Encoding.UTF8.GetString(InterchangeSchemaCatalogue.GetApplicationSchema(
                AgentInterchangeV1.ChangeProposalSchemaId).Span).ReplaceLineEndings("\n"),
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Live Planning snapshot", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void CombinedCompositionNormalizesCrlfAndLoneCrSnapshotInput()
    {
        var result = AgentInterchangeDocumentComposer.ComposeWithSnapshot(
            Encoding.UTF8.GetBytes("first\r\nsecond\rthird\nfourth"));

        Assert.True(result.Succeeded);
        var text = Encoding.UTF8.GetString(result.Payload!.Value.Span);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("first\nsecond\nthird\nfourth", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedCompositionNormalizesBeforeApplyingTheCompleteOutputLimit()
    {
        var snapshot = new byte[AgentInterchangeV1.MaximumOutputBytes];
        for (var index = 0; index < snapshot.Length; index += 2)
        {
            snapshot[index] = (byte)'\r';
            snapshot[index + 1] = (byte)'\n';
        }

        var result = AgentInterchangeDocumentComposer.ComposeWithSnapshot(snapshot);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Payload);
        Assert.True(result.Payload.Value.Length < AgentInterchangeV1.MaximumOutputBytes);
        Assert.DoesNotContain((byte)'\r', result.Payload.Value.ToArray());
    }

    [Fact]
    public void SnapshotCompositionSelectsValidJsonOnlyOrDocumentedCombinedOutput()
    {
        var snapshot = CreateSnapshot();

        var snapshotOnly = PlanningInterchangeCopyComposer.ComposeSnapshot(
            snapshot,
            includeAgentInstructionsAndSchema: false);
        var combined = PlanningInterchangeCopyComposer.ComposeSnapshot(
            snapshot,
            includeAgentInstructionsAndSchema: true);

        Assert.True(snapshotOnly.Succeeded);
        using var snapshotDocument = JsonDocument.Parse(snapshotOnly.Payload!.Value);
        Assert.True(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            snapshotDocument.RootElement).IsValid);
        Assert.True(combined.Succeeded);
        var combinedText = Encoding.UTF8.GetString(combined.Payload!.Value.Span);
        Assert.Contains("## Live Planning snapshot", combinedText, StringComparison.Ordinal);
        Assert.Contains(Encoding.UTF8.GetString(snapshotOnly.Payload.Value.Span), combinedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotOnlyCopyIsIndentedValidJsonAndMatchesTheCombinedSnapshot()
    {
        var snapshotOnly = PlanningInterchangeCopyComposer.ComposeSnapshot(
            CreateSnapshot(),
            includeAgentInstructionsAndSchema: false);
        var combined = PlanningInterchangeCopyComposer.ComposeSnapshot(
            CreateSnapshot(),
            includeAgentInstructionsAndSchema: true);

        Assert.True(snapshotOnly.Succeeded);
        var snapshotText = Encoding.UTF8.GetString(snapshotOnly.Payload!.Value.Span);
        Assert.DoesNotContain('\r', snapshotText);
        Assert.Contains('\n', snapshotText);
        Assert.True(snapshotText.Contains(
            "\n  \"format\": \"dot-orbit-planning-snapshot\"",
            StringComparison.Ordinal));
        Assert.True(snapshotText.Contains("\n    {\n      \"id\":", StringComparison.Ordinal));
        using var snapshotDocument = JsonDocument.Parse(snapshotOnly.Payload.Value);
        Assert.True(InterchangeSchemaCatalogue.Validate(
            AgentInterchangeV1.PlanningSnapshotSchemaId,
            snapshotDocument.RootElement).IsValid);

        Assert.True(combined.Succeeded);
        var combinedText = Encoding.UTF8.GetString(combined.Payload!.Value.Span);
        Assert.DoesNotContain('\r', combinedText);
        var liveSnapshotHeading = combinedText.IndexOf("## Live Planning snapshot", StringComparison.Ordinal);
        Assert.True(liveSnapshotHeading >= 0);
        var snapshotStart = combinedText.IndexOf("```json\n", liveSnapshotHeading, StringComparison.Ordinal)
            + "```json\n".Length;
        Assert.True(snapshotStart >= "```json\n".Length);
        var snapshotEnd = combinedText.IndexOf("\n```", snapshotStart, StringComparison.Ordinal);
        Assert.True(snapshotEnd > snapshotStart);
        Assert.True(snapshotText.AsSpan().SequenceEqual(combinedText.AsSpan(snapshotStart, snapshotEnd - snapshotStart)));
    }

    [Fact]
    public void CombinedOutputLimitRejectsCompletePayloadWithoutTruncation()
    {
        var reusable = AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas();
        Assert.True(reusable.Succeeded);
        var descriptionLength = AgentInterchangeV1.MaximumOutputBytes
            - reusable.Payload!.Value.Length
            - 1024;
        var content = CreateSnapshot() with
        {
            Tasks =
            [
                CreateSnapshot().Tasks[0] with
                {
                    Description = new string('x', descriptionLength),
                },
            ],
        };

        var snapshotOnly = PlanningInterchangeCopyComposer.ComposeSnapshot(
            content,
            includeAgentInstructionsAndSchema: false);
        var combined = PlanningInterchangeCopyComposer.ComposeSnapshot(
            content,
            includeAgentInstructionsAndSchema: true);

        Assert.True(snapshotOnly.Succeeded);
        Assert.False(combined.Succeeded);
        Assert.Null(combined.Payload);
        Assert.Equal("workspace-too-large-for-v1-interchange", combined.ErrorCode);
    }

    [Fact]
    public void GenerationRejectsInvalidUnicodeBeforeMaterialisingJson()
    {
        var content = CreateSnapshot() with
        {
            Tasks = [CreateSnapshot().Tasks[0] with { Title = "\ud800" }],
        };

        Assert.Throws<InvalidDataException>(() => PlanningSnapshotJsonGenerator.Generate(content));
    }

    private static PlanningSnapshotContent CreateSnapshot() => new(
        new WorkspaceRevision(42),
        [new("category-b", "Second"), new("category-a", "First")],
        [new("participant-a", "Alex")],
        [
            new(
                "project-a", "Project", "Description", "category-a",
                new DateOnly(2026, 12, 1), PlanningProjectStatus.InProgress,
                new(0, 1), null, 2, ["task-a"]),
        ],
        [
            new(
                "task-a", "project-a", "Task", "Line\nTwo",
                new(PlanningTaskCategoryMode.Inherited, "category-a"),
                null, false, null, ["participant-a"], TodayLane.InProgress),
        ],
        new(["category-b", "category-a"], ["project-a"], ["task-a"]));

    private static JsonDocument ReadExample(string fileName) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "Examples",
            fileName)));
}
