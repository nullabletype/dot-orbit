using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class ParticipantLabelTests
{
    [Fact]
    public void NormalizeTrimsTheUserLabelWithoutChangingItsIdentityText()
    {
        Assert.Equal("SD", ParticipantLabel.Normalize("  SD  "));
        Assert.Equal("Garden pal", ParticipantLabel.Normalize("Garden pal"));
    }

    [Theory]
    [InlineData("SD", "sd")]
    [InlineData("  Garden pal  ", "GARDEN PAL")]
    [InlineData("ＳＤ", "sd")]
    public void ComparisonKeyUsesTrimmedCompatibilityNormalizedInvariantCase(string left, string right)
    {
        Assert.Equal(ParticipantLabel.ComparisonKey(left), ParticipantLabel.ComparisonKey(right));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void NormalizeRejectsMissingLabels(string label)
    {
        Assert.Throws<ArgumentException>(() => ParticipantLabel.Normalize(label));
    }
}
