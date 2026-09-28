using Xunit;

namespace DotOrbit.IssueReadiness.Tests;

public sealed class IssueSpecificationParserTests
{
    [Fact]
    public void ParseAcceptsCompleteSpecificationWithoutDependencies()
    {
        var result = IssueSpecificationParser.Parse(IssueFixture.Body());

        Assert.True(result.IsValid);
        Assert.Empty(result.Specification!.Dependencies);
    }

    [Fact]
    public void ParseAcceptsStructuredIssueUsingLevelTwoHeadings()
    {
        var body = IssueFixture.Body().Replace("### ", "## ", StringComparison.Ordinal);

        var result = IssueSpecificationParser.Parse(body);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("User-visible outcome")]
    [InlineData("Readiness contract")]
    [InlineData("Acceptance criteria")]
    [InlineData("Non-goals")]
    [InlineData("Domain and decision context")]
    [InlineData("Known constraints")]
    [InlineData("Verification")]
    [InlineData("Open decisions")]
    [InlineData("Blocked by")]
    public void ParseFailsClosedWhenMandatedSectionIsMissing(string heading)
    {
        var result = IssueSpecificationParser.Parse(IssueFixture.Body(omitSection: heading));

        Assert.False(result.IsValid);
        Assert.Contains("missing-or-duplicate", result.FailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("User-visible outcome")]
    [InlineData("Readiness contract")]
    [InlineData("Acceptance criteria")]
    [InlineData("Non-goals")]
    [InlineData("Domain and decision context")]
    [InlineData("Known constraints")]
    [InlineData("Verification")]
    [InlineData("Open decisions")]
    [InlineData("Blocked by")]
    public void ParseFailsClosedWhenMandatedSectionIsBlank(string heading)
    {
        var result = IssueSpecificationParser.Parse(IssueFixture.Body(blankSection: heading));

        Assert.False(result.IsValid);
        Assert.Contains("blank", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFailsClosedForUnresolvedOpenDecision()
    {
        var result = IssueSpecificationParser.Parse(IssueFixture.Body(openDecisions: "Choose the API shape."));

        Assert.False(result.IsValid);
        Assert.Equal("open-decisions-unresolved", result.FailureReason);
    }

    [Fact]
    public void ParseFailsClosedForUnsupportedReadinessContract()
    {
        var body = IssueFixture.Body().Replace(
            IssueSpecificationParser.ContractValue,
            "dot-orbit-issue-readiness:v2",
            StringComparison.Ordinal);

        var result = IssueSpecificationParser.Parse(body);

        Assert.False(result.IsValid);
        Assert.Equal("readiness-contract-unsupported", result.FailureReason);
    }

    [Fact]
    public void ParseSortsAndDeduplicatesLocalIssueBullets()
    {
        var result = IssueSpecificationParser.Parse(IssueFixture.Body("- #41\n- #7\n- #41"));

        Assert.True(result.IsValid);
        Assert.Equal([7, 41], result.Specification!.Dependencies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("- nullabletype/dot-orbit#12")]
    [InlineData("- https://github.com/nullabletype/dot-orbit/issues/12")]
    [InlineData("None\n- #12")]
    [InlineData("- #0")]
    [InlineData("- #12 dependency")]
    public void ParseFailsClosedForMalformedDependencySection(string blockedBy)
    {
        var result = IssueSpecificationParser.Parse(IssueFixture.Body(blockedBy));

        Assert.False(result.IsValid);
        Assert.Contains("blocked-by", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseIgnoresIssueReferencesOutsideBlockedBy()
    {
        var body = IssueFixture.Body(extra: "\n\n### Notes\n\nSee #999 and nullabletype/elsewhere#3.");

        var result = IssueSpecificationParser.Parse(body);

        Assert.True(result.IsValid);
        Assert.Empty(result.Specification!.Dependencies);
    }

    [Fact]
    public void ParseFailsClosedForDuplicateBlockedBySection()
    {
        var body = IssueFixture.Body(extra: "\n\n### Blocked by\n\nNone");

        var result = IssueSpecificationParser.Parse(body);

        Assert.False(result.IsValid);
        Assert.Equal("section-blocked-by-missing-or-duplicate", result.FailureReason);
    }
}
