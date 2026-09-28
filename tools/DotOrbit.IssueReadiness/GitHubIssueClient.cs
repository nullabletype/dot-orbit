using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotOrbit.IssueReadiness;

internal interface IIssueClient
{
    Task<IReadOnlyList<IssueSnapshot>> ReadAllAsync(CancellationToken cancellationToken);

    Task AddLabelAsync(int issueNumber, string label, CancellationToken cancellationToken);

    Task RemoveLabelAsync(int issueNumber, string label, CancellationToken cancellationToken);
}

internal sealed class GitHubIssueClient(
    HttpClient httpClient,
    string repository) : IIssueClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<IssueSnapshot>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        var issues = new List<IssueSnapshot>();
        var next = $"repos/{repository}/issues?state=all&per_page=100&page=1";
        while (next is not null)
        {
            using var response = await httpClient.GetAsync(next, cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<IssueResponse[]>(
                JsonOptions,
                cancellationToken) ?? [];
            issues.AddRange(page
                .Where(issue => issue.PullRequest is null)
                .Select(issue => new IssueSnapshot(
                    issue.Number,
                    issue.Body ?? string.Empty,
                    issue.Labels.Select(label => label.Name).ToHashSet(StringComparer.Ordinal),
                    string.Equals(issue.State, "open", StringComparison.Ordinal))));
            next = ReadNextLink(response);
        }

        return issues;
    }

    public async Task AddLabelAsync(
        int issueNumber,
        string label,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"repos/{repository}/issues/{issueNumber}/labels",
            new { labels = new[] { label } },
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveLabelAsync(
        int issueNumber,
        string label,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync(
            $"repos/{repository}/issues/{issueNumber}/labels/{Uri.EscapeDataString(label)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string? ReadNextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in string.Join(',', values).Split(','))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length >= 2
                && segments.Skip(1).Any(segment =>
                    string.Equals(segment, "rel=\"next\"", StringComparison.Ordinal)))
            {
                return segments[0].Trim().Trim('<', '>');
            }
        }

        return null;
    }

    private sealed record IssueResponse(
        int Number,
        string? Body,
        string State,
        LabelResponse[] Labels,
        [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);

    private sealed record LabelResponse(string Name);
}
