using DotOrbit.Verification;
using Xunit;

namespace DotOrbit.Verification.Tests;

public sealed class VerificationGateTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void EvidenceOptionsRequireAFullShaAndEvidenceMode()
    {
        Assert.Null(VerificationOptions.Parse(["--expected-sha", Commit]));
        Assert.Null(VerificationOptions.Parse(["--evidence", "--expected-sha", "abc123"]));

        var options = VerificationOptions.Parse(["--evidence", "--expected-sha", Commit.ToUpperInvariant()]);

        Assert.NotNull(options);
        Assert.True(options.Evidence);
        Assert.Equal(Commit, options.ExpectedSha);
    }

    [Fact]
    public async Task SuccessfulEvidenceRunEmitsVerifiedCommitAndUsesOrderedPhases()
    {
        var repository = new FakeRepositoryInspector(
            new RepositoryState(Commit, true),
            new RepositoryState(Commit, true));
        var runner = FakeProcessRunner.Successful();
        var output = new FakeOutput();

        var result = await CreateGate(repository, runner, output)
            .RunAsync(new VerificationOptions(true, Commit), CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(
            ["restore", "vulnerability-audit", "deprecation-audit", "format", "build", "test", "native-smoke", "native-smoke-failure=startup", "native-smoke-failure=navigation"],
            runner.Requests.Select(RequestIdentity));
        Assert.All(runner.Requests, request => Assert.Equal("/snapshot", request.WorkingDirectory));
        Assert.Contains($"verify: result=verified commit={Commit}", output.Messages);
    }

    [Fact]
    public async Task DirtyPreflightRefusesEvidenceWithoutRunningPhases()
    {
        var repository = new FakeRepositoryInspector(new RepositoryState(Commit, false));
        var runner = FakeProcessRunner.Successful();
        var output = new FakeOutput();

        var result = await CreateGate(repository, runner, output)
            .RunAsync(new VerificationOptions(true, Commit), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(runner.Requests);
        Assert.Contains($"verify: repository commit={Commit} state=dirty", output.Messages);
        Assert.Contains(output.Messages, message => message.StartsWith("verify: environment os=", StringComparison.Ordinal));
        Assert.DoesNotContain(output.Messages, message => message.Contains("result=verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpectedShaMismatchRefusesEvidenceWithoutRunningPhases()
    {
        var repository = new FakeRepositoryInspector(new RepositoryState(Commit, true));
        var runner = FakeProcessRunner.Successful();
        var output = new FakeOutput();

        var result = await CreateGate(repository, runner, output)
            .RunAsync(new VerificationOptions(true, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(false, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "dirty-worktree")]
    [InlineData(true, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "head-changed")]
    public async Task PostflightRepositoryChangeRefusesEvidence(bool clean, string finalCommit, string reason)
    {
        var repository = new FakeRepositoryInspector(
            new RepositoryState(Commit, true),
            new RepositoryState(finalCommit, clean));
        var runner = FakeProcessRunner.Successful();
        var output = new FakeOutput();

        var result = await CreateGate(repository, runner, output)
            .RunAsync(new VerificationOptions(true, Commit), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains(output.Errors, message => message.Contains($"reason={reason}", StringComparison.Ordinal));
        Assert.DoesNotContain(output.Messages, message => message.Contains("result=verified", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("restore", 1)]
    [InlineData("vulnerability-audit", 2)]
    [InlineData("deprecation-audit", 3)]
    [InlineData("format", 4)]
    [InlineData("build", 5)]
    [InlineData("test", 6)]
    [InlineData("native-smoke", 7)]
    public async Task PositivePhaseFailureStopsTheGate(string failingIdentity, int expectedRequestCount)
    {
        var runner = FakeProcessRunner.Successful(failingIdentity, new ProcessResult(31, "", ""));
        var output = new FakeOutput();

        var result = await CreateGate(new FakeRepositoryInspector(), runner, output)
            .RunAsync(new VerificationOptions(false, null), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(expectedRequestCount, runner.Requests.Count);
        Assert.DoesNotContain(output.Messages, message => message.Contains("result=verified", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("native-smoke-failure=startup", 0)]
    [InlineData("native-smoke-failure=startup", 20)]
    [InlineData("native-smoke-failure=startup", 21)]
    [InlineData("native-smoke-failure=navigation", 0)]
    [InlineData("native-smoke-failure=navigation", 20)]
    [InlineData("native-smoke-failure=navigation", 21)]
    public async Task NegativeControlRequiresItsExactSentinel(string identity, int exitCode)
    {
        var runner = FakeProcessRunner.Successful(identity, new ProcessResult(exitCode, "", ""));

        var result = await CreateGate(new FakeRepositoryInspector(), runner, new FakeOutput())
            .RunAsync(new VerificationOptions(false, null), CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task SmokeTimeoutFailsTheGate()
    {
        var runner = FakeProcessRunner.Successful(
            "native-smoke",
            new ProcessResult(-1, "", "", TimedOut: true));

        var result = await CreateGate(new FakeRepositoryInspector(), runner, new FakeOutput())
            .RunAsync(new VerificationOptions(false, null), CancellationToken.None);

        Assert.Equal(1, result);
    }

    [Fact]
    public void PackageAuditRequiresValidJsonWithNoReportedPackages()
    {
        const string clean = """{"version":1,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"clean.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[],"transitivePackages":[]}]}]}""";
        const string finding = """{"version":1,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"affected.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"Affected.Package"}]}]}]}""";

        Assert.True(VerificationGate.PackageAuditPassed(new ProcessResult(0, clean, ""), "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(new ProcessResult(0, finding, ""), "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(new ProcessResult(0, "{\"projects\":[]}", ""), "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(
            new ProcessResult(0, "{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[{\"path\":\"incomplete.csproj\"}]}", ""),
            "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(
            new ProcessResult(0, "{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"problems\":[\"audit failed\"],\"projects\":[{\"path\":\"clean.csproj\",\"frameworks\":[{\"topLevelPackages\":[]}]}]}", ""),
            "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(
            new ProcessResult(0, "{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[{\"path\":\"malformed.csproj\",\"frameworks\":[{\"topLevelPackages\":[],\"transitivePackages\":\"invalid\"}]}]}", ""),
            "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(new ProcessResult(0, "not json", ""), "--vulnerable"));
        Assert.False(VerificationGate.PackageAuditPassed(new ProcessResult(1, clean, ""), "--vulnerable"));
    }

    [Fact]
    public async Task EvidenceRunAttemptsSnapshotCleanupWhenAPhaseThrows()
    {
        var repository = new FakeRepositoryInspector(new RepositoryState(Commit, true));
        var workspaces = new FakeWorkspaceProvider();
        var output = new FakeOutput();
        var gate = new VerificationGate("/repo", repository, workspaces, new ThrowingProcessRunner(), output);

        var result = await gate.RunAsync(new VerificationOptions(true, Commit), CancellationToken.None);

        Assert.Equal(1, result);
        Assert.True(workspaces.RemoveCalled);
        Assert.Contains("verify: result=failed reason=unexpected-error", output.Errors);
        Assert.DoesNotContain(output.Errors, message => message.Contains("simulated", StringComparison.Ordinal));
    }

    private static string RequestIdentity(ProcessRequest request) =>
        request.Arguments.Contains("--vulnerable", StringComparer.Ordinal)
            ? "vulnerability-audit"
            : request.Arguments.Contains("--deprecated", StringComparer.Ordinal)
                ? "deprecation-audit"
                : request.Arguments.First(argument =>
            argument is "restore" or "format" or "build" or "test"
            || argument.StartsWith("--native-smoke", StringComparison.Ordinal))
        .TrimStart('-');

    private static VerificationGate CreateGate(
        IRepositoryInspector repository,
        IProcessRunner runner,
        IVerificationOutput output) =>
        new("/repo", repository, new FakeWorkspaceProvider(), runner, output);

    private sealed class FakeWorkspaceProvider : IVerificationWorkspaceProvider
    {
        public bool RemoveCalled { get; private set; }

        public Task<string?> CreateAsync(string commit, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("/snapshot");

        public Task<bool> RemoveAsync(string path, CancellationToken cancellationToken)
        {
            RemoveCalled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic process failure.");
    }

    private sealed class FakeRepositoryInspector(params RepositoryState[] states) : IRepositoryInspector
    {
        private readonly Queue<RepositoryState> states = new(states);

        public Task<RepositoryState?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RepositoryState?>(states.Count == 0 ? new RepositoryState(Commit, true) : states.Dequeue());
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly string? failingIdentity;
        private readonly ProcessResult? failure;

        private FakeProcessRunner(string? failingIdentity, ProcessResult? failure)
        {
            this.failingIdentity = failingIdentity;
            this.failure = failure;
        }

        public List<ProcessRequest> Requests { get; } = [];

        public static FakeProcessRunner Successful(string? failingIdentity = null, ProcessResult? failure = null) =>
            new(failingIdentity, failure);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var identity = RequestIdentity(request);
            if (identity == failingIdentity)
            {
                return Task.FromResult(failure!);
            }

            return Task.FromResult(identity switch
            {
                "vulnerability-audit" or "deprecation-audit" =>
                    new ProcessResult(
                        0,
                        identity == "vulnerability-audit"
                            ? "{\"version\":1,\"parameters\":\"--vulnerable --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[{\"path\":\"clean.csproj\",\"frameworks\":[{\"topLevelPackages\":[]}]}]}"
                            : "{\"version\":1,\"parameters\":\"--deprecated --include-transitive\",\"sources\":[\"https://api.nuget.org/v3/index.json\"],\"projects\":[{\"path\":\"clean.csproj\"}]}",
                        ""),
                "native-smoke-failure=startup" => new ProcessResult(20, "native-smoke: phase=startup result=failed code=20", ""),
                "native-smoke-failure=navigation" => new ProcessResult(21, "native-smoke: phase=navigation-assertion result=failed code=21", ""),
                "native-smoke" => new ProcessResult(0, "native-smoke: phase=complete result=passed shutdown=requested", ""),
                _ => new ProcessResult(0, "", ""),
            });
        }
    }

    private sealed class FakeOutput : IVerificationOutput
    {
        public List<string> Messages { get; } = [];
        public List<string> Errors { get; } = [];
        public void Write(string message) => Messages.Add(message);
        public void WriteError(string message) => Errors.Add(message);
    }
}
