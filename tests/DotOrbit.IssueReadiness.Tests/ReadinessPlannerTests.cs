using Xunit;

namespace DotOrbit.IssueReadiness.Tests;

public sealed class ReadinessPlannerTests
{
    [Fact]
    public void PlanPromotesBlockedIssueAndPreservesUnrelatedLabelsWhenAllDependenciesClose()
    {
        var issues = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #2\n- #3"), labels: ["blocked", "enhancement"]),
            IssueFixture.Issue(2, IssueFixture.Body(), isOpen: false, labels: ["done"]),
            IssueFixture.Issue(3, IssueFixture.Body(), isOpen: false, labels: ["done"]),
        };

        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.Equal(10, plan.IssueNumber);
        Assert.Equal("dependencies-closed", plan.Reason);
        Assert.Equal(
            [new LabelDelta(LabelOperation.Add, "ready-for-agent"), new LabelDelta(LabelOperation.Remove, "blocked")],
            plan.Deltas);
    }

    [Fact]
    public void PlanBlocksReadyIssueForOpenDependency()
    {
        var issues = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #2"), labels: ["ready-for-agent"]),
            IssueFixture.Issue(2, IssueFixture.Body(), labels: ["needs-triage"]),
        };

        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.Equal("dependencies-open:2", plan.Reason);
        Assert.Equal(
            [new LabelDelta(LabelOperation.Add, "blocked"), new LabelDelta(LabelOperation.Remove, "ready-for-agent")],
            plan.Deltas);
    }

    [Fact]
    public void PlanRestoresBlockedStateForReopenedDependency()
    {
        var closed = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #2"), labels: ["blocked"]),
            IssueFixture.Issue(2, IssueFixture.Body(), isOpen: false),
        };
        var promoted = Assert.Single(ReadinessPlanner.Plan(closed));
        var reopened = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #2"), labels: ["ready-for-agent"]),
            IssueFixture.Issue(2, IssueFixture.Body()),
        };

        var blocked = Assert.Single(ReadinessPlanner.Plan(reopened));

        Assert.Contains(promoted.Deltas, delta => delta.Operation is LabelOperation.Add && delta.Label == "ready-for-agent");
        Assert.Contains(blocked.Deltas, delta => delta.Operation is LabelOperation.Add && delta.Label == "blocked");
    }

    [Fact]
    public void PlanHasNoLabelWritesForCorrectCurrentState()
    {
        var issues = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #2"), labels: ["blocked", "enhancement"]),
            IssueFixture.Issue(2, IssueFixture.Body(), labels: ["needs-triage"]),
        };

        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.Empty(plan.Deltas);
    }

    [Theory]
    [InlineData("needs-triage")]
    [InlineData("needs-info")]
    [InlineData("ready-for-human")]
    [InlineData("wontfix")]
    [InlineData("enhancement")]
    public void PlanDoesNotManageManualOrUnrelatedState(string label)
    {
        var issue = IssueFixture.Issue(10, IssueFixture.Body(), labels: [label]);

        Assert.Empty(ReadinessPlanner.Plan([issue]));
    }

    [Theory]
    [InlineData("needs-triage")]
    [InlineData("needs-info")]
    [InlineData("ready-for-human")]
    [InlineData("wontfix")]
    public void PlanLeavesManualTriageStateUntouchedWhenManagedLabelIsAlsoPresent(string manualLabel)
    {
        var issue = IssueFixture.Issue(
            10,
            IssueFixture.Body("- #2"),
            labels: ["ready-for-agent", manualLabel]);
        var dependency = IssueFixture.Issue(2, IssueFixture.Body(), labels: ["needs-triage"]);

        Assert.Empty(ReadinessPlanner.Plan([issue, dependency]));
    }

    [Fact]
    public void PlanDoesNotManageClosedCandidate()
    {
        var issue = IssueFixture.Issue(
            10,
            IssueFixture.Body(),
            isOpen: false,
            labels: ["blocked"]);

        Assert.Empty(ReadinessPlanner.Plan([issue]));
    }

    [Fact]
    public void PlanDoesNotManageLegacyIssueWithoutImplementationSliceLabel()
    {
        var legacyBody = IssueFixture.Body().Replace(
            $"### Readiness contract\n\n{IssueSpecificationParser.ContractValue}\n\n",
            string.Empty,
            StringComparison.Ordinal);
        var issue = IssueFixture.Issue(
            10,
            legacyBody,
            isImplementationSlice: false,
            labels: ["ready-for-agent"]);

        Assert.Empty(ReadinessPlanner.Plan([issue]));
    }

    [Fact]
    public void PlanReturnsScopedIssueWithDeletedReadinessContractToNeedsTriage()
    {
        var body = IssueFixture.Body().Replace(
            $"### Readiness contract\n\n{IssueSpecificationParser.ContractValue}\n\n",
            string.Empty,
            StringComparison.Ordinal);
        var issue = IssueFixture.Issue(10, body, labels: ["ready-for-agent"]);

        var plan = Assert.Single(ReadinessPlanner.Plan([issue]));

        Assert.Equal("section-readiness-contract-missing-or-duplicate", plan.Reason);
        Assert.Equal(
            [new LabelDelta(LabelOperation.Add, "needs-triage"), new LabelDelta(LabelOperation.Remove, "ready-for-agent")],
            plan.Deltas);
    }

    [Fact]
    public void PlanReturnsUnsupportedReadinessContractToNeedsTriage()
    {
        var body = IssueFixture.Body().Replace(
            IssueSpecificationParser.ContractValue,
            "dot-orbit-issue-readiness:v2",
            StringComparison.Ordinal);
        var issue = IssueFixture.Issue(10, body, labels: ["ready-for-agent"]);

        var plan = Assert.Single(ReadinessPlanner.Plan([issue]));

        Assert.Equal("readiness-contract-unsupported", plan.Reason);
        Assert.Equal("needs-triage", Assert.Single(plan.Deltas, delta => delta.Operation is LabelOperation.Add).Label);
    }

    [Fact]
    public void PlanReturnsInvalidManagedSpecificationToNeedsTriage()
    {
        var issue = IssueFixture.Issue(
            10,
            IssueFixture.Body(openDecisions: "Choose a format."),
            labels: ["ready-for-agent", "enhancement"]);

        var plan = Assert.Single(ReadinessPlanner.Plan([issue]));

        Assert.Equal("open-decisions-unresolved", plan.Reason);
        Assert.Equal(
            [new LabelDelta(LabelOperation.Add, "needs-triage"), new LabelDelta(LabelOperation.Remove, "ready-for-agent")],
            plan.Deltas);
    }

    [Fact]
    public void PlanFailsClosedForMissingDependency()
    {
        var issue = IssueFixture.Issue(10, IssueFixture.Body("- #404"), labels: ["blocked"]);

        var plan = Assert.Single(ReadinessPlanner.Plan([issue]));

        Assert.Equal("dependency-not-found:404", plan.Reason);
        Assert.Equal("needs-triage", Assert.Single(plan.Deltas, delta => delta.Operation is LabelOperation.Add).Label);
    }

    [Fact]
    public void PlanFailsClosedForSelfDependency()
    {
        var issue = IssueFixture.Issue(10, IssueFixture.Body("- #10"), labels: ["blocked"]);

        var plan = Assert.Single(ReadinessPlanner.Plan([issue]));

        Assert.Equal("dependency-self-reference", plan.Reason);
    }

    [Fact]
    public void PlanFailsClosedForDirectDependencyCycle()
    {
        var issues = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #20"), labels: ["blocked"]),
            IssueFixture.Issue(20, IssueFixture.Body("- #10"), labels: ["needs-triage"]),
        };

        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.StartsWith("dependency-cycle:", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanFailsClosedForTransitiveDependencyCycle()
    {
        var issues = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #20"), labels: ["ready-for-agent"]),
            IssueFixture.Issue(20, IssueFixture.Body("- #30"), labels: ["needs-triage"]),
            IssueFixture.Issue(30, IssueFixture.Body("- #10"), labels: ["needs-info"]),
        };

        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.StartsWith("dependency-cycle:", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanFailsClosedWhenDependencyChainReachesACycle()
    {
        var issues = new[]
        {
            IssueFixture.Issue(10, IssueFixture.Body("- #20"), labels: ["blocked"]),
            IssueFixture.Issue(20, IssueFixture.Body("- #30"), labels: ["needs-triage"]),
            IssueFixture.Issue(30, IssueFixture.Body("- #20"), labels: ["needs-info"]),
        };

        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.Equal("dependency-cycle:20,30,20", plan.Reason);
    }
}
