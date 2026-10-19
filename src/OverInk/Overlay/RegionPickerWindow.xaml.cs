using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using OverInk.Services;

namespace OverInk.Overlay;

public partial class RegionPickerWindow : Window
{
    private Point _start;
    private bool _dragging;
    private Rect _monitorDips;

    public RegionPickerWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    public Rect? SelectedDipsRect { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var geometry = MonitorHelper.GetPrimaryMonitorGeometry();
        _monitorDips = geometry.Dips;
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
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(RootCanvas);
        SelectionFill.Visibility = Visibility.Visible;
        UpdateDimPanels(new Rect(_start, _start));
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
        {
            base.OnMouseMove(e);
            return;
        }

        var current = e.GetPosition(RootCanvas);
        var rect = new Rect(
            Math.Min(_start.X, current.X),
            Math.Min(_start.Y, current.Y),
            Math.Abs(_start.X - current.X),
            Math.Abs(_start.Y - current.Y));
        UpdateDimPanels(rect);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        var current = e.GetPosition(RootCanvas);
        var rect = new Rect(
            Math.Min(_start.X, current.X),
            Math.Min(_start.Y, current.Y),
            Math.Abs(_start.X - current.X),
            Math.Abs(_start.Y - current.Y));

        if (rect.Width < 40 || rect.Height < 40)
        {
            DialogResult = false;
            Close();
            return;
        }

        var screenRect = new Rect(
            _monitorDips.Left + rect.Left,
            _monitorDips.Top + rect.Top,
            rect.Width,
            rect.Height);
        SelectedDipsRect = screenRect;
        DialogResult = true;
        Close();
        base.OnMouseLeftButtonUp(e);
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
