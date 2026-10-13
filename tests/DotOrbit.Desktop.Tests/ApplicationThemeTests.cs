using Avalonia;
using Avalonia.Styling;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ApplicationThemeTests
{
    [Fact]
    public void PreferenceStoreDefaultsToDarkAndIgnoresMalformedOrUnknownValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        Assert.Equal(ApplicationTheme.Dark, store.Load());

        File.WriteAllText(path, "{not-json");
        Assert.Equal(ApplicationTheme.Dark, store.Load());

        File.WriteAllText(path, "{\"theme\":\"Sepia\"}");
        Assert.Equal(ApplicationTheme.Dark, store.Load());
        Assert.True(store.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());
    }

    [Fact]
    public void PreferenceStoreRoundTripsLightAndDarkUsingAtomicReplacement()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        Assert.True(store.TrySave(ApplicationTheme.Light));
        Assert.Equal(ApplicationTheme.Light, store.Load());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));

        Assert.True(store.TrySave(ApplicationTheme.Dark));
        Assert.Equal(ApplicationTheme.Dark, store.Load());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void InterchangePreferenceDefaultsEnabledAndPersistsWithoutClobberingTheme()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        Assert.True(store.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());
        Assert.True(store.TrySave(ApplicationTheme.Light));
        Assert.True(store.TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(false));

        var reloaded = new JsonDevicePreferenceStore(path);
        Assert.Equal(ApplicationTheme.Light, reloaded.Load());
        Assert.False(reloaded.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());

        Assert.True(reloaded.TrySave(ApplicationTheme.Dark));
        Assert.False(reloaded.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void InterchangeToggleSurvivesStoreServiceAndAiViewModelRecreation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var service = new InterchangePreferenceService(new JsonDevicePreferenceStore(path));
        var ai = new AiViewModel(service);

        Assert.True(ai.IncludeAgentInstructionsAndSchemaWithSnapshot);
        ai.IncludeAgentInstructionsAndSchemaWithSnapshot = false;

        var recreatedService = new InterchangePreferenceService(new JsonDevicePreferenceStore(path));
        var recreatedAi = new AiViewModel(recreatedService);

        Assert.False(recreatedAi.IncludeAgentInstructionsAndSchemaWithSnapshot);
    }

    [Fact]
    public void InterchangePreferenceServiceChangesStateOnlyAfterPersistenceSucceeds()
    {
        var store = new RecordingInterchangePreferenceStore();
        var service = new InterchangePreferenceService(store);

        Assert.True(service.IncludeAgentInstructionsAndSchemaWithSnapshot);
        Assert.True(service.TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(false));
        Assert.False(service.IncludeAgentInstructionsAndSchemaWithSnapshot);

        store.AllowSave = false;
        Assert.False(service.TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(true));
        Assert.False(service.IncludeAgentInstructionsAndSchemaWithSnapshot);
    }

    [Fact]
    public void InterchangePreferencePersistenceDoesNotAdvanceWorkspaceRevision()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic preference revision password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        using var session = Assert.IsAssignableFrom<IWorkspaceSession>(new EncryptedWorkspaceStore().Create(
            Path.Combine(directory.Path, "workspace.db"),
            passphrase,
            CategoryName.Create("Home").CategoryName!).Session);
        var before = session.Planning.Read().WorkspaceRevision;
        var preferences = new InterchangePreferenceService(
            new JsonDevicePreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        Assert.True(preferences.TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(false));

        Assert.Equal(before, session.Planning.Read().WorkspaceRevision);
    }

    [Fact]
    public void PreferenceStoreReportsAnUnwritableDestinationWithoutLeavingATemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var blockedParent = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(blockedParent, "blocked");
        var store = new JsonDevicePreferenceStore(Path.Combine(blockedParent, "preferences.json"));

        Assert.False(store.TrySave(ApplicationTheme.Light));
        Assert.Equal(["not-a-directory"], Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ThemeServiceLoadsAppliesAndPersistsSuccessfulChanges()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonDevicePreferenceStore(Path.Combine(directory.Path, "preferences.json"));
        Assert.True(store.TrySave(ApplicationTheme.Light));
        var application = Assert.IsType<App>(Application.Current);
        var originalTheme = application.RequestedThemeVariant;
        try
        {
            var service = new ApplicationThemeService(application, store);

            Assert.Equal(ApplicationTheme.Light, service.CurrentTheme);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.True(service.TrySetTheme(ApplicationTheme.Dark));
            Assert.Equal(ApplicationTheme.Dark, service.CurrentTheme);
            Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
            Assert.Equal(ApplicationTheme.Dark, store.Load());
        }
        finally
        {
            application.RequestedThemeVariant = originalTheme;
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ThemeServiceKeepsThePreviousThemeWhenPersistenceFails()
    {
        var application = Application.Current ?? new App();
        application.RequestedThemeVariant = ThemeVariant.Dark;
        var service = new ApplicationThemeService(application, new RejectingPreferenceStore());

        Assert.False(service.TrySetTheme(ApplicationTheme.Light));
        Assert.Equal(ApplicationTheme.Dark, service.CurrentTheme);
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
    }

    private sealed class RejectingPreferenceStore : IApplicationThemePreferenceStore
    {
        public ApplicationTheme Load() => ApplicationTheme.Dark;

        public bool TrySave(ApplicationTheme theme) => false;
    }

    private sealed class RecordingInterchangePreferenceStore : IInterchangePreferenceStore
    {
        private bool _include = true;

        public bool AllowSave { get; set; } = true;

        public bool LoadIncludeAgentInstructionsAndSchemaWithSnapshot() => _include;

        public bool TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(bool include)
        {
            if (!AllowSave)
            {
                return false;
            }

            _include = include;
            return true;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dot-orbit-theme-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
