using DotOrbit.Desktop.ApplicationUpdates;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ApplicationUpdatePreferenceTests
{
    [Fact]
    public void UpdatePreferencesDefaultEnabledAndRoundTripWithoutClobberingTheme()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dot-orbit-updates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.json");
            var store = new JsonDevicePreferenceStore(path);
            var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

            Assert.True(store.LoadApplicationUpdatePreferences().AutomaticEnabled);
            Assert.True(store.TrySave(ApplicationTheme.Light));
            Assert.True(store.TrySaveApplicationUpdatePreferences(new(
                AutomaticEnabled: false,
                LastAutomaticAttemptUtc: now,
                LastSuccessfulCheckUtc: now,
                ETag: "\"catalog\"",
                HasVerifiedCatalog: true,
                VerifiedStableVersion: "2.0.0",
                VerifiedPrereleaseVersion: "2.1.0-beta.1",
                DismissedVersion: "2.0.0")));

            var reloaded = new JsonDevicePreferenceStore(path);
            Assert.Equal(ApplicationTheme.Light, reloaded.Load());
            Assert.Equal(
                new ApplicationUpdatePreferences(
                    AutomaticEnabled: false,
                    LastAutomaticAttemptUtc: now,
                    LastSuccessfulCheckUtc: now,
                    ETag: "\"catalog\"",
                    HasVerifiedCatalog: true,
                    VerifiedStableVersion: "2.0.0",
                    VerifiedPrereleaseVersion: "2.1.0-beta.1",
                    DismissedVersion: "2.0.0"),
                reloaded.LoadApplicationUpdatePreferences());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MalformedPreferenceFileFallsBackWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dot-orbit-updates-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{not-json");
            Assert.Equal(new ApplicationUpdatePreferences(),
                new JsonDevicePreferenceStore(path).LoadApplicationUpdatePreferences());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OversizedPreferenceFileFallsBackWithoutReadingUnboundedContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dot-orbit-updates-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, new byte[JsonDevicePreferenceStore.MaximumPreferenceBytes + 1]);

            Assert.Equal(
                new ApplicationUpdatePreferences(),
                new JsonDevicePreferenceStore(path).LoadApplicationUpdatePreferences());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnreadableAndUnwritablePreferencePathFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dot-orbit-updates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonDevicePreferenceStore(directory);

            Assert.Equal(new ApplicationUpdatePreferences(), store.LoadApplicationUpdatePreferences());
            Assert.False(store.TrySaveApplicationUpdatePreferences(new(AutomaticEnabled: false)));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}
