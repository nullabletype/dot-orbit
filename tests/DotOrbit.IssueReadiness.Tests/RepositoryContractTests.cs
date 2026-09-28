using Xunit;

namespace DotOrbit.IssueReadiness.Tests;

public sealed class RepositoryContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void IssueFormStartsAtNeedsTriageAndContainsEveryParserHeading()
    {
        var issueForm = Read(".github/ISSUE_TEMPLATE/agent-ready.yml");

        Assert.Contains("labels:\n  - needs-triage", issueForm, StringComparison.Ordinal);
        Assert.DoesNotContain("  - ready-for-agent", issueForm, StringComparison.Ordinal);
        Assert.Contains(IssueSpecificationParser.ContractValue, issueForm, StringComparison.Ordinal);
        foreach (var heading in new[]
                 {
                     "Readiness contract",
                     "User-visible outcome",
                     "Acceptance criteria",
                     "Non-goals",
                     "Domain and decision context",
                     "Known constraints",
                     "Verification",
                     "Open decisions",
                     "Blocked by",
                 })
        {
            Assert.Contains($"label: {heading}", issueForm, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HandoffTemplateContainsEveryResumptionField()
    {
        var template = Read(".github/HANDOFF_TEMPLATE.md");

        foreach (var field in new[]
                 {
                     "Branch:",
                     "Current commit SHA:",
                     "Changed files",
                     "Completed",
                     "Remaining",
                     "Command or manual check",
                     "Blocker",
                     "Next safe action",
                     "Review state:",
                 })
        {
            Assert.Contains(field, template, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PullRequestTemplateContainsReviewAndEvidenceContracts()
    {
        var template = Read(".github/PULL_REQUEST_TEMPLATE.md");

        Assert.Contains("Acceptance criteria and evidence", template, StringComparison.Ordinal);
        Assert.Contains("Accessibility impact", template, StringComparison.Ordinal);
        Assert.Contains("Dependency-baseline impact", template, StringComparison.Ordinal);
        Assert.Contains("Independent review", template, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowSeparatesReadOnlyPreviewFromIssueMutationPermissions()
    {
        var workflow = Read(".github/workflows/issue-readiness.yml");

        Assert.Contains("permissions: {}", workflow, StringComparison.Ordinal);
        Assert.Contains("dry-run:", workflow, StringComparison.Ordinal);
        Assert.Contains("issues: read", workflow, StringComparison.Ordinal);
        Assert.Contains("reconcile:", workflow, StringComparison.Ordinal);
        Assert.Contains("issues: write", workflow, StringComparison.Ordinal);
        Assert.Contains("--dry-run", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event.issue.body", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentGuidanceDoesNotRetainCandidateSequenceOrOpenQuestionGate()
    {
        var agentLoop = Read("docs/development/agent-loop.md");
        var repositorySetup = Read("docs/planning/repository-setup.md");

        Assert.DoesNotContain("Suggested first issue sequence", agentLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("These are candidate slices", agentLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("Resolve the remaining", repositorySetup, StringComparison.Ordinal);
        Assert.Contains("docs/agents/issue-tracker.md", agentLoop, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotOrbit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository-root-not-found");
    }
}
