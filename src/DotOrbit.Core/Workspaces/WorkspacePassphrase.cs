using System.Globalization;
using System.Text;

namespace DotOrbit.Core.Workspaces;

public enum PassphraseValidationError
{
    None,
    Required,
    TooShort,
    ConfirmationDoesNotMatch,
}

public sealed class PassphraseValidationResult
{
    private PassphraseValidationResult(
        WorkspacePassphrase? passphrase,
        PassphraseValidationError error)
    {
        Passphrase = passphrase;
        Error = error;
    }

    public bool IsValid => Error == PassphraseValidationError.None;

    public WorkspacePassphrase? Passphrase { get; }

    public PassphraseValidationError Error { get; }

    internal static PassphraseValidationResult Success(WorkspacePassphrase passphrase) =>
        new(passphrase, PassphraseValidationError.None);

    internal static PassphraseValidationResult Failure(PassphraseValidationError error) =>
        new(null, error);
}

public sealed class WorkspacePassphrase
{
    public const int MinimumCharacterCount = 12;

    public const int RecommendedCharacterCount = 16;

    private readonly string _normalisedValue;

    private WorkspacePassphrase(string normalisedValue)
    {
        _normalisedValue = normalisedValue;
        CharacterCount = StringInfo.ParseCombiningCharacters(normalisedValue).Length;
    }

    public int CharacterCount { get; }

    public static PassphraseValidationResult Create(string? passphrase, string? confirmation)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return PassphraseValidationResult.Failure(PassphraseValidationError.Required);
        }

        var candidate = new WorkspacePassphrase(Normalise(passphrase));
        if (candidate.CharacterCount < MinimumCharacterCount)
        {
            return PassphraseValidationResult.Failure(PassphraseValidationError.TooShort);
        }

        if (confirmation is null || !candidate.Matches(confirmation))
        {
            return PassphraseValidationResult.Failure(
                PassphraseValidationError.ConfirmationDoesNotMatch);
        }

        return PassphraseValidationResult.Success(candidate);
    }

    public static WorkspacePassphrase? ForUnlock(string? passphrase) =>
        string.IsNullOrEmpty(passphrase)
            ? null
            : new WorkspacePassphrase(Normalise(passphrase));

    public bool Matches(string candidate) =>
        string.Equals(_normalisedValue, Normalise(candidate), StringComparison.Ordinal);

    public TResult Use<TResult>(Func<string, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action(_normalisedValue);
    }

    public override string ToString() => "[REDACTED]";

    private static string Normalise(string value) => value.Normalize(NormalizationForm.FormC);
}
