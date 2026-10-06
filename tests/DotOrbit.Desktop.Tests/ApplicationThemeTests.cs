using Avalonia;
using Avalonia.Styling;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ApplicationThemeTests
{
    [Fact]
    public void PreferenceStoreDefaultsToDarkAndIgnoresMalformedOrUnknownValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonApplicationThemePreferenceStore(path);

        Assert.Equal(ApplicationTheme.Dark, store.Load());

        File.WriteAllText(path, "{not-json");
        Assert.Equal(ApplicationTheme.Dark, store.Load());

        File.WriteAllText(path, "{\"theme\":\"Sepia\"}");
        Assert.Equal(ApplicationTheme.Dark, store.Load());
    }

    [Fact]
    public void PreferenceStoreRoundTripsLightAndDarkUsingAtomicReplacement()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var store = new JsonApplicationThemePreferenceStore(path);

        Assert.True(store.TrySave(ApplicationTheme.Light));
        Assert.Equal(ApplicationTheme.Light, store.Load());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));

        Assert.True(store.TrySave(ApplicationTheme.Dark));
        Assert.Equal(ApplicationTheme.Dark, store.Load());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void PreferenceStoreReportsAnUnwritableDestinationWithoutLeavingATemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var blockedParent = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(blockedParent, "blocked");
        var store = new JsonApplicationThemePreferenceStore(Path.Combine(blockedParent, "preferences.json"));

        Assert.False(store.TrySave(ApplicationTheme.Light));
        Assert.Equal(["not-a-directory"], Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ThemeServiceLoadsAppliesAndPersistsSuccessfulChanges()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonApplicationThemePreferenceStore(Path.Combine(directory.Path, "preferences.json"));
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
