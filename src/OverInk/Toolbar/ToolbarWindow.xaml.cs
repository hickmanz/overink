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
    private bool _vertical;
    private Style? _dividerStyle;

    public event EventHandler<CaptureRequestedEventArgs>? CaptureRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? OpenEditorRequested;
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

        _dividerStyle = (Style)FindResource("ToolbarDivider");
        if (session.Settings.Current.ToolbarVertical)
        {
            SetVertical(true, keepGripInPlace: false);
        }

        _session.Changed += SyncFromSession;
        _session.UndoStack.Changed += (_, _) => SyncFromSession();
        SyncFromSession();
        RefreshHotkeyDisplay();
        RefreshCaptureButtonHint();

        LocationChanged += (_, _) =>
        {
            SavePosition();
            UpdatePopupPlacement();
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        };

        SizeChanged += (_, _) => BoundsChanged?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) =>
        {
            // A vertical layout restored at startup can leave the window wider than its content
            // (stale first-show size); re-arming SizeToContent snaps it back to the content size.
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.WidthAndHeight;
            UpdateLayout();
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
        if (Left + ActualWidth > dips.Right)
        {
            Left = dips.Right - ActualWidth - 16;
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
        if (PressureCheck.IsChecked != _session.PressureSensitive)
        {
            PressureCheck.IsChecked = _session.PressureSensitive;
        }

        UpdateToolButtons();
        UpdateColorSwatches();
        UpdateStrokeVisuals();
        UpdateVeilVisuals();
    }

    private void UpdateVeilVisuals()
    {
        if (VeilSlider is null || VeilValue is null)
        {
            return;
        }

        var percent = Math.Round(_session.ScreenVeil * 100);
        if (Math.Abs(VeilSlider.Value - percent) > 0.01)
        {
            VeilSlider.Value = percent;
        }

        VeilValue.Text = percent <= 0 ? "Off" : $"{(int)percent}%";
        VeilButton.ToolTip = percent <= 0 ? "Screen veil (off)" : $"Screen veil ({(int)percent}%)";
    }

    private void VeilSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _session.SetScreenVeil(e.NewValue / 100.0);
        _session.PersistSettings();
    }

    /// <summary>
    /// The stroke button and its popover preview show a curved line whose thickness tracks the
    /// width setting (1–20 maps to 1.25–8 units on the 28x24 icon grid, same as the iPad dock).
    /// </summary>
    private void UpdateStrokeVisuals()
    {
        if (StrokeIconPath is null || StrokePreviewPath is null || ThicknessValue is null)
        {
            return;
        }

        var width = Math.Clamp(ThicknessSlider.Value, 1, 20);
        var units = 1.25 + (width - 1) / 19.0 * 6.75;
        StrokeIconPath.StrokeThickness = units;
        StrokePreviewPath.StrokeThickness = units;
        StrokePreviewPath.Stroke = new SolidColorBrush(_session.PenColor);
        ThicknessValue.Text = ((int)Math.Round(width)).ToString();
    }

    private void UpdateDrawToggle()
    {
        var drawing = _session.Mode == InteractionMode.Draw;
        DrawToggle.IsChecked = drawing;
        DrawToggle.Content = _vertical ? "Draw" : drawing ? "Drawing" : "Draw mode";
        DrawToggle.ToolTip = drawing
            ? "Drawing on screen — Esc or toggle off to use apps normally"
            : "Turn on to sketch on screen (Esc turns off when drawing)";
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
                Style = (Style)FindResource("GridSwatch"),
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
        ColorDot.Fill = new SolidColorBrush(_session.PenColor);
        UpdateStrokeVisuals();
    }

    private void SavePosition()
    {
        _session.Settings.Current.ToolbarLeft = Left;
        _session.Settings.Current.ToolbarTop = Top;
        _session.PersistSettings();
    }

    /// <summary>
    /// Lays the dock out horizontally or vertically. The double-clicked grip stays under the cursor
    /// (when <paramref name="keepGripInPlace"/>) so the bar appears to rotate around the handle.
    /// </summary>
    private void SetVertical(bool vertical, bool keepGripInPlace)
    {
        var before = keepGripInPlace ? GripCenterOnScreen() : (Point?)null;
        _vertical = vertical;

        var panelOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DockRow.Orientation = panelOrientation;
        BrandGroup.Orientation = panelOrientation;
        BrandGroup.Margin = vertical ? new Thickness(0, 2, 0, 4) : new Thickness(2, 0, 4, 0);
        ToolPanel.Orientation = panelOrientation;
        ColorGroup.Orientation = panelOrientation;
        UndoGroup.Orientation = panelOrientation;
        CaptureGroup.Orientation = panelOrientation;
        SettingsGroup.Orientation = panelOrientation;
        CaptureSplitPanel.Orientation = panelOrientation;

        // Settings panel opens beside a vertical dock instead of under it.
        RootStack.Orientation = panelOrientation == Orientation.Vertical ? Orientation.Horizontal : Orientation.Vertical;
        SettingsPanel.Margin = vertical ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        SettingsPanel.VerticalAlignment = VerticalAlignment.Top;
        StatusText.MaxWidth = vertical ? 220 : double.PositiveInfinity;
        StatusText.Margin = vertical ? new Thickness(8, 4, 4, 0) : new Thickness(4, 6, 4, 0);

        // Grip: swap footprint and rotate the dots to match the bar direction.
        DragGrip.Width = vertical ? 34 : 24;
        DragGrip.Height = vertical ? 24 : 34;
        GripIcon.RenderTransform = vertical ? new RotateTransform(90) : Transform.Identity;

        // Dividers become horizontal rules in a vertical dock.
        foreach (var child in DockRow.Children.OfType<Border>())
        {
            if (!ReferenceEquals(child.Style, _dividerStyle))
            {
                continue;
            }

            if (vertical)
            {
                child.Width = 24;
                child.Height = 1;
                child.Margin = new Thickness(0, 7, 0, 7);
                child.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                child.ClearValue(WidthProperty);
                child.ClearValue(HeightProperty);
                child.ClearValue(MarginProperty);
                child.ClearValue(HorizontalAlignmentProperty);
            }
        }

        // Draw pill: compact "Draw" chip that is the same width as the buttons below it.
        DrawToggle.MinWidth = vertical ? 0 : 104;
        DrawToggle.Width = vertical ? 36 : double.NaN;
        DrawToggle.Padding = vertical ? new Thickness(0) : new Thickness(14, 0, 14, 0);
        DrawToggle.FontSize = vertical ? 11 : 12.5;
        UpdateDrawToggle();

        // Capture: camera-only button stacked over the chevron.
        CaptureLabel.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        CaptureSplit.Margin = vertical ? new Thickness(1, 4, 1, 1) : new Thickness(4, 0, 0, 0);
        EditorButton.Margin = vertical ? new Thickness(1, 5, 1, 1) : new Thickness(5, 1, 1, 1);
        CaptureMainButton.MinWidth = vertical ? 34 : 68;
        CaptureMainButton.Padding = vertical ? new Thickness(0) : new Thickness(12, 0, 12, 0);
        CaptureMenuButton.MinWidth = vertical ? 34 : 28;
        CaptureMenuButton.Padding = vertical ? new Thickness(0) : new Thickness(6, 0, 6, 0);
        CaptureMenuButton.Height = vertical ? 24 : 34;
        CaptureSplitDivider.Width = vertical ? double.NaN : 1;
        CaptureSplitDivider.Height = vertical ? 1 : double.NaN;
        CaptureSplitDivider.Margin = vertical ? new Thickness(7, 0, 7, 0) : new Thickness(0, 7, 0, 7);

        UpdatePopupPlacement();

        if (before is { } anchor)
        {
            UpdateLayout();
            var after = GripCenterOnScreen();
            Left += anchor.X - after.X;
            Top += anchor.Y - after.Y;
            ClampToVisibleScreen();
        }

        if (_session.Settings.Current.ToolbarVertical != vertical)
        {
            _session.Settings.Current.ToolbarVertical = vertical;
            _session.PersistSettings();
        }

        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Center of the drag grip in screen DIPs (same space as Left/Top).</summary>
    private Point GripCenterOnScreen()
    {
        var device = DragGrip.PointToScreen(new Point(DragGrip.ActualWidth / 2, DragGrip.ActualHeight / 2));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(device)
            : device;
    }

    /// <summary>Popovers open above a horizontal bar, and to the open side of a vertical one.</summary>
    private void UpdatePopupPlacement()
    {
        PlacementMode placement;
        if (!_vertical)
        {
            placement = PlacementMode.Top;
        }
        else
        {
            var dips = MonitorHelper.GetPrimaryMonitorGeometry().Dips;
            var center = Left + ActualWidth / 2;
            placement = center > (dips.Left + dips.Right) / 2 ? PlacementMode.Left : PlacementMode.Right;
        }

        ColorPickerPopup.Placement = placement;
        StrokePopup.Placement = placement;
        VeilPopup.Placement = placement;
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            _dragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
            SetVertical(!_vertical, keepGripInPlace: true);
            e.Handled = true;
            return;
        }

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
        // Expand / collapse the wheel inside the color popover.
        var show = ColorPicker.Visibility != Visibility.Visible;
        if (show)
        {
            ColorPicker.SetColor(_session.PenColor);
        }

        ColorPicker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The button's IsChecked mirrors "color is not a preset"; undo the click's own toggle.
        UpdateColorSwatches();
    }

    private void ColorPopup_Opened(object? sender, EventArgs e)
    {
        ColorPicker.Visibility = Visibility.Collapsed;
        UpdateColorSwatches();
    }

    private void ColorPopup_Closed(object? sender, EventArgs e)
    {
        ColorPicker.Visibility = Visibility.Collapsed;
    }

    private void ColorPicker_ColorChanged(object sender, Color color)
    {
        _session.SetColor(color);
        _session.PersistSettings();
        UpdateColorSwatches();
    }

    private void PressureCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _session.SetPressureSensitive(PressureCheck.IsChecked == true);
        _session.PersistSettings();
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateStrokeVisuals();
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
    private void Capture_Click(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(this, new CaptureRequestedEventArgs { Kind = CaptureRequestKind.Default });

    private void CaptureMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("ShellBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };

        var auto = new MenuItem
        {
            Header = "Auto — active region if set, else full screen",
            IsCheckable = true,
            IsChecked = _session.Settings.Current.ToolbarCaptureMode == CaptureToolbarMode.Auto
        };
        auto.Click += (_, _) => SetCaptureMode(CaptureToolbarMode.Auto);
        menu.Items.Add(auto);

        var full = new MenuItem
        {
            Header = "Full screen",
            IsCheckable = true,
            IsChecked = _session.Settings.Current.ToolbarCaptureMode == CaptureToolbarMode.FullScreen
        };
        full.Click += (_, _) => SetCaptureMode(CaptureToolbarMode.FullScreen);
        menu.Items.Add(full);

        menu.Items.Add(new Separator());

        var pick = new MenuItem { Header = "Select region for this capture…" };
        pick.Click += (_, _) =>
            CaptureRequested?.Invoke(this, new CaptureRequestedEventArgs { Kind = CaptureRequestKind.PickRegion });
        menu.Items.Add(pick);

        menu.PlacementTarget = CaptureMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SetCaptureMode(CaptureToolbarMode mode)
    {
        _session.Settings.Current.ToolbarCaptureMode = mode;
        _session.Settings.Save();
        RefreshCaptureButtonHint();
    }

    public void RefreshCaptureButtonHint()
    {
        if (CaptureMainButton is null)
        {
            return;
        }

        var regionOn = _session.Settings.Current.RemoteRegionEnabled;
        var mode = _session.Settings.Current.ToolbarCaptureMode;
        CaptureMainButton.ToolTip = mode switch
        {
            CaptureToolbarMode.FullScreen => "Capture full screen with ink",
            CaptureToolbarMode.Auto when regionOn =>
                "Capture active region with ink (▾ for full screen or one-off region)",
            CaptureToolbarMode.Auto => "Capture full screen with ink (uses active stream region when region is on)",
            _ => "Capture with ink"
        };
    }

    private void SetRegion_Click(object sender, RoutedEventArgs e) => SetRegionRequested?.Invoke(this, EventArgs.Empty);

    public void SetCaptureRegionActive(bool active)
    {
        SetRegionButton.IsChecked = active;
        SetRegionButton.ToolTip = active
            ? "Region on — click to turn off (full-screen remote stream)"
            : "Click to drag a capture region for remote clients";
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e) => OpenEditorRequested?.Invoke(this, EventArgs.Empty);

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
        if (opening)
        {
            // The panel can push the window past the screen edge (e.g. beside a vertical dock on the right).
            Dispatcher.BeginInvoke(ClampToVisibleScreen, System.Windows.Threading.DispatcherPriority.Loaded);
        }
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
        RecordHotkeyButton.Content = "Change";
        HotkeyHintText.Text = "Esc turns draw off · Ctrl+Z undo while draw is on";
        HotkeyChanged?.Invoke(this, new HotkeyChangedEventArgs(modifiers, virtualKey));
        RefreshHotkeyDisplay();
    }

    private void CancelHotkeyRecording()
    {
        _recordingHotkey = false;
        _session.IsHotkeyCaptureActive = false;
        RecordHotkeyButton.Content = "Change";
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
