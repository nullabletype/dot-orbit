using DotOrbit.Desktop.ApplicationUpdates;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ApplicationUpdatePolicyTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0+build.1", "1.0.0+build.2", 0)]
    [InlineData("999999999999999999999.0.0", "1000000000000000000000.0.0", -1)]
    public void SemanticVersionImplementsSemVerPrecedence(
        string leftText,
        string rightText,
        int expected)
    {
        Assert.True(SemanticVersion.TryParse(leftText, out var left));
        Assert.True(SemanticVersion.TryParse(rightText, out var right));

        Assert.Equal(expected, Math.Sign(left.CompareTo(right)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0 alpha")]
    public void SemanticVersionRejectsNonCanonicalValues(string value) =>
        Assert.False(SemanticVersion.TryParse(value, out _));

    [Fact]
    public void UnstampedDesktopAssemblyIsAnUnsupportedDevelopmentBuild()
    {
        var build = InstalledApplicationBuild.FromAssembly(typeof(App).Assembly);

        Assert.False(build.IsOfficialRelease);
        Assert.Equal(ApplicationReleaseChannel.Development, build.Channel);
    }

    [Theory]
    [InlineData("1.2.3", "v1.2.3", "Stable")]
    [InlineData("1.2.3-rc.1", "v1.2.3-rc.1", "Prerelease")]
    [InlineData("1.2.3", "v1.2.4", "Development")]
    [InlineData("1.2.3+local", null, "Development")]
    public void InstalledBuildRequiresAnExactMatchingReleaseTagStamp(
        string informationalVersion,
        string? releaseTag,
        string expectedChannelName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new($"DotOrbit.Update.Tests.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            [informationalVersion]));
        if (releaseTag is not null)
        {
            assembly.SetCustomAttribute(new(
                typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
                ["DotOrbitReleaseTag", releaseTag]));
        }

        var build = InstalledApplicationBuild.FromAssembly(assembly);

        var expectedChannel = Enum.Parse<ApplicationReleaseChannel>(expectedChannelName);
        Assert.Equal(expectedChannel, build.Channel);
        Assert.Equal(expectedChannel is not ApplicationReleaseChannel.Development, build.IsOfficialRelease);
    }
}
