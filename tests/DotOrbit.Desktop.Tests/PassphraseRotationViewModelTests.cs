using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PassphraseRotationViewModelTests
{
    [Theory]
    [InlineData("", "new portable passphrase", "new portable passphrase", "Enter your current passphrase.", "CurrentPassphraseTextBox")]
    [InlineData("correct horse battery", "short", "short", "Use at least 12 characters for the new passphrase.", "NewPassphraseTextBox")]
    [InlineData("correct horse battery", "new portable passphrase", "different confirmation", "New passphrase confirmation does not match.", "ConfirmationTextBox")]
    [InlineData("correct horse battery", "correct horse battery", "correct horse battery", "Choose a new passphrase that is different from the current passphrase.", "NewPassphraseTextBox")]
    public void ValidationStopsBeforeStorageAndFocusesTheFieldThatNeedsCorrection(
        string current,
        string next,
        string confirmation,
        string expectedMessage,
        string expectedFocus)
    {
        var session = new StubWorkspaceSession();
        var viewModel = new PassphraseRotationViewModel(session, _ => { })
        {
            CurrentPassphrase = current,
            NewPassphrase = next,
            Confirmation = confirmation,
        };
        string? focusTarget = null;
        viewModel.FocusRequested += target => focusTarget = target;

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(expectedMessage, viewModel.ValidationMessage);
        Assert.Equal(expectedFocus, focusTarget);
        Assert.Equal(0, session.RotateCallCount);
    }

    [Fact]
    public void SuccessfulRotationUsesNormalisedValidatedSecretsReplacesSessionAndClearsText()
    {
        var replacement = new StubWorkspaceSession();
        var session = new StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.Rotated(
                replacement,
                "/safe/pre-rotation.dotorbit-recovery"),
        };
        IWorkspaceSession? receivedSession = null;
        var viewModel = new PassphraseRotationViewModel(
            session,
            result => receivedSession = result)
        {
            CurrentPassphrase = "correct horse battery",
            NewPassphrase = "Cafe\u0301 portable phrase",
            Confirmation = "Caf\u00e9 portable phrase",
        };
        var completed = 0;
        viewModel.RotationCompleted += (_, _) => completed++;

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(1, session.RotateCallCount);
        Assert.True(session.LastCurrentPassphrase?.Matches("correct horse battery"));
        Assert.True(session.LastNewPassphrase?.Matches("Caf\u00e9 portable phrase"));
        Assert.Same(replacement, receivedSession);
        Assert.Equal(1, completed);
        Assert.Empty(viewModel.CurrentPassphrase);
        Assert.Empty(viewModel.NewPassphrase);
        Assert.Empty(viewModel.Confirmation);
        Assert.False(viewModel.HasValidationMessage);
    }

    [Fact]
    public void UnverifiedCurrentPassphraseUsesOneNonSensitiveErrorAndClearsSecrets()
    {
        var session = new StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.InvalidCurrentPassphraseOrStore(),
        };
        var viewModel = ValidViewModel(session);
        string? focusTarget = null;
        viewModel.FocusRequested += target => focusTarget = target;

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(
            "The current passphrase or workspace could not be verified.",
            viewModel.ValidationMessage);
        Assert.DoesNotContain("correct horse battery", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.Equal("CurrentPassphraseTextBox", focusTarget);
        Assert.Empty(viewModel.CurrentPassphrase);
        Assert.Empty(viewModel.NewPassphrase);
        Assert.Empty(viewModel.Confirmation);
    }

    [Fact]
    public void RecoveryFailureExplainsThatNoChangeWasMade()
    {
        var session = new StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.RecoveryPointCreationFailed(),
        };
        var viewModel = ValidViewModel(session);

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(
            "The passphrase was not changed because a validated recovery point could not be created.",
            viewModel.ValidationMessage);
        Assert.Equal(1, session.RotateCallCount);
    }

    [Fact]
    public void ReplacementFailureAdoptsTheReopenedCurrentSessionAndAllowsASecondAttempt()
    {
        var continuedSession = new StubWorkspaceSession();
        var session = new StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.Failed(
                continuedSession,
                "/safe/pre-rotation.dotorbit-recovery"),
        };
        IWorkspaceSession? receivedSession = null;
        var viewModel = new PassphraseRotationViewModel(
            session,
            replacement => receivedSession = replacement)
        {
            CurrentPassphrase = "correct horse battery",
            NewPassphrase = "new portable passphrase",
            Confirmation = "new portable passphrase",
        };

        viewModel.SubmitCommand.Execute(null);

        Assert.Same(continuedSession, receivedSession);
        Assert.Equal(
            "The passphrase was not changed. The existing workspace remains available.",
            viewModel.ValidationMessage);
        viewModel.CurrentPassphrase = "correct horse battery";
        viewModel.NewPassphrase = "another portable phrase";
        viewModel.Confirmation = "another portable phrase";
        viewModel.SubmitCommand.Execute(null);
        Assert.Equal(1, continuedSession.RotateCallCount);
    }

    [Fact]
    public void UnavailableWorkspaceDisablesEditingAndRequiresReturnToUnlock()
    {
        var session = new StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.WorkspaceUnavailable(
                "/safe/pre-rotation.dotorbit-recovery"),
        };
        var viewModel = ValidViewModel(session);
        var returnRequests = 0;
        viewModel.WorkspaceUnavailableExitRequested += (_, _) => returnRequests++;

        viewModel.SubmitCommand.Execute(null);

        Assert.True(viewModel.IsWorkspaceUnavailable);
        Assert.False(viewModel.CanEdit);
        Assert.Equal("Return to unlock", viewModel.CancelActionName);
        Assert.Contains("validated recovery point", viewModel.ValidationMessage, StringComparison.Ordinal);
        viewModel.CancelCommand.Execute(null);
        Assert.Equal(1, returnRequests);
    }

    [Fact]
    public void StaleDisposedSessionClearsSecretsAndUsesBoundedFeedback()
    {
        var session = new StubWorkspaceSession { ThrowDisposedOnRotate = true };
        var viewModel = ValidViewModel(session);
        string? focusTarget = null;
        viewModel.FocusRequested += target => focusTarget = target;

        viewModel.SubmitCommand.Execute(null);

        Assert.Empty(viewModel.CurrentPassphrase);
        Assert.Empty(viewModel.NewPassphrase);
        Assert.Empty(viewModel.Confirmation);
        Assert.Equal(
            "The workspace session changed. Close this window and try again.",
            viewModel.ValidationMessage);
        Assert.Equal("CancelPassphraseButton", focusTarget);
    }

    [Fact]
    public void ShowHideAndCancelClearAllSecretsWithoutCallingStorage()
    {
        var session = new StubWorkspaceSession();
        var viewModel = ValidViewModel(session);
        var cancelled = 0;
        viewModel.CancelRequested += (_, _) => cancelled++;

        Assert.NotEqual('\0', viewModel.PasswordCharacter);
        Assert.Equal("Show passphrases", viewModel.PassphraseVisibilityActionName);
        viewModel.TogglePassphraseVisibilityCommand.Execute(null);
        Assert.Equal('\0', viewModel.PasswordCharacter);
        Assert.Equal("Hide passphrases", viewModel.PassphraseVisibilityActionName);

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(1, cancelled);
        Assert.Equal(0, session.RotateCallCount);
        Assert.Empty(viewModel.CurrentPassphrase);
        Assert.Empty(viewModel.NewPassphrase);
        Assert.Empty(viewModel.Confirmation);
    }

    private static PassphraseRotationViewModel ValidViewModel(StubWorkspaceSession session) =>
        new(session, _ => { })
        {
            CurrentPassphrase = "correct horse battery",
            NewPassphrase = "new portable passphrase",
            Confirmation = "new portable passphrase",
        };

    internal sealed class StubWorkspaceSession : IWorkspaceSession
    {
        public int RotateCallCount { get; private set; }

        public WorkspacePassphrase? LastCurrentPassphrase { get; private set; }

        public WorkspacePassphrase? LastNewPassphrase { get; private set; }

        public PassphraseRotationResult RotationResult { get; init; } =
            PassphraseRotationResult.Failed();

        public bool ThrowDisposedOnRotate { get; init; }

        public int SchemaVersion => 6;

        public string FirstCategoryName => "Home";

        public IWorkspaceRecovery Recovery { get; } =
            new RecoveryViewModelTests.StubWorkspaceRecovery();

        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase)
        {
            RotateCallCount++;
            ObjectDisposedException.ThrowIf(
                ThrowDisposedOnRotate,
                typeof(IWorkspaceSession));

            LastCurrentPassphrase = currentPassphrase;
            LastNewPassphrase = newPassphrase;
            return RotationResult;
        }

        public void Dispose()
        {
        }
    }
}
