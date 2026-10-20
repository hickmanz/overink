using System.Windows;

namespace OverInk;

public partial class App : Application
{
    private const string EditorArgument = "--editor";
    private const string MutexName = "OverInk_SingleInstance_2026";
    private const string ShowToolbarEventName = "OverInk_ShowToolbar_2026";
    private const string OpenEditorEventName = "OverInk_OpenEditor_2026";

    private static Mutex? _singleInstanceMutex;
    private ApplicationHost? _host;
    private EventWaitHandle? _showToolbarSignal;
    private EventWaitHandle? _openEditorSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Inno's silent upgrade asks Windows to end this process. OnExplicitShutdown would otherwise ignore that.
        SessionEnding += (_, _) => Shutdown();

        var wantsEditor = e.Args.Any(a => string.Equals(a, EditorArgument, StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // OverInk is already running (usually in the tray): ask that copy to come forward instead of
            // starting a second one. The Start menu "OverInk Editor" shortcut lands here with --editor.
            SignalRunningInstance(wantsEditor ? OpenEditorEventName : ShowToolbarEventName);
            Shutdown();
            return;
        }

        // Created before the host so a second launch that races startup can never miss us.
        _showToolbarSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowToolbarEventName);
        _openEditorSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEditorEventName);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "OverInk error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
        try
        {
            _host = new ApplicationHost();
            StartSignalListener();
            if (wantsEditor)
            {
                _host.ShowEditor();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "OverInk failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
        Exit += (_, _) =>
        {
            _host?.Dispose();
            _showToolbarSignal?.Dispose();
            _openEditorSignal?.Dispose();
            ReleaseSingleInstanceMutex();
            _singleInstanceMutex?.Dispose();
        };
    }

    /// <summary>
    /// Lets another process's "is OverInk running?" check pass while this copy keeps running. The installer
    /// is built with AppMutex on this mutex and, run silently, cancels itself if the mutex exists at all
    /// (it only checks that the name can be opened, not who owns it), so ownership alone isn't enough: the
    /// handle has to be closed. Once past that check the installer closes this copy itself (see
    /// InstallerCloseHook). Must run on the thread that created the mutex.
    /// </summary>
    public static void ReleaseSingleInstanceMutex()
    {
        var mutex = _singleInstanceMutex;
        _singleInstanceMutex = null;
        if (mutex == null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (already released).
        }

        mutex.Dispose();
    }

    /// <summary>Takes the single-instance mutex back, e.g. when the installer could not be started.</summary>
    public static void ReacquireSingleInstanceMutex()
    {
        if (_singleInstanceMutex != null)
        {
            return;
        }

        _singleInstanceMutex = new Mutex(true, MutexName, out _);
    }

    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            // Creates the event if the first instance hasn't yet, so the request is still delivered.
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            signal.Set();
        }
        catch
        {
            // Nothing sensible to do; the running copy simply won't react.
        }
    }

    /// <summary>Listens for later launches (Start menu, shortcuts) and reacts on the UI thread.</summary>
    private void StartSignalListener()
    {
        var handles = new WaitHandle[] { _showToolbarSignal!, _openEditorSignal! };
        var listener = new Thread(() =>
        {
            while (true)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (index == 1)
                    {
                        _host?.ShowEditor();
                    }
                    else
                    {
                        _host?.ShowToolbar();
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "OverInk signal listener"
        };
        listener.Start();
    }
}
