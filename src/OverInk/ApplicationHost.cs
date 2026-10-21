using System.Windows;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;
using H.NotifyIcon;
using OverInk.Editor;
using OverInk.Input;
using OverInk.Interop;
using OverInk.Models;
using OverInk.Overlay;
using OverInk.Remote;
using OverInk.Services;
using System.Windows.Media.Imaging;
using OverInk.Toolbar;

namespace OverInk;

public sealed class ApplicationHost : IDisposable
{
    private readonly SettingsService _settings = new();
    private readonly UpdateService _updates = new();
    private bool _updateCheckRunning;
    private bool _disposed;
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
        InstallerCloseHook.Watch(_toolbar);
        InstallerCloseHook.Watch(_overlay);
        _toolbar.CheckForUpdatesRequested += (_, _) => _ = CheckForUpdatesAsync(userInitiated: true);
        // Make the toolbar an owned window of the overlay: Windows keeps an owned window above its owner
        // in the Z-order no matter which one is active, so the screen veil can never cover the toolbar.
        new WindowInteropHelper(_toolbar).Owner = new WindowInteropHelper(_overlay).EnsureHandle();
        _overlay.SetToolbarWindow(_toolbar);
        _toolbar.PairingRequested += async (_, _) => await ShowPairingWindowAsync();
        _remoteServer = new RemoteSketchServer(_settings, _pairing, _strokeSource, _session, _overlay, _captureLibrary);
        _remoteServer.CaptureToLibraryHandler = CaptureForRemote;
        _remoteServer.ClientCountChanged += count =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _toolbar.SetRemoteConnected(count > 0, count);
                _overlay.SetRemoteActive(count > 0);
            });
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

        _toolbar.CaptureRequested += (_, e) => CaptureToEditor(e.Kind);
        _toolbar.SetRegionRequested += (_, _) => OnSetRegionToolbarClick();
        _toolbar.HideRequested += (_, _) => SetVisible(false);
        _toolbar.OpenEditorRequested += (_, _) => ShowEditor();
        _toolbar.HotkeyChanged += (_, args) => ApplyHotkey(args.Modifiers, args.VirtualKey);
        _toolbar.CaptureHotkeyChanged += (_, args) => ApplyCaptureHotkey(args.Modifiers, args.VirtualKey);
        _toolbar.AutoCopyChanged += (_, enabled) =>
        {
            _settings.Current.AutoCopyCaptures = enabled;
            _settings.Save();
        };
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
            if (!_hotkeyService.RegisterCapture(
                    _settings.Current.CaptureHotkeyModifiers, _settings.Current.CaptureHotkeyVirtualKey))
            {
                _toolbar.SetStatus("The capture shortcut is already used by another app — set a different one in Settings.");
            }
        };

        _hotkeyService.CaptureRegionRequested += (_, _) =>
            // Leave the WM_HOTKEY handler before opening the (modal) region picker.
            Application.Current.Dispatcher.BeginInvoke(new Action(CaptureRegionFromHotkey));

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

        _ = CheckForUpdatesOnStartupAsync();
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

        var libraryItem = new System.Windows.Controls.MenuItem
        {
            Header = "Open editor…",
            FontWeight = FontWeights.SemiBold
        };
        libraryItem.Click += (_, _) => ShowEditor();
        tray.ContextMenu.Items.Add(libraryItem);

        // Double-clicking the tray icon is the quickest way in.
        tray.TrayMouseDoubleClick += (_, _) => ShowEditor();

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

    private bool _capturing;
    private bool _lastCaptureCopied;

    private void CaptureToEditor(CaptureRequestKind kind = CaptureRequestKind.Default)
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        try
        {
            // Ink only ends up in the capture (and is only worth clearing) when the overlay was showing.
            var inkWasShown = _overlay.IsVisible;
            var item = CaptureToLibrary(kind);
            if (item is not null)
            {
                if (inkWasShown && _settings.Current.ClearDrawingAfterCapture)
                {
                    _overlay.ClearAll();
                }

                if (_settings.Current.ExitDrawModeOnCapture && _session.Mode == InteractionMode.Draw)
                {
                    _session.SetMode(InteractionMode.PassThrough);
                    _session.PersistSettings();
                }

                OpenEditor(item);
                if (_lastCaptureCopied)
                {
                    _editor?.NotifyCopied();
                }
            }
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>The global region-capture shortcut: drag an area, capture it, and open it in the editor.</summary>
    private void CaptureRegionFromHotkey() => CaptureToEditor(CaptureRequestKind.PickRegion);

    /// <summary>Puts the capture on the clipboard (image + file) when "auto-copy" is on. Never throws.</summary>
    private void AutoCopyToClipboard(BitmapSource flattened, CaptureItem item)
    {
        _lastCaptureCopied = false;
        if (!_settings.Current.AutoCopyCaptures)
        {
            return;
        }

        try
        {
            ImageTransfer.SetClipboardWithRetry(ImageTransfer.BuildDataObjectFromSavedFile(flattened, item.FlattenedPath));
            _lastCaptureCopied = true;
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"auto-copy failed: {ex.Message}");
        }
    }

    private void ApplyCaptureHotkey(uint modifiers, uint virtualKey)
    {
        _settings.Current.CaptureHotkeyModifiers = modifiers;
        _settings.Current.CaptureHotkeyVirtualKey = virtualKey;
        _settings.Save();

        if (!_hotkeyService.RegisterCapture(modifiers, virtualKey))
        {
            _toolbar.SetStatus("That shortcut is in use — pick another.");
        }

        _toolbar.RefreshHotkeyDisplay();
    }

    /// <summary>Captures the screen into the library without opening the editor. Null when cancelled.</summary>
    private CaptureItem? CaptureToLibrary(CaptureRequestKind kind)
    {
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        if (!TryResolveCapturePixels(kind, geometry, out var pixelBounds))
        {
            return null;
        }

        var (bitmap, ink, veil) = CaptureScreenWithInk(pixelBounds, geometry);
        var item = _captureLibrary.CreateFromCapture(bitmap, ink, veil, out var flattened);
        AutoCopyToClipboard(flattened, item);
        return item;
    }

    /// <summary>
    /// Grabs the clean screen (overlay ink hidden for the instant of the grab) together with whatever is drawn
    /// on the overlay, as strokes in image-pixel coordinates. The capture keeps that ink editable.
    /// </summary>
    private (BitmapSource Bitmap, StrokeCollection Ink, double Veil) CaptureScreenWithInk(Rect pixelBounds, MonitorGeometry geometry)
    {
        // A hidden overlay shows no ink on screen, so none belongs in the capture either.
        var includeInk = _overlay.IsVisible;
        // The veil is captured as a value (not baked into the screenshot) so the editor can adjust it.
        var veil = includeInk ? _overlay.VisibleVeilOpacity : 0;
        BitmapSource bitmap;
        using (includeInk ? _overlay.HideInkForCapture() : null)
        using (_settings.Current.CaptureToolbarInGrabs ? _toolbar.IncludeInCapture() : null)
        {
            bitmap = ScreenCaptureService.Capture(pixelBounds, CollectWindowsHiddenDuringCapture());
        }

        if (!includeInk)
        {
            return (bitmap, new StrokeCollection(), veil);
        }

        var scaleX = geometry.Dips.Width > 0 ? geometry.Pixels.Width / geometry.Dips.Width : 1;
        var scaleY = geometry.Dips.Height > 0 ? geometry.Pixels.Height / geometry.Dips.Height : 1;
        var toImage = new Matrix(scaleX, 0, 0, scaleY, geometry.Pixels.X - pixelBounds.X, geometry.Pixels.Y - pixelBounds.Y);
        var all = _overlay.ExportInk(toImage);

        // Drop strokes entirely outside a region capture.
        var imageRect = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var visible = new StrokeCollection();
        foreach (var stroke in all)
        {
            if (stroke.GetBounds().IntersectsWith(imageRect))
            {
                visible.Add(stroke);
            }
        }

        return (bitmap, visible, veil);
    }

    /// <summary>
    /// Capture requested from the iPad. Never opens the region picker or the PC editor, and refuses to save
    /// a black frame (locked session, display off, UAC prompt).
    /// </summary>
    private (string? Id, string? Error) CaptureForRemote()
    {
        try
        {
            var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
            if (!TryResolveCapturePixels(CaptureRequestKind.Default, geometry, out var pixelBounds))
            {
                return (null, "Capture was cancelled.");
            }

            var (bitmap, ink, veil) = CaptureScreenWithInk(pixelBounds, geometry);
            if (ScreenCaptureService.IsBlank(bitmap))
            {
                return (null, "Capture failed: the PC screen is blank or locked.");
            }

            return (_captureLibrary.CreateFromCapture(bitmap, ink, veil).Id, null);
        }
        catch (Exception ex)
        {
            RemoteDebugLog.Write($"remote capture failed: {ex.Message}");
            return (null, "Capture failed: the PC screen could not be read.");
        }
    }

    private bool TryResolveCapturePixels(CaptureRequestKind kind, MonitorGeometry geometry, out Rect pixelBounds)
    {
        pixelBounds = default;

        if (kind == CaptureRequestKind.PickRegion)
        {
            _captureRegionChrome?.Hide();
            var picker = new RegionPickerWindow("Click a window or drag an area · Esc cancel");
            // A hidden toolbar (auto-hide, or the shortcut used while not drawing) can't own a visible window.
            if (_toolbar.IsVisible)
            {
                picker.Owner = _toolbar;
            }

            if (picker.ShowDialog() != true || picker.SelectedDipsRect is not { } dipRect)
            {
                RestoreCaptureRegionChromeAfterPick();
                return false;
            }

            pixelBounds = CaptureRegionHelper.DipsRectToPixels(dipRect, geometry);
            return true;
        }

        if (_settings.Current.ToolbarCaptureMode == CaptureToolbarMode.FullScreen)
        {
            pixelBounds = geometry.Pixels;
            return true;
        }

        pixelBounds = _settings.Current.RemoteRegionEnabled
            ? CaptureRegionHelper.GetStreamPixels(_settings.Current)
            : geometry.Pixels;
        return true;
    }

    private Window[] CollectWindowsHiddenDuringCapture()
    {
        // The toolbar is normally hidden for the grab; the "toolbar in grabs" option keeps it (see IncludeInCapture).
        var list = new List<Window>();
        if (!_settings.Current.CaptureToolbarInGrabs)
        {
            list.Add(_toolbar);
        }

        if (_captureRegionChrome is { IsVisible: true })
        {
            list.Add(_captureRegionChrome);
        }

        return list.ToArray();
    }

    private void RestoreCaptureRegionChromeAfterPick()
    {
        if (_settings.Current.RemoteRegionEnabled)
        {
            ShowCaptureRegionChrome();
        }
    }

    /// <summary>
    /// Opens the editor from the toolbar, tray icon or Start menu. An already-open editor is just brought
    /// to the front (keeping the capture you were on); otherwise the newest capture is opened, or an empty
    /// editor when the library has no captures yet.
    /// </summary>
    public void ShowEditor()
    {
        // The overlay swallows mouse input while drawing, which would leave the editor unusable.
        if (_session.Mode == InteractionMode.Draw)
        {
            _session.SetMode(InteractionMode.PassThrough);
            _session.PersistSettings();
        }

        var existing = _editor is not null;
        var editor = EnsureEditor();
        if (!existing && _captureLibrary.Enumerate().FirstOrDefault() is { } newest)
        {
            editor.OpenItem(newest);
        }

        BringEditorToFront(editor);
    }

    private void OpenEditorLibrary() => ShowEditor();

    private void OpenEditor(CaptureItem item)
    {
        var editor = EnsureEditor();
        editor.OpenItem(item);
        BringEditorToFront(editor);
    }

    private EditorWindow EnsureEditor()
    {
        if (_editor is null)
        {
            _editor = new EditorWindow(_captureLibrary, _session.PenColor, _session.PenThickness, _session.CustomColors);
            _editor.Closed += (_, _) => _editor = null;
        }

        return _editor;
    }

    private static void BringEditorToFront(EditorWindow editor)
    {
        editor.Show();
        if (editor.WindowState == WindowState.Minimized)
        {
            editor.WindowState = WindowState.Normal;
        }

        editor.Activate();
    }

    /// <summary>Reveals the toolbar and overlay (used by the tray menu and when OverInk is launched again).</summary>
    public void ShowToolbar() => ShowToolbarFromTray();

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

        _toolbar.RefreshCaptureButtonHint();
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
        var picker = new RegionPickerWindow("Click a window or drag the stream region · Esc cancel")
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

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            // After a successful update the installer that just ran is still on disk; tidy it up.
            UpdateService.CleanUpDownloads(AppVersion.Current);
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (_disposed || _updateCheckRunning)
                {
                    return;
                }

                await CheckForUpdatesAsync(userInitiated: false);
            }).Task.Unwrap();
        }
        catch (Exception)
        {
            // A failed background check stays quiet; Check now reports the error.
        }
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckRunning || _disposed)
        {
            return;
        }

        _updateCheckRunning = true;
        _toolbar.SetUpdateCheckBusy(true);
        var launched = false;
        try
        {
            var result = await _updates.CheckAsync(_settings.Current.IncludeBetaUpdates);
            if (_disposed)
            {
                return;
            }

            if (result.ReachedServer)
            {
                _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
                _settings.Save();
            }

            _toolbar.RefreshUpdateDetails();
            // A quiet startup check shouldn't surface a network blip. Check now still reports it.
            if (result.Status == UpdateCheckStatus.Failed && !userInitiated && !result.ReachedServer)
            {
                return;
            }

            _toolbar.SetUpdateStatus(result.Message, result.Status == UpdateCheckStatus.Failed);

            if (result.Offer is not { } offer || result.Status != UpdateCheckStatus.UpdateAvailable)
            {
                return;
            }

            var skipped = !userInitiated && !UpdateReminder.ShouldPromptAutomatically(_settings.Current, offer.Version);
            if (skipped)
            {
                return;
            }

            if (result.CanInstall)
            {
                var install = Ask(
                    $"OverInk {offer.Version} is available. You're running {AppVersion.Current}.\n\nOverInk will close, install the update, and reopen. If Windows asks, choose Run anyway or Yes.",
                    "Update now",
                    "Not now",
                    offer.Notes);
                if (!install)
                {
                    UpdateReminder.RecordDismissal(_settings.Current, offer.Version);
                    _settings.Save();
                    return;
                }

                launched = await InstallUpdateAsync(offer);
            }
            else if (userInitiated)
            {
                var open = Ask(
                    $"OverInk {offer.Version} is available. You're running {AppVersion.Current}.\n\nThis copy wasn't installed with OverInk Setup, so it can't update itself.",
                    "Open releases",
                    "Not now",
                    offer.Notes);
                if (open)
                {
                    UpdateService.OpenReleasesPage();
                }
            }
        }
        finally
        {
            if (!_disposed && !launched)
            {
                _updateCheckRunning = false;
                _toolbar.SetUpdateCheckBusy(false);
            }
        }
    }

    private async Task<bool> InstallUpdateAsync(UpdateOffer offer)
    {
        try
        {
            _toolbar.SetUpdateCheckBusy(true, "Downloading…");
            _toolbar.SetUpdateStatus("Downloading update…");
            var setupPath = await _updates.DownloadVerifiedInstallerAsync(offer);
            if (_disposed)
            {
                return false;
            }

            // Setup refuses to run (silently cancels) while this copy holds the single-instance mutex, because
            // that is what its AppMutex check looks for. Let go of it, then Setup closes this copy itself.
            App.ReleaseSingleInstanceMutex();
            try
            {
                UpdateService.LaunchInstaller(setupPath);
            }
            catch
            {
                App.ReacquireSingleInstanceMutex();
                throw;
            }

            _toolbar.SetUpdateCheckBusy(true, "Installing…");
            _toolbar.SetUpdateStatus("Installing… OverInk will reopen when it finishes.");
            _ = ReenableUpdateButtonIfStillRunningAsync();
            return true;
        }
        catch (Exception ex)
        {
            _toolbar.SetUpdateStatus(ex.Message, isError: true);
            Ask(ex.Message, "OK", null);
            return false;
        }
    }

    /// <summary>
    /// The installer is supposed to close this process. If it never does, leave Check now usable again.
    /// </summary>
    private async Task ReenableUpdateButtonIfStillRunningAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        var app = Application.Current;
        if (_disposed || app == null)
        {
            return;
        }

        await app.Dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            _updateCheckRunning = false;
            App.ReacquireSingleInstanceMutex();
            _toolbar.SetUpdateCheckBusy(false);
            _toolbar.SetUpdateStatus("The installer didn't finish. You can try again.", isError: true);
        });
    }

    private bool Ask(string message, string confirm, string? cancel, string? notes = null)
    {
        var dialog = new UpdatePromptWindow(message, confirm, cancel, notes)
        {
            Owner = _overlay,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        return dialog.ShowDialog() == true;
    }

    public void Dispose()
    {
        _disposed = true;
        _updates.Dispose();
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
