using DotOrbit.Core.Interchange;
using Xunit;

namespace DotOrbit.Interchange.Json.Tests;

public sealed class ChangeProposalJsonParserTests
{
    private const string Empty = "{\"format\":\"dot-orbit-change-proposal\",\"schemaVersion\":1,\"sourceSnapshotId\":\"sha256:" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sourceWorkspaceRevision\":\"1\",\"operations\":[]}";
    private const string EmptyNewWork =
        "{\"format\":\"dot-orbit-new-work-proposal\",\"schemaVersion\":1,\"operations\":[]}";

    [Fact]
    public void ParsesNewWorkWithoutAWorkspaceSource()
    {
        var path = Path.Combine(FindRoot(), "examples/interchange/v1/new-work-proposal.json");

        var result = ChangeProposalJsonParser.Parse(File.ReadAllBytes(path));

        Assert.True(result.IsValid, result.ErrorCode);
        Assert.Equal(ProposalKind.NewWork, result.Proposal!.Kind);
        Assert.Null(result.Proposal.SourceSnapshotId);
        Assert.Null(result.Proposal.SourceWorkspaceRevision);
        Assert.Collection(
            result.Proposal.Operations,
            operation => Assert.IsType<CreateCategoryOperation>(operation),
            operation => Assert.IsType<CreateParticipantOperation>(operation),
            operation => Assert.IsType<CreateProjectOperation>(operation),
            operation => Assert.IsType<CreateTaskOperation>(operation));
    }

    [Theory]
    [InlineData("\"sourceWorkspaceRevision\":\"1\",")]
    [InlineData("\"sourceSnapshotId\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",")]
    public void NewWorkRejectsWorkspaceSourceFields(string member) =>
        Assert.Equal(
            "invalid-schema",
            ChangeProposalJsonParser.Parse("{" + member + EmptyNewWork[1..]).ErrorCode);

    [Fact]
    public void NewWorkRejectsExistingReferencesAndNonCreateOperations()
    {
        var existingReference = EmptyNewWork.Replace(
            "[]",
            "[{\"operationId\":\"o\",\"operation\":\"createProject\",\"localId\":\"p\",\"title\":\"P\",\"description\":\"\",\"category\":{\"existingId\":\"c\"},\"targetDate\":null}]",
            StringComparison.Ordinal);
        var edit = EmptyNewWork.Replace(
            "[]",
            "[{\"operationId\":\"o\",\"operation\":\"editTask\",\"task\":{\"localId\":\"t\"},\"changes\":{\"title\":\"Changed\"}}]",
            StringComparison.Ordinal);

        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(existingReference).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(edit).ErrorCode);
    }

    [Fact]
    public void ParsesAllPublishedOperationTypes()
    {
        var path = Path.Combine(FindRoot(), "examples/interchange/v1/change-proposal.json");
        var result = ChangeProposalJsonParser.Parse(File.ReadAllBytes(path));
        Assert.True(result.IsValid, result.ErrorCode);
        Assert.Equal(19, result.Proposal!.Operations.Count);
        Assert.Equal(ProposalKind.WorkspaceChange, result.Proposal.Kind);
        Assert.Equal(19, result.Proposal.Operations.Select(operation => operation.GetType()).Distinct().Count());
    }

    [Fact]
    public void PreservesOmittedAndExplicitNullChanges()
    {
        var input = Empty.Replace("[]", "[{\"operationId\":\"edit\",\"operation\":\"editTask\",\"task\":{\"existingId\":\"t\"},\"changes\":{\"dueDate\":null}}]", StringComparison.Ordinal);
        var result = ChangeProposalJsonParser.Parse(input);
        var operation = Assert.IsType<EditTaskOperation>(Assert.Single(result.Proposal!.Operations));
        Assert.False(operation.Changes.Title.IsSpecified);
        Assert.True(operation.Changes.DueDate.IsSpecified);
        Assert.Null(operation.Changes.DueDate.Value);
    }

    [Theory]
    [InlineData("1.0", "invalid-number")]
    [InlineData("1e0", "invalid-number")]
    [InlineData("0", "unsupported-version")]
    [InlineData("3", "unsupported-version")]
    public void RejectsUnsupportedIntegerVersions(string version, string code) =>
        Assert.Equal(code, ChangeProposalJsonParser.Parse(Empty.Replace("\"schemaVersion\":1", $"\"schemaVersion\":{version}", StringComparison.Ordinal)).ErrorCode);

    [Theory]
    [InlineData("\"format\":\"x\",", "duplicate-member")]
    [InlineData("\"f\\u006frmat\":\"x\",", "duplicate-member")]
    [InlineData("\"unknown\":\"private-marker\",", "invalid-schema")]
    [InlineData("\"unknown\":\"\\ud800\",", "invalid-unicode")]
    [InlineData("\"unknown\":\"\\udfff\",", "invalid-unicode")]
    public void RejectsUnsafeMembersWithoutEchoingContent(string member, string code)
    {
        var result = ChangeProposalJsonParser.Parse("{" + member + Empty[1..]);
        Assert.Equal(code, result.ErrorCode);
        Assert.Null(result.Proposal);
        Assert.DoesNotContain("private-marker", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsSurrogatePairsAndEscapedBackslashes()
    {
        var input = Empty.Replace("[]", "[{\"operationId\":\"o\",\"operation\":\"createCategory\",\"localId\":\"c\",\"name\":\"\\ud83d\\ude00\\\\ud800\"}]", StringComparison.Ordinal);
        Assert.True(ChangeProposalJsonParser.Parse(input).IsValid);
    }

    [Fact]
    public void BoundsBytesDepthTokensAndStrictEncoding()
    {
        Assert.True(ChangeProposalJsonParser.Parse(Empty + new string(' ', ChangeProposalJsonParser.MaximumInputBytes - Empty.Length)).IsValid);
        Assert.Equal("input-limit", ChangeProposalJsonParser.Parse(new byte[ChangeProposalJsonParser.MaximumInputBytes + 1]).ErrorCode);
        Assert.Equal("invalid-utf8", ChangeProposalJsonParser.Parse(new byte[] { 0xff }).ErrorCode);
        Assert.Equal("byte-order-mark", ChangeProposalJsonParser.Parse(new byte[] { 0xef, 0xbb, 0xbf }).ErrorCode);
        Assert.Equal("invalid-unicode", ChangeProposalJsonParser.Parse("\ud800").ErrorCode);
        Assert.Equal("invalid-json", ChangeProposalJsonParser.Parse(new string('[', 33) + new string(']', 33)).ErrorCode);
        Assert.Equal("invalid-json", ChangeProposalJsonParser.Parse("[" + string.Join(',', Enumerable.Repeat("null", 99_998)) + "]").ErrorCode);
        Assert.Equal("token-limit", ChangeProposalJsonParser.Parse("[" + string.Join(',', Enumerable.Repeat("null", 99_999)) + "]").ErrorCode);
    }

    [Theory]
    [InlineData("{} {}")]
    [InlineData("{/*comment*/}")]
    [InlineData("{\"x\":1,}")]
    [InlineData("[]")]
    public void RejectsNonStrictDocuments(string input) => Assert.False(ChangeProposalJsonParser.Parse(input).IsValid);

    [Fact]
    public void RejectsMissingFormatWithoutThrowing()
    {
        var result = ChangeProposalJsonParser.Parse("{\"schemaVersion\":1,\"operations\":[]}");

        Assert.Equal("invalid-schema", result.ErrorCode);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public void DigestIsCanonicalAndResultFormattingIsSafe()
    {
        var first = ChangeProposalJsonParser.Parse(Empty);
        var second = ChangeProposalJsonParser.Parse(Empty.Replace("{", "{ ", StringComparison.Ordinal));
        Assert.True(first.IsValid);
        Assert.Equal(first.CanonicalDigest.ToArray(), second.CanonicalDigest.ToArray());
        Assert.Equal(32, first.CanonicalDigest.Length);
        Assert.Equal("Valid proposal.", first.ToString());
    }

    [Theory]
    [InlineData("operationId", 128)]
    [InlineData("localId", 128)]
    [InlineData("name", 1024)]
    [InlineData("explanation", 65536)]
    public void EnforcesUnicodeScalarFieldLimits(string field, int maximum)
    {
        var operation = "{\"operationId\":\"o\",\"operation\":\"createCategory\",\"localId\":\"c\",\"name\":\"n\",\"explanation\":\"e\"}";
        var node = System.Text.Json.Nodes.JsonNode.Parse(operation)!;
        node[field] = string.Concat(Enumerable.Repeat("😀", maximum));
        Assert.True(ChangeProposalJsonParser.Parse(WithOperations(node.ToJsonString())).IsValid);
        node[field] = string.Concat(Enumerable.Repeat("😀", maximum + 1));
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(WithOperations(node.ToJsonString())).ErrorCode);
    }

    [Fact]
    public void EnforcesOperationAndAssociationLimits()
    {
        const string operation = "{\"operationId\":\"o\",\"operation\":\"completeTask\",\"task\":{\"existingId\":\"t\"}}";
        Assert.True(ChangeProposalJsonParser.Parse(WithOperations(string.Join(',', Enumerable.Repeat(operation, 512)))).IsValid);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(WithOperations(string.Join(',', Enumerable.Repeat(operation, 513)))).ErrorCode);
        foreach (var count in new[] { 256, 257 })
        {
            var references = string.Join(',', Enumerable.Range(0, count).Select(index => $"{{\"existingId\":\"p{index}\"}}"));
            var input = WithOperations("{\"operationId\":\"o\",\"operation\":\"replaceTaskParticipants\",\"task\":{\"existingId\":\"t\"},\"participants\":[" + references + "]}");
            Assert.Equal(count == 256, ChangeProposalJsonParser.Parse(input).IsValid);
        }
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("0000-01-01")]
    public void RejectsInvalidCalendarDates(string date)
    {
        var operation = "{\"operationId\":\"o\",\"operation\":\"editTask\",\"task\":{\"existingId\":\"t\"},\"changes\":{\"dueDate\":\"" + date + "\"}}";
        Assert.False(ChangeProposalJsonParser.Parse(WithOperations(operation)).IsValid);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("deleteTask")]
    [InlineData("renameCategory")]
    [InlineData("setToday")]
    [InlineData("restoreTask")]
    public void RejectsUnknownAndProhibitedOperations(string operation) =>
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(WithOperations("{\"operationId\":\"o\",\"operation\":\"" + operation + "\"}")).ErrorCode);

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("99999999999999999999")]
    public void RejectsInvalidRevisions(string revision) =>
        Assert.False(ChangeProposalJsonParser.Parse(Empty.Replace("\"sourceWorkspaceRevision\":\"1\"", "\"sourceWorkspaceRevision\":\"" + revision + "\"", StringComparison.Ordinal)).IsValid);

    [Fact]
    public void SyntheticArbitraryInputsNeverEscapeAsExceptions()
    {
        var random = new Random(129);
        for (var index = 0; index < 500; index++)
        {
            var bytes = new byte[random.Next(0, 1024)];
            random.NextBytes(bytes);
            Assert.False(ChangeProposalJsonParser.Parse(bytes).IsValid);
        }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void RejectsNonFiniteNumbers(string number) =>
        Assert.False(ChangeProposalJsonParser.Parse(Empty.Replace("\"schemaVersion\":1", "\"schemaVersion\":" + number, StringComparison.Ordinal)).IsValid);

    [Fact]
    public void EnforcesDescriptionLimitAndSnapshotIdentity()
    {
        foreach (var count in new[] { 65536, 65537 })
        {
            var input = WithOperations("{\"operationId\":\"o\",\"operation\":\"editTask\",\"task\":{\"existingId\":\"t\"},\"changes\":{\"description\":\"" + new string('a', count) + "\"}}");
            Assert.Equal(count == 65536, ChangeProposalJsonParser.Parse(input).IsValid);
        }
        Assert.False(ChangeProposalJsonParser.Parse(Empty.Replace("sha256:", "sha512:", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void ParsedResultDoesNotRetainCallerOwnedInput()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Empty);
        var result = ChangeProposalJsonParser.Parse(bytes);
        Array.Fill(bytes, (byte)'x');
        Assert.True(result.IsValid);
        Assert.Equal(AgentInterchangeV1.ChangeProposalFormat, result.Proposal!.Format);
        Assert.Equal(32, result.CanonicalDigest.Length);
    }

    private static string WithOperations(string operations) => Empty.Replace("[]", "[" + operations + "]", StringComparison.Ordinal);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "schemas"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
}
