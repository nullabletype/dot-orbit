using DotOrbit.Core.Interchange;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void RenameValidatesBlankAndCollisionThenRefreshesTaskParticipantLabels()
    {
        var work = new MemoryWorkspaceWork();
        var participant = work.CreateParticipant("SD");
        var other = work.CreateParticipant("AB");
        var task = work.CreateStandaloneTask("Call", "", "home", null,
            new([participant.Id], []));
        var shell = new ShellViewModel(work);
        shell.Work!.SelectTask(task.Id);
        var settings = shell.Settings!;
        var row = settings.Participants.Single(item => item.Id == participant.Id);

        row.BeginRename();
        row.DraftLabel = "  ";
        row.SaveRenameCommand.Execute(null);
        Assert.Equal("Enter a Participant label.", row.ValidationMessage);
        Assert.Equal(row.LabelAutomationId, settings.FocusAutomationId);

        row.DraftLabel = " ab ";
        row.SaveRenameCommand.Execute(null);
        Assert.Equal("A Participant with this label already exists.", row.ValidationMessage);
        Assert.Equal("SD", work.Read().Participants.Single(item => item.Id == participant.Id).Label);
        Assert.Equal("AB", work.Read().Participants.Single(item => item.Id == other.Id).Label);

        row.DraftLabel = "Ste";
        row.SaveRenameCommand.Execute(null);
        Assert.Equal("Ste", work.Read().Participants.Single(item => item.Id == participant.Id).Label);
        Assert.Contains(shell.Work.AvailableParticipants, item => item.Id == participant.Id && item.Label == "Ste");
        Assert.Equal($"settings-participant-rename-{participant.Id}", settings.FocusAutomationId);
    }

    [Fact]
    public void DeleteReportsReferencesAndSuccessfulDeleteFocusesTheNextRow()
    {
        var work = new MemoryWorkspaceWork();
        var referenced = work.CreateParticipant("Referenced");
        var free = work.CreateParticipant("Free");
        work.CreateStandaloneTask("Call", "", "home", null, new([referenced.Id], []));
        var settings = new SettingsViewModel(work, () => { });

        var referencedRow = settings.Participants.Single(item => item.Id == referenced.Id);
        referencedRow.DeleteCommand.Execute(null);
        Assert.Contains("every Task", referencedRow.ValidationMessage, StringComparison.Ordinal);
        Assert.Equal(referencedRow.DeleteAutomationId, settings.FocusAutomationId);

        settings.Participants.Single(item => item.Id == free.Id).DeleteCommand.Execute(null);
        Assert.Single(settings.Participants);
        Assert.Equal(referencedRow.RenameAutomationId, settings.FocusAutomationId);
        Assert.DoesNotContain(free.Id, work.Read().Participants.Select(item => item.Id));
    }

    [Fact]
    public void UsageCopyAndLastRowStateDescribeTheSettingsList()
    {
        var work = new MemoryWorkspaceWork();
        var used = work.CreateParticipant("Used");
        work.CreateParticipant("Free");
        work.CreateStandaloneTask("First", "", "home", null, new([used.Id], []));
        work.CreateStandaloneTask("Second", "", "home", null, new([used.Id], []));

        var settings = new SettingsViewModel(work, () => { });

        Assert.Equal("Used by 2 tasks", settings.Participants[0].UsageText);
        Assert.False(settings.Participants[0].IsLast);
        Assert.Equal("Not in use", settings.Participants[1].UsageText);
        Assert.True(settings.Participants[1].IsLast);
        settings.Participants[0].BeginRename();
        settings.Participants[1].BeginRename();
        Assert.False(settings.Participants[0].IsEditing);
        Assert.True(settings.Participants[1].IsEditing);
    }

    [Fact]
    public void ThemeSelectionAppliesAndAnnouncesOnlyAfterPersistenceSucceeds()
    {
        var themes = new RecordingThemeService(ApplicationTheme.Dark);
        var settings = new SettingsViewModel(new MemoryWorkspaceWork(), () => { }, themes);

        Assert.Equal("Dark", settings.SelectedTheme.Name);
        Assert.Equal("Dark theme selected", settings.ThemeStatus);

        settings.SelectedTheme = settings.ThemeOptions.Single(option => option.Value == ApplicationTheme.Light);

        Assert.Equal(ApplicationTheme.Light, themes.CurrentTheme);
        Assert.Equal("Light", settings.SelectedTheme.Name);
        Assert.Equal("Light theme selected.", settings.Announcement);

        themes.AllowChange = false;
        settings.SelectedTheme = settings.ThemeOptions.Single(option => option.Value == ApplicationTheme.Dark);

        Assert.Equal(ApplicationTheme.Light, themes.CurrentTheme);
        Assert.Equal("Light", settings.SelectedTheme.Name);
        Assert.Contains("previous theme is still active", settings.Announcement, StringComparison.Ordinal);
    }

    [Fact]
    public void AiFeatureToggleAppliesAndAnnouncesOnlyAfterPersistenceSucceeds()
    {
        var preferences = new RecordingAiFeaturePreferenceService();
        var observed = new List<bool>();
        var settings = new SettingsViewModel(
            new MemoryWorkspaceWork(),
            () => { },
            aiFeaturePreferenceService: preferences,
            aiFeaturesChanged: observed.Add);

        Assert.False(settings.AiFeaturesEnabled);
        Assert.Equal("AI features disabled", settings.AiFeaturesStatus);

        settings.AiFeaturesEnabled = true;

        Assert.True(preferences.AiFeaturesEnabled);
        Assert.True(settings.AiFeaturesEnabled);
        Assert.Equal("AI features enabled", settings.AiFeaturesStatus);
        Assert.Equal([true], observed);
        Assert.Equal(
            "AI features enabled. Manual agent interchange is now available.",
            settings.Announcement);

        preferences.AllowChange = false;
        settings.AiFeaturesEnabled = false;

        Assert.True(preferences.AiFeaturesEnabled);
        Assert.True(settings.AiFeaturesEnabled);
        Assert.Equal([true], observed);
        Assert.Contains("previous setting is still active", settings.Announcement, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalMcpIsSubordinateAndAiDisableForcesItOff()
    {
        var preferences = new RecordingAiFeaturePreferenceService();
        var mcpChanges = new List<bool>();
        var settings = new SettingsViewModel(
            new MemoryWorkspaceWork(),
            () => { },
            aiFeaturePreferenceService: preferences,
            localMcpChanged: mcpChanges.Add);

        Assert.False(settings.ShowLocalMcpSettings);
        Assert.False(settings.LocalMcpEnabled);

        settings.AiFeaturesEnabled = true;
        settings.LocalMcpEnabled = true;

        Assert.True(settings.ShowLocalMcpSettings);
        Assert.True(settings.LocalMcpEnabled);
        Assert.Equal([true], mcpChanges);

        settings.AiFeaturesEnabled = false;

        Assert.False(settings.ShowLocalMcpSettings);
        Assert.False(settings.LocalMcpEnabled);
        Assert.Equal("Local MCP disabled", settings.LocalMcpStatus);
    }

    [Fact]
    public void RememberedLocalMcpClientExposesExactTrustAndRevocationAction()
    {
        var settings = new SettingsViewModel(new MemoryWorkspaceWork(), () => { });
        string? revoked = null;
        settings.ReportAuthorizedMcpClients(
        [
            new AgentClientAuthorization(
                "authorization-1",
                new string('a', 64),
                "verified-user",
                "unverified-client",
                "1.2.3",
                AgentClientScopes.ReadInterchangeContext | AgentClientScopes.SubmitProposals,
                DateTimeOffset.UnixEpoch),
        ],
        id => revoked = id);

        var client = Assert.Single(settings.AuthorizedMcpClients);
        Assert.True(settings.HasAuthorizedMcpClients);
        Assert.False(settings.HasNoAuthorizedMcpClients);
        Assert.Equal("Unverified client: unverified-client 1.2.3", client.ClientLabel);
        Assert.Equal("Verified OS account: verified-user", client.VerifiedAccount);
        Assert.Equal("Scopes: read context, submit proposals", client.Scopes);
        Assert.Contains("Unverified client", client.RevokeAccessibleName, StringComparison.Ordinal);

        client.RevokeCommand.Execute(null);

        Assert.Equal("authorization-1", revoked);

        settings.ReportAuthorizedMcpClients([], _ => { });
        settings.ReportLocalMcpRevocation(removed: true, "settings-local-mcp");
        Assert.Equal(
            "Remembered local MCP access revoked. Connected clients were disconnected.",
            settings.Announcement);
        Assert.Equal("settings-local-mcp", settings.FocusAutomationId);
    }

    private sealed class RecordingThemeService(ApplicationTheme theme) : IApplicationThemeService
    {
        public ApplicationTheme CurrentTheme { get; private set; } = theme;

        public bool AllowChange { get; set; } = true;

        public bool TrySetTheme(ApplicationTheme theme)
        {
            if (!AllowChange)
            {
                return false;
            }

            CurrentTheme = theme;
            return true;
        }
    }

    private sealed class RecordingAiFeaturePreferenceService : IAiFeaturePreferenceService
    {
        public bool AiFeaturesEnabled { get; private set; }
        public bool LocalMcpEnabled { get; private set; }

        public bool AllowChange { get; set; } = true;

        public bool TrySetAiFeaturesEnabled(bool enabled)
        {
            if (!AllowChange)
            {
                return false;
            }

            AiFeaturesEnabled = enabled;
            if (!enabled)
            {
                LocalMcpEnabled = false;
            }

            return true;
        }

        public bool TrySetLocalMcpEnabled(bool enabled)
        {
            if (!AllowChange || (enabled && !AiFeaturesEnabled))
            {
                return false;
            }

            LocalMcpEnabled = enabled;
            return true;
        }
    }

}
