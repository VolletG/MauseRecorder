using System.Windows;
using System.Windows.Media;
using MauseRecorder.Services;

namespace MauseRecorder;

public partial class App : Application
{
    public StorageService Storage { get; private set; } = null!;
    public Models.AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Storage = new StorageService();
        Settings = Storage.LoadSettings();
        ThemeManager.ApplyAccent(Settings.Accent);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mauserecorder-crash.txt"),
                    args.Exception.ToString());
            }
            catch
            {
                // Best effort logging.
            }

            MessageBox.Show(
                "Something went sideways:\n\n" + args.Exception.Message,
                "MauseRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = new MainWindow(Storage, Settings);
        MainWindow = window;
        window.Show();
    }
}
