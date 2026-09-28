using Xunit;

namespace DotOrbit.IssueReadiness.Tests;

public sealed class ReadinessOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string?> Environment =
        new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GITHUB_API_URL"] = "https://api.github.test",
            ["GITHUB_TOKEN"] = "test-token",
        };

    [Fact]
    public void ParseReadsEnvironmentAndEnablesDryRun()
    {
        var options = ReadinessOptions.Parse(
            ["--dry-run"],
            name => Environment.GetValueOrDefault(name));

        Assert.Equal("owner/repository", options.Repository);
        Assert.Equal("https://api.github.test/", options.ApiUrl);
        Assert.Equal("test-token", options.Token);
        Assert.True(options.DryRun);
    }

    [Theory]
    [InlineData("--write")]
    [InlineData("--dry-run --extra")]
    public void ParseRejectsUnsupportedArguments(string arguments)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ReadinessOptions.Parse(
                arguments.Split(' '),
                name => Environment.GetValueOrDefault(name)));

        Assert.Equal("invalid-arguments", exception.Message);
    }

    [Fact]
    public void ParseRejectsMissingRequiredEnvironment()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ReadinessOptions.Parse([], _ => null));

        Assert.Equal("missing-environment:GITHUB_REPOSITORY", exception.Message);
    }
}
