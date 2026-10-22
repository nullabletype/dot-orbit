using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DotOrbit.Desktop;

internal interface IWorkspaceLocationPicker
{
    Task<string?> PickExistingAsync(Window owner);

    Task<string?> PickNewAsync(Window owner);
}

internal sealed class AvaloniaWorkspaceLocationPicker : IWorkspaceLocationPicker
{
    public async Task<string?> PickExistingAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.StorageProvider.CanOpen)
        {
            throw new NotSupportedException("This platform cannot choose a local workspace file.");
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(CreateOpenOptions());
        if (files.Count != 1)
        {
            return null;
        }

        var file = files[0];
        try
        {
            return file.TryGetLocalPath()
                   ?? throw new NotSupportedException("The selected workspace is not a local file.");
        }
        finally
        {
            (file as IDisposable)?.Dispose();
        }
    }

    public async Task<string?> PickNewAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.StorageProvider.CanSave)
        {
            throw new NotSupportedException("This platform cannot choose a local workspace file.");
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(CreateSaveOptions());
        if (file is null)
        {
            return null;
        }

        try
        {
            return file.TryGetLocalPath()
                   ?? throw new NotSupportedException("The selected workspace is not a local file.");
        }
        finally
        {
            (file as IDisposable)?.Dispose();
        }
    }

    internal static FilePickerOpenOptions CreateOpenOptions() => new()
    {
        Title = "Choose an existing dot-orbit workspace",
        AllowMultiple = false,
    };

    internal static FilePickerSaveOptions CreateSaveOptions() => new()
    {
        Title = "Choose a location for a new dot-orbit workspace",
        SuggestedFileName = "workspace.orb",
        ShowOverwritePrompt = true,
    };
}
