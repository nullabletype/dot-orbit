namespace DotOrbit.IssueReadiness.Tests;

internal static class IssueFixture
{
    public static string Body(
        string blockedBy = "None",
        string openDecisions = "None",
        string? omitSection = null,
        string? blankSection = null,
        string extra = "")
    {
        var sections = new Dictionary<string, string>
        {
            ["Readiness contract"] = IssueSpecificationParser.ContractValue,
            ["User-visible outcome"] = "A maintainer can rely on the workflow.",
            ["Acceptance criteria"] = "- [ ] The transition is deterministic.",
            ["Non-goals"] = "Project automation.",
            ["Domain and decision context"] = "See docs/agents/issue-tracker.md.",
            ["Known constraints"] = "No untrusted content is executed.",
            ["Verification"] = "Run the focused tests.",
            ["Open decisions"] = openDecisions,
            ["Blocked by"] = blockedBy,
        };
        if (omitSection is not null)
        {
            sections.Remove(omitSection);
        }

        if (blankSection is not null)
        {
            sections[blankSection] = "   ";
        }

        return string.Join(
            "\n\n",
            sections.Select(section => $"### {section.Key}\n\n{section.Value}")) + extra;
    }

    public static IssueSnapshot Issue(
        int number,
        string body,
        bool isOpen = true,
        bool isImplementationSlice = true,
        params string[] labels) =>
        new(
            number,
            body,
            labels
                .Concat(isImplementationSlice ? [ReadinessLabels.ImplementationSlice] : [])
                .ToHashSet(StringComparer.Ordinal),
            isOpen);
}
