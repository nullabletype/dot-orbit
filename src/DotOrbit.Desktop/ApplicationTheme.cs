using System.Text.Json;
using Avalonia;
using Avalonia.Styling;

namespace DotOrbit.Desktop;

public enum ApplicationTheme
{
    Dark,
    Light,
}

public sealed record ApplicationThemeOption(ApplicationTheme Value, string Name);

public interface IApplicationThemeService
{
    ApplicationTheme CurrentTheme { get; }

    bool TrySetTheme(ApplicationTheme theme);
}

internal interface IApplicationThemePreferenceStore
{
    ApplicationTheme Load();

    bool TrySave(ApplicationTheme theme);
}

internal sealed class ApplicationThemeService : IApplicationThemeService
{
    private readonly Application _application;
    private readonly IApplicationThemePreferenceStore _preferenceStore;

    public ApplicationThemeService(
        Application application,
        IApplicationThemePreferenceStore preferenceStore)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(preferenceStore);
        _application = application;
        _preferenceStore = preferenceStore;
        CurrentTheme = preferenceStore.Load();
        Apply(CurrentTheme);
    }

    public ApplicationTheme CurrentTheme { get; private set; }

    public bool TrySetTheme(ApplicationTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        if (!_preferenceStore.TrySave(theme))
        {
            return false;
        }

        CurrentTheme = theme;
        Apply(theme);
        return true;
    }

    private void Apply(ApplicationTheme theme) =>
        _application.RequestedThemeVariant = theme == ApplicationTheme.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
}

internal sealed class TransientApplicationThemeService : IApplicationThemeService
{
    private readonly Application? _application;

    public TransientApplicationThemeService(
        ApplicationTheme theme = ApplicationTheme.Dark,
        Application? application = null)
    {
        _application = application;
        CurrentTheme = theme;
        Apply(theme);
    }

    public ApplicationTheme CurrentTheme { get; private set; }

    public bool TrySetTheme(ApplicationTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        CurrentTheme = theme;
        Apply(theme);
        return true;
    }

    private void Apply(ApplicationTheme theme)
    {
        if (_application is null)
        {
            return;
        }

        _application.RequestedThemeVariant = theme == ApplicationTheme.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}

internal sealed class JsonApplicationThemePreferenceStore : IApplicationThemePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public JsonApplicationThemePreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static JsonApplicationThemePreferenceStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-orbit",
        "preferences.json"));

    public ApplicationTheme Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ApplicationTheme.Dark;
            }

            var document = JsonSerializer.Deserialize<ThemePreferenceDocument>(
                File.ReadAllText(_path),
                JsonOptions);
            return Enum.TryParse<ApplicationTheme>(document?.Theme, ignoreCase: true, out var theme)
                && Enum.IsDefined(theme)
                    ? theme
                    : ApplicationTheme.Dark;
        }
        catch (IOException)
        {
            return ApplicationTheme.Dark;
        }
        catch (UnauthorizedAccessException)
        {
            return ApplicationTheme.Dark;
        }
        catch (JsonException)
        {
            return ApplicationTheme.Dark;
        }
    }

    public bool TrySave(ApplicationTheme theme)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var candidate = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                candidate,
                JsonSerializer.Serialize(new ThemePreferenceDocument(theme.ToString()), JsonOptions));
            File.Move(candidate, _path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ThemePreferenceDocument(string Theme);
}
