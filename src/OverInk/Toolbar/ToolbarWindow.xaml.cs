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
    private bool _recordingCaptureHotkey;
    private DateTime _capturePopupClosedAt = DateTime.MinValue;
    private bool _syncingSettingsUi;
    private bool _vertical;
    private Style? _dividerStyle;
    private SketchTool _lastShapeTool = SketchTool.Pen;
    private DateTime _toolPopupClosedAt = DateTime.MinValue;

    public event EventHandler<CaptureRequestedEventArgs>? CaptureRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? OpenEditorRequested;
    public event EventHandler? BoundsChanged;
    public event EventHandler? PairingRequested;
    public event EventHandler<bool>? RemoteServerEnabledChanged;
    public event EventHandler<bool>? HideToolbarWhenNotDrawingChanged;
    public event EventHandler? SetRegionRequested;
    public event EventHandler<HotkeyChangedEventArgs>? HotkeyChanged;
    public event EventHandler<HotkeyChangedEventArgs>? CaptureHotkeyChanged;
    public event EventHandler<bool>? AutoCopyChanged;
    public event EventHandler? CheckForUpdatesRequested;

    public ToolbarWindow(SketchSession session, OverlayWindow overlay)
    {
        _session = session;
        _overlay = overlay;
        InitializeComponent();
        BrandLogo.Source = BrandIcons.CreateToolbarImage(22);
        BuildColorSwatches();
        ColorPicker.SetColor(session.PenColor);

        RefreshUpdateDetails();

        Left = session.Settings.Current.ToolbarLeft;
        Top = session.Settings.Current.ToolbarTop;
        ThicknessSlider.Value = session.PenThickness;

        _dividerStyle = (Style)FindResource("ToolbarDivider");
        if (session.Settings.Current.ToolbarVertical)
        {
            SetVertical(true, keepGripInPlace: false);
        }

        if (session.Tool != SketchTool.Eraser)
        {
            _lastShapeTool = session.Tool;
        }

        ApplyToolLayout();

        _session.CustomColors.Changed += CustomColors_Changed;
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
        RefreshUpdateDetails();
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
        CompactToolsCheck.IsChecked = _session.Settings.Current.CompactToolbarTools;
        ExitDrawOnCaptureCheck.IsChecked = _session.Settings.Current.ExitDrawModeOnCapture;
        ClearAfterCaptureCheck.IsChecked = _session.Settings.Current.ClearDrawingAfterCapture;
        ToolbarInGrabsCheck.IsChecked = _session.Settings.Current.CaptureToolbarInGrabs;
        RemoteServerCheck.IsChecked = _session.Settings.Current.RemoteEnabled;
        AutoCopyCheck.IsChecked = _session.Settings.Current.AutoCopyCaptures;
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

        var captureBound = settings.CaptureHotkeyVirtualKey != 0;
        var captureText = captureBound
            ? HotkeySettingsHelper.Format(settings.CaptureHotkeyModifiers, settings.CaptureHotkeyVirtualKey)
            : "Not set";
        CaptureHotkeyDisplayText.Text = captureText;
        CaptureHotkeyDisplayText.Foreground = (Brush)FindResource(captureBound ? "TextBrush" : "MutedTextBrush");
        CapturePopupHotkeyText.Text = captureBound ? captureText : "No shortcut";
        ClearCaptureHotkeyButton.IsEnabled = captureBound;
        ClearCaptureHotkeyButton.Opacity = captureBound ? 1 : 0.45;
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
        if (_session.Tool != SketchTool.Eraser)
        {
            _lastShapeTool = _session.Tool;
        }

        var toggles = ToolPanel.Children.OfType<ToggleButton>()
            .Concat(ToolOptionsPanel.Children.OfType<ToggleButton>())
            .Append(EraserQuickButton);
        foreach (var toggle in toggles)
        {
            if (toggle.Tag is string tag && Enum.TryParse<SketchTool>(tag, out var tool))
            {
                toggle.IsChecked = _session.Tool == tool;
            }
        }

        // The compact button shows the drawing tool you last used, lit while it's the active one.
        ShapeToolButton.IsChecked = _session.Tool != SketchTool.Eraser;
        ShapeToolIcon.Tag = FindResource("Icon" + _lastShapeTool);
        ShapeToolButton.ToolTip = _session.Tool == SketchTool.Eraser
            ? $"{_lastShapeTool} — click to draw again"
            : $"{_lastShapeTool} — click again for more tools";
    }

    /// <summary>Shows either the six separate tool buttons or the compact pair (drawing tool + eraser).</summary>
    private void ApplyToolLayout()
    {
        var compact = _session.Settings.Current.CompactToolbarTools;
        ToolPopup.IsOpen = false;
        ToolPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ToolMenuGroup.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The toolbar is normally excluded from every screen capture (so it never shows in grabs or in the tablet's
    /// stream). This lifts that exclusion until the returned object is disposed, for a grab that should include it.
    /// </summary>
    public IDisposable IncludeInCapture()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return new RestoreAffinity(null);
        }

        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaNone);
        return new RestoreAffinity(handle);
    }

    private sealed class RestoreAffinity(IntPtr? handle) : IDisposable
    {
        private IntPtr? _handle = handle;

        public void Dispose()
        {
            if (_handle is { } h)
            {
                NativeMethods.SetWindowDisplayAffinity(h, NativeMethods.WdaExcludeFromCapture);
                _handle = null;
            }
        }
    }

    private void CaptureBehavior_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _session.Settings.Current.ExitDrawModeOnCapture = ExitDrawOnCaptureCheck.IsChecked == true;
        _session.Settings.Current.ClearDrawingAfterCapture = ClearAfterCaptureCheck.IsChecked == true;
        _session.Settings.Current.CaptureToolbarInGrabs = ToolbarInGrabsCheck.IsChecked == true;
        _session.PersistSettings();
    }

    private void CompactTools_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi)
        {
            return;
        }

        _session.Settings.Current.CompactToolbarTools = CompactToolsCheck.IsChecked == true;
        _session.PersistSettings();
        ApplyToolLayout();
    }

    private void ShapeToolButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session.Tool == SketchTool.Eraser)
            {
                // Back from the eraser: the first click just resumes the last drawing tool.
                _session.SetTool(_lastShapeTool);
                _session.PersistSettings();
                return;
            }

            if (ToolPopup.IsOpen)
            {
                ToolPopup.IsOpen = false;
                return;
            }

            // Clicking the button while the list is open closes it (focus loss) and then fires Click;
            // don't pop it straight back open.
            if ((DateTime.UtcNow - _toolPopupClosedAt).TotalMilliseconds < 250)
            {
                return;
            }

            ToolPopup.IsOpen = true;
        }
        finally
        {
            // The toggle flips itself on click; put its lit state back to "a drawing tool is active".
            UpdateToolButtons();
        }
    }

    private void ToolPopup_Closed(object? sender, EventArgs e) => _toolPopupClosedAt = DateTime.UtcNow;

    /// <summary>Presets first, then the colors the user saved from the wheel (right-click one to remove it).</summary>
    private void BuildColorSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var hex in PresetPalette.HexColors)
        {
            AddSwatch(hex, saved: false);
        }

        foreach (var hex in _session.CustomColors.Colors)
        {
            AddSwatch(hex, saved: true);
        }
    }

    private void AddSwatch(string hex, bool saved)
    {
        if (!PresetPalette.TryParseHex(hex, out var color))
        {
            return;
        }

        var swatch = new ToggleButton
        {
            Style = (Style)FindResource("GridSwatch"),
            Background = new SolidColorBrush(color),
            Tag = hex,
            ToolTip = saved ? $"{hex} (right-click to remove)" : hex
        };
        swatch.Click += ColorSwatch_Click;
        if (saved)
        {
            CustomSwatchMenu.Attach(
                swatch,
                () => _session.CustomColors.Remove(hex),
                // The popover dismisses on outside clicks, which would otherwise close it under the menu.
                open => ColorPickerPopup.StaysOpen = open);
        }

        SwatchPanel.Children.Add(swatch);
    }

    private void CustomColors_Changed()
    {
        BuildColorSwatches();
        UpdateColorSwatches();
    }

    private void ColorPicker_SaveRequested(object sender, Color color)
    {
        _session.CustomColors.Add(color);
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
        var hasSwatch = _session.CustomColors.HasSwatch(_session.PenColor);
        CustomColorButton.IsChecked = !hasSwatch;
        ColorPicker.SetCanSave(!hasSwatch);
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
        ToolMenuGroup.Orientation = panelOrientation;
        ToolOptionsPanel.Orientation = panelOrientation;
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

        ToolPopup.Placement = placement;
        ColorPickerPopup.Placement = placement;
        StrokePopup.Placement = placement;
        VeilPopup.Placement = placement;
        CapturePopup.Placement = placement;
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
            ToolPopup.IsOpen = false;
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

    private void CustomColorRow_Click(object sender, MouseButtonEventArgs e)
    {
        // The wheel button handles its own clicks; the rest of the row does the same job.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender);
             node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node)
                 : LogicalTreeHelper.GetParent(node))
        {
            if (node is ToggleButton)
            {
                return;
            }
        }

        ToggleCustomColor();
    }

    private void CustomColorButton_Click(object sender, RoutedEventArgs e) => ToggleCustomColor();

    private void ToggleCustomColor()
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
        if (CapturePopup.IsOpen)
        {
            CapturePopup.IsOpen = false;
            return;
        }

        // Clicking the arrow while the popup is open closes it (focus loss) and then fires Click;
        // don't pop it straight back open.
        if ((DateTime.UtcNow - _capturePopupClosedAt).TotalMilliseconds < 250)
        {
            return;
        }

        RefreshCapturePopup();
        CapturePopup.IsOpen = true;
    }

    private void CapturePopup_Closed(object? sender, EventArgs e) => _capturePopupClosedAt = DateTime.UtcNow;

    private void RefreshCapturePopup()
    {
        var settings = _session.Settings.Current;
        var auto = settings.ToolbarCaptureMode == CaptureToolbarMode.Auto;
        CaptureAutoCheck.Visibility = auto ? Visibility.Visible : Visibility.Hidden;
        CaptureFullCheck.Visibility = auto ? Visibility.Hidden : Visibility.Visible;
        _syncingSettingsUi = true;
        CapturePopupAutoCopyCheck.IsChecked = settings.AutoCopyCaptures;
        _syncingSettingsUi = false;
        RefreshHotkeyDisplay();
    }

    private void CaptureMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<CaptureToolbarMode>(tag, out var mode))
        {
            SetCaptureMode(mode);
        }

        CapturePopup.IsOpen = false;
    }

    private void CapturePickRegion_Click(object sender, RoutedEventArgs e)
    {
        CapturePopup.IsOpen = false;
        CaptureRequested?.Invoke(this, new CaptureRequestedEventArgs { Kind = CaptureRequestKind.PickRegion });
    }

    private void AutoCopy_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingSettingsUi || sender is not CheckBox check)
        {
            return;
        }

        var enabled = check.IsChecked == true;
        // Keep the popup switch and the settings-panel switch in step.
        _syncingSettingsUi = true;
        AutoCopyCheck.IsChecked = enabled;
        CapturePopupAutoCopyCheck.IsChecked = enabled;
        _syncingSettingsUi = false;
        AutoCopyChanged?.Invoke(this, enabled);
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

    public void RefreshUpdateDetails()
    {
        CurrentVersionText.Text = AppVersion.Current;
        LastCheckedText.Text = AppVersion.FormatLastChecked(_session.Settings.Current.LastUpdateCheckUtc);
    }

    public void SetUpdateCheckBusy(bool busy, string buttonText = "Checking…")
    {
        CheckForUpdatesButton.IsEnabled = !busy;
        CheckForUpdatesButton.Opacity = busy ? 0.55 : 1;
        CheckForUpdatesButton.Content = busy ? buttonText : "Check now";
    }

    public void SetUpdateStatus(string? text, bool isError = false)
    {
        var show = !string.IsNullOrWhiteSpace(text);
        UpdateStatusText.Text = text ?? "";
        UpdateStatusText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73))
            : (Brush)FindResource("MutedTextBrush");
    }

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = SettingsPanel.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening)
        {
            RefreshHotkeyDisplay();
            RefreshUpdateDetails();
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
        CancelHotkeyRecording();
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

    private void RecordCaptureHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyRecording();
        _recordingCaptureHotkey = true;
        _session.IsHotkeyCaptureActive = true;
        CaptureHotkeyHintText.Text = "Press your shortcut now… (Esc to cancel)";
        RecordCaptureHotkeyButton.Content = "Listening…";
        SetStatus(string.Empty);
        Activate();
        Focus();
    }

    private void ClearCaptureHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyRecording();
        CaptureHotkeyChanged?.Invoke(this, new HotkeyChangedEventArgs(0, 0));
        RefreshHotkeyDisplay();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => HandleHotkeyKey(e);

    // Windows only sends a key-UP for Print Screen, so it can never be recorded from a key-down.
    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Snapshot)
        {
            HandleHotkeyKey(e);
        }
    }

    private void HandleHotkeyKey(KeyEventArgs e)
    {
        if (!_recordingHotkey && !_recordingCaptureHotkey)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelHotkeyRecording();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!HotkeySettingsHelper.TryCreateFromKey(key, out var modifiers, out var virtualKey))
        {
            return;
        }

        var settings = _session.Settings.Current;
        if (_recordingCaptureHotkey)
        {
            if (modifiers == settings.HotkeyModifiers && virtualKey == settings.HotkeyVirtualKey)
            {
                CaptureHotkeyHintText.Text = "That's already the draw shortcut — pick another.";
                return;
            }

            ApplyCaptureHotkey(modifiers, virtualKey);
            return;
        }

        if (modifiers == settings.CaptureHotkeyModifiers && virtualKey == settings.CaptureHotkeyVirtualKey)
        {
            HotkeyHintText.Text = "That's already the capture shortcut — pick another.";
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

    private void ApplyCaptureHotkey(uint modifiers, uint virtualKey)
    {
        _recordingCaptureHotkey = false;
        _session.IsHotkeyCaptureActive = false;
        RecordCaptureHotkeyButton.Content = "Set shortcut";
        CaptureHotkeyHintText.Text = "Ctrl/Alt/Shift/Win + a key, or Print Screen / an F-key on its own";
        CaptureHotkeyChanged?.Invoke(this, new HotkeyChangedEventArgs(modifiers, virtualKey));
        RefreshHotkeyDisplay();
    }

    private void CancelHotkeyRecording()
    {
        _recordingHotkey = false;
        _recordingCaptureHotkey = false;
        _session.IsHotkeyCaptureActive = false;
        RecordHotkeyButton.Content = "Change";
        HotkeyHintText.Text = "Esc turns draw off · Ctrl+Z undo while draw is on";
        RecordCaptureHotkeyButton.Content = "Set shortcut";
        CaptureHotkeyHintText.Text = "Ctrl/Alt/Shift/Win + a key, or Print Screen / an F-key on its own";
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
