using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class WorkspaceSetupRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyCreationPassphraseIsRequired(string? value)
    {
        var result = WorkspacePassphrase.Create(value, value);

        Assert.False(result.IsValid);
        Assert.Equal(PassphraseValidationError.Required, result.Error);
        Assert.Null(result.Passphrase);
    }

    [Fact]
    public void CanonicallyEquivalentConfirmationIsAcceptedAsNfc()
    {
        var result = WorkspacePassphrase.Create("Cafe\u0301 passphrase", "Caf\u00e9 passphrase");

        Assert.True(result.IsValid);
        Assert.Equal(PassphraseValidationError.None, result.Error);
        Assert.NotNull(result.Passphrase);
        Assert.True(result.Passphrase.Matches("Caf\u00e9 passphrase"));
        Assert.Equal(15, result.Passphrase.CharacterCount);
    }

    [Fact]
    public void ElevenTextElementsIsRejected()
    {
        var passphrase = string.Concat(Enumerable.Repeat("\U0001F642", 11));

        var result = WorkspacePassphrase.Create(passphrase, passphrase);

        Assert.False(result.IsValid);
        Assert.Equal(PassphraseValidationError.TooShort, result.Error);
        Assert.Null(result.Passphrase);
    }

    [Fact]
    public void TwelveTextElementsWithoutCompositionRulesIsAccepted()
    {
        var passphrase = string.Concat(Enumerable.Repeat("\U0001F642", 12));

        var result = WorkspacePassphrase.Create(passphrase, passphrase);

        Assert.True(result.IsValid);
        Assert.Equal(12, result.Passphrase?.CharacterCount);
    }

    [Theory]
    [InlineData("abcdefghijkl", "ABCDEFGHIJKL")]
    [InlineData("abcdefghijkl", "\uff41bcdefghijkl")]
    [InlineData(" abcdefghijk", "abcdefghijkl ")]
    public void NonCanonicalDifferenceDoesNotMatch(
        string passphrase,
        string confirmation)
    {
        var result = WorkspacePassphrase.Create(passphrase, confirmation);

        Assert.False(result.IsValid);
        Assert.Equal(PassphraseValidationError.ConfirmationDoesNotMatch, result.Error);
    }

    [Fact]
    public void UnlockNormalisesWithoutApplyingCreationLengthRule()
    {
        var passphrase = WorkspacePassphrase.ForUnlock("Cafe\u0301");

        Assert.NotNull(passphrase);
        Assert.True(passphrase.Matches("Caf\u00e9"));
    }

    [Fact]
    public void PassphraseStringRepresentationIsAlwaysRedacted()
    {
        var passphrase = WorkspacePassphrase.ForUnlock("do not disclose this");

        Assert.NotNull(passphrase);
        Assert.Equal("[REDACTED]", passphrase.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFirstCategoryIsRejected(string? input)
    {
        var result = CategoryName.Create(input);

        Assert.False(result.IsValid);
        Assert.Equal(CategoryNameValidationError.Required, result.Error);
        Assert.Null(result.CategoryName);
    }

    [Fact]
    public void FirstCategoryOuterWhitespaceIsTrimmedWhileSpellingIsPreserved()
    {
        var result = CategoryName.Create("  Personal Admin  ");

        Assert.True(result.IsValid);
        Assert.Equal(CategoryNameValidationError.None, result.Error);
        Assert.Equal("Personal Admin", result.CategoryName?.Value);
    }
}
