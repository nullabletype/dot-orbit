using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Bebop.JsonSchema;
using DotOrbit.Core.Interchange;

namespace DotOrbit.Interchange.Json;

public sealed record InterchangeSchemaValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

public static class InterchangeSchemaCatalogue
{
    private const string PlanningResourceName =
        "DotOrbit.Interchange.Json.Schemas.planning-snapshot.schema.json";
    private const string ProposalResourceName =
        "DotOrbit.Interchange.Json.Schemas.change-proposal.schema.json";
    private const string NewWorkProposalResourceName =
        "DotOrbit.Interchange.Json.Schemas.new-work-proposal.schema.json";
    private const string MetaSchemaResourcePrefix =
        "DotOrbit.Interchange.Json.MetaSchemas.draft2020-12.";
    private static readonly Dictionary<string, JsonDocument> MetaSchemaDocuments =
        LoadMetaSchemaDocuments();
    private static readonly SchemaRegistry Registry =
        SchemaRegistry.Custom(new EmbeddedSchemaResolver(MetaSchemaDocuments));
    private static readonly ErrorCollection NonCollectingErrors = CreateNonCollectingErrorSink();

    private static readonly Dictionary<string, byte[]> ApplicationSchemaBytes =
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [AgentInterchangeV2.PlanningSnapshotSchemaId] = ReadResource("DotOrbit.Interchange.Json.Schemas.v2.planning-snapshot.schema.json"),
            [AgentInterchangeV2.ChangeProposalSchemaId] = ReadResource("DotOrbit.Interchange.Json.Schemas.v2.change-proposal.schema.json"),
            [AgentInterchangeV2.NewWorkProposalSchemaId] = ReadResource("DotOrbit.Interchange.Json.Schemas.v2.new-work-proposal.schema.json"),
            [AgentInterchangeV1.PlanningSnapshotSchemaId] = ReadResource(PlanningResourceName),
            [AgentInterchangeV1.ChangeProposalSchemaId] = ReadResource(ProposalResourceName),
            [AgentInterchangeV1.NewWorkProposalSchemaId] = ReadResource(NewWorkProposalResourceName),
        };

    private static readonly JsonSchema Draft202012MetaSchema = LoadDraft202012MetaSchema();
    private static readonly Dictionary<string, SchemaEntry> Schemas = BuildSchemas();

    public static IReadOnlyList<string> ApplicationSchemaIds { get; } =
    [
        AgentInterchangeV1.PlanningSnapshotSchemaId,
        AgentInterchangeV1.ChangeProposalSchemaId,
        AgentInterchangeV1.NewWorkProposalSchemaId,
        AgentInterchangeV2.PlanningSnapshotSchemaId,
        AgentInterchangeV2.ChangeProposalSchemaId,
        AgentInterchangeV2.NewWorkProposalSchemaId,
    ];

    public static ReadOnlyMemory<byte> GetApplicationSchema(string schemaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        return ApplicationSchemaBytes.TryGetValue(schemaId, out var schema)
            ? schema
            : throw new ArgumentException("The schema identifier is not in the interchange catalogue.", nameof(schemaId));
    }

    public static InterchangeSchemaValidationResult Validate(
        string schemaId,
        JsonElement instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        if (!Schemas.TryGetValue(schemaId, out var schema))
            throw new ArgumentException("The schema identifier is not in the interchange catalogue.", nameof(schemaId));

        var isValid = schema.Schema.Validate(instance, NonCollectingErrors)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new(
            isValid,
            isValid ? [] : ["Schema validation failed."]);
    }

    public static InterchangeSchemaValidationResult ValidateApplicationSchema(string schemaId)
    {
        _ = GetApplicationSchema(schemaId);
        var schema = Schemas[schemaId];
        var isValid = Draft202012MetaSchema.Validate(schema.Document.RootElement, NonCollectingErrors)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new(
            isValid,
            isValid ? [] : ["Schema validation failed."]);
    }

    private static Dictionary<string, SchemaEntry> BuildSchemas()
    {
        var schemas = new Dictionary<string, SchemaEntry>(StringComparer.Ordinal);
        foreach (var (id, bytes) in ApplicationSchemaBytes)
        {
            EnsureClosedReferences(bytes);
            var document = JsonDocument.Parse(bytes);
            var schema = JsonSchema.Create(document, Registry)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            schema.Prepare().AsTask().GetAwaiter().GetResult();
            schemas.Add(id, new(document, schema));
        }

        return schemas;
    }

    private static JsonSchema LoadDraft202012MetaSchema()
    {
        var document = MetaSchemaDocuments["https://json-schema.org/draft/2020-12/schema"];
        var schema = JsonSchema.Create(document, Registry)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        schema.Prepare().AsTask().GetAwaiter().GetResult();
        return schema;
    }

    private static Dictionary<string, JsonDocument> LoadMetaSchemaDocuments()
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://json-schema.org/draft/2020-12/schema"] = "schema",
            ["https://json-schema.org/draft/2020-12/meta/core"] = "meta.core",
            ["https://json-schema.org/draft/2020-12/meta/applicator"] = "meta.applicator",
            ["https://json-schema.org/draft/2020-12/meta/unevaluated"] = "meta.unevaluated",
            ["https://json-schema.org/draft/2020-12/meta/validation"] = "meta.validation",
            ["https://json-schema.org/draft/2020-12/meta/meta-data"] = "meta.meta-data",
            ["https://json-schema.org/draft/2020-12/meta/format-annotation"] = "meta.format-annotation",
            ["https://json-schema.org/draft/2020-12/meta/content"] = "meta.content",
        };
        return resources.ToDictionary(
            pair => pair.Key,
            pair => JsonDocument.Parse(ReadResource($"{MetaSchemaResourcePrefix}{pair.Value}.json")),
            StringComparer.Ordinal);
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicFields,
        "Bebop.JsonSchema.NopErrorCollection",
        "Bebop.JsonSchema")]
    private static ErrorCollection CreateNonCollectingErrorSink()
    {
        const string typeName = "Bebop.JsonSchema.NopErrorCollection";
        var sinkType = typeof(ErrorCollection).Assembly.GetType(typeName, throwOnError: true)
            ?? throw new InvalidOperationException("The schema validator's non-collecting error sink is unavailable.");
        return sinkType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)
                   ?.GetValue(null) as ErrorCollection
               ?? throw new InvalidOperationException("The schema validator's non-collecting error sink is unavailable.");
    }

    private static void EnsureClosedReferences(byte[] schemaBytes)
    {
        using var document = JsonDocument.Parse(schemaBytes);
        Inspect(document.RootElement);

        static void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "$ref" or "$dynamicRef" or "$schema")
                    {
                        var reference = property.Value.GetString()
                            ?? throw new InvalidDataException("Schema references must be strings.");
                        if (!reference.StartsWith('#')
                            && reference != "https://json-schema.org/draft/2020-12/schema")
                        {
                            throw new InvalidDataException("The interchange schema catalogue is closed.");
                        }
                    }

                    Inspect(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray()) Inspect(child);
            }
        }
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(InterchangeSchemaCatalogue).Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded schema resource '{name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record SchemaEntry(JsonDocument Document, JsonSchema Schema);

    private sealed class EmbeddedSchemaResolver(
        IReadOnlyDictionary<string, JsonDocument> documents) : ISchemaResolver
    {
        public ValueTask<JsonElement?> Resolve(Uri id) =>
            new(documents.TryGetValue(id.AbsoluteUri, out var document)
                ? document.RootElement
                : null);
    }
}
