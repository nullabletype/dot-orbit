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
        Assert.False(store.LoadAiFeaturesEnabled());
        Assert.False(store.LoadLocalMcpEnabled());

        File.WriteAllText(path, "{\"theme\":\"Sepia\"}");
        Assert.Equal(ApplicationTheme.Dark, store.Load());
        Assert.True(store.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());
        Assert.False(store.LoadAiFeaturesEnabled());
        Assert.False(store.LoadLocalMcpEnabled());

        File.WriteAllText(path, "{\"theme\":\"2\"}");
        Assert.Equal(ApplicationTheme.Dark, store.Load());
    }

    [Fact]
    public void PreferenceStoreRoundTripsEveryStableThemeIdUsingAtomicReplacement()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        foreach (var option in ApplicationThemeCatalog.Options)
        {
            Assert.True(store.TrySave(option.Value));
            Assert.Equal(option.Value, store.Load());
            Assert.Contains($"\"theme\": \"{option.Id}\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }
    }

    [Fact]
    public void ThemeCatalogHasOrderedUniqueStableIdsAndExplicitAppearances()
    {
        Assert.Equal(
            [
                "Dark",
                "Light",
                "MonokaiClassic",
                "DraculaClassic",
                "AlucardClassic",
                "CatppuccinMocha",
                "CatppuccinLatte",
                "GruvboxDark",
                "GruvboxLight",
            ],
            ApplicationThemeCatalog.Options.Select(option => option.Id));
        Assert.Equal(
            [
                "Dark",
                "Light",
                "Monokai Classic",
                "Dracula Classic",
                "Alucard Classic",
                "Catppuccin Mocha",
                "Catppuccin Latte",
                "Gruvbox Dark",
                "Gruvbox Light",
            ],
            ApplicationThemeCatalog.Options.Select(option => option.Name));
        Assert.Equal(
            ApplicationThemeCatalog.Options.Count,
            ApplicationThemeCatalog.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            ApplicationThemeCatalog.Options.Where(option => option.Appearance == ApplicationThemeAppearance.Dark),
            option => Assert.True(
                option.Variant == ThemeVariant.Dark
                || option.Variant.InheritVariant == ThemeVariant.Dark));
        Assert.All(
            ApplicationThemeCatalog.Options.Where(option => option.Appearance == ApplicationThemeAppearance.Light),
            option => Assert.True(
                option.Variant == ThemeVariant.Light
                || option.Variant.InheritVariant == ThemeVariant.Light));
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
    public void AiFeaturesDefaultOffAndPersistWithoutClobberingOtherDevicePreferences()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        Assert.False(store.LoadAiFeaturesEnabled());
        Assert.True(store.TrySave(ApplicationTheme.Light));
        Assert.True(store.TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(false));
        Assert.True(store.TrySaveAiFeaturesEnabled(true));

        var reloaded = new JsonDevicePreferenceStore(path);
        Assert.True(reloaded.LoadAiFeaturesEnabled());
        Assert.Equal(ApplicationTheme.Light, reloaded.Load());
        Assert.False(reloaded.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());

        Assert.True(reloaded.TrySaveAiFeaturesEnabled(false));
        Assert.False(reloaded.LoadAiFeaturesEnabled());
        Assert.Equal(ApplicationTheme.Light, reloaded.Load());
        Assert.False(reloaded.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void LocalMcpDefaultsOffAndAiDisablePersistsBothGatesOffAtomically()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);
        var service = new AiFeaturePreferenceService(store);

        Assert.False(service.LocalMcpEnabled);
        Assert.False(service.TrySetLocalMcpEnabled(true));
        Assert.True(service.TrySetAiFeaturesEnabled(true));
        Assert.True(service.TrySetLocalMcpEnabled(true));
        Assert.True(service.LocalMcpEnabled);

        Assert.True(service.TrySetAiFeaturesEnabled(false));

        var reloaded = new AiFeaturePreferenceService(new JsonDevicePreferenceStore(path));
        Assert.False(reloaded.AiFeaturesEnabled);
        Assert.False(reloaded.LocalMcpEnabled);
    }

    [Fact]
    public void WindowSizePreferenceDefaultsForMissingMalformedAndInvalidValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        Assert.Null(store.LoadMainWindowSize());

        foreach (var json in new[]
                 {
                     "{not-json",
                     "{\"mainWindowWidth\":1280}",
                     "{\"mainWindowHeight\":720}",
                     "{\"mainWindowWidth\":-1,\"mainWindowHeight\":720}",
                     "{\"mainWindowWidth\":0,\"mainWindowHeight\":720}",
                     "{\"mainWindowWidth\":1280,\"mainWindowHeight\":0}",
                     "{\"mainWindowWidth\":1280,\"mainWindowHeight\":-1}",
                     "{\"mainWindowWidth\":1e400,\"mainWindowHeight\":720}",
                 })
        {
            File.WriteAllText(path, json);
            Assert.Null(store.LoadMainWindowSize());
        }

        var invalidContents = File.ReadAllText(path);
        Assert.False(store.TrySaveMainWindowSize(new MainWindowSize(double.NaN, 720)));
        Assert.False(store.TrySaveMainWindowSize(new MainWindowSize(1280, 0)));
        Assert.Equal(invalidContents, File.ReadAllText(path));
    }

    [Fact]
    public void WindowSizePreferenceRoundTripsWithoutClobberingOtherDevicePreferences()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(path);

        Assert.True(store.TrySave(ApplicationTheme.Light));
        Assert.True(store.TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(false));
        Assert.True(store.TrySaveAiFeaturesEnabled(true));
        Assert.True(store.TrySaveLocalMcpEnabled(true));
        Assert.True(store.TrySaveMainWindowSize(new MainWindowSize(1320.5, 741.25)));

        var reloaded = new JsonDevicePreferenceStore(path);
        Assert.Equal(new MainWindowSize(1320.5, 741.25), reloaded.LoadMainWindowSize());
        Assert.Equal(ApplicationTheme.Light, reloaded.Load());
        Assert.False(reloaded.LoadIncludeAgentInstructionsAndSchemaWithSnapshot());
        Assert.True(reloaded.LoadAiFeaturesEnabled());
        Assert.True(reloaded.LoadLocalMcpEnabled());

        Assert.True(reloaded.TrySave(ApplicationTheme.Dark));
        Assert.Equal(new MainWindowSize(1320.5, 741.25), reloaded.LoadMainWindowSize());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void WorkspacePathPreferenceRoundTripsCanonicalAbsolutePathWithoutClobberingOtherPreferences()
    {
        using var directory = new TemporaryDirectory();
        var preferencePath = Path.Combine(directory.Path, "preferences.json");
        var workspacePath = Path.Combine(directory.Path, "nested", "..", "chosen.workspace");
        var expected = Path.GetFullPath(workspacePath);
        var store = new JsonDevicePreferenceStore(preferencePath);

        Assert.Null(store.LoadWorkspacePath());
        Assert.True(store.TrySave(ApplicationTheme.Light));
        Assert.True(store.TrySaveWorkspacePath(workspacePath));

        var reloaded = new JsonDevicePreferenceStore(preferencePath);
        Assert.Equal(expected, reloaded.LoadWorkspacePath());
        Assert.Equal(ApplicationTheme.Light, reloaded.Load());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void WorkspacePathPreferenceRejectsRelativeAndMalformedValues()
    {
        using var directory = new TemporaryDirectory();
        var preferencePath = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonDevicePreferenceStore(preferencePath);

        Assert.False(store.TrySaveWorkspacePath("relative/workspace.orb"));
        Assert.False(File.Exists(preferencePath));

        File.WriteAllText(preferencePath, "{\"lastWorkspacePath\":\"relative/workspace.orb\"}");
        Assert.Null(store.LoadWorkspacePath());
    }

    [Fact]
    public void AiFeaturePreferenceServiceChangesStateOnlyAfterPersistenceSucceeds()
    {
        var store = new RecordingAiFeaturePreferenceStore();
        var service = new AiFeaturePreferenceService(store);

        Assert.False(service.AiFeaturesEnabled);
        Assert.True(service.TrySetAiFeaturesEnabled(true));
        Assert.True(service.AiFeaturesEnabled);

        store.AllowSave = false;
        Assert.False(service.TrySetAiFeaturesEnabled(false));
        Assert.True(service.AiFeaturesEnabled);
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
    public void AiFeaturePreferencePersistenceDoesNotAdvanceWorkspaceRevision()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic AI preference revision password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        using var session = Assert.IsAssignableFrom<IWorkspaceSession>(new EncryptedWorkspaceStore().Create(
            Path.Combine(directory.Path, "workspace.db"),
            passphrase,
            CategoryName.Create("Home").CategoryName!).Session);
        var before = session.Planning.Read().WorkspaceRevision;
        var preferences = new AiFeaturePreferenceService(
            new JsonDevicePreferenceStore(Path.Combine(directory.Path, "preferences.json")));

        Assert.True(preferences.TrySetAiFeaturesEnabled(true));

        Assert.Equal(before, session.Planning.Read().WorkspaceRevision);
    }

    [Fact]
    public void WindowSizePreferencePersistenceDoesNotAdvanceWorkspaceRevision()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic window preference revision password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        using var session = Assert.IsAssignableFrom<IWorkspaceSession>(new EncryptedWorkspaceStore().Create(
            Path.Combine(directory.Path, "workspace.db"),
            passphrase,
            CategoryName.Create("Home").CategoryName!).Session);
        var before = session.Planning.Read().WorkspaceRevision;
        var preferences = new JsonDevicePreferenceStore(
            Path.Combine(directory.Path, "preferences.json"));

        Assert.True(preferences.TrySaveMainWindowSize(new MainWindowSize(1280, 720)));

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
        Assert.False(store.TrySaveMainWindowSize(new MainWindowSize(1280, 720)));
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
            Assert.True(service.TrySetTheme(ApplicationTheme.GruvboxDark));
            Assert.Equal(ApplicationTheme.GruvboxDark, service.CurrentTheme);
            Assert.Equal(ApplicationThemeVariants.GruvboxDark, application.RequestedThemeVariant);
            Assert.Equal(ApplicationTheme.GruvboxDark, store.Load());
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

    private sealed class RecordingAiFeaturePreferenceStore : IAiFeaturePreferenceStore
    {
        private bool _enabled;
        private bool _localMcpEnabled;

        public bool AllowSave { get; set; } = true;

        public bool LoadAiFeaturesEnabled() => _enabled;

        public bool LoadLocalMcpEnabled() => _localMcpEnabled;

        public bool TrySaveAiFeaturesEnabled(bool enabled)
        {
            if (!AllowSave)
            {
                return false;
            }

            _enabled = enabled;
            if (!enabled)
            {
                _localMcpEnabled = false;
            }

            return true;
        }

        public bool TrySaveLocalMcpEnabled(bool enabled)
        {
            if (!AllowSave || (enabled && !_enabled))
            {
                return false;
            }

            _localMcpEnabled = enabled;
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
