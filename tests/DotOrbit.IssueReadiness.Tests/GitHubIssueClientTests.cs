using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DotOrbit.IssueReadiness.Tests;

public sealed class GitHubIssueClientTests
{
    [Fact]
    public async Task ReadAllAsyncFollowsPaginationAndExcludesPullRequests()
    {
        var handler = new RecordingHandler(
            Response(
                """
                [
                  {"number":1,"body":"first","state":"open","labels":[{"name":"blocked"}]},
                  {"number":99,"body":"pull request","state":"open","labels":[],"pull_request":{"url":"ignored"}}
                ]
                """,
                "<https://api.github.test/repos/owner/repo/issues?state=all&per_page=100&page=2>; rel=\"next\""),
            Response(
                """
                [{"number":2,"body":null,"state":"closed","labels":[{"name":"done"}]}]
                """));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubIssueClient(httpClient, "owner/repo");

        var issues = await client.ReadAllAsync(CancellationToken.None);

        Assert.Equal([1, 2], issues.Select(issue => issue.Number));
        Assert.True(issues[0].IsOpen);
        Assert.Contains("blocked", issues[0].Labels);
        Assert.False(issues[1].IsOpen);
        Assert.Equal(string.Empty, issues[1].Body);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LabelOperationsUseTargetedGitHubEndpoints()
    {
        var handler = new RecordingHandler(Response("[]"), Response("{}"));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubIssueClient(httpClient, "owner/repo");

        await client.AddLabelAsync(12, "ready-for-agent", CancellationToken.None);
        await client.RemoveLabelAsync(12, "blocked state", CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("repos/owner/repo/issues/12/labels", request.Uri);
                Assert.Equal("{\"labels\":[\"ready-for-agent\"]}", request.Body);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal("repos/owner/repo/issues/12/labels/blocked%20state", request.Uri);
                Assert.Equal(string.Empty, request.Body);
            });
    }

    [Fact]
    public async Task PullRequestDependencyIsExcludedAndFailsClosedAsMissing()
    {
        var body = JsonSerializer.Serialize(IssueFixture.Body("- #99"));
        var responseBody =
            "[{\"number\":10,\"body\":" + body
            + ",\"state\":\"open\",\"labels\":[{\"name\":\"blocked\"}]},"
            + "{\"number\":99,\"body\":\"pull request\",\"state\":\"open\","
            + "\"labels\":[],\"pull_request\":{\"url\":\"ignored\"}}]";
        var handler = new RecordingHandler(Response(responseBody));
        using var httpClient = CreateHttpClient(handler);
        var client = new GitHubIssueClient(httpClient, "owner/repo");

        var issues = await client.ReadAllAsync(CancellationToken.None);
        var plan = Assert.Single(ReadinessPlanner.Plan(issues));

        Assert.Equal("dependency-not-found:99", plan.Reason);
        Assert.Equal("needs-triage", Assert.Single(plan.Deltas, delta => delta.Add).Label);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.github.test/"),
    };

    private static HttpResponseMessage Response(string json, string? link = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (link is not null)
        {
            response.Headers.Add("Link", link);
        }

        return response;
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.IsAbsoluteUri
                    ? request.RequestUri.PathAndQuery.TrimStart('/')
                    : request.RequestUri.OriginalString,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body);
}
