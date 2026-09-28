using System.Globalization;
using System.Text.RegularExpressions;

namespace DotOrbit.IssueReadiness;

internal static partial class IssueSpecificationParser
{
    public const string ContractValue = "dot-orbit-issue-readiness:v1";

    internal static readonly IReadOnlyList<string> RequiredHeadings =
    [
        "Readiness contract",
        "User-visible outcome",
        "Acceptance criteria",
        "Non-goals",
        "Domain and decision context",
        "Known constraints",
        "Verification",
        "Open decisions",
        "Blocked by",
    ];

    public static ParseResult Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var sections = ReadSections(body);
        foreach (var heading in RequiredHeadings)
        {
            if (!sections.TryGetValue(heading, out var values) || values.Count != 1)
            {
                return ParseResult.Invalid($"section-{Slug(heading)}-missing-or-duplicate");
            }

            if (string.IsNullOrWhiteSpace(values[0]))
            {
                return ParseResult.Invalid($"section-{Slug(heading)}-blank");
            }
        }

        if (!string.Equals(
                sections["Readiness contract"][0].Trim(),
                ContractValue,
                StringComparison.Ordinal))
        {
            return ParseResult.Invalid("readiness-contract-unsupported");
        }

        if (!string.Equals(
                sections["Open decisions"][0].Trim(),
                "None",
                StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Invalid("open-decisions-unresolved");
        }

        return ParseDependencies(sections["Blocked by"][0]);
    }

    public static ParseResult ParseDependenciesOnly(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var sections = ReadSections(body);
        if (!sections.TryGetValue("Blocked by", out var values) || values.Count != 1)
        {
            return ParseResult.Invalid("section-blocked-by-missing-or-duplicate");
        }

        return ParseDependencies(values[0]);
    }

    private static ParseResult ParseDependencies(string content)
    {
        var trimmed = content.Trim();
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Valid([]);
        }

        var dependencies = new SortedSet<int>();
        foreach (var line in trimmed.Split('\n'))
        {
            var match = DependencyLine().Match(line.TrimEnd('\r'));
            if (!match.Success
                || !int.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var issueNumber)
                || issueNumber <= 0)
            {
                return ParseResult.Invalid("blocked-by-malformed");
            }

            dependencies.Add(issueNumber);
        }

        return dependencies.Count == 0
            ? ParseResult.Invalid("blocked-by-malformed")
            : ParseResult.Valid([.. dependencies]);
    }

    private static Dictionary<string, List<string>> ReadSections(string body)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? heading = null;
        var content = new List<string>();

        void CompleteSection()
        {
            if (heading is null)
            {
                return;
            }

            if (!sections.TryGetValue(heading, out var values))
            {
                values = [];
                sections.Add(heading, values);
            }

            values.Add(string.Join('\n', content).Trim());
            content.Clear();
        }

        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var headingPrefixLength = line.StartsWith("### ", StringComparison.Ordinal)
                ? 4
                : line.StartsWith("## ", StringComparison.Ordinal)
                    ? 3
                    : 0;
            if (headingPrefixLength > 0)
            {
                CompleteSection();
                heading = line[headingPrefixLength..].Trim();
                continue;
            }

            if (heading is not null)
            {
                content.Add(line);
            }
        }

        CompleteSection();
        return sections;
    }

    private static string Slug(string heading) =>
        heading.ToLowerInvariant().Replace(' ', '-');

    [GeneratedRegex(@"^-\s+#([0-9]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DependencyLine();
}
