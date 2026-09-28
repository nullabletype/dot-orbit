using Xunit;

namespace DotOrbit.IssueReadiness.Tests;

public sealed class ReadinessReconcilerTests
{
    [Fact]
    public async Task ReconcileAsyncDryRunReportsDeterministicDeltaWithoutWritesOrContent()
    {
        const string sensitiveBodyMarker = "private-task-content";
        var client = new RecordingIssueClient(
            IssueFixture.Issue(
                8,
                IssueFixture.Body().Replace(
                    "A maintainer can rely on the workflow.",
                    sensitiveBodyMarker,
                    StringComparison.Ordinal),
                labels: ["blocked", "enhancement"]));
        var output = new RecordingOutput();
        var reconciler = CreateReconciler(client, output);

        await reconciler.ReconcileAsync(dryRun: true, CancellationToken.None);

        Assert.Equal(
            ["issue-readiness: issue=#8 reason=dependencies-closed labels=+ready-for-agent,-blocked dry-run=true"],
            output.Messages);
        Assert.DoesNotContain(sensitiveBodyMarker, output.Messages[0], StringComparison.Ordinal);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task ReconcileAsyncAddsReplacementBeforeRemovingPreviousState()
    {
        var client = new RecordingIssueClient(
            IssueFixture.Issue(8, IssueFixture.Body(), labels: ["blocked", "enhancement"]));
        var reconciler = CreateReconciler(client, new RecordingOutput());

        await reconciler.ReconcileAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(["add:8:ready-for-agent", "remove:8:blocked"], client.Writes);
    }

    [Fact]
    public async Task ReconcileAsyncProducesNoWritesOrOutputForRepeatedCorrectState()
    {
        var client = new RecordingIssueClient(
            IssueFixture.Issue(8, IssueFixture.Body(), labels: ["ready-for-agent", "enhancement"]));
        var output = new RecordingOutput();
        var reconciler = CreateReconciler(client, output);

        await reconciler.ReconcileAsync(dryRun: false, CancellationToken.None);

        Assert.Empty(client.Writes);
        Assert.Empty(output.Messages);
    }

    private static ReadinessReconciler CreateReconciler(
        IIssueClient client,
        IReadinessOutput output) =>
        new(client, output);

    private sealed class RecordingIssueClient(params IssueSnapshot[] issues) : IIssueClient
    {
        public List<string> Writes { get; } = [];

        public Task<IReadOnlyList<IssueSnapshot>> ReadAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IssueSnapshot>>(issues);

        public Task AddLabelAsync(
            int issueNumber,
            string label,
            CancellationToken cancellationToken)
        {
            Writes.Add($"add:{issueNumber}:{label}");
            return Task.CompletedTask;
        }

        public Task RemoveLabelAsync(
            int issueNumber,
            string label,
            CancellationToken cancellationToken)
        {
            Writes.Add($"remove:{issueNumber}:{label}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutput : IReadinessOutput
    {
        public List<string> Messages { get; } = [];

        public void Write(string message) => Messages.Add(message);
    }
}
