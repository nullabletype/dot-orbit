using DotOrbit.Desktop;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class NativeSmokeRunnerTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3-rc.1+build.42", "1.2.3-rc.1+build.42", true)]
    [InlineData("1.2.3", "1.2.3+commit", false)]
    [InlineData(null, "1.2.3", false)]
    public void PackageSmokeRequiresTheExactExpectedInformationalVersion(
        string? expected,
        string? actual,
        bool matches)
    {
        Assert.Equal(matches, PackagedWorkspaceSmoke.MatchesExpectedVersion(expected, actual));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("v1.2.3", "v1.2.3", true)]
    [InlineData("v1.2.3", null, false)]
    [InlineData(null, "v1.2.3", false)]
    [InlineData("1.2.3", "1.2.3", false)]
    public void PackageSmokeRequiresTheExactTagDerivedEligibilityStamp(
        string? expected,
        string? actual,
        bool matches) =>
        Assert.Equal(matches, PackagedWorkspaceSmoke.MatchesExpectedReleaseTag(expected, actual));

    [Fact]
    public void PackageSmokeArgumentEnablesWorkspaceAndNativeDesktopChecks()
    {
        var scenario = NativeSmokeScenario.FromArguments(["--package-smoke"]);

        Assert.True(scenario.IsEnabled);
        Assert.True(scenario.ExercisesPackagedWorkspace);
        Assert.Equal(NativeSmokeFailure.None, scenario.Failure);
    }

    [Fact]
    public void PackagedWorkspaceSmokeCreatesReopensAndRemovesSyntheticWorkspace()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dot-orbit-package-smoke-test-{Guid.NewGuid():N}");

        var exitCode = PackagedWorkspaceSmoke.Run(directory);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void PackagedWorkspaceSmokeRestoresCheckedInPortableRecoveryFixture()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dot-orbit-package-portability-test-{Guid.NewGuid():N}");
        var recoveryFixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "portable-current-v13.dotorbit-recovery");

        Assert.True(File.Exists(recoveryFixturePath));

        var exitCode = PackagedWorkspaceSmoke.Run(directory, recoveryFixturePath);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(directory));
    }
}
