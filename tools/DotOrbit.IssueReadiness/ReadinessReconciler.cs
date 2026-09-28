namespace DotOrbit.IssueReadiness;

internal interface IReadinessOutput
{
    void Write(string message);
}

internal sealed class ReadinessReconciler(
    IIssueClient issueClient,
    IReadinessOutput output)
{
    public async Task ReconcileAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var issues = await issueClient.ReadAllAsync(cancellationToken);
        foreach (var plan in ReadinessPlanner.Plan(issues))
        {
            if (plan.Deltas.Count == 0)
            {
                continue;
            }

            output.Write(
                $"issue-readiness: issue=#{plan.IssueNumber} reason={plan.Reason} labels={string.Join(',', plan.Deltas)} dry-run={dryRun.ToString().ToLowerInvariant()}");
            if (dryRun)
            {
                continue;
            }

            foreach (var delta in plan.Deltas.Where(delta => delta.Operation is LabelOperation.Add))
            {
                await issueClient.AddLabelAsync(
                    plan.IssueNumber,
                    delta.Label,
                    cancellationToken);
            }

            foreach (var delta in plan.Deltas.Where(delta => delta.Operation is LabelOperation.Remove))
            {
                await issueClient.RemoveLabelAsync(
                    plan.IssueNumber,
                    delta.Label,
                    cancellationToken);
            }
        }
    }
}
