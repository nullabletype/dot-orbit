using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class CategoryIdentityTests
{
    [Fact]
    public void PaletteHasSixteenStableUniqueKeysAndUsesADistinctProjectSequence()
    {
        Assert.Equal(16, IdentityColourPalette.Keys.Count);
        Assert.Equal(16, IdentityColourPalette.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(IdentityColourPalette.DefaultKey, IdentityColourPalette.KeyForPosition(0));
        Assert.Equal(IdentityColourPalette.Keys[15], IdentityColourPalette.KeyForPosition(15));
        Assert.Equal(IdentityColourPalette.DefaultKey, IdentityColourPalette.KeyForPosition(16));
        Assert.Equal("lime", IdentityColourPalette.KeyForProjectPosition(0));
        Assert.NotEqual(IdentityColourPalette.KeyForPosition(0), IdentityColourPalette.KeyForProjectPosition(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityColourPalette.KeyForPosition(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityColourPalette.KeyForProjectPosition(-1));
    }

    [Fact]
    public void UnsupportedPersistedColourKeyIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CategoryIdentity.Create("raw-hex-or-unknown"));

        Assert.Equal("colourKey", exception.ParamName);
    }

    [Fact]
    public void ProjectIdentityRejectsUnsupportedPersistedColourKey()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ProjectIdentity.Create("raw-hex-or-unknown"));

        Assert.Equal("colourKey", exception.ParamName);
    }
}
