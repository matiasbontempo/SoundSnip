using System.Windows;
using System.Windows.Threading;
using SoundSnip.Core;

namespace SoundSnip;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled: " + e.Exception);
        MessageBox.Show(
            "Something unexpected went wrong:\n\n" + e.Exception.Message +
            "\n\nDetails were saved to:\n" + AppPaths.LogPath,
            "SoundSnip", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
