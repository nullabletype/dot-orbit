using DotOrbit.Desktop;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class NativeSmokeRunnerTests
{
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
}
