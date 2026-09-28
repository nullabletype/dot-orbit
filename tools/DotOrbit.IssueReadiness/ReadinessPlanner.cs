namespace DotOrbit.IssueReadiness;

internal static class ReadinessPlanner
{
    public static IReadOnlyList<ReadinessPlan> Plan(IReadOnlyCollection<IssueSnapshot> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var byNumber = issues.ToDictionary(issue => issue.Number);
        var dependencyGraph = issues.ToDictionary(
            issue => issue.Number,
            issue => IssueSpecificationParser.ParseDependenciesOnly(issue.Body));
        var plans = new List<ReadinessPlan>();

        foreach (var issue in issues.OrderBy(issue => issue.Number))
        {
            if (!issue.IsOpen || !IsManaged(issue))
            {
                continue;
            }

            var specification = IssueSpecificationParser.Parse(issue.Body);
            if (!specification.IsValid)
            {
                plans.Add(CreatePlan(
                    issue,
                    ReadinessLabels.NeedsTriage,
                    specification.FailureReason!));
                continue;
            }

            var dependencies = specification.Specification!.Dependencies;
            var invalidReason = ValidateDependencies(issue.Number, dependencies, byNumber, dependencyGraph);
            if (invalidReason is not null)
            {
                plans.Add(CreatePlan(issue, ReadinessLabels.NeedsTriage, invalidReason));
                continue;
            }

            var openDependencies = dependencies
                .Where(number => byNumber[number].IsOpen)
                .Order()
                .ToArray();
            plans.Add(openDependencies.Length == 0
                ? CreatePlan(issue, ReadinessLabels.ReadyForAgent, "dependencies-closed")
                : CreatePlan(
                    issue,
                    ReadinessLabels.Blocked,
                    $"dependencies-open:{string.Join(',', openDependencies)}"));
        }

        return plans;
    }

    private static bool IsManaged(IssueSnapshot issue) =>
        IssueSpecificationParser.HasReadinessContractSection(issue.Body)
        && ReadinessLabels.Managed.Any(issue.Labels.Contains)
        && !ReadinessLabels.Manual.Any(issue.Labels.Contains);

    private static string? ValidateDependencies(
        int issueNumber,
        IReadOnlyList<int> dependencies,
        Dictionary<int, IssueSnapshot> issues,
        IReadOnlyDictionary<int, ParseResult> dependencyGraph)
    {
        if (dependencies.Contains(issueNumber))
        {
            return "dependency-self-reference";
        }

        var missing = dependencies.Where(number => !issues.ContainsKey(number)).ToArray();
        if (missing.Length > 0)
        {
            return $"dependency-not-found:{string.Join(',', missing)}";
        }

        return HasCycle(issueNumber, dependencyGraph, [], out var cycle)
            ? $"dependency-cycle:{string.Join(',', cycle)}"
            : null;
    }

    private static bool HasCycle(
        int current,
        IReadOnlyDictionary<int, ParseResult> dependencyGraph,
        List<int> path,
        out IReadOnlyList<int> cycle)
    {
        var repeatedAt = path.IndexOf(current);
        if (repeatedAt >= 0)
        {
            cycle = [.. path.Skip(repeatedAt), current];
            return true;
        }

        path.Add(current);
        if (dependencyGraph.TryGetValue(current, out var parsed) && parsed.IsValid)
        {
            foreach (var dependency in parsed.Specification!.Dependencies)
            {
                if (HasCycle(dependency, dependencyGraph, path, out cycle))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        cycle = [];
        return false;
    }

    private static ReadinessPlan CreatePlan(
        IssueSnapshot issue,
        string desiredLabel,
        string reason)
    {
        var deltas = new List<LabelDelta>();
        if (!issue.Labels.Contains(desiredLabel))
        {
            deltas.Add(new LabelDelta(true, desiredLabel));
        }

        foreach (var label in ReadinessLabels.Managed)
        {
            if (!string.Equals(label, desiredLabel, StringComparison.Ordinal)
                && issue.Labels.Contains(label))
            {
                deltas.Add(new LabelDelta(false, label));
            }
        }

        return new ReadinessPlan(issue.Number, reason, deltas);
    }
}
