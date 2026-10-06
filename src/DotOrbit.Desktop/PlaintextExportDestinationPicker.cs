using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DotOrbit.Desktop;

internal sealed record PlaintextExportDestination(string Name, string Path);

internal interface IPlaintextExportDestinationPicker
{
    Task<PlaintextExportDestination?> SelectAsync();
}

internal sealed class PlaintextExportDestinationPicker(Window owner) : IPlaintextExportDestinationPicker
{
    public async Task<PlaintextExportDestination?> SelectAsync()
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(CreateOptions());
        var path = file?.TryGetLocalPath();
        return file is null || path is null ? null : new(file.Name, path);
    }

    internal static FilePickerSaveOptions CreateOptions() =>
        new()
        {
            Title = "Export unencrypted workspace JSON",
            SuggestedFileName = "dot-orbit-workspace-export.json",
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("JSON document")
                {
                    Patterns = ["*.json"],
                    MimeTypes = ["application/json"],
                },
            ],
        };
}
