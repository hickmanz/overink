using System.Windows;

namespace OverInk;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private ApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "OverInk_SingleInstance_2026", out var created);
        if (!created)
        {
            MessageBox.Show("OverInk is already running.", "OverInk", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "OverInk error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
        try
        {
            _host = new ApplicationHost();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "OverInk failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
        Exit += (_, _) =>
        {
            _host?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        };
    }
}
