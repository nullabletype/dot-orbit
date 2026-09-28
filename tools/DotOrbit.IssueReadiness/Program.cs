using System.Net.Http.Headers;

namespace DotOrbit.IssueReadiness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ReadinessOptions.Parse(args);
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(options.ApiUrl, UriKind.Absolute),
            };
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.Token);
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dot-orbit-issue-readiness/1.0");
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            var client = new GitHubIssueClient(httpClient, options.Repository);
            var reconciler = new ReadinessReconciler(
                client,
                new ConsoleReadinessOutput());
            await reconciler.ReconcileAsync(options.DryRun, CancellationToken.None);
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or HttpRequestException
                or InvalidOperationException
                or UriFormatException)
        {
            Console.Error.WriteLine($"issue-readiness: result=failed reason={exception.Message}");
            return 1;
        }
    }
}

internal sealed record ReadinessOptions(
    string Repository,
    string ApiUrl,
    string Token,
    bool DryRun)
{
    public static ReadinessOptions Parse(string[] args)
    {
        return Parse(args, Environment.GetEnvironmentVariable);
    }

    internal static ReadinessOptions Parse(
        string[] args,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        var dryRun = args.Length == 1 && string.Equals(args[0], "--dry-run", StringComparison.Ordinal);
        if (args.Length != 0 && !dryRun)
        {
            throw new ArgumentException("invalid-arguments");
        }

        return new(
            ReadRequiredEnvironmentVariable("GITHUB_REPOSITORY", readEnvironmentVariable),
            EnsureTrailingSlash(ReadRequiredEnvironmentVariable("GITHUB_API_URL", readEnvironmentVariable)),
            ReadRequiredEnvironmentVariable("GITHUB_TOKEN", readEnvironmentVariable),
            dryRun);
    }

    private static string ReadRequiredEnvironmentVariable(
        string name,
        Func<string, string?> readEnvironmentVariable)
    {
        var value = readEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"missing-environment:{name}")
            : value;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : $"{value}/";
}

internal sealed class ConsoleReadinessOutput : IReadinessOutput
{
    public void Write(string message) => Console.WriteLine(message);
}
