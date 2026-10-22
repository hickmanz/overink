using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OverInk.Input;
using OverInk.Interop;
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
    private readonly Dictionary<Guid, StylusPointCollection> _liveRemoteRaw = new();
    private readonly Dictionary<Guid, UIElement> _remoteShapePreviews = new();
    private readonly Dictionary<Guid, string> _strokeAuthors = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Stroke, object> _remoteStrokeIds = new();
    private long _inkRevision;
    private bool _recordingUndo;
    private bool _applyingUndo;
    private bool _shapeDragging;
    private Point _shapeStart;
    private UIElement? _shapePreview;
    private Window? _toolbarWindow;
    private HwndSource? _hwndSource;
    private CaptureRegionOutlineWindow? _regionOutline;
    private Rect? _regionOutlinePixels;
    private PenCursorWindow? _penCursor;
    private Rect _desktopPixels = new(0, 0, 1, 1);
    private bool _replacing;
    private double _dpiScale = 1;

    public OverlayWindow(SketchSession session, IStrokeSource remoteStrokeSource)
    {
        _session = session;
        _remoteStrokeSource = remoteStrokeSource;
        InitializeComponent();

        InkSurface.Strokes.StrokesChanged += OnStrokesChanged;
        InkSurface.PreviewMouseDown += OnInkPreviewDown;
        InkSurface.PreviewStylusDown += OnInkPreviewDown;
        InkSurface.PreviewTouchDown += OnInkPreviewDown;
        InkSurface.StrokeCollected += (_, e) =>
        {
            StrokeSmoother.Apply(e.Stroke);
            BumpInkRevision();
        };
        // The pen draws its own cursor (see PenCursorWindow) so thin strokes never make the pointer vanish.
        InkSurface.PreviewMouseMove += (_, e) => MovePenCursor(e.GetPosition(InkSurface));
        InkSurface.PreviewStylusMove += (_, e) => MovePenCursor(e.GetPosition(InkSurface));
        InkSurface.MouseLeave += (_, _) => HidePenCursor();
        InkSurface.StylusLeave += (_, _) => HidePenCursor();
        ShapeLayer.MouseLeftButtonDown += OnShapeMouseDown;
        ShapeLayer.MouseMove += OnShapeMouseMove;
        ShapeLayer.MouseLeftButtonUp += OnShapeMouseUp;
        InitializeDrawMenu();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, e) => _dpiScale = e.NewDpi.DpiScaleX;
        Closed += (_, _) =>
        {
            _hwndSource?.RemoveHook(HitTestHook);
            _regionOutline?.Close();
            _penCursor?.Close();
        };
        IsVisibleChanged += (_, _) =>
        {
            SyncRegionOutline();
            if (!IsVisible)
            {
                HidePenCursor();
            }
        };

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

    /// <summary>Raised (on the UI thread) when strokes that came from a remote client leave the canvas
    /// (erase, undo, clear...) so remote clients can drop their local vector copies.</summary>
    public event Action<IReadOnlyList<Guid>>? RemoteStrokesRemoved;

    /// <summary>Raised when strokes a remote client knows by id (re)appear on the canvas: pieces left by a partial
    /// erase, or strokes restored by undo/redo. The remote client replaces/creates its vector copies from these.</summary>
    public event Action<IReadOnlyList<(Guid Id, Stroke Stroke)>>? RemoteStrokesAdded;

    /// <summary>Raised after <see cref="ClearAll"/>.</summary>
    public event Action? InkCleared;

    private List<Guid> RemoteIdsOf(StrokeCollection strokes)
    {
        var ids = new List<Guid>();
        foreach (var stroke in strokes)
        {
            if (_remoteStrokeIds.TryGetValue(stroke, out var id))
            {
                ids.Add((Guid)id);
            }
        }

        return ids;
    }

    private void PublishRemoteStrokesAdded(IEnumerable<Stroke> strokes)
    {
        if (RemoteStrokesAdded is not { } handler)
        {
            return;
        }

        var list = new List<(Guid, Stroke)>();
        foreach (var stroke in strokes)
        {
            if (_remoteStrokeIds.TryGetValue(stroke, out var id))
            {
                list.Add(((Guid)id, stroke));
            }
        }

        if (list.Count > 0)
        {
            handler(list);
        }
    }

    /// <summary>
    /// Gives each piece produced by erasing a remote client's stroke a fresh id (pieces are matched to the
    /// original by bounds), so remote clients can track them for later erase/undo.
    /// </summary>
    private void AssignPieceIds(IEnumerable<Stroke> originals, IEnumerable<Stroke> pieces)
    {
        var originalsWithIds = originals.Where(o => _remoteStrokeIds.TryGetValue(o, out _)).ToList();
        if (originalsWithIds.Count == 0)
        {
            return;
        }

        foreach (var piece in pieces)
        {
            if (_remoteStrokeIds.TryGetValue(piece, out _))
            {
                continue;
            }

            var bounds = piece.GetBounds();
            bounds.Inflate(1, 1);
            if (originalsWithIds.Any(o => o.GetBounds().Contains(bounds) || o.GetBounds().IntersectsWith(bounds)))
            {
                _remoteStrokeIds.Add(piece, Guid.NewGuid());
            }
        }
    }

    public void SetToolbarWindow(Window toolbarWindow) => _toolbarWindow = toolbarWindow;

    /// <summary>
    /// Shows (or clears, with null) the dashed frame around the iPad stream region (desktop pixels). It is a
    /// separate window excluded from screen capture, and follows this overlay's visibility.
    /// </summary>
    public void SetCaptureRegionOutline(Rect? regionPixels)
    {
        _regionOutlinePixels = regionPixels is { Width: > 0, Height: > 0 } ? regionPixels : null;
        SyncRegionOutline();
    }

    private void SyncRegionOutline()
    {
        if (_regionOutlinePixels is { } rect && IsVisible)
        {
            _regionOutline ??= new CaptureRegionOutlineWindow();
            _regionOutline.ShowAt(rect);
        }
        else if (_regionOutline is { IsVisible: true })
        {
            _regionOutline.Hide();
        }
    }

    /// <summary>Opacity (0–1) of the white veil as currently seen on screen; 0 when it isn't showing.</summary>
    public double VisibleVeilOpacity => ScreenVeil.Visibility == Visibility.Visible ? ScreenVeil.Opacity : 0;

    /// <summary>
    /// Hides the drawn ink, shapes and white veil until the returned object is disposed, so a screen capture
    /// shows the clean screen. The ink is saved separately as editable strokes and the veil as a value
    /// (<see cref="VisibleVeilOpacity"/>), so both stay adjustable in the editor.
    /// </summary>
    public IDisposable HideInkForCapture()
    {
        var inkVisibility = InkSurface.Visibility;
        var shapeVisibility = ShapeLayer.Visibility;
        var veilVisibility = ScreenVeil.Visibility;
        InkSurface.Visibility = Visibility.Hidden;
        ShapeLayer.Visibility = Visibility.Hidden;
        if (veilVisibility == Visibility.Visible)
        {
            ScreenVeil.Visibility = Visibility.Hidden;
        }

        return new RestoreOnDispose(() =>
        {
            InkSurface.Visibility = inkVisibility;
            ShapeLayer.Visibility = shapeVisibility;
            // Only put the veil back if nothing changed it while hidden.
            if (ScreenVeil.Visibility == Visibility.Hidden)
            {
                ScreenVeil.Visibility = veilVisibility;
            }
        });
    }

    /// <summary>
    /// Everything drawn on the overlay as one stroke collection, transformed by <paramref name="dipsToPixels"/>
    /// (overlay DIPs to capture-image pixels). Shapes become polyline strokes so they stay editable (and
    /// erasable) in the editors. In-progress previews are left out.
    /// </summary>
    public StrokeCollection ExportInk(Matrix dipsToPixels)
    {
        var result = new StrokeCollection();
        foreach (var stroke in InkSurface.Strokes)
        {
            result.Add(stroke.Clone());
        }

        foreach (var element in ShapeLayer.Children.Cast<UIElement>())
        {
            if (ReferenceEquals(element, _shapePreview) || _remoteShapePreviews.ContainsValue(element))
            {
                continue;
            }

            foreach (var stroke in ShapeStrokeConverter.ToStrokes(element))
            {
                result.Add(stroke);
            }
        }

        result.Transform(dipsToPixels, applyToStylusTip: true);
        return result;
    }

    private sealed class RestoreOnDispose(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }

    // One overlay window spans the whole virtual desktop (every monitor). A single window has a single DPI
    // scale, so strokes, shapes, undo and remote strokes share one coordinate space (local DIPs); the maths below
    // converts that to and from desktop pixels, which is what screen capture and the other windows use.

    /// <summary>The rectangle this overlay covers, in desktop pixels (can start at negative coordinates).</summary>
    public Rect DesktopPixels => _desktopPixels;

    /// <summary>
    /// Pixels per overlay DIP. Strokes are sized in DIPs, so one pen width is the same on every monitor.
    /// Cached (updated when the window is placed or its DPI changes) so it can be read from any thread.
    /// </summary>
    public double DpiScale => _dpiScale;

    public Point LocalDipsToDesktopPixels(Point local)
    {
        var scale = DpiScale;
        return new Point(_desktopPixels.Left + local.X * scale, _desktopPixels.Top + local.Y * scale);
    }

    public Point DesktopPixelsToLocalDips(Point pixel)
    {
        var scale = DpiScale;
        return new Point((pixel.X - _desktopPixels.Left) / scale, (pixel.Y - _desktopPixels.Top) / scale);
    }

    /// <summary>Overlay DIPs to the pixels of an image whose top-left is <paramref name="imageOrigin"/> on the desktop.</summary>
    public Matrix LocalDipsToImagePixels(Point imageOrigin)
    {
        var scale = DpiScale;
        return new Matrix(scale, 0, 0, scale, _desktopPixels.Left - imageOrigin.X, _desktopPixels.Top - imageOrigin.Y);
    }

    /// <summary>
    /// Covers every monitor with this window. Called at start-up and whenever the monitor layout changes. The
    /// window is positioned in physical pixels; if Windows resizes it because the DPI of its monitor changed,
    /// it is placed again until the size settles.
    /// </summary>
    public void PlaceOverDesktop()
    {
        if (_replacing)
        {
            return;
        }

        _replacing = true;
        try
        {
            var desktop = MonitorHelper.GetVirtualDesktopPixels();
            _dpiScale = WindowPlacement.PlaceSettling(this, _ => desktop);
            _desktopPixels = desktop;
        }
        finally
        {
            _replacing = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOverDesktop();
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
        if (msg == NativeMethods.WmDpiChanged && IsLoaded)
        {
            // Windows (and WPF) resize the window to suit the new DPI. Put it back over the whole desktop.
            Dispatcher.BeginInvoke(PlaceOverDesktop, System.Windows.Threading.DispatcherPriority.Normal);
            return IntPtr.Zero;
        }

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
            // Default: erase only the part under the eraser. Holding Ctrl while pressing switches to
            // whole-stroke erase (see OnInkPreviewDown).
            SketchTool.Eraser => InkCanvasEditingMode.EraseByPoint,
            SketchTool.Pen => InkCanvasEditingMode.Ink,
            _ => InkCanvasEditingMode.None
        };

        var eraserDiameter = EraserDiameter(_session.PenThickness);
        InkSurface.EraserShape = new EllipseStylusShape(eraserDiameter, eraserDiameter);

        InkSurface.IsHitTestVisible = _session.Mode == InteractionMode.Draw && !isShape;
        ShapeLayer.IsHitTestVisible = _session.Mode == InteractionMode.Draw && isShape;

        var attributes = new DrawingAttributes
        {
            Color = _session.PenColor,
            Width = _session.PenThickness,
            Height = _session.PenThickness,
            FitToCurve = false, // smoothing is done by StrokeSmoother on stroke completion
            IgnorePressure = !_session.PressureSensitive
        };
        InkSurface.DefaultDrawingAttributes = attributes;

        // With the pen the system pointer is replaced by the ring/reticle window.
        var penCursor = UsesPenCursor;
        InkSurface.UseCustomCursor = penCursor;
        if (penCursor)
        {
            InkSurface.Cursor = Cursors.None;
        }
        else
        {
            InkSurface.ClearValue(FrameworkElement.CursorProperty);
            HidePenCursor();
        }

        UpdateVeil();
    }

    private bool UsesPenCursor => _session.Mode == InteractionMode.Draw && _session.Tool == SketchTool.Pen;

    private void MovePenCursor(Point position)
    {
        if (!UsesPenCursor)
        {
            HidePenCursor();
            return;
        }

        var screen = LocalDipsToDesktopPixels(position);

        // Over the toolbar the normal pointer is needed (the toolbar is click-through for the overlay there).
        if (_toolbarWindow is { IsVisible: true } toolbar &&
            DpiHelper.GetPhysicalScreenRect(toolbar, paddingDips: 0).Contains(screen))
        {
            HidePenCursor();
            return;
        }

        _penCursor ??= new PenCursorWindow();
        _penCursor.ShowAtPixels(screen, _session.PenThickness * DpiScale);
    }

    private void HidePenCursor() => _penCursor?.HideCursor();

    private bool _remoteActive;

    /// <summary>
    /// The veil shows while drawing on the PC, or while a remote tablet is connected (tablet strokes
    /// don't need the PC in draw mode). Otherwise the screen looks normal for working in other apps.
    /// </summary>
    private void UpdateVeil()
    {
        var active = _session.Mode == InteractionMode.Draw || _remoteActive;
        var show = active && _session.ScreenVeil > 0;
        ScreenVeil.Opacity = show ? _session.ScreenVeil : 0;
        ScreenVeil.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRemoteActive(bool active)
    {
        _remoteActive = active;
        UpdateVeil();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkControl = 0x11;

    /// <summary>Eraser diameter in DIPs; the iPad client uses the same formula.</summary>
    private static double EraserDiameter(double penThickness) => Math.Max(12, penThickness * 2);

    /// <summary>Ctrl held when pressing with the eraser = erase whole strokes instead of just the touched part.</summary>
    private void OnInkPreviewDown(object? sender, InputEventArgs e)
    {
        if (_session.Tool != SketchTool.Eraser)
        {
            return;
        }

        var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        InkSurface.EditingMode = ctrl ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.EraseByPoint;
    }

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (e.Removed.Count > 0)
        {
            var ids = RemoteIdsOf(e.Removed);
            if (ids.Count > 0)
            {
                RemoteStrokesRemoved?.Invoke(ids);
            }
        }

        // Undo/redo can bring back strokes a remote client no longer has a vector copy of.
        if (_applyingUndo && e.Added.Count > 0)
        {
            PublishRemoteStrokesAdded(e.Added);
        }

        if (_recordingUndo)
        {
            return;
        }

        // Erase-by-point on the PC: the canvas swaps a stroke for its remaining pieces in one change.
        // Record that as a single undo step and give pieces of remote strokes ids so the iPad can mirror them.
        if (e.Added.Count > 0 && e.Removed.Count > 0)
        {
            AssignPieceIds(e.Removed, e.Added);
            PublishRemoteStrokesAdded(e.Added);
            _session.UndoStack.Push(new InkReplaceAction(InkSurface, e.Removed, e.Added, UndoAuthors.Host));
            BumpInkRevision();
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
        var stroke = new Stroke(points.Clone(), e.Attributes.Clone());
        _liveRemoteStrokes[e.Id] = stroke;
        _liveRemoteRaw[e.Id] = points;
        _remoteStrokeIds.Add(stroke, e.Id);
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
        if (!_liveRemoteStrokes.TryGetValue(e.Id, out var stroke) ||
            !_liveRemoteRaw.TryGetValue(e.Id, out var raw))
        {
            return;
        }

        raw.Add(e.Points);

        _recordingUndo = true;
        try
        {
            // Re-smooth from the raw samples each batch so the stroke looks smooth while it is drawn.
            stroke.StylusPoints = StrokeSmoother.Smooth(raw, stroke.DrawingAttributes.Width) ?? raw.Clone();
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
        _recordingUndo = true;
        try
        {
            if (_liveRemoteRaw.Remove(e.Id, out var raw))
            {
                stroke.StylusPoints = StrokeSmoother.Smooth(raw, stroke.DrawingAttributes.Width) ?? raw.Clone();
            }
        }
        finally
        {
            _recordingUndo = false;
        }

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
        _liveRemoteRaw.Remove(e.Id);
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

        // NOTE: Strokes.HitTest(IEnumerable<Point>, int) is a *lasso* test (percentage inside polygon), which
        // never matches a thin eraser path. Use a stylus-shaped hit test instead.
        var diameter = EraserDiameter(e.EraserSize);
        var shape = new EllipseStylusShape(diameter, diameter);
        var found = e.PointsDips.Length == 1
            ? InkSurface.Strokes.HitTest(e.PointsDips[0], diameter)
            : InkSurface.Strokes.HitTest(e.PointsDips, shape);

        // Strokes still being drawn by a remote client must not be swapped out underneath it.
        var hits = new StrokeCollection(found.Where(s => !_liveRemoteStrokes.ContainsValue(s)));
        if (hits.Count > 0)
        {
            if (e.Whole)
            {
                EraseWholeStrokes(hits, e.AuthorId);
            }
            else
            {
                ErasePartOfStrokes(hits, e.PointsDips, shape, e.AuthorId);
            }
        }

        RemoveIntersectingShapes(e.PointsDips, diameter / 2, e.AuthorId);
    }

    private void EraseWholeStrokes(StrokeCollection hits, string authorId)
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

        _session.UndoStack.Push(new InkStrokeAction(InkSurface, hits, added: false, authorId));
        BumpInkRevision();
    }

    /// <summary>Cuts the touched part out of each stroke, keeping the rest as new strokes (one undo step).</summary>
    private void ErasePartOfStrokes(StrokeCollection hits, Point[] path, StylusShape eraser, string authorId)
    {
        var removed = new StrokeCollection();
        var added = new StrokeCollection();

        // A tap is a single point; give the eraser path a (tiny) extent so it is a valid path.
        if (path.Length == 1)
        {
            path = [path[0], new Point(path[0].X + 0.01, path[0].Y)];
        }

        foreach (var stroke in hits)
        {
            StrokeCollection pieces;
            try
            {
                pieces = stroke.GetEraseResult(path, eraser);
            }
            catch (ArgumentException)
            {
                continue;
            }

            // Hit test said touched but nothing was actually cut away.
            if (pieces.Count == 1 && pieces[0].StylusPoints.Count == stroke.StylusPoints.Count)
            {
                continue;
            }

            var keep = pieces.Where(p => !IsStub(p)).ToList();
            removed.Add(stroke);
            foreach (var piece in keep)
            {
                added.Add(piece);
            }

            AssignPieceIds([stroke], keep);
        }

        if (removed.Count == 0)
        {
            return;
        }

        _recordingUndo = true;
        try
        {
            InkSurface.Strokes.Remove(removed);
            if (added.Count > 0)
            {
                InkSurface.Strokes.Add(added);
            }
        }
        finally
        {
            _recordingUndo = false;
        }

        PublishRemoteStrokesAdded(added);
        _session.UndoStack.Push(new InkReplaceAction(InkSurface, removed, added, authorId));
        BumpInkRevision();
    }

    /// <summary>A leftover sliver shorter than the pen is just a dot; drop it.</summary>
    private static bool IsStub(Stroke piece)
    {
        var points = piece.StylusPoints;
        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
        }

        return length < piece.DrawingAttributes.Width * 0.75;
    }

    private void RemoveIntersectingShapes(Point[] points, double eraserSize, string authorId)
    {
        if (points.Length == 0)
        {
            return;
        }

        // eraserSize here is the eraser *radius*. Test against each shape's actual outline (lines and arrows are
        // Canvases with absolutely positioned children, so a bounding-box test on the element never hit them).
        var toRemove = ShapeLayer.Children.Cast<UIElement>()
            .Where(el => !ReferenceEquals(el, _shapePreview) && !_remoteShapePreviews.ContainsValue(el))
            .Where(el => ShapeHitTester.Hits(el, points, eraserSize))
            .ToList();

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
        InkCleared?.Invoke();
    }

    public void Undo(string? authorId = null) => ApplyUndoRedo(() => _session.UndoStack.Undo(authorId));
    public void Redo(string? authorId = null) => ApplyUndoRedo(() => _session.UndoStack.Redo(authorId));

    /// <summary>
    /// Runs an undo/redo step. Changes it makes to the canvas must not be recorded as new undo entries
    /// (which would clear the redo stack), but remote clients still need to hear about them.
    /// </summary>
    private void ApplyUndoRedo(Action step)
    {
        _applyingUndo = true;
        _recordingUndo = true;
        try
        {
            step();
        }
        finally
        {
            _recordingUndo = false;
            _applyingUndo = false;
        }

        BumpInkRevision();
    }
}
