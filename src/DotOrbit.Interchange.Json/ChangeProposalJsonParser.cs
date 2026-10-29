using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Interchange.Json;

public sealed record ChangeProposalParseResult(ChangeProposal? Proposal, string? ErrorCode)
{
    public bool IsValid => Proposal is not null;
    internal ReadOnlyMemory<byte> CanonicalDigest { get; init; }
    public override string ToString() => IsValid ? "Valid proposal." : $"Rejected proposal: {ErrorCode}.";
}

/// <summary>Parses the closed versioned wire formats without resolving references or changing workspace data.</summary>
public static class ChangeProposalJsonParser
{
    public const int MaximumInputBytes = 4 * 1024 * 1024;
    public const int MaximumDepth = 32;
    public const int MaximumTokens = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ChangeProposalParseResult Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            if (StrictUtf8.GetByteCount(input) > MaximumInputBytes) return Failure("input-limit");
            return Parse(StrictUtf8.GetBytes(input));
        }
        catch (EncoderFallbackException) { return Failure("invalid-unicode"); }
    }

    public static ChangeProposalParseResult Parse(ReadOnlyMemory<byte> input)
    {
        if (input.Length > MaximumInputBytes) return Failure("input-limit");
        input = input.ToArray();
        if (input.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Failure("byte-order-mark");
        try
        {
            _ = StrictUtf8.GetCharCount(input.Span);
            var failure = InspectTokens(input.Span);
            if (failure is not null) return Failure(failure);
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failure("invalid-json");
            if (!root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)
                || number is not (AgentInterchangeV1.SchemaVersion or AgentInterchangeV2.SchemaVersion)) return Failure("unsupported-version");
            if (!root.TryGetProperty("format", out var formatElement)
                || formatElement.ValueKind != JsonValueKind.String
                || formatElement.GetString() is not { } format)
                return Failure("invalid-schema");
            var kind = format switch
            {
                AgentInterchangeV1.ChangeProposalFormat => ProposalKind.WorkspaceChange,
                AgentInterchangeV1.NewWorkProposalFormat => ProposalKind.NewWork,
                _ => (ProposalKind?)null,
            };
            if (kind is null)
                return Failure("invalid-schema");
            var schemaId = (number, kind) switch
            {
                (1, ProposalKind.NewWork) => AgentInterchangeV1.NewWorkProposalSchemaId,
                (1, _) => AgentInterchangeV1.ChangeProposalSchemaId,
                (2, ProposalKind.NewWork) => AgentInterchangeV2.NewWorkProposalSchemaId,
                _ => AgentInterchangeV2.ChangeProposalSchemaId,
            };
            if (!InterchangeSchemaCatalogue.Validate(schemaId, root).IsValid)
                return Failure("invalid-schema");
            var operations = root.GetProperty("operations").EnumerateArray().Select(ReadOperation).ToArray();
            return new(new(
                kind.Value, format, number,
                kind == ProposalKind.WorkspaceChange ? Text(root, "sourceSnapshotId") : null,
                kind == ProposalKind.WorkspaceChange
                    ? new WorkspaceRevision(long.Parse(Text(root, "sourceWorkspaceRevision"), CultureInfo.InvariantCulture))
                    : null,
                Array.AsReadOnly(operations)), null)
            {
                CanonicalDigest = SHA256.HashData(Rfc8785Canonicalizer.Canonicalize(root)),
            };
        }
        catch (DecoderFallbackException) { return Failure("invalid-utf8"); }
        catch (JsonException) { return Failure("invalid-json"); }
        catch (OverflowException) { return Failure("invalid-value"); }
        catch (FormatException) { return Failure("invalid-value"); }
        catch (ArgumentException) { return Failure("invalid-value"); }
        catch (InvalidOperationException) { return Failure("invalid-value"); }
    }

    private static string? InspectTokens(ReadOnlySpan<byte> input)
    {
        var reader = new Utf8JsonReader(input, new JsonReaderOptions { MaxDepth = MaximumDepth });
        var objects = new Stack<HashSet<string>>();
        var tokens = 0;
        while (reader.Read())
        {
            if (++tokens > MaximumTokens) return "token-limit";
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                case JsonTokenType.String:
                    // GetString replaces malformed escaped UTF-16 on some runtimes; inspect escapes first.
                    if (!ValidEscapedSurrogates(reader.ValueSpan)) return "invalid-unicode";
                    var value = reader.GetString()!;
                    if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(value))
                        return "duplicate-member";
                    break;
                case JsonTokenType.Number:
                    foreach (var digit in reader.ValueSpan)
                        if (digit is (byte)'.' or (byte)'e' or (byte)'E') return "invalid-number";
                    break;
            }
        }
        return null;
    }

    private static bool ValidEscapedSurrogates(ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\') continue;
            if (value[++index] != 'u') continue;
            var code = Hex(value.Slice(index + 1, 4));
            index += 4;
            if (code is >= 0xdc00 and <= 0xdfff) return false;
            if (code is < 0xd800 or > 0xdbff) continue;
            if (index + 6 >= value.Length || value[index + 1] != '\\' || value[index + 2] != 'u') return false;
            var low = Hex(value.Slice(index + 3, 4));
            if (low is < 0xdc00 or > 0xdfff) return false;
            index += 6;
        }
        return true;
    }

    private static int Hex(ReadOnlySpan<byte> value)
    {
        var result = 0;
        foreach (var digit in value)
            result = (result * 16) + (digit <= '9' ? digit - '0' : (digit | 32) - 'a' + 10);
        return result;
    }

    private static ChangeProposalParseResult Failure(string code) => new(null, code);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static ProposalReference Reference(JsonElement value) => value.TryGetProperty("existingId", out var id)
        ? ProposalReference.Existing(id.GetString()!) : ProposalReference.Local(Text(value, "localId"));
    private static ProposalReference Ref(JsonElement value, string name) => Reference(value.GetProperty(name));
    private static DateOnly? Date(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : DateOnly.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static OptionalChange<string> StringChange(JsonElement value, string name) => value.TryGetProperty(name, out var field)
        ? new(true, field.GetString()!) : default;
    private static OptionalChange<DateOnly?> DateChange(JsonElement value, string name) => value.TryGetProperty(name, out var field)
        ? new(true, Date(field)) : default;
    private static System.Collections.ObjectModel.ReadOnlyCollection<ProposalReference> Participants(JsonElement value) =>
        Array.AsReadOnly(value.GetProperty("participants").EnumerateArray().Select(Reference).ToArray());
    private static ProposedTaskCategory Category(JsonElement value) => Text(value, "mode") == "inherit"
        ? new(ProposedTaskCategoryMode.Inherit) : new(ProposedTaskCategoryMode.Explicit, Ref(value, "category"));
    private static AttachmentCategoryChoice? Choice(JsonElement value) => !value.TryGetProperty("categoryChoice", out var choice)
        ? null : choice.GetString() == "adoptProjectCategory" ? AttachmentCategoryChoice.AdoptProjectCategory
            : AttachmentCategoryChoice.PreserveAsOverride;
    private static ProjectTaskPlacement Placement(JsonElement value)
    {
        if (value.TryGetProperty("first", out _)) return new(ProjectTaskPlacementKind.First);
        if (value.TryGetProperty("last", out _)) return new(ProjectTaskPlacementKind.Last);
        return value.TryGetProperty("before", out var before)
            ? new(ProjectTaskPlacementKind.Before, Reference(before))
            : new(ProjectTaskPlacementKind.After, Ref(value, "after"));
    }

    private static TodayLane? Today(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null : value.GetString() switch
        {
            "planned" => TodayLane.Planned,
            "inProgress" => TodayLane.InProgress,
            "none" => null,
            _ => throw new InvalidOperationException("Invalid Today lane."),
        };

    private static ChangeOperation ReadOperation(JsonElement value)
    {
        var id = Text(value, "operationId");
        var explanation = value.TryGetProperty("explanation", out var text) ? text.GetString() : null;
        return Text(value, "operation") switch
        {
            "createCategory" => new CreateCategoryOperation(id, Text(value, "localId"), Text(value, "name"), explanation),
            "createParticipant" => new CreateParticipantOperation(id, Text(value, "localId"), Text(value, "label"), explanation),
            "createProject" => new CreateProjectOperation(id, Text(value, "localId"), Text(value, "title"), Text(value, "description"), Ref(value, "category"), Date(value.GetProperty("targetDate")), explanation),
            "editProject" => new EditProjectOperation(id, Ref(value, "project"), ProjectChanges(value.GetProperty("changes")), explanation),
            "setProjectCategory" => new SetProjectCategoryOperation(id, Ref(value, "project"), Ref(value, "category"), explanation),
            "createTask" => new CreateTaskOperation(id, Text(value, "localId"), Text(value, "title"), Text(value, "description"), value.GetProperty("project").ValueKind == JsonValueKind.Null ? null : Ref(value, "project"), Category(value.GetProperty("category")), Date(value.GetProperty("dueDate")), Participants(value), explanation, value.TryGetProperty("todayLane", out var lane) ? Today(lane) : null),
            "setTaskToday" => new SetTaskTodayOperation(id, Ref(value, "task"), Today(value.GetProperty("todayLane")), explanation),
            "editTask" => new EditTaskOperation(id, Ref(value, "task"), TaskChanges(value.GetProperty("changes")), explanation),
            "setTaskCategory" => new SetTaskCategoryOperation(id, Ref(value, "task"), Category(value.GetProperty("category")), explanation),
            "replaceTaskParticipants" => new ReplaceTaskParticipantsOperation(id, Ref(value, "task"), Participants(value), explanation),
            "attachTask" => new AttachTaskOperation(id, Ref(value, "task"), Ref(value, "project"), Choice(value), explanation),
            "detachTask" => new DetachTaskOperation(id, Ref(value, "task"), explanation),
            "moveTask" => new MoveTaskOperation(id, Ref(value, "task"), Ref(value, "project"), Choice(value), explanation),
            "completeTask" => new CompleteTaskOperation(id, Ref(value, "task"), explanation),
            "reopenTask" => new ReopenTaskOperation(id, Ref(value, "task"), explanation),
            "reorderProjectTask" => new ReorderProjectTaskOperation(id, Ref(value, "task"), Placement(value.GetProperty("placement")), explanation),
            "archiveProject" => new ArchiveProjectOperation(id, Ref(value, "project"), explanation),
            "archiveTask" => new ArchiveTaskOperation(id, Ref(value, "task"), explanation),
            "moveProjectToBin" => new MoveProjectToBinOperation(id, Ref(value, "project"), explanation),
            "moveTaskToBin" => new MoveTaskToBinOperation(id, Ref(value, "task"), explanation),
            _ => throw new InvalidOperationException("Invalid operation."),
        };
    }

    private static ProjectContentChanges ProjectChanges(JsonElement value) => new(
        StringChange(value, "title"), StringChange(value, "description"), DateChange(value, "targetDate"));
    private static TaskContentChanges TaskChanges(JsonElement value) => new(
        StringChange(value, "title"), StringChange(value, "description"), DateChange(value, "dueDate"));
}
