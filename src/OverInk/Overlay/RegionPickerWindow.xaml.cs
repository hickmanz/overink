using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using OverInk.Services;

namespace OverInk.Overlay;

public partial class RegionPickerWindow : Window
{
    private const double DragThreshold = 4;

    private Point _start;
    private bool _pressed;
    private bool _dragging;
    private Rect _monitorDips;
    private Rect _monitorPixels;
    private double _scale = 1;
    private WindowHitTester? _windows;
    private Rect? _hoverLocal;

    public RegionPickerWindow(string? hint = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
            }
        };
    }

    public Rect? SelectedDipsRect { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        _monitorDips = geometry.Dips;
        _monitorPixels = geometry.Pixels;
        _scale = _monitorDips.Width > 0 ? _monitorPixels.Width / _monitorDips.Width : 1;
        Left = _monitorDips.Left;
        Top = _monitorDips.Top;
        Width = _monitorDips.Width;
        Height = _monitorDips.Height;
        RootCanvas.Width = _monitorDips.Width;
        RootCanvas.Height = _monitorDips.Height;
        HintBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintBar, Math.Max(12, (_monitorDips.Width - HintBar.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBar, 16);
        UpdateDimPanels(new Rect(_start, _start));

        _windows = WindowHitTester.Capture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragging = false;
        _start = e.GetPosition(RootCanvas);
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        Cancel();
        base.OnMouseRightButtonUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var current = e.GetPosition(RootCanvas);

        if (_pressed && !_dragging &&
            (Math.Abs(current.X - _start.X) >= DragThreshold || Math.Abs(current.Y - _start.Y) >= DragThreshold))
        {
            _dragging = true;
            HintText.Text = "Release to capture";
        }

        if (_dragging)
        {
            ShowSelection(RectFrom(_start, current));
        }
        else if (!_pressed)
        {
            _hoverLocal = FindWindowUnder(current);
            if (_hoverLocal is { } hover)
            {
                ShowSelection(hover);
            }
            else
            {
                HideSelection();
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _pressed = false;
        ReleaseMouseCapture();
        var current = e.GetPosition(RootCanvas);

        Rect rect;
        if (_dragging)
        {
            rect = RectFrom(_start, current);
            if (rect.Width < 40 || rect.Height < 40)
            {
                Cancel();
                return;
            }
        }
        else if (FindWindowUnder(current) is { } window)
        {
            rect = window;
        }
        else
        {
            // Plain click on nothing snappable: keep waiting.
            base.OnMouseLeftButtonUp(e);
            return;
        }

        SelectedDipsRect = new Rect(
            _monitorDips.Left + rect.Left,
            _monitorDips.Top + rect.Top,
            rect.Width,
            rect.Height);
        DialogResult = true;
        Close();
        base.OnMouseLeftButtonUp(e);
    }

    private void Cancel()
    {
        DialogResult = false;
        Close();
    }

    private static Rect RectFrom(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    /// <summary>Window under the cursor as a rect local to this picker (DIPs), clipped to the monitor.</summary>
    private Rect? FindWindowUnder(Point local)
    {
        if (_windows is null)
        {
            return null;
        }

        var pixel = new Point(
            _monitorPixels.X + local.X * _scale,
            _monitorPixels.Y + local.Y * _scale);
        if (_windows.HitTest(pixel) is not { } px)
        {
            return null;
        }

        var dips = new Rect(
            (px.X - _monitorPixels.X) / _scale,
            (px.Y - _monitorPixels.Y) / _scale,
            px.Width / _scale,
            px.Height / _scale);
        dips.Intersect(new Rect(0, 0, _monitorDips.Width, _monitorDips.Height));
        return dips.IsEmpty || dips.Width < 8 || dips.Height < 8 ? null : dips;
    }

    private void ShowSelection(Rect rect)
    {
        SelectionFill.Visibility = Visibility.Visible;
        UpdateDimPanels(rect);

        SizeText.Text = $"{Math.Round(rect.Width * _scale)} × {Math.Round(rect.Height * _scale)}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badge = SizeBadge.DesiredSize;
        var x = Math.Clamp(rect.Left, 4, Math.Max(4, _monitorDips.Width - badge.Width - 4));
        var y = rect.Bottom + 8;
        if (y + badge.Height > _monitorDips.Height - 4)
        {
            y = Math.Max(4, rect.Bottom - badge.Height - 8);
        }

        Canvas.SetLeft(SizeBadge, x);
        Canvas.SetTop(SizeBadge, y);
    }

    private void HideSelection()
    {
        SelectionFill.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        UpdateDimPanels(new Rect(0, 0, 0, 0));
    }
    private void UpdateDimPanels(Rect localRect)
    {
        var w = _monitorDips.Width;
        var h = _monitorDips.Height;
        SetRect(DimTop, 0, 0, w, Math.Max(0, localRect.Top));
        SetRect(DimLeft, 0, localRect.Top, Math.Max(0, localRect.Left), localRect.Height);
        SetRect(DimRight, localRect.Right, localRect.Top, Math.Max(0, w - localRect.Right), localRect.Height);
        SetRect(DimBottom, 0, localRect.Bottom, w, Math.Max(0, h - localRect.Bottom));
        SetRect(SelectionFill, localRect.Left, localRect.Top, localRect.Width, localRect.Height);
    }

    private static void SetRect(Rectangle rect, double x, double y, double width, double height)
    {
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
    }
}
