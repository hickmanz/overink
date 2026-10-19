using System.Windows;
using System.Windows.Interop;
using H.NotifyIcon;
using OverInk.Editor;
using OverInk.Input;
using OverInk.Models;
using OverInk.Overlay;
using OverInk.Remote;
using OverInk.Services;
using OverInk.Toolbar;

namespace OverInk;

public sealed class ApplicationHost : IDisposable
{
    private readonly SettingsService _settings = new();
    private readonly SketchSession _session;
    private readonly RemoteStrokeSource _strokeSource = new();
    private readonly PairingService _pairing;
    private readonly HotkeyService _hotkeyService = new();
    private readonly OverlayWindow _overlay;
    private readonly ToolbarWindow _toolbar;
    private readonly RemoteSketchServer _remoteServer;
    private readonly TaskbarIcon _tray;
    private readonly GlobalKeyboardService _keyboardService;
    private CaptureRegionChromeWindow? _captureRegionChrome;
    private System.Windows.Controls.MenuItem? _remoteMenuItem;
    private readonly CaptureLibrary _captureLibrary;
    private EditorWindow? _editor;
    private bool _sketchUiVisible = true;

    public ApplicationHost()
    {
        _session = new SketchSession(_settings);
        _captureLibrary = new CaptureLibrary(
            string.IsNullOrWhiteSpace(_settings.Current.LibraryFolder)
                ? null
                : _settings.Current.LibraryFolder);
        _captureLibrary.StartWatching();
        _pairing = new PairingService(_settings);
        _overlay = new OverlayWindow(_session, _strokeSource);
        _toolbar = new ToolbarWindow(_session, _overlay);
        _overlay.SetToolbarWindow(_toolbar);
        _toolbar.PairingRequested += async (_, _) => await ShowPairingWindowAsync();
        _remoteServer = new RemoteSketchServer(_settings, _pairing, _strokeSource, _session, _overlay);
        _remoteServer.ClientCountChanged += count =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
                _toolbar.SetRemoteConnected(count > 0, count));
        };
        _remoteServer.JoinApprovalRequired += OnJoinApprovalRequiredAsync;

        _tray = CreateTrayIcon();

        _keyboardService = new GlobalKeyboardService(
            _session,
            _overlay,
            _toolbar,
            () => _overlay.Visibility == Visibility.Visible,
            () => _overlay.Undo(),
            () => _overlay.Redo());

        _toolbar.CaptureRequested += (_, _) => CaptureToEditor();
        _toolbar.SetRegionRequested += (_, _) => OnSetRegionToolbarClick();
        _toolbar.HideRequested += (_, _) => SetVisible(false);
        _toolbar.HotkeyChanged += (_, args) => ApplyHotkey(args.Modifiers, args.VirtualKey);
        _toolbar.RemoteServerEnabledChanged += async (_, enabled) => await SetRemoteServerAsync(enabled);
        _toolbar.HideToolbarWhenNotDrawingChanged += (_, enabled) =>
        {
            _settings.Current.HideToolbarWhenNotDrawing = enabled;
            _settings.Save();
            UpdateToolbarAutoHide();
        };

        _session.Changed += () =>
        {
            UpdateToolbarAutoHide();
            if (_remoteServer.IsRunning)
            {
                _remoteServer.BroadcastState();
            }
        };

        _toolbar.Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(_toolbar).Handle;
            _hotkeyService.Attach(handle);
            ApplyHotkey(_settings.Current.HotkeyModifiers, _settings.Current.HotkeyVirtualKey, quiet: true);
        };

        _hotkeyService.ToggleDrawRequested += (_, _) =>
        {
            _session.ToggleMode();
            _session.PersistSettings();
        };

        if (_settings.Current.RunAtStartup != StartupService.IsEnabled())
        {
            StartupService.SetEnabled(_settings.Current.RunAtStartup);
        }

        ShowAll();
        SyncCaptureRegionUi();
        _toolbar.SetHideToolbarWhenNotDrawing(_settings.Current.HideToolbarWhenNotDrawing);
        SyncRemoteServerUi(_settings.Current.RemoteEnabled);
        UpdateToolbarAutoHide();
        _keyboardService.Start();

        if (_settings.Current.RemoteEnabled)
        {
            _ = StartRemoteAsync();
        }
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var tray = new TaskbarIcon
        {
            ToolTipText = BuildTrayTooltip(),
            Icon = TrayIconFactory.Create(),
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick
        };

        Application.Current.Resources["OverInkTrayIcon"] = tray;
        tray.ForceCreate(enablesEfficiencyMode: false);

        tray.ContextMenu = new System.Windows.Controls.ContextMenu();
        var showItem = new System.Windows.Controls.MenuItem { Header = "Show toolbar" };
        showItem.Click += (_, _) => ShowToolbarFromTray();
        tray.ContextMenu.Items.Add(showItem);

        var drawItem = new System.Windows.Controls.MenuItem { Header = "Toggle draw / pass-through" };
        drawItem.Click += (_, _) =>
        {
            _session.ToggleMode();
            _session.PersistSettings();
        };
        tray.ContextMenu.Items.Add(drawItem);

        var clearItem = new System.Windows.Controls.MenuItem { Header = "Clear annotations" };
        clearItem.Click += (_, _) => _overlay.ClearAll();
        tray.ContextMenu.Items.Add(clearItem);

        var captureItem = new System.Windows.Controls.MenuItem { Header = "Capture to editor" };
        captureItem.Click += (_, _) => CaptureToEditor();
        tray.ContextMenu.Items.Add(captureItem);

        var libraryItem = new System.Windows.Controls.MenuItem { Header = "Open capture library…" };
        libraryItem.Click += (_, _) => OpenEditorLibrary();
        tray.ContextMenu.Items.Add(libraryItem);

        tray.ContextMenu.Items.Add(new System.Windows.Controls.Separator());

        _remoteMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = "Remote sketching server",
            IsCheckable = true,
            IsChecked = _settings.Current.RemoteEnabled
        };
        _remoteMenuItem.Click += async (_, _) =>
        {
            await SetRemoteServerAsync(_remoteMenuItem.IsChecked);
        };
        tray.ContextMenu.Items.Add(_remoteMenuItem);

        var pairItem = new System.Windows.Controls.MenuItem { Header = "Remote sketch pair…" };
        pairItem.Click += async (_, _) => await ShowPairingWindowAsync();
        tray.ContextMenu.Items.Add(pairItem);

        var logItem = new System.Windows.Controls.MenuItem { Header = "Open remote log…" };
        logItem.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = RemoteDebugLog.LogFilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _toolbar.SetStatus($"Log: {RemoteDebugLog.LogFilePath} — {ex.Message}");
            }
        };
        tray.ContextMenu.Items.Add(logItem);

        var startupItem = new System.Windows.Controls.MenuItem
        {
            Header = "Run at Windows startup",
            IsCheckable = true,
            IsChecked = _settings.Current.RunAtStartup
        };
        startupItem.Click += (_, _) =>
        {
            _settings.Current.RunAtStartup = startupItem.IsChecked;
            StartupService.SetEnabled(_settings.Current.RunAtStartup);
            _settings.Save();
        };
        tray.ContextMenu.Items.Add(startupItem);

        tray.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        tray.ContextMenu.Items.Add(exitItem);

        tray.TrayMouseDoubleClick += (_, _) => ShowToolbarFromTray();
        return tray;
    }

    private async Task ShowPairingWindowAsync()
    {
        if (!_remoteServer.IsRunning)
        {
            await StartRemoteAsync();
        }

        _toolbar.ShowSettingsPanel();
        new PairingWindow(_pairing, _remoteServer, _settings).Show();
    }

    private async Task SetRemoteServerAsync(bool enabled)
    {
        if (enabled)
        {
            await StartRemoteAsync();
        }
        else
        {
            await StopRemoteAsync();
        }
    }

    private async Task StartRemoteAsync()
    {
        try
        {
            await _remoteServer.StartAsync();
            _settings.Current.RemoteEnabled = true;
            _settings.Save();
            SyncRemoteServerUi(true);
            _toolbar.SetStatus($"Remote sketching server on port {_settings.Current.RemotePort}");
        }
        catch (Exception ex)
        {
            _toolbar.SetStatus($"Remote server failed: {ex.Message}");
            SyncRemoteServerUi(false);
        }
    }

    private async Task StopRemoteAsync()
    {
        await _remoteServer.StopAsync();
        _settings.Current.RemoteEnabled = false;
        _settings.Save();
        _toolbar.SetRemoteConnected(false, 0);
        _toolbar.SetStatus(string.Empty);
        SyncRemoteServerUi(false);
    }

    private void SyncRemoteServerUi(bool enabled)
    {
        if (_remoteMenuItem is not null)
        {
            _remoteMenuItem.IsChecked = enabled;
        }

        _toolbar.SetRemoteServerChecked(enabled);
    }

    private Task<bool> OnJoinApprovalRequiredAsync(string clientId, string deviceName)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var result = MessageBox.Show(
                $"Allow \"{deviceName}\" to join this sketch session?",
                "OverInk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }).Task;
    }

    private void CaptureToEditor()
    {
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        var bitmap = ScreenCaptureService.Capture(geometry.Pixels, _toolbar);
        var item = _captureLibrary.CreateFromCapture(bitmap);
        OpenEditor(item);
    }

    private void OpenEditorLibrary()
    {
        var items = _captureLibrary.Enumerate();
        if (items.Count == 0)
        {
            MessageBox.Show(
                $"No captures yet. Saved images appear in:\n{_captureLibrary.FolderPath}",
                "OverInk",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        OpenEditor(items[0]);
    }

    private void OpenEditor(CaptureItem item)
    {
        if (_editor is null)
        {
            _editor = new EditorWindow(_captureLibrary, _session.PenColor, _session.PenThickness);
            _editor.Closed += (_, _) => _editor = null;
        }

        _editor.OpenItem(item);
        _editor.Show();
        _editor.Activate();
    }

    private void ShowToolbarFromTray()
    {
        SetVisible(true);
        _toolbar.Visibility = Visibility.Visible;
    }

    private void SetVisible(bool visible)
    {
        _sketchUiVisible = visible;
        if (!visible)
        {
            _toolbar.Visibility = Visibility.Hidden;
            _overlay.Visibility = Visibility.Hidden;
            if (_captureRegionChrome is not null)
            {
                _captureRegionChrome.Visibility = Visibility.Hidden;
            }

            return;
        }

        _overlay.Visibility = Visibility.Visible;
        if (_captureRegionChrome is not null)
        {
            _captureRegionChrome.Visibility = Visibility.Visible;
        }

        UpdateToolbarAutoHide();
    }

    private void UpdateToolbarAutoHide()
    {
        if (!_sketchUiVisible)
        {
            return;
        }

        if (_settings.Current.HideToolbarWhenNotDrawing &&
            _session.Mode != InteractionMode.Draw)
        {
            _toolbar.Visibility = Visibility.Hidden;
            return;
        }

        _toolbar.Visibility = Visibility.Visible;
    }

    private void SyncCaptureRegionUi()
    {
        if (_settings.Current.RemoteRegionEnabled)
        {
            var stream = CaptureRegionHelper.GetStreamDips(_settings.Current);
            _overlay.SetCaptureRegionOutline(stream);
            _toolbar.SetCaptureRegionActive(true);
            ShowCaptureRegionChrome();
        }
        else
        {
            HideCaptureRegionChrome();
            _overlay.SetCaptureRegionOutline(null);
            _toolbar.SetCaptureRegionActive(false);
        }
    }

    private void OnSetRegionToolbarClick()
    {
        if (_settings.Current.RemoteRegionEnabled)
        {
            ClearCaptureRegion();
            return;
        }

        BeginCaptureRegionPick();
    }

    private void BeginCaptureRegionPick()
    {
        _captureRegionChrome?.Hide();
        var picker = new RegionPickerWindow
        {
            Owner = _toolbar
        };

        if (picker.ShowDialog() != true || picker.SelectedDipsRect is not { } rect)
        {
            if (_settings.Current.RemoteRegionEnabled)
            {
                ShowCaptureRegionChrome();
            }
            else
            {
                _toolbar.SetCaptureRegionActive(false);
            }

            return;
        }

        CaptureRegionHelper.SetFromDipsRect(_settings.Current, rect);
        _settings.Save();
        ApplyCaptureRegion();
    }

    private void ApplyCaptureRegion()
    {
        var stream = CaptureRegionHelper.GetStreamDips(_settings.Current);
        _overlay.SetCaptureRegionOutline(stream);
        _toolbar.SetCaptureRegionActive(true);
        ShowCaptureRegionChrome();
        if (_remoteServer.IsRunning)
        {
            _remoteServer.NotifyCaptureRegionChanged();
        }
    }

    private void ClearCaptureRegion()
    {
        CaptureRegionHelper.Clear(_settings.Current);
        _settings.Save();
        SyncCaptureRegionUi();
        if (_remoteServer.IsRunning)
        {
            _remoteServer.NotifyCaptureRegionChanged();
        }
    }

    private void ShowCaptureRegionChrome()
    {
        var stream = CaptureRegionHelper.GetStreamDips(_settings.Current);
        _captureRegionChrome ??= CreateCaptureRegionChrome();
        _captureRegionChrome.ApplyRegion(stream);
        _captureRegionChrome.Show();
    }

    private CaptureRegionChromeWindow CreateCaptureRegionChrome()
    {
        var chrome = new CaptureRegionChromeWindow();
        chrome.AdjustRequested += (_, _) => BeginCaptureRegionPick();
        chrome.ClearRequested += (_, _) => ClearCaptureRegion();
        return chrome;
    }

    private void HideCaptureRegionChrome()
    {
        if (_captureRegionChrome is not null)
        {
            _captureRegionChrome.Hide();
        }
    }

    private void ShowAll()
    {
        _toolbar.Show();
        _overlay.Show();
        _toolbar.Activate();
        _toolbar.ClampToVisibleScreen();
    }

    private void ApplyHotkey(uint modifiers, uint virtualKey, bool quiet = false)
    {
        _settings.Current.HotkeyModifiers = modifiers;
        _settings.Current.HotkeyVirtualKey = virtualKey;
        _settings.Save();

        if (!_hotkeyService.Register(modifiers, virtualKey) && !quiet)
        {
            _toolbar.SetStatus("That shortcut is in use — pick another.");
        }

        _toolbar.RefreshHotkeyDisplay();
        _tray.ToolTipText = BuildTrayTooltip();
    }

    private string BuildTrayTooltip() =>
        $"OverInk — {HotkeySettingsHelper.Format(_settings.Current.HotkeyModifiers, _settings.Current.HotkeyVirtualKey)} toggles draw / pass-through";

    public void Dispose()
    {
        _captureRegionChrome?.Close();
        _editor?.Close();
        _captureLibrary.Dispose();
        _session.PersistSettings();
        _remoteServer.Dispose();
        _keyboardService.Dispose();
        _hotkeyService.Dispose();
        _tray.Dispose();
    }
}
