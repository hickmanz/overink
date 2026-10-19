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

    public EditorWindow(CaptureLibrary library, Color penColor, double penThickness)
    {
        _library = library;
        _penThickness = penThickness;
        InitializeComponent();

        InkSurface.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = penColor,
            Width = penThickness,
            Height = penThickness,
            FitToCurve = false
        };

        InkSurface.StrokeCollected += (_, e) => StrokeSmoother.Apply(e.Stroke);
        InkSurface.Strokes.StrokesChanged += (_, _) => MarkDirty();

        EditorColorPicker.SetColor(penColor);
        EditorColorPicker.ColorChanged += (_, color) => ApplyPenColor(color);
        PenSizeSlider.Value = penThickness;
        BuildPresetSwatches(penColor);

        _library.LibraryChanged += OnLibraryChanged;
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
        _zoomMode = ZoomMode.Fit;
        FitToggle.IsChecked = true;
        UpdateZoom();
        Title = $"OverInk — {item.DisplayName}";
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
    }

    private void BuildPresetSwatches(Color current)
    {
        PresetSwatchesPanel.Children.Clear();
        foreach (var hex in PresetPalette.HexColors.Take(10))
        {
            if (!PresetPalette.TryParseHex(hex, out var color))
            {
                continue;
            }

            var swatch = new ToggleButton
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            if (color == current)
            {
                swatch.BorderBrush = (Brush)FindResource("AccentBrush");
                swatch.IsChecked = true;
            }

            swatch.Click += (_, _) =>
            {
                EditorColorPicker.SetColor(color);
                ApplyPenColor(color);
            };
            PresetSwatchesPanel.Children.Add(swatch);
        }
    }

    private void ApplyPenColor(Color color)
    {
        var attrs = InkSurface.DefaultDrawingAttributes.Clone();
        attrs.Color = color;
        InkSurface.DefaultDrawingAttributes = attrs;
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

    private void RestoreInkEditingMode()
    {
        if (PenToggle.IsChecked == true)
        {
            InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        }
        else if (EraserToggle.IsChecked == true)
        {
            InkSurface.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
    }

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

    private void ResetCrop_Click(object sender, RoutedEventArgs e) => ResetCropToFull();

    private void MarkDirty()
    {
        if (_currentItem is null || _loadingDocument)
        {
            return;
        }

        _isDirty = true;
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
        ZoomLabel.Text = _zoomMode == ZoomMode.Fit ? $"Fit ({percent}%)" : $"{percent}%";

        var needsScroll = scale * imgW > viewportW + 1 || scale * imgH > viewportH + 1;
        CanvasScroll.HorizontalScrollBarVisibility = needsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        CanvasScroll.VerticalScrollBarVisibility = needsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private void EditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control &&
            Keyboard.FocusedElement is not TextBox)
        {
            Copy_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetZoomMode(ZoomMode.Fit);
            e.Handled = true;
        }
    }

    private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        var delta = e.Delta > 0 ? 1.1 : 1 / 1.1;
        _freeScale = _zoomMode == ZoomMode.Free ? _freeScale * delta : CanvasScale.ScaleX * delta;
        _zoomMode = ZoomMode.Free;
        FitToggle.IsChecked = false;
        UpdateZoom();
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

    private void Pen_Click(object sender, RoutedEventArgs e)
    {
        PenToggle.IsChecked = true;
        EraserToggle.IsChecked = false;
        _activeCropHandle = null;
        if (CropHandleLayer.IsMouseCaptured)
        {
            CropHandleLayer.ReleaseMouseCapture();
        }

        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        InkSurface.Focus();
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        EraserToggle.IsChecked = true;
        PenToggle.IsChecked = false;
        _activeCropHandle = null;
        if (CropHandleLayer.IsMouseCaptured)
        {
            CropHandleLayer.ReleaseMouseCapture();
        }

        InkSurface.EditingMode = InkCanvasEditingMode.EraseByStroke;
        InkSurface.Focus();
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

        var matrix = new Matrix(1, 0, 0, 1, -x, -y);
        InkSurface.Strokes.Transform(matrix, false);

        _originalSource = cropped;
        BackgroundImage.Source = cropped;
        ApplyCanvasDimensions();
        ResetCropToFull();
        MarkDirty();
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
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("ShellBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) =>
        {
            SelectEntry(entry);
            LoadItem(entry.Item);
            Copy_Click(this, new RoutedEventArgs());
        };
        menu.Items.Add(copy);

        var reveal = new MenuItem { Header = "Show in Explorer" };
        reveal.Click += (_, _) => RevealInExplorer(entry.Item.FlattenedPath);
        menu.Items.Add(reveal);

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => RenameItem(entry);
        menu.Items.Add(rename);

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteItem(entry);
        menu.Items.Add(delete);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

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
                InkSurface.Strokes = new StrokeCollection();
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
