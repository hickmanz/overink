using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

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
    private BitmapSource _originalSource = null!;
    private bool _isDirty;
    private bool _loadingDocument;
    private bool _suppressLibrarySelection;
    private ZoomMode _zoomMode = ZoomMode.Fit;
    private double _freeScale = 1;
    private Point _cropStart;
    private Rect _cropBounds;
    private Point? _dragStart;
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
            FitToCurve = true
        };

        InkSurface.Strokes.StrokesChanged += (_, _) => MarkDirty();

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
        Dispatcher.BeginInvoke(() => RefreshTray(keepSelection: true));
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
        _originalSource = original;
        BackgroundImage.Source = original;
        InkSurface.Strokes.Clear();
        foreach (var stroke in strokes)
        {
            InkSurface.Strokes.Add(stroke);
        }
        ApplyCanvasDimensions();
        CropOverlay.Visibility = Visibility.Collapsed;
        DetachCropHandlers();
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
        CropOverlay.Width = w;
        CropOverlay.Height = h;
    }

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
        _isDirty = false;

        var entry = _trayEntries.FirstOrDefault(e =>
            string.Equals(e.Item.FlattenedPath, _currentItem.FlattenedPath, StringComparison.OrdinalIgnoreCase));
        entry?.RefreshThumbnail();

        if (showToast)
        {
            ShowStatus("Saved");
        }
    }

    private BitmapSource RenderFlattened()
    {
        var width = (int)CanvasHost.Width;
        var height = (int)CanvasHost.Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_originalSource, new Rect(0, 0, width, height));
            var brush = new VisualBrush(InkSurface) { Stretch = Stretch.None };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

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
        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        CropOverlay.Visibility = Visibility.Collapsed;
        DetachCropHandlers();
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        EraserToggle.IsChecked = true;
        PenToggle.IsChecked = false;
        InkSurface.EditingMode = InkCanvasEditingMode.EraseByStroke;
        CropOverlay.Visibility = Visibility.Collapsed;
        DetachCropHandlers();
    }

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        PenToggle.IsChecked = false;
        EraserToggle.IsChecked = false;
        CropOverlay.Visibility = Visibility.Visible;
        InkSurface.EditingMode = InkCanvasEditingMode.None;
        AttachCropHandlers();
    }

    private void AttachCropHandlers()
    {
        CropOverlay.MouseLeftButtonDown += CropOverlay_MouseLeftButtonDown;
        CropOverlay.MouseMove += CropOverlay_MouseMove;
        CropOverlay.MouseLeftButtonUp += CropOverlay_MouseLeftButtonUp;
    }

    private void DetachCropHandlers()
    {
        CropOverlay.MouseLeftButtonDown -= CropOverlay_MouseLeftButtonDown;
        CropOverlay.MouseMove -= CropOverlay_MouseMove;
        CropOverlay.MouseLeftButtonUp -= CropOverlay_MouseLeftButtonUp;
    }

    private void CropOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cropStart = e.GetPosition(CropOverlay);
        _cropBounds = new Rect(_cropStart, _cropStart);
        UpdateCropRect();
        CropOverlay.CaptureMouse();
    }

    private void CropOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!CropOverlay.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(CropOverlay);
        _cropBounds = new Rect(
            Math.Min(_cropStart.X, current.X),
            Math.Min(_cropStart.Y, current.Y),
            Math.Abs(_cropStart.X - current.X),
            Math.Abs(_cropStart.Y - current.Y));
        UpdateCropRect();
    }

    private void CropOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CropOverlay.ReleaseMouseCapture();
    }

    private void UpdateCropRect()
    {
        Canvas.SetLeft(CropRect, _cropBounds.X);
        Canvas.SetTop(CropRect, _cropBounds.Y);
        CropRect.Width = Math.Max(1, _cropBounds.Width);
        CropRect.Height = Math.Max(1, _cropBounds.Height);
    }

    private void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (_cropBounds.Width < 2 || _cropBounds.Height < 2)
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
        CropOverlay.Visibility = Visibility.Collapsed;
        DetachCropHandlers();
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
