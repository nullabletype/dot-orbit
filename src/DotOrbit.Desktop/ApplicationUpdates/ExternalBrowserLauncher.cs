using Avalonia.Platform.Storage;

namespace DotOrbit.Desktop.ApplicationUpdates;

internal interface IExternalBrowserLauncher
{
    Task<bool> TryOpenAsync(Uri uri);
}

internal sealed class AvaloniaExternalBrowserLauncher(ILauncher launcher) : IExternalBrowserLauncher
{
    public async Task<bool> TryOpenAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            return await launcher.LaunchUriAsync(uri).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
