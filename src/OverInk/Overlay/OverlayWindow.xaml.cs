using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OverInk.Input;
using OverInk.Models;
using OverInk.Services;

namespace OverInk.Overlay;

public partial class OverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private readonly SketchSession _session;
    private readonly IStrokeSource _remoteStrokeSource;
    private readonly Dictionary<Guid, Stroke> _liveRemoteStrokes = new();
    private readonly Dictionary<Guid, UIElement> _remoteShapePreviews = new();
    private readonly Dictionary<Guid, string> _strokeAuthors = new();
    private long _inkRevision;
    private bool _recordingUndo;
    private bool _shapeDragging;
    private Point _shapeStart;
    private UIElement? _shapePreview;
    private Window? _toolbarWindow;
    private HwndSource? _hwndSource;

    public OverlayWindow(SketchSession session, IStrokeSource remoteStrokeSource)
    {
        _session = session;
        _remoteStrokeSource = remoteStrokeSource;
        InitializeComponent();

        InkSurface.Strokes.StrokesChanged += OnStrokesChanged;
        ShapeLayer.MouseLeftButtonDown += OnShapeMouseDown;
        ShapeLayer.MouseMove += OnShapeMouseMove;
        ShapeLayer.MouseLeftButtonUp += OnShapeMouseUp;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _hwndSource?.RemoveHook(HitTestHook);

        _session.Changed += ApplySession;
        _session.UndoStack.Changed += (_, _) => BumpInkRevision();

        _remoteStrokeSource.StrokeBegan += OnRemoteStrokeBegan;
        _remoteStrokeSource.StrokePointsAdded += OnRemoteStrokePoints;
        _remoteStrokeSource.StrokeEnded += OnRemoteStrokeEnded;
        _remoteStrokeSource.StrokeCancelled += OnRemoteStrokeCancelled;
        _remoteStrokeSource.ShapePreview += OnRemoteShapePreview;
        _remoteStrokeSource.ShapeCommitted += OnRemoteShapeCommitted;
        _remoteStrokeSource.EraseRequested += OnRemoteErase;

        ApplySession();
    }

    public InkCanvas InkCanvas => InkSurface;

    public Panel ShapeLayerPanel => ShapeLayer;

    public long InkRevision => Interlocked.Read(ref _inkRevision);

    public void SetToolbarWindow(Window toolbarWindow) => _toolbarWindow = toolbarWindow;

    public void SetCaptureRegionOutline(Rect? regionDips)
    {
        if (regionDips is not { Width: > 0, Height: > 0 } rect)
        {
            CaptureRegionOutline.Visibility = Visibility.Collapsed;
            return;
        }

        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        CaptureRegionOutline.HorizontalAlignment = HorizontalAlignment.Left;
        CaptureRegionOutline.VerticalAlignment = VerticalAlignment.Top;
        CaptureRegionOutline.Width = rect.Width;
        CaptureRegionOutline.Height = rect.Height;
        CaptureRegionOutline.Margin = new Thickness(
            rect.Left - geometry.Dips.Left,
            rect.Top - geometry.Dips.Top,
            0,
            0);
        CaptureRegionOutline.Visibility = Visibility.Visible;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        Left = geometry.Dips.Left;
        Top = geometry.Dips.Top;
        Width = geometry.Dips.Width;
        Height = geometry.Dips.Height;

        ClickThroughHelper.SetClickThrough(this, _session.Mode == InteractionMode.PassThrough);
        InkSurface.Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(HitTestHook);
    }

    private IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest ||
            _session.Mode != InteractionMode.Draw ||
            _toolbarWindow == null ||
            _toolbarWindow.Visibility != Visibility.Visible)
        {
            return IntPtr.Zero;
        }

        var packed = lParam.ToInt64();
        var x = (short)(packed & 0xFFFF);
        var y = (short)((packed >> 16) & 0xFFFF);
        var toolbarRect = DpiHelper.GetPhysicalScreenRect(_toolbarWindow, paddingDips: 14);

        if (DpiHelper.PhysicalScreenRectContains(toolbarRect, x, y))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void ApplySession()
    {
        ClickThroughHelper.SetClickThrough(this, _session.Mode == InteractionMode.PassThrough);

        var isShape = _session.Tool is SketchTool.Line or SketchTool.Arrow or SketchTool.Rectangle or SketchTool.Ellipse;
        InkSurface.EditingMode = _session.Tool switch
        {
            SketchTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            SketchTool.Pen => InkCanvasEditingMode.Ink,
            _ => InkCanvasEditingMode.None
        };

        InkSurface.IsHitTestVisible = _session.Mode == InteractionMode.Draw && !isShape;
        ShapeLayer.IsHitTestVisible = _session.Mode == InteractionMode.Draw && isShape;

        var attributes = new DrawingAttributes
        {
            Color = _session.PenColor,
            Width = _session.PenThickness,
            Height = _session.PenThickness,
            FitToCurve = true,
            IgnorePressure = false
        };
        InkSurface.DefaultDrawingAttributes = attributes;
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_recordingUndo)
        {
            return;
        }

        if (e.Added.Count > 0)
        {
            _session.UndoStack.Push(new InkStrokeAction(InkSurface, e.Added, added: true));
            BumpInkRevision();
        }

        if (e.Removed.Count > 0)
        {
            _session.UndoStack.Push(new InkStrokeAction(InkSurface, e.Removed, added: false));
            BumpInkRevision();
        }
    }

    private void OnShapeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_session.Mode != InteractionMode.Draw)
        {
            return;
        }

        _shapeDragging = true;
        _shapeStart = e.GetPosition(ShapeLayer);
        _shapePreview = ShapeDrawingHelper.CreatePreview(
            _session.Tool, _shapeStart, _shapeStart, _session.PenColor, _session.PenThickness);
        ShapeDrawingHelper.PositionShape(_shapePreview, _shapeStart, _shapeStart);
        ShapeLayer.Children.Add(_shapePreview);
        ShapeLayer.CaptureMouse();
    }

    private void OnShapeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_shapeDragging || _shapePreview == null)
        {
            return;
        }

        var current = e.GetPosition(ShapeLayer);
        ShapeDrawingHelper.PositionShape(_shapePreview, _shapeStart, current);
    }

    private void OnShapeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_shapeDragging || _shapePreview == null)
        {
            return;
        }

        _shapeDragging = false;
        ShapeLayer.ReleaseMouseCapture();

        var end = e.GetPosition(ShapeLayer);
        ShapeLayer.Children.Remove(_shapePreview);
        _shapePreview = null;

        AddShape(_session.Tool, _shapeStart, end, _session.PenColor, _session.PenThickness, UndoAuthors.Host);
    }

    private void OnRemoteStrokeBegan(object? sender, RemoteStrokeBeginEventArgs e)
    {
        EnsureVisibleForRemoteInk();

        var points = new StylusPointCollection { new StylusPoint(e.FirstPointDips.X, e.FirstPointDips.Y, e.Pressure) };
        var stroke = new Stroke(points, e.Attributes.Clone());
        _liveRemoteStrokes[e.Id] = stroke;
        _strokeAuthors[e.Id] = e.AuthorId;

        _recordingUndo = true;
        try
        {
            InkSurface.Strokes.Add(stroke);
        }
        finally
        {
            _recordingUndo = false;
        }
    }

    private void OnRemoteStrokePoints(object? sender, RemoteStrokePointsEventArgs e)
    {
        if (!_liveRemoteStrokes.TryGetValue(e.Id, out var stroke))
        {
            return;
        }

        _recordingUndo = true;
        try
        {
            stroke.StylusPoints.Add(e.Points);
        }
        finally
        {
            _recordingUndo = false;
        }

        BumpInkRevision();
    }

    private void OnRemoteStrokeEnded(object? sender, RemoteStrokeIdEventArgs e)
    {
        if (!_liveRemoteStrokes.Remove(e.Id, out var stroke))
        {
            Remote.RemoteDebugLog.Write($"remote strokeEnd unknown id={e.Id}");
            return;
        }

        _strokeAuthors.Remove(e.Id);
        var batch = new StrokeCollection { stroke };
        _session.UndoStack.Push(new InkStrokeAction(InkSurface, batch, added: true, e.AuthorId));
        BumpInkRevision();
    }

    private void OnRemoteStrokeCancelled(object? sender, RemoteStrokeIdEventArgs e)
    {
        if (!_liveRemoteStrokes.Remove(e.Id, out var stroke))
        {
            return;
        }

        _strokeAuthors.Remove(e.Id);
        _recordingUndo = true;
        try
        {
            InkSurface.Strokes.Remove(stroke);
        }
        finally
        {
            _recordingUndo = false;
        }

        BumpInkRevision();
    }

    private void OnRemoteShapePreview(object? sender, RemoteShapeEventArgs e)
    {
        EnsureVisibleForRemoteInk();

        if (!_remoteShapePreviews.TryGetValue(e.Id, out var preview))
        {
            preview = ShapeDrawingHelper.CreatePreview(e.Tool, e.StartDips, e.EndDips, e.Color, e.Thickness);
            _remoteShapePreviews[e.Id] = preview;
            ShapeLayer.Children.Add(preview);
        }

        ShapeDrawingHelper.PositionShape(preview, e.StartDips, e.EndDips);
    }

    private void OnRemoteShapeCommitted(object? sender, RemoteShapeEventArgs e)
    {
        if (_remoteShapePreviews.Remove(e.Id, out var preview))
        {
            ShapeLayer.Children.Remove(preview);
        }

        AddShape(e.Tool, e.StartDips, e.EndDips, e.Color, e.Thickness, e.AuthorId);
    }

    private void OnRemoteErase(object? sender, RemoteEraseEventArgs e)
    {
        if (e.PointsDips.Length == 0)
        {
            return;
        }

        var hits = InkSurface.Strokes.HitTest(e.PointsDips, (int)Math.Max(1, e.EraserSize));
        if (hits.Count > 0)
        {
            _recordingUndo = true;
            try
            {
                InkSurface.Strokes.Remove(hits);
            }
            finally
            {
                _recordingUndo = false;
            }

            _session.UndoStack.Push(new InkStrokeAction(InkSurface, hits, added: false, e.AuthorId));
            BumpInkRevision();
        }

        RemoveIntersectingShapes(e.PointsDips, e.EraserSize, e.AuthorId);
    }

    private void RemoveIntersectingShapes(Point[] points, double eraserSize, string authorId)
    {
        if (points.Length == 0)
        {
            return;
        }

        var bounds = new Rect(points[0], points[0]);
        foreach (var p in points.Skip(1))
        {
            bounds.Union(p);
        }

        bounds.Inflate(eraserSize, eraserSize);
        var toRemove = ShapeLayer.Children.Cast<UIElement>().Where(el =>
        {
            var left = Canvas.GetLeft(el);
            var top = Canvas.GetTop(el);
            if (double.IsNaN(left))
            {
                left = 0;
            }

            if (double.IsNaN(top))
            {
                top = 0;
            }

            var w = (el as FrameworkElement)?.ActualWidth ?? 0;
            var h = (el as FrameworkElement)?.ActualHeight ?? 0;
            return bounds.IntersectsWith(new Rect(left, top, Math.Max(1, w), Math.Max(1, h)));
        }).ToList();

        foreach (var element in toRemove)
        {
            ShapeLayer.Children.Remove(element);
            _session.UndoStack.Push(new ShapeAction(ShapeLayer, element, added: false, authorId));
        }

        if (toRemove.Count > 0)
        {
            BumpInkRevision();
        }
    }

    private void AddShape(SketchTool tool, Point start, Point end, Color color, double thickness, string authorId)
    {
        if ((start - end).Length < 4)
        {
            return;
        }

        var finalShape = ShapeDrawingHelper.CreateShapeElement(tool, start, end, color, thickness);
        ShapeDrawingHelper.PositionShape(finalShape, start, end);
        ShapeLayer.Children.Add(finalShape);
        _session.UndoStack.Push(new ShapeAction(ShapeLayer, finalShape, added: true, authorId));
        BumpInkRevision();
    }

    private void EnsureVisibleForRemoteInk()
    {
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }
    }

    private void BumpInkRevision() => Interlocked.Increment(ref _inkRevision);

    public void ClearAll()
    {
        _recordingUndo = true;
        try
        {
            InkSurface.Strokes.Clear();
            ShapeLayer.Children.Clear();
            _liveRemoteStrokes.Clear();
            _remoteShapePreviews.Clear();
            _strokeAuthors.Clear();
        }
        finally
        {
            _recordingUndo = false;
        }

        _session.UndoStack.Clear();
        BumpInkRevision();
    }

    public void Undo(string? authorId = null) => _session.UndoStack.Undo(authorId);
    public void Redo(string? authorId = null) => _session.UndoStack.Redo(authorId);
}
