using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using DotOrbit.Desktop.ApplicationUpdates;

namespace DotOrbit.Desktop;

public enum ApplicationTheme
{
    Dark,
    Light,
    MonokaiClassic,
    DraculaClassic,
    AlucardClassic,
    CatppuccinMocha,
    CatppuccinLatte,
    GruvboxDark,
    GruvboxLight,
}

public enum ApplicationThemeAppearance
{
    Dark,
    Light,
}

public static class ApplicationThemeVariants
{
    public static readonly ThemeVariant MonokaiClassic = new(
        nameof(MonokaiClassic),
        ThemeVariant.Dark);

    public static readonly ThemeVariant DraculaClassic = new(
        nameof(DraculaClassic),
        ThemeVariant.Dark);

    public static readonly ThemeVariant AlucardClassic = new(
        nameof(AlucardClassic),
        ThemeVariant.Light);

    public static readonly ThemeVariant CatppuccinMocha = new(
        nameof(CatppuccinMocha),
        ThemeVariant.Dark);

    public static readonly ThemeVariant CatppuccinLatte = new(
        nameof(CatppuccinLatte),
        ThemeVariant.Light);

    public static readonly ThemeVariant GruvboxDark = new(
        nameof(GruvboxDark),
        ThemeVariant.Dark);

    public static readonly ThemeVariant GruvboxLight = new(
        nameof(GruvboxLight),
        ThemeVariant.Light);
}

public sealed record ApplicationThemeOption(
    ApplicationTheme Value,
    string Id,
    string Name,
    ApplicationThemeAppearance Appearance,
    ThemeVariant Variant);

public static class ApplicationThemeCatalog
{
    public static IReadOnlyList<ApplicationThemeOption> Options { get; } =
    [
        new(
            ApplicationTheme.Dark,
            "Dark",
            "Dark",
            ApplicationThemeAppearance.Dark,
            ThemeVariant.Dark),
        new(
            ApplicationTheme.Light,
            "Light",
            "Light",
            ApplicationThemeAppearance.Light,
            ThemeVariant.Light),
        new(
            ApplicationTheme.MonokaiClassic,
            "MonokaiClassic",
            "Monokai Classic",
            ApplicationThemeAppearance.Dark,
            ApplicationThemeVariants.MonokaiClassic),
        new(
            ApplicationTheme.DraculaClassic,
            "DraculaClassic",
            "Dracula Classic",
            ApplicationThemeAppearance.Dark,
            ApplicationThemeVariants.DraculaClassic),
        new(
            ApplicationTheme.AlucardClassic,
            "AlucardClassic",
            "Alucard Classic",
            ApplicationThemeAppearance.Light,
            ApplicationThemeVariants.AlucardClassic),
        new(
            ApplicationTheme.CatppuccinMocha,
            "CatppuccinMocha",
            "Catppuccin Mocha",
            ApplicationThemeAppearance.Dark,
            ApplicationThemeVariants.CatppuccinMocha),
        new(
            ApplicationTheme.CatppuccinLatte,
            "CatppuccinLatte",
            "Catppuccin Latte",
            ApplicationThemeAppearance.Light,
            ApplicationThemeVariants.CatppuccinLatte),
        new(
            ApplicationTheme.GruvboxDark,
            "GruvboxDark",
            "Gruvbox Dark",
            ApplicationThemeAppearance.Dark,
            ApplicationThemeVariants.GruvboxDark),
        new(
            ApplicationTheme.GruvboxLight,
            "GruvboxLight",
            "Gruvbox Light",
            ApplicationThemeAppearance.Light,
            ApplicationThemeVariants.GruvboxLight),
    ];

    public static ApplicationThemeOption Get(ApplicationTheme theme) =>
        Options.SingleOrDefault(option => option.Value == theme)
        ?? throw new ArgumentOutOfRangeException(nameof(theme));

    public static bool TryGetById(string? id, out ApplicationThemeOption option)
    {
        option = Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return option is not null;
    }
}

public interface IApplicationThemeService
{
    ApplicationTheme CurrentTheme { get; }

    bool TrySetTheme(ApplicationTheme theme);
}

public interface IInterchangePreferenceService
{
    bool IncludeAgentInstructionsAndSchemaWithSnapshot { get; }

    bool TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(bool include);
}

public interface IAiFeaturePreferenceService
{
    bool AiFeaturesEnabled { get; }

    bool LocalMcpEnabled { get; }

    bool TrySetAiFeaturesEnabled(bool enabled);

    bool TrySetLocalMcpEnabled(bool enabled);
}

internal interface IApplicationThemePreferenceStore
{
    ApplicationTheme Load();

    bool TrySave(ApplicationTheme theme);
}

internal interface IInterchangePreferenceStore
{
    bool LoadIncludeAgentInstructionsAndSchemaWithSnapshot();

    bool TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(bool include);
}

internal interface IAiFeaturePreferenceStore
{
    bool LoadAiFeaturesEnabled();

    bool LoadLocalMcpEnabled();

    bool TrySaveAiFeaturesEnabled(bool enabled);

    bool TrySaveLocalMcpEnabled(bool enabled);
}

internal interface IWorkspacePathPreferenceStore
{
    string? LoadWorkspacePath();

    bool TrySaveWorkspacePath(string path);
}

internal sealed class TransientWorkspacePathPreferenceStore(
    string? workspacePath = null) : IWorkspacePathPreferenceStore
{
    private string? _workspacePath = WorkspacePathPolicy.TryCanonicalize(
        workspacePath,
        out var canonicalPath)
        ? canonicalPath
        : null;

    public string? LoadWorkspacePath() => _workspacePath;

    public bool TrySaveWorkspacePath(string path)
    {
        if (!WorkspacePathPolicy.TryCanonicalize(path, out var canonicalPath))
        {
            return false;
        }

        _workspacePath = canonicalPath;
        return true;
    }
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
        _application.RequestedThemeVariant = ApplicationThemeCatalog.Get(theme).Variant;
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

        _application.RequestedThemeVariant = ApplicationThemeCatalog.Get(theme).Variant;
    }
}

internal sealed class InterchangePreferenceService(
    IInterchangePreferenceStore preferenceStore) : IInterchangePreferenceService
{
    public bool IncludeAgentInstructionsAndSchemaWithSnapshot { get; private set; } =
        preferenceStore.LoadIncludeAgentInstructionsAndSchemaWithSnapshot();

    public bool TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(bool include)
    {
        if (!preferenceStore.TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(include))
        {
            return false;
        }

        IncludeAgentInstructionsAndSchemaWithSnapshot = include;
        return true;
    }
}

internal sealed class TransientInterchangePreferenceService(
    bool includeAgentInstructionsAndSchemaWithSnapshot = true) : IInterchangePreferenceService
{
    public bool IncludeAgentInstructionsAndSchemaWithSnapshot { get; private set; } =
        includeAgentInstructionsAndSchemaWithSnapshot;

    public bool TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(bool include)
    {
        IncludeAgentInstructionsAndSchemaWithSnapshot = include;
        return true;
    }
}

internal sealed class AiFeaturePreferenceService : IAiFeaturePreferenceService
{
    private readonly IAiFeaturePreferenceStore _preferenceStore;

    public AiFeaturePreferenceService(IAiFeaturePreferenceStore preferenceStore)
    {
        ArgumentNullException.ThrowIfNull(preferenceStore);
        _preferenceStore = preferenceStore;
        AiFeaturesEnabled = preferenceStore.LoadAiFeaturesEnabled();
        LocalMcpEnabled = AiFeaturesEnabled && preferenceStore.LoadLocalMcpEnabled();
    }

    public bool AiFeaturesEnabled { get; private set; }

    public bool LocalMcpEnabled { get; private set; }

    public bool TrySetAiFeaturesEnabled(bool enabled)
    {
        if (!_preferenceStore.TrySaveAiFeaturesEnabled(enabled))
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
        if (enabled && !AiFeaturesEnabled)
        {
            return false;
        }

        if (!_preferenceStore.TrySaveLocalMcpEnabled(enabled))
        {
            return false;
        }

        LocalMcpEnabled = enabled;
        return true;
    }
}

internal sealed class TransientAiFeaturePreferenceService(
    bool aiFeaturesEnabled = false,
    bool localMcpEnabled = false) : IAiFeaturePreferenceService
{
    public bool AiFeaturesEnabled { get; private set; } = aiFeaturesEnabled;

    public bool LocalMcpEnabled { get; private set; } = aiFeaturesEnabled && localMcpEnabled;

    public bool TrySetAiFeaturesEnabled(bool enabled)
    {
        AiFeaturesEnabled = enabled;
        if (!enabled)
        {
            LocalMcpEnabled = false;
        }

        return true;
    }

    public bool TrySetLocalMcpEnabled(bool enabled)
    {
        if (enabled && !AiFeaturesEnabled)
        {
            return false;
        }

        LocalMcpEnabled = enabled;
        return true;
    }
}

internal sealed class JsonDevicePreferenceStore :
    IApplicationThemePreferenceStore,
    IInterchangePreferenceStore,
    IAiFeaturePreferenceStore,
    IWorkspacePathPreferenceStore,
    IMainWindowSizePreferenceStore,
    IApplicationUpdatePreferenceStore
{
    internal const int MaximumPreferenceBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public JsonDevicePreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static JsonDevicePreferenceStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-orbit",
        "preferences.json"));

    public ApplicationTheme Load()
    {
        var document = ReadDocument();
        return ApplicationThemeCatalog.TryGetById(document.Theme, out var theme)
                ? theme.Value
                : ApplicationTheme.Dark;
    }

    public bool TrySave(ApplicationTheme theme) => TryUpdateDocument(
        document => document with { Theme = ApplicationThemeCatalog.Get(theme).Id });

    public bool LoadIncludeAgentInstructionsAndSchemaWithSnapshot() =>
        ReadDocument().IncludeAgentInstructionsAndSchemaWithSnapshot ?? true;

    public bool TrySaveIncludeAgentInstructionsAndSchemaWithSnapshot(bool include) =>
        TryUpdateDocument(document => document with
        {
            IncludeAgentInstructionsAndSchemaWithSnapshot = include,
        });

    public bool LoadAiFeaturesEnabled() =>
        ReadDocument().AiFeaturesEnabled ?? false;

    public bool LoadLocalMcpEnabled() =>
        ReadDocument().LocalMcpEnabled ?? false;

    public string? LoadWorkspacePath()
    {
        var path = ReadDocument().LastWorkspacePath;
        return WorkspacePathPolicy.TryCanonicalize(path, out var canonicalPath)
            ? canonicalPath
            : null;
    }

    public MainWindowSize? LoadMainWindowSize()
    {
        var document = ReadDocument();
        MainWindowSize? size = document.MainWindowWidth is { } width
                               && document.MainWindowHeight is { } height
            ? new MainWindowSize(width, height)
            : null;
        return MainWindowSizePolicy.IsValid(size) ? size : null;
    }

    public bool TrySaveAiFeaturesEnabled(bool enabled)
    {
        return TryUpdateDocument(document => document with
        {
            AiFeaturesEnabled = enabled,
            LocalMcpEnabled = enabled ? document.LocalMcpEnabled : false,
        });
    }

    public bool TrySaveLocalMcpEnabled(bool enabled)
    {
        lock (_gate)
        {
            var document = LoadDocument();
            if (enabled && document.AiFeaturesEnabled is not true)
            {
                return false;
            }

            return TrySaveDocument(document with { LocalMcpEnabled = enabled });
        }
    }

    public bool TrySaveWorkspacePath(string path)
    {
        if (!WorkspacePathPolicy.TryCanonicalize(path, out var canonicalPath))
        {
            return false;
        }

        return TryUpdateDocument(document => document with { LastWorkspacePath = canonicalPath });
    }

    public bool TrySaveMainWindowSize(MainWindowSize size)
    {
        if (!MainWindowSizePolicy.IsValid(size))
        {
            return false;
        }

        return TryUpdateDocument(document => document with
        {
            MainWindowWidth = size.Width,
            MainWindowHeight = size.Height,
        });
    }

    public ApplicationUpdatePreferences LoadApplicationUpdatePreferences() =>
        ReadDocument().ApplicationUpdates ?? new();

    public bool TrySaveApplicationUpdatePreferences(ApplicationUpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return TryUpdateDocument(document => document with { ApplicationUpdates = preferences });
    }

    private PreferenceDocument ReadDocument()
    {
        lock (_gate)
        {
            return LoadDocument();
        }
    }

    private bool TryUpdateDocument(Func<PreferenceDocument, PreferenceDocument> update)
    {
        lock (_gate)
        {
            return TrySaveDocument(update(LoadDocument()));
        }
    }

    private PreferenceDocument LoadDocument()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return EmptyDocument();
            }

            using var stream = File.Open(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length > MaximumPreferenceBytes)
            {
                return EmptyDocument();
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() >= 0)
            {
                return EmptyDocument();
            }

            return JsonSerializer.Deserialize<PreferenceDocument>(bytes, JsonOptions)
                   ?? EmptyDocument();
        }
        catch (IOException)
        {
            return EmptyDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return EmptyDocument();
        }
        catch (JsonException)
        {
            return EmptyDocument();
        }
    }

    private static PreferenceDocument EmptyDocument() =>
        new(null, null, null, null, null, null, null, null);

    private bool TrySaveDocument(PreferenceDocument document)
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
                JsonSerializer.Serialize(document, JsonOptions));
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

    private sealed record PreferenceDocument(
        string? Theme,
        bool? IncludeAgentInstructionsAndSchemaWithSnapshot,
        bool? AiFeaturesEnabled,
        bool? LocalMcpEnabled,
        double? MainWindowWidth,
        double? MainWindowHeight,
        string? LastWorkspacePath,
        ApplicationUpdatePreferences? ApplicationUpdates);
}
