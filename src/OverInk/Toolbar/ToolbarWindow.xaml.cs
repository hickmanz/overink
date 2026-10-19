using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OverInk.Interop;
using OverInk.Models;
using OverInk.Overlay;
using OverInk.Services;

namespace OverInk.Toolbar;

public partial class ToolbarWindow : Window
{
    private readonly SketchSession _session;
    private readonly OverlayWindow _overlay;
    private Point _dragStart;
    private bool _dragging;
    private bool _recordingHotkey;
    private bool _syncingSettingsUi;

    public event EventHandler? CaptureRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? BoundsChanged;
    public event EventHandler? PairingRequested;
    public event EventHandler<bool>? RemoteServerEnabledChanged;
    public event EventHandler<bool>? HideToolbarWhenNotDrawingChanged;
    public event EventHandler? SetRegionRequested;
    public event EventHandler<HotkeyChangedEventArgs>? HotkeyChanged;

    public ToolbarWindow(SketchSession session, OverlayWindow overlay)
    {
        _session = session;
        _overlay = overlay;
        InitializeComponent();
        BrandLogo.Source = BrandIcons.CreateToolbarImage(22);
        BuildColorSwatches();
        ColorPicker.SetColor(session.PenColor);

        Left = session.Settings.Current.ToolbarLeft;
        Top = session.Settings.Current.ToolbarTop;
        ThicknessSlider.Value = session.PenThickness;

        _session.Changed += SyncFromSession;
        _session.UndoStack.Changed += (_, _) => SyncFromSession();
        SyncFromSession();
        RefreshHotkeyDisplay();

        LocationChanged += (_, _) =>
        {
            SavePosition();
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        };

        SizeChanged += (_, _) => BoundsChanged?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) =>
        {
            ClampToVisibleScreen();
            BoundsChanged?.Invoke(this, EventArgs.Empty);
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
            StreamSettings.Bind(_session.Settings);
        };
    }

    public void ShowSettingsPanel()
    {
        SettingsPanel.Visibility = Visibility.Visible;
        RefreshHotkeyDisplay();
        SyncSettingsCheckboxes();
        StreamSettings.Bind(_session.Settings);
        StreamSettings.RefreshSummary();
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRemoteServerChecked(bool enabled)
    {
        _syncingSettingsUi = true;
        RemoteServerCheck.IsChecked = enabled;
        _syncingSettingsUi = false;
    }

    public void SetHideToolbarWhenNotDrawing(bool enabled)
    {
        _syncingSettingsUi = true;
        HideToolbarWhenNotDrawingCheck.IsChecked = enabled;
        _syncingSettingsUi = false;
    }

    private void SyncSettingsCheckboxes()
    {
        _syncingSettingsUi = true;
        HideToolbarWhenNotDrawingCheck.IsChecked = _session.Settings.Current.HideToolbarWhenNotDrawing;
        RemoteServerCheck.IsChecked = _session.Settings.Current.RemoteEnabled;
        _syncingSettingsUi = false;
    }

    public void SetRemoteConnected(bool connected, int count)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetRemoteConnected(connected, count));
            return;
        }

        RemoteDot.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        RemoteDot.ToolTip = connected ? $"{count} remote client(s) connected" : "Remote client connected";
    }

    public void SetStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    public void RefreshHotkeyDisplay()
    {
        var settings = _session.Settings.Current;
        HotkeyDisplayText.Text = HotkeySettingsHelper.Format(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
    }

    public void ClampToVisibleScreen()
    {
        var dips = MonitorHelper.GetPrimaryMonitorGeometry().Dips;
        if (Left + Width > dips.Right)
        {
            Left = dips.Right - Width - 16;
        }

        if (Top + ActualHeight > dips.Bottom)
        {
            Top = dips.Bottom - ActualHeight - 16;
        }

        if (Left < dips.Left)
        {
            Left = dips.Left + 16;
        }

        if (Top < dips.Top)
        {
            Top = dips.Top + 16;
        }
    }

    private void SyncFromSession()
    {
        UpdateDrawToggle();
        ThicknessSlider.Value = _session.PenThickness;
        UpdateToolButtons();
        UpdateColorSwatches();
    }

    private void UpdateDrawToggle()
    {
        var drawing = _session.Mode == InteractionMode.Draw;
        DrawToggle.IsChecked = drawing;
        DrawToggle.Content = drawing ? "●  Drawing" : "Pass-through";
        DrawToggle.ToolTip = drawing
            ? "Drawing on screen — ink captures pointer (Esc or toggle for pass-through)"
            : "Pass-through — use apps normally (toggle on to draw)";
    }

    private void UpdateToolButtons()
    {
        foreach (var child in ToolPanel.Children)
        {
            if (child is ToggleButton toggle && toggle.Tag is string tag &&
                Enum.TryParse<SketchTool>(tag, out var tool))
            {
                toggle.IsChecked = _session.Tool == tool;
            }
        }
    }

    private void BuildColorSwatches()
    {
        foreach (var hex in PresetPalette.HexColors)
        {
            if (!PresetPalette.TryParseHex(hex, out var color))
            {
                continue;
            }

            var swatch = new ToggleButton
            {
                Style = (Style)FindResource("ColorSwatch"),
                Background = new SolidColorBrush(color),
                Tag = hex,
                ToolTip = hex
            };
            swatch.Click += ColorSwatch_Click;
            SwatchPanel.Children.Add(swatch);
        }
    }

    private void UpdateColorSwatches()
    {
        var current = ColorHex.ToRgbHex(_session.PenColor);
        foreach (var child in SwatchPanel.Children)
        {
            if (child is ToggleButton swatch && swatch.Tag is string hex)
            {
                swatch.IsChecked = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            }
        }

        CustomColorButton.Background = new SolidColorBrush(_session.PenColor);
        CustomColorButton.IsChecked = !PresetPalette.IsPreset(_session.PenColor);
    }

    private void SavePosition()
    {
        _session.Settings.Current.ToolbarLeft = Left;
        _session.Settings.Current.ToolbarTop = Top;
        _session.PersistSettings();
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void DragBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var screen = PointToScreen(e.GetPosition(this));
        Left = screen.X - _dragStart.X;
        Top = screen.Y - _dragStart.Y;
    }

    private void DragBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }
    }

    private void DrawToggle_Click(object sender, RoutedEventArgs e)
    {
        _session.SetMode(DrawToggle.IsChecked == true ? InteractionMode.Draw : InteractionMode.PassThrough);
        _session.PersistSettings();
        StatusText.Text = string.Empty;
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } &&
            Enum.TryParse<SketchTool>(tag, out var tool))
        {
            _session.SetTool(tool);
            _session.PersistSettings();
        }
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string hex })
        {
            var color = ColorHex.Parse(hex);
            _session.SetColor(color);
            _session.PersistSettings();
            ColorPickerPopup.IsOpen = false;
            UpdateColorSwatches();
        }
    }

    private void CustomColorButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPicker.SetColor(_session.PenColor);
        ColorPickerPopup.IsOpen = true;
    }

    private void ColorPicker_ColorChanged(object sender, Color color)
    {
        _session.SetColor(color);
        _session.PersistSettings();
        UpdateColorSwatches();
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _session.SetThickness(e.NewValue);
        _session.PersistSettings();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _overlay.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _overlay.Redo();
    private void Clear_Click(object sender, RoutedEventArgs e) => _overlay.ClearAll();
    private void Capture_Click(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(this, EventArgs.Empty);

    private void SetRegion_Click(object sender, RoutedEventArgs e) => SetRegionRequested?.Invoke(this, EventArgs.Empty);

    public void SetCaptureRegionActive(bool active)
    {
        SetRegionButton.IsChecked = active;
        SetRegionButton.Content = active ? "Region on" : "Set region";
        SetRegionButton.ToolTip = active
            ? "Region on — click to turn off (full-screen remote stream)"
            : "Click to drag a capture region for remote clients";
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void PairRemote_Click(object sender, RoutedEventArgs e) => PairingRequested?.Invoke(this, EventArgs.Empty);

    private void RemoteServerCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        RemoteServerEnabledChanged?.Invoke(this, RemoteServerCheck.IsChecked == true);
    }

    private void HideToolbarWhenNotDrawing_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        HideToolbarWhenNotDrawingChanged?.Invoke(this, HideToolbarWhenNotDrawingCheck.IsChecked == true);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = SettingsPanel.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening)
        {
            RefreshHotkeyDisplay();
            SyncSettingsCheckboxes();
            StreamSettings.Bind(_session.Settings);
            StreamSettings.RefreshSummary();
        }

        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        _session.IsHotkeyCaptureActive = true;
        HotkeyHintText.Text = "Press your shortcut now… (Esc to cancel)";
        RecordHotkeyButton.Content = "Listening…";
        SetStatus(string.Empty);
        Activate();
        Focus();
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyHotkey(HotkeySettingsHelper.DefaultModifiers, HotkeySettingsHelper.DefaultVirtualKey);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelHotkeyRecording();
            return;
        }

        if (!HotkeySettingsHelper.TryCreateFromKey(e.Key, out var modifiers, out var virtualKey))
        {
            return;
        }

        ApplyHotkey(modifiers, virtualKey);
    }

    private void ApplyHotkey(uint modifiers, uint virtualKey)
    {
        _recordingHotkey = false;
        _session.IsHotkeyCaptureActive = false;
        RecordHotkeyButton.Content = "Change shortcut";
        HotkeyHintText.Text = "Esc turns draw off · Ctrl+Z undo while draw is on";
        HotkeyChanged?.Invoke(this, new HotkeyChangedEventArgs(modifiers, virtualKey));
        RefreshHotkeyDisplay();
    }

    private void CancelHotkeyRecording()
    {
        _recordingHotkey = false;
        _session.IsHotkeyCaptureActive = false;
        RecordHotkeyButton.Content = "Change shortcut";
        HotkeyHintText.Text = "Esc turns draw off · Ctrl+Z undo while draw is on";
    }
}

public sealed class HotkeyChangedEventArgs : EventArgs
{
    public HotkeyChangedEventArgs(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public uint Modifiers { get; }
    public uint VirtualKey { get; }
}
