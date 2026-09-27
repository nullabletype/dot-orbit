using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DotOrbit.Desktop;

internal interface IRecoveryPathPicker
{
    Task<string?> SelectRecoveryDirectoryAsync();

    Task<string?> SelectRecoveryPointAsync();
}

internal sealed class RecoveryPathPicker(Window owner) : IRecoveryPathPicker
{
    public async Task<string?> SelectRecoveryDirectoryAsync()
    {
        var results = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose recovery directory",
                AllowMultiple = false,
            });
        return results.Count == 1 ? results[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SelectRecoveryPointAsync()
    {
        var results = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Choose encrypted recovery point",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("dot-orbit encrypted recovery points")
                    {
                        Patterns = ["*.dotorbit-recovery"],
                    },
                ],
            });
        return results.Count == 1 ? results[0].TryGetLocalPath() : null;
    }
}
