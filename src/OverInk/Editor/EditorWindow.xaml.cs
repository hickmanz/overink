using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OverInk.Models;
using OverInk.Overlay;
using OverInk.Services;

namespace OverInk.Editor;

public partial class EditorWindow : Window
{
    private enum ZoomMode
    {
        Fit,
        Actual,
        Free
    }

    private readonly CaptureLibrary _library;
    private readonly double _penThickness;
    private readonly List<CaptureTrayEntry> _trayEntries = new();

    private CaptureItem? _currentItem;
    /// <summary>Library revision of the current capture as last loaded or saved here; detects edits made elsewhere (iPad).</summary>
    private string? _loadedRevision;
    private BitmapSource _originalSource = null!;
    private bool _isDirty;
    private bool _loadingDocument;
    private bool _suppressLibrarySelection;
    private ZoomMode _zoomMode = ZoomMode.Fit;
    private double _freeScale = 1;
    private Rect _cropBounds;
    private string? _activeCropHandle;
    private Rect _cropBoundsAtDragStart;
    private Point? _dragStart;
    private const double MinCropSize = 48;
    private const double HandleHalf = 6;
    private CaptureTrayEntry? _dragEntry;

    private sealed record HistoryEntry(Action Undo, Action Redo);

    private const int MaxHistory = 100;
    private readonly List<HistoryEntry> _undoStack = new();
    private readonly List<HistoryEntry> _redoStack = new();
    private bool _applyingHistory;
    private Color _penColor;
    private SketchTool _tool = SketchTool.Pen;
    private bool _shapeDragging;
    private Point _shapeStart;
    private UIElement? _shapePreview;

    public EditorWindow(CaptureLibrary library, Color penColor, double penThickness)
    {
        _library = library;
        _penThickness = penThickness;
        _penColor = penColor;
        InitializeComponent();

        // Open large: ~90% of the usable screen (never below the minimums), centered.
        var work = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(work.Width * 0.9, 1900));
        Height = Math.Max(MinHeight, Math.Min(work.Height * 0.9, 1200));

        InkSurface.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = penColor,
            Width = penThickness,
            Height = penThickness,
            FitToCurve = false
        };

        InkSurface.StrokeCollected += (_, e) => StrokeSmoother.Apply(e.Stroke);
        InkSurface.Strokes.StrokesChanged += Strokes_Changed;

        EditorColorPicker.SetColor(penColor);
        EditorColorPicker.ColorChanged += (_, color) =>
        {
            ApplyPenColor(color);
            UpdateSwatchSelection();
        };
        PenSizeSlider.Value = penThickness;
        BuildPresetSwatches();
        UpdateSwatchSelection();
        UpdatePenVisuals();
        UpdateEmptyState();
        UpdateDocInfo();

        // Pen is the starting tool; without this the custom pen cursor only appears after picking a tool.
        ApplyEditingMode();

        _library.LibraryChanged += OnLibraryChanged;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) =>
        {
            RefreshTray(keepSelection: true);
            UpdateZoom();
        };
        SizeChanged += (_, _) => UpdateZoom();
        Deactivated += (_, _) => TrySaveCurrent();
        Closing += (_, _) => TrySaveCurrent();
        PreviewKeyDown += EditorWindow_PreviewKeyDown;
    }

    public void OpenItem(CaptureItem item)
    {
        TrySaveCurrent();
        LoadItem(item);
        RefreshTray(keepSelection: true);
        SelectItemInTray(item);
    }

    public void RefreshTrayFromLibrary() => RefreshTray(keepSelection: true);

    /// <summary>Tells the user the capture they just took was already put on the clipboard.</summary>
    public void NotifyCopied() => ShowStatus("Copied to clipboard");

    private void OnLibraryChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshTray(keepSelection: true);
            ReloadIfChangedExternally();
        });
    }

    /// <summary>
    /// The open capture may have been saved by another client (the iPad). Reload it silently when we have
    /// no unsaved edits; otherwise warn that our next save will overwrite theirs.
    /// </summary>
    private void ReloadIfChangedExternally()
    {
        if (_currentItem is null || _loadingDocument)
        {
            return;
        }

        if (!File.Exists(_currentItem.FlattenedPath))
        {
            return;
        }

        var revision = _library.GetRevision(_currentItem);
        if (revision == _loadedRevision)
        {
            return;
        }

        if (_isDirty)
        {
            ShowStatus("Changed on iPad - saving here will overwrite it");
            return;
        }

        var item = _currentItem;
        LoadItem(item);
        SelectItemInTray(item);
        ShowStatus("Updated from iPad");
    }

    private void RefreshTray(bool keepSelection)
    {
        var selectedPath = (_currentItem?.FlattenedPath);
        _trayEntries.Clear();
        foreach (var item in _library.Enumerate())
        {
            _trayEntries.Add(new CaptureTrayEntry(item));
        }

        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = _trayEntries;
        TrayCountLabel.Text = _trayEntries.Count == 0 ? string.Empty : _trayEntries.Count.ToString();
        TrayEmptyLabel.Visibility = _trayEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (keepSelection && selectedPath is not null)
        {
            var entry = _trayEntries.FirstOrDefault(e =>
                string.Equals(e.Item.FlattenedPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                SelectEntry(entry);
            }
        }
    }

    private void SelectItemInTray(CaptureItem item)
    {
        var entry = _trayEntries.FirstOrDefault(e =>
            string.Equals(e.Item.FlattenedPath, item.FlattenedPath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            SelectEntry(entry);
        }
    }

    private void SelectEntry(CaptureTrayEntry entry)
    {
        _suppressLibrarySelection = true;
        LibraryList.SelectedItem = entry;
        _suppressLibrarySelection = false;
    }

    private void LoadItem(CaptureItem item)
    {
        CancelShapeDrag();
        _loadingDocument = true;
        var (original, strokes) = _library.LoadDocument(item);
        _currentItem = item;
        _loadedRevision = _library.GetRevision(item);
        _originalSource = original;
        BackgroundImage.Source = original;
        InkSurface.Strokes.Clear();
        foreach (var stroke in strokes)
        {
            InkSurface.Strokes.Add(stroke);
        }
        ApplyCanvasDimensions();
        ResetCropToFull();
        _isDirty = false;
        _loadingDocument = false;
        ClearHistory();
        _zoomMode = ZoomMode.Fit;
        FitToggle.IsChecked = true;
        UpdateEmptyState();
        UpdateZoom();
        UpdateDocInfo();
        Title = $"OverInk — {item.DisplayName}";
    }

    private void UpdateEmptyState()
    {
        var hasDocument = _currentItem is not null;
        EmptyState.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        CanvasScroll.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDocInfo()
    {
        if (_currentItem is null || _originalSource is null)
        {
            DocTitle.Text = "OverInk Editor";
            DocMeta.Text = "Nothing open";
            return;
        }

        DocTitle.Text = CaptureTrayEntry.TitleFor(_currentItem);
        var size = $"{_originalSource.PixelWidth} × {_originalSource.PixelHeight}";
        DocMeta.Text = _isDirty ? $"{size}  ·  Unsaved changes" : size;
    }

    private void ApplyCanvasDimensions()
    {
        var w = _originalSource.PixelWidth;
        var h = _originalSource.PixelHeight;
        CanvasHost.Width = w;
        CanvasHost.Height = h;
        InkSurface.Width = w;
        InkSurface.Height = h;
        CropVisualLayer.Width = w;
        CropVisualLayer.Height = h;
        CropHandleLayer.Width = w;
        CropHandleLayer.Height = h;
        ShapeCanvas.Width = w;
        ShapeCanvas.Height = h;
    }

    private void BuildPresetSwatches()
    {
        PresetSwatchesPanel.Children.Clear();
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
                Margin = new Thickness(1),
                Tag = hex,
                ToolTip = hex
            };
            swatch.Click += (_, _) =>
            {
                EditorColorPicker.SetColor(color);
                ApplyPenColor(color);
                UpdateSwatchSelection();
            };
            PresetSwatchesPanel.Children.Add(swatch);
        }
    }

    /// <summary>Marks the swatch matching the pen colour; the wheel button takes over for custom colours.</summary>
    private void UpdateSwatchSelection()
    {
        var current = ColorHex.ToRgbHex(_penColor);
        foreach (var child in PresetSwatchesPanel.Children)
        {
            if (child is ToggleButton { Tag: string hex } swatch)
            {
                swatch.IsChecked = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            }
        }

        CustomColorButton.Background = new SolidColorBrush(_penColor);
        CustomColorButton.IsChecked = !PresetPalette.IsPreset(_penColor);
        UpdatePenVisuals();
    }

    private void CustomColorButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCustomColorCard();
    }

    private void CustomColorRow_Click(object sender, MouseButtonEventArgs e)
    {
        // The wheel button handles its own clicks; the rest of the row does the same job.
        if (e.OriginalSource is DependencyObject source && FindAncestor<ToggleButton>(source) is not null)
        {
            return;
        }

        ToggleCustomColorCard();
    }

    private void ToggleCustomColorCard()
    {
        var show = CustomColorCard.Visibility != Visibility.Visible;
        if (show)
        {
            EditorColorPicker.SetColor(_penColor);
        }

        CustomColorCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The button's IsChecked mirrors "colour is not a preset"; undo the click's own toggle.
        UpdateSwatchSelection();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void ApplyPenColor(Color color)
    {
        _penColor = color;
        var attrs = InkSurface.DefaultDrawingAttributes.Clone();
        attrs.Color = color;
        InkSurface.DefaultDrawingAttributes = attrs;
        UpdatePenVisuals();
    }

    private void PenSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var size = Math.Max(1, e.NewValue);
        var attrs = InkSurface.DefaultDrawingAttributes.Clone();
        attrs.Width = size;
        attrs.Height = size;
        InkSurface.DefaultDrawingAttributes = attrs;
        UpdatePenVisuals();
    }

    /// <summary>Preview curve thickness tracks the width (1–24 maps to 1.25–8 units on the 28x24 grid).</summary>
    private void UpdatePenVisuals()
    {
        if (StrokePreviewPath is null || PenSizeValue is null)
        {
            return;
        }

        var width = Math.Clamp(PenSizeSlider.Value, 1, 24);
        StrokePreviewPath.StrokeThickness = 1.25 + (width - 1) / 23.0 * 6.75;
        StrokePreviewPath.Stroke = new SolidColorBrush(_penColor);
        PenSizeValue.Text = $"{(int)Math.Round(width)} px";
    }

    private Rect FullImageCropBounds() =>
        new(0, 0, CanvasHost.Width, CanvasHost.Height);

    private void ResetCropToFull()
    {
        _cropBounds = FullImageCropBounds();
        UpdateCropVisuals();
    }

    private bool IsCropModified()
    {
        var full = FullImageCropBounds();
        return Math.Abs(_cropBounds.X) > 0.5
               || Math.Abs(_cropBounds.Y) > 0.5
               || Math.Abs(_cropBounds.Width - full.Width) > 0.5
               || Math.Abs(_cropBounds.Height - full.Height) > 0.5;
    }

    private void UpdateCropVisuals()
    {
        if (CanvasHost.Width < 1 || CanvasHost.Height < 1)
        {
            return;
        }

        var w = CanvasHost.Width;
        var h = CanvasHost.Height;
        var r = _cropBounds;

        var showDim = IsCropModified();
        CropDimTop.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;
        CropDimLeft.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;
        CropDimRight.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;
        CropDimBottom.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;

        if (showDim)
        {
            SetRect(CropDimTop, 0, 0, w, Math.Max(0, r.Top));
            SetRect(CropDimLeft, 0, r.Top, Math.Max(0, r.Left), r.Height);
            SetRect(CropDimRight, r.Right, r.Top, Math.Max(0, w - r.Right), r.Height);
            SetRect(CropDimBottom, 0, r.Bottom, w, Math.Max(0, h - r.Bottom));
        }

        SetRect(CropFrame, r.Left, r.Top, r.Width, r.Height);
        CropFrame.Visibility = showDim ? Visibility.Visible : Visibility.Collapsed;

        PlaceHandle(HandleTopLeft, r.Left - HandleHalf, r.Top - HandleHalf);
        PlaceHandle(HandleTop, r.Left + r.Width / 2 - HandleHalf, r.Top - HandleHalf);
        PlaceHandle(HandleTopRight, r.Right - HandleHalf, r.Top - HandleHalf);
        PlaceHandle(HandleRight, r.Right - HandleHalf, r.Top + r.Height / 2 - HandleHalf);
        PlaceHandle(HandleBottomRight, r.Right - HandleHalf, r.Bottom - HandleHalf);
        PlaceHandle(HandleBottom, r.Left + r.Width / 2 - HandleHalf, r.Bottom - HandleHalf);
        PlaceHandle(HandleBottomLeft, r.Left - HandleHalf, r.Bottom - HandleHalf);
        PlaceHandle(HandleLeft, r.Left - HandleHalf, r.Top + r.Height / 2 - HandleHalf);
    }

    private static void SetRect(System.Windows.Shapes.Rectangle rect, double x, double y, double width, double height)
    {
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
    }

    private static void PlaceHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    private void CropHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        _activeCropHandle = tag;
        _cropBoundsAtDragStart = _cropBounds;
        CropHandleLayer.CaptureMouse();
        InkSurface.EditingMode = InkCanvasEditingMode.None;
        e.Handled = true;
    }

    private void CropHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_activeCropHandle is null || !CropHandleLayer.IsMouseCaptured)
        {
            return;
        }

        var pos = e.GetPosition(CropHandleLayer);
        _cropBounds = AdjustCropBounds(_activeCropHandle, _cropBoundsAtDragStart, pos);
        UpdateCropVisuals();
        e.Handled = true;
    }

    private void CropHandle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _activeCropHandle is not null;
        if (wasDragging)
        {
            _activeCropHandle = null;
            CommitCropIfNeeded();
            RestoreInkEditingMode();
        }

        if (CropHandleLayer.IsMouseCaptured)
        {
            CropHandleLayer.ReleaseMouseCapture();
        }

        if (wasDragging)
        {
            e.Handled = true;
        }
    }

    private void RestoreInkEditingMode() => ApplyEditingMode();

    private void ApplyEditingMode()
    {
        InkSurface.EditingMode = _tool switch
        {
            SketchTool.Pen => InkCanvasEditingMode.Ink,
            SketchTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            _ => InkCanvasEditingMode.None
        };
        ShapeCanvas.IsHitTestVisible = IsShapeTool(_tool);

        // The pen draws its own cursor (see BrushCursor) so thin strokes never make the pointer vanish.
        var customPenCursor = _tool == SketchTool.Pen && InkSurface.EditingMode == InkCanvasEditingMode.Ink;
        InkSurface.UseCustomCursor = customPenCursor;
        if (customPenCursor)
        {
            InkSurface.Cursor = Cursors.None;
        }
        else
        {
            InkSurface.ClearValue(FrameworkElement.CursorProperty);
            HideBrushCursor();
        }
    }

    /// <summary>
    /// Puts the editor back on top of the other topmost windows (the markup overlay re-raises itself whenever
    /// it is activated) without taking focus. Other apps are already behind it because it is topmost.
    /// </summary>
    public void RaiseAboveOverlay()
    {
        if (!IsVisible)
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        OverInk.Interop.NativeMethods.SetWindowPos(
            handle,
            new IntPtr(-1), // HWND_TOPMOST
            0, 0, 0, 0,
            OverInk.Interop.NativeMethods.SwpNomove |
            OverInk.Interop.NativeMethods.SwpNosize |
            OverInk.Interop.NativeMethods.SwpNoactivate);
    }

    private void InkSurface_PreviewMouseMove(object sender, MouseEventArgs e) =>
        UpdateBrushCursor(e.GetPosition(BrushCursorLayer));

    private void InkSurface_PreviewStylusMove(object sender, StylusEventArgs e) =>
        UpdateBrushCursor(e.GetPosition(BrushCursorLayer));

    private void UpdateBrushCursor(Point position)
    {
        if (!InkSurface.UseCustomCursor)
        {
            HideBrushCursor();
            return;
        }

        BrushCursorVisual.SetDiameter(InkSurface.DefaultDrawingAttributes.Width * CanvasScale.ScaleX);
        Canvas.SetLeft(BrushCursorVisual, position.X);
        Canvas.SetTop(BrushCursorVisual, position.Y);
        BrushCursorVisual.Visibility = Visibility.Visible;
    }

    private void InkSurface_PointerLeft(object sender, InputEventArgs e) => HideBrushCursor();

    private void HideBrushCursor() => BrushCursorVisual.Visibility = Visibility.Collapsed;

    private static bool IsShapeTool(SketchTool tool) =>
        tool is SketchTool.Line or SketchTool.Arrow or SketchTool.Rectangle or SketchTool.Ellipse;

    private Rect AdjustCropBounds(string handle, Rect start, Point pointer)
    {
        var maxW = CanvasHost.Width;
        var maxH = CanvasHost.Height;
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        switch (handle)
        {
            case "Left":
                left = Math.Clamp(pointer.X, 0, right - MinCropSize);
                break;
            case "Right":
                right = Math.Clamp(pointer.X, left + MinCropSize, maxW);
                break;
            case "Top":
                top = Math.Clamp(pointer.Y, 0, bottom - MinCropSize);
                break;
            case "Bottom":
                bottom = Math.Clamp(pointer.Y, top + MinCropSize, maxH);
                break;
            case "TopLeft":
                left = Math.Clamp(pointer.X, 0, right - MinCropSize);
                top = Math.Clamp(pointer.Y, 0, bottom - MinCropSize);
                break;
            case "TopRight":
                right = Math.Clamp(pointer.X, left + MinCropSize, maxW);
                top = Math.Clamp(pointer.Y, 0, bottom - MinCropSize);
                break;
            case "BottomLeft":
                left = Math.Clamp(pointer.X, 0, right - MinCropSize);
                bottom = Math.Clamp(pointer.Y, top + MinCropSize, maxH);
                break;
            case "BottomRight":
                right = Math.Clamp(pointer.X, left + MinCropSize, maxW);
                bottom = Math.Clamp(pointer.Y, top + MinCropSize, maxH);
                break;
        }

        return new Rect(left, top, Math.Max(MinCropSize, right - left), Math.Max(MinCropSize, bottom - top));
    }

    private void MarkDirty()
    {
        if (_currentItem is null || _loadingDocument)
        {
            return;
        }

        _isDirty = true;
        UpdateDocInfo();
    }

    // ----- Undo / redo -----

    private void Strokes_Changed(object? sender, StrokeCollectionChangedEventArgs e)
    {
        MarkDirty();
        if (_applyingHistory || _loadingDocument || _currentItem is null)
        {
            return;
        }

        var added = e.Added.ToList();
        var removed = e.Removed.ToList();
        PushHistory(new HistoryEntry(
            Undo: () =>
            {
                InkSurface.Strokes.Remove(new StrokeCollection(added));
                InkSurface.Strokes.Add(new StrokeCollection(removed));
            },
            Redo: () =>
            {
                InkSurface.Strokes.Remove(new StrokeCollection(removed));
                InkSurface.Strokes.Add(new StrokeCollection(added));
            }));
    }

    private void PushHistory(HistoryEntry entry)
    {
        _undoStack.Add(entry);
        if (_undoStack.Count > MaxHistory)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        UpdateHistoryButtons();
    }

    private void ClearHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _undoStack.Count > 0;
        RedoButton.IsEnabled = _redoStack.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undoStack.Count == 0 || _currentItem is null)
        {
            return;
        }

        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RunHistory(entry.Undo);
        _redoStack.Add(entry);
        UpdateHistoryButtons();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0 || _currentItem is null)
        {
            return;
        }

        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RunHistory(entry.Redo);
        _undoStack.Add(entry);
        UpdateHistoryButtons();
    }

    private void RunHistory(Action action)
    {
        _applyingHistory = true;
        try
        {
            action();
        }
        finally
        {
            _applyingHistory = false;
        }

        MarkDirty();
    }

    private void TrySaveCurrent()
    {
        if (_currentItem is null || !_isDirty)
        {
            return;
        }

        SaveCurrent(showToast: false);
    }

    private void SaveCurrent(bool showToast)
    {
        if (_currentItem is null)
        {
            return;
        }

        var flattened = RenderFlattened();
        _library.SaveDocument(_currentItem, _originalSource, InkSurface.Strokes, flattened);
        _loadedRevision = _library.GetRevision(_currentItem);
        _isDirty = false;
        UpdateDocInfo();

        var entry = _trayEntries.FirstOrDefault(e =>
            string.Equals(e.Item.FlattenedPath, _currentItem.FlattenedPath, StringComparison.OrdinalIgnoreCase));
        entry?.RefreshThumbnail();

        if (showToast)
        {
            ShowStatus("Saved");
        }
    }

    private BitmapSource RenderFlattened() =>
        CaptureRenderer.Flatten(_originalSource, InkSurface.Strokes);

    private void UpdateZoom()
    {
        if (_originalSource is null)
        {
            return;
        }

        var imgW = CanvasHost.Width;
        var imgH = CanvasHost.Height;
        if (imgW < 1 || imgH < 1)
        {
            return;
        }

        var viewportW = CanvasScroll.ViewportWidth > 0 ? CanvasScroll.ViewportWidth : CanvasArea.ActualWidth;
        var viewportH = CanvasScroll.ViewportHeight > 0 ? CanvasScroll.ViewportHeight : CanvasArea.ActualHeight;
        if (viewportW < 1 || viewportH < 1)
        {
            return;
        }

        double scale = _zoomMode switch
        {
            ZoomMode.Actual => 1,
            ZoomMode.Free => _freeScale,
            _ => Math.Min(1, Math.Min(viewportW / imgW, viewportH / imgH))
        };

        scale = Math.Max(0.05, Math.Min(8, scale));
        CanvasScale.ScaleX = scale;
        CanvasScale.ScaleY = scale;

        var percent = (int)Math.Round(scale * 100);
        ZoomLabel.Text = $"{percent}%";

        var needsScroll = scale * imgW > viewportW + 1 || scale * imgH > viewportH + 1;
        CanvasScroll.HorizontalScrollBarVisibility = needsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        CanvasScroll.VerticalScrollBarVisibility = needsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private void EditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var handled = true;

        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.C: Copy_Click(this, new RoutedEventArgs()); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.S: SaveCurrent(showToast: true); break;
                case Key.D0 or Key.NumPad0: SetZoomMode(ZoomMode.Fit); break;
                case Key.D1 or Key.NumPad1: SetZoomMode(ZoomMode.Actual); break;
                case Key.OemPlus or Key.Add: StepZoom(1.25); break;
                case Key.OemMinus or Key.Subtract: StepZoom(1 / 1.25); break;
                default: handled = false; break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.Z: Redo(); break;
                case Key.S: Save_Click(this, new RoutedEventArgs()); break;
                default: handled = false; break;
            }
        }
        else if (mods == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.P: SelectPen(); break;
                case Key.E: SelectEraser(); break;
                case Key.L: SelectTool(SketchTool.Line); break;
                case Key.A: SelectTool(SketchTool.Arrow); break;
                case Key.R: SelectTool(SketchTool.Rectangle); break;
                case Key.O: SelectTool(SketchTool.Ellipse); break;
                default: handled = false; break;
            }
        }
        else
        {
            handled = false;
        }

        if (handled)
        {
            e.Handled = true;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(1.25);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(1 / 1.25);

    private void StepZoom(double factor)
    {
        if (_originalSource is null)
        {
            return;
        }

        ZoomAt(CanvasScale.ScaleX * factor, ViewportCenter());
    }

    private Point ViewportCenter() =>
        new(CanvasScroll.ViewportWidth / 2, CanvasScroll.ViewportHeight / 2);

    /// <summary>
    /// Zooms to <paramref name="newScale"/> keeping the image point under <paramref name="viewportPoint"/>
    /// (relative to the scroll viewer) fixed on screen.
    /// </summary>
    private void ZoomAt(double newScale, Point viewportPoint)
    {
        var imagePoint = CanvasScroll.TranslatePoint(viewportPoint, CanvasHost);

        _freeScale = Math.Clamp(newScale, 0.05, 8);
        _zoomMode = ZoomMode.Free;
        FitToggle.IsChecked = false;
        UpdateZoom();
        CanvasScroll.UpdateLayout();

        var moved = CanvasHost.TranslatePoint(imagePoint, CanvasScroll);
        CanvasScroll.ScrollToHorizontalOffset(CanvasScroll.HorizontalOffset + moved.X - viewportPoint.X);
        CanvasScroll.ScrollToVerticalOffset(CanvasScroll.VerticalOffset + moved.Y - viewportPoint.Y);
    }

    private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (_originalSource is null)
        {
            return;
        }

        // ~10% per wheel notch, proportional for smooth/high-resolution wheels; zoom toward the pointer.
        var factor = Math.Pow(1.1, e.Delta / 120.0);
        ZoomAt(CanvasScale.ScaleX * factor, e.GetPosition(CanvasScroll));
        e.Handled = true;
    }

    private void FitZoom_Click(object sender, RoutedEventArgs e) => SetZoomMode(ZoomMode.Fit);

    private void ActualZoom_Click(object sender, RoutedEventArgs e) => SetZoomMode(ZoomMode.Actual);

    private void SetZoomMode(ZoomMode mode)
    {
        _zoomMode = mode;
        if (mode == ZoomMode.Fit)
        {
            FitToggle.IsChecked = true;
        }
        else
        {
            FitToggle.IsChecked = false;
        }

        if (mode == ZoomMode.Free)
        {
            _freeScale = CanvasScale.ScaleX;
        }

        UpdateZoom();
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SketchTool>(tag, out var tool))
        {
            SelectTool(tool);
        }
    }

    private void SelectPen() => SelectTool(SketchTool.Pen);

    private void SelectEraser() => SelectTool(SketchTool.Eraser);

    private void SelectTool(SketchTool tool)
    {
        CancelShapeDrag();
        _tool = tool;

        foreach (var (toggle, mapped) in new[]
                 {
                     (PenToggle, SketchTool.Pen),
                     (LineToggle, SketchTool.Line),
                     (ArrowToggle, SketchTool.Arrow),
                     (RectangleToggle, SketchTool.Rectangle),
                     (EllipseToggle, SketchTool.Ellipse),
                     (EraserToggle, SketchTool.Eraser)
                 })
        {
            toggle.IsChecked = mapped == tool;
        }

        var erasing = tool == SketchTool.Eraser;
        PenSection.Visibility = erasing ? Visibility.Collapsed : Visibility.Visible;
        EraserHint.Visibility = erasing ? Visibility.Visible : Visibility.Collapsed;

        _activeCropHandle = null;
        if (CropHandleLayer.IsMouseCaptured)
        {
            CropHandleLayer.ReleaseMouseCapture();
        }

        ApplyEditingMode();
        InkSurface.Focus();
    }

    // ----- Shapes (line, arrow, rectangle, ellipse) -----
    // Dragged out on a transparent layer, then committed as polyline ink strokes (the same conversion the overlay
    // uses for captures) so they erase, undo and save exactly like pen strokes.

    private void ShapeCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (BeginShape(e.GetPosition(ShapeCanvas)))
        {
            ShapeCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void ShapeCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_shapeDragging)
        {
            UpdateShapePreview(ConstrainShapeEnd(e.GetPosition(ShapeCanvas)));
        }
    }

    private void ShapeCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_shapeDragging)
        {
            return;
        }

        CommitShape(e.GetPosition(ShapeCanvas));
        e.Handled = true;
    }

    private bool BeginShape(Point start)
    {
        if (!IsShapeTool(_tool) || _currentItem is null)
        {
            return false;
        }

        _shapeStart = start;
        _shapeDragging = true;
        UpdateShapePreview(start);
        return true;
    }

    private void CommitShape(Point rawEnd)
    {
        var end = ConstrainShapeEnd(rawEnd);
        UpdateShapePreview(end);
        var preview = _shapePreview;
        _shapePreview = null;
        _shapeDragging = false;
        if (ShapeCanvas.IsMouseCaptured)
        {
            ShapeCanvas.ReleaseMouseCapture();
        }

        ShapeCanvas.Children.Clear();

        if (preview is not null && (end - _shapeStart).Length >= 3)
        {
            InkSurface.Strokes.Add(new StrokeCollection(ShapeStrokeConverter.ToStrokes(preview)));
        }
    }
    private void ShapeCanvas_LostMouseCapture(object sender, MouseEventArgs e) => CancelShapeDrag();

    private void CancelShapeDrag()
    {
        if (!_shapeDragging && _shapePreview is null)
        {
            return;
        }

        _shapeDragging = false;
        _shapePreview = null;
        ShapeCanvas.Children.Clear();
        if (ShapeCanvas.IsMouseCaptured)
        {
            ShapeCanvas.ReleaseMouseCapture();
        }
    }

    /// <summary>Holding Shift snaps lines to 45° steps and rectangles/ellipses to squares/circles.</summary>
    private Point ConstrainShapeEnd(Point end)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return end;
        }

        var d = end - _shapeStart;
        if (_tool is SketchTool.Rectangle or SketchTool.Ellipse)
        {
            var size = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            return new Point(_shapeStart.X + Math.Sign(d.X == 0 ? 1 : d.X) * size,
                _shapeStart.Y + Math.Sign(d.Y == 0 ? 1 : d.Y) * size);
        }

        var length = d.Length;
        var angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(_shapeStart.X + Math.Cos(angle) * length, _shapeStart.Y + Math.Sin(angle) * length);
    }

    private void UpdateShapePreview(Point end)
    {
        ShapeCanvas.Children.Clear();
        var attrs = InkSurface.DefaultDrawingAttributes;
        _shapePreview = ShapeDrawingHelper.CreateShapeElement(_tool, _shapeStart, end, _penColor, attrs.Width);
        ShapeDrawingHelper.PositionShape(_shapePreview, _shapeStart, end);
        ShapeCanvas.Children.Add(_shapePreview);
    }
    private void CommitCropIfNeeded()
    {
        if (!IsCropModified() || _cropBounds.Width < 2 || _cropBounds.Height < 2)
        {
            return;
        }

        var x = (int)Math.Floor(_cropBounds.X);
        var y = (int)Math.Floor(_cropBounds.Y);
        var w = (int)Math.Ceiling(_cropBounds.Width);
        var h = (int)Math.Ceiling(_cropBounds.Height);
        w = Math.Min(w, _originalSource.PixelWidth - x);
        h = Math.Min(h, _originalSource.PixelHeight - y);

        var cropped = new CroppedBitmap(_originalSource, new Int32Rect(x, y, w, h));
        cropped.Freeze();

        var previous = _originalSource;
        ApplyCrop(cropped, -x, -y);
        PushHistory(new HistoryEntry(
            Undo: () => ApplyCrop(previous, x, y),
            Redo: () => ApplyCrop(cropped, -x, -y)));
    }

    /// <summary>Swaps in a new background and shifts the ink by (dx, dy) so it stays aligned.</summary>
    private void ApplyCrop(BitmapSource source, double dx, double dy)
    {
        InkSurface.Strokes.Transform(new Matrix(1, 0, 0, 1, dx, dy), false);
        _originalSource = source;
        BackgroundImage.Source = source;
        ApplyCanvasDimensions();
        ResetCropToFull();
        MarkDirty();
        UpdateDocInfo();
        UpdateZoom();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG Image|*.png",
            FileName = Path.GetFileName(_currentItem.FlattenedPath),
            InitialDirectory = _library.FolderPath
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SaveCurrent(showToast: false);
        var flattened = RenderFlattened();
        SaveBitmapToPath(flattened, dialog.FileName);
        ShowStatus("Exported");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            return;
        }

        SaveCurrent(showToast: false);
        var flattened = RenderFlattened();
        var data = ImageTransfer.BuildDataObject(flattened, _currentItem.FlattenedPath);
        ImageTransfer.SetClipboardWithRetry(data);
        ShowStatus("Copied");
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLibrarySelection || LibraryList.SelectedItem is not CaptureTrayEntry entry)
        {
            return;
        }

        if (_currentItem is not null &&
            string.Equals(entry.Item.FlattenedPath, _currentItem.FlattenedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TrySaveCurrent();
        LoadItem(entry.Item);
    }

    private void LibraryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(LibraryList);
        _dragEntry = FindTrayEntryFromElement(e.OriginalSource as DependencyObject);

        // Items are non-focusable (so they don't steal keyboard focus from the canvas), which stops the
        // ListBox's own click-to-select from firing on most of the card. Select explicitly instead.
        if (_dragEntry is not null && !ReferenceEquals(LibraryList.SelectedItem, _dragEntry))
        {
            LibraryList.SelectedItem = _dragEntry;
        }
    }

    private void LibraryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is null || _dragEntry is null)
        {
            return;
        }

        var pos = e.GetPosition(LibraryList);
        if (Math.Abs(pos.X - _dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        BeginDragOut(_dragEntry);
        _dragStart = null;
        _dragEntry = null;
    }

    private void BeginDragOut(CaptureTrayEntry entry)
    {
        if (!string.Equals(entry.Item.FlattenedPath, _currentItem?.FlattenedPath, StringComparison.OrdinalIgnoreCase))
        {
            TrySaveCurrent();
            LoadItem(entry.Item);
            SelectEntry(entry);
        }
        else if (_isDirty)
        {
            SaveCurrent(showToast: false);
        }

        var flattened = RenderFlattened();
        var data = ImageTransfer.BuildDataObject(flattened, entry.Item.FlattenedPath);
        DragDrop.DoDragDrop(LibraryList, data, DragDropEffects.Copy);
    }

    private static CaptureTrayEntry? FindTrayEntryFromElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: CaptureTrayEntry entry })
            {
                return entry;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void TrayItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureTrayEntry entry })
        {
            return;
        }

        e.Handled = true;
        ShowTrayContextMenu(entry);
    }

    private void ShowTrayContextMenu(CaptureTrayEntry entry)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };

        // Non-interactive header so it's obvious which capture the menu is for.
        var header = new StackPanel
        {
            Margin = new Thickness(8, 6, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        header.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220
        });
        header.Children.Add(new TextBlock
        {
            Text = entry.Subtitle,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)FindResource("MutedTextBrush")
        });
        menu.Items.Add(header);
        menu.Items.Add(MenuSeparator());

        var open = MenuEntry("Open", "IconImage", () =>
        {
            SelectEntry(entry);
            LoadItem(entry.Item);
        });
        menu.Items.Add(open);

        menu.Items.Add(MenuEntry("Copy", "IconCopy", () =>
        {
            SelectEntry(entry);
            LoadItem(entry.Item);
            Copy_Click(this, new RoutedEventArgs());
        }));

        menu.Items.Add(MenuEntry("Show in Explorer", "IconFolder", () => RevealInExplorer(entry.Item.FlattenedPath)));
        menu.Items.Add(MenuEntry("Rename…", "IconPencil", () => RenameItem(entry)));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuEntry("Delete", "IconTrash", () => DeleteItem(entry), danger: true));

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private MenuItem MenuEntry(string header, string iconKey, Action onClick, bool danger = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource(danger ? "DarkMenuItemDanger" : "DarkMenuItem"),
            Icon = new ContentControl
            {
                Style = (Style)FindResource("LucideIcon"),
                Tag = FindResource(iconKey),
                Width = 16,
                Height = 16
            }
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private Separator MenuSeparator() => new() { Style = (Style)FindResource("DarkMenuSeparator") };
    private void RenameItem(CaptureTrayEntry entry)
    {
        var dialog = new RenameCaptureDialog(entry.Item.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.NewName))
        {
            return;
        }

        var updated = _library.Rename(entry.Item, dialog.NewName);
        if (updated is null)
        {
            ShowStatus("Rename failed");
            return;
        }

        if (_currentItem is not null &&
            string.Equals(_currentItem.FlattenedPath, entry.Item.FlattenedPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentItem = updated;
            Title = $"OverInk — {updated.DisplayName}";
        }

        RefreshTray(keepSelection: true);
        SelectItemInTray(updated);
    }

    private void DeleteItem(CaptureTrayEntry entry)
    {
        if (MessageBox.Show(
                $"Delete \"{entry.Item.DisplayName}\"?",
                "OverInk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var wasCurrent = _currentItem is not null &&
                         string.Equals(_currentItem.FlattenedPath, entry.Item.FlattenedPath, StringComparison.OrdinalIgnoreCase);
        _library.Delete(entry.Item);

        if (wasCurrent)
        {
            _currentItem = null;
            _isDirty = false;
            var next = _library.Enumerate().FirstOrDefault();
            if (next is not null)
            {
                LoadItem(next);
            }
            else
            {
                BackgroundImage.Source = null;
                _loadingDocument = true;
                InkSurface.Strokes.Clear();
                _loadingDocument = false;
                ClearHistory();
                UpdateEmptyState();
                UpdateDocInfo();
                Title = "OverInk Editor";
            }
        }

        RefreshTray(keepSelection: true);
    }

    private static void RevealInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void ToggleTray_Click(object sender, RoutedEventArgs e)
    {
        var show = TrayPanel.Visibility != Visibility.Visible;
        TrayPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowTrayButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        UpdateZoom();
    }

    private void ShowStatus(string message)
    {
        StatusLabel.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (_, _) =>
        {
            StatusLabel.Text = string.Empty;
            timer.Stop();
        };
        timer.Start();
    }

    private static void SaveBitmapToPath(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    protected override void OnClosed(EventArgs e)
    {
        _library.LibraryChanged -= OnLibraryChanged;
        base.OnClosed(e);
    }
}
