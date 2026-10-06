using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class CategoryIdentityTests
{
    [Fact]
    public void PaletteHasEightStableUniqueKeysAndCyclesByPersistedPosition()
    {
        Assert.Equal(8, IdentityColourPalette.Keys.Count);
        Assert.Equal(8, IdentityColourPalette.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(IdentityColourPalette.DefaultKey, IdentityColourPalette.KeyForPosition(0));
        Assert.Equal(IdentityColourPalette.Keys[7], IdentityColourPalette.KeyForPosition(7));
        Assert.Equal(IdentityColourPalette.DefaultKey, IdentityColourPalette.KeyForPosition(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityColourPalette.KeyForPosition(-1));
    }

    [Fact]
    public void UnsupportedPersistedColourKeyIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CategoryIdentity.Create("raw-hex-or-unknown"));

        Assert.Equal("colourKey", exception.ParamName);
    }
}
