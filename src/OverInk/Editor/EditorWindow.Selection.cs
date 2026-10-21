using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OverInk.Models;

namespace OverInk.Editor;

/// <summary>The Select tool: pick strokes/shapes, drag them, recolor, resize, delete.</summary>
public partial class EditorWindow
{
    private enum SelectDrag
    {
        None,
        Move,
        Marquee
    }

    private sealed record AttributeSnapshot(Stroke Stroke, Color Color, double Width, double Height);

    private const double SelectHitRadius = 6;
    private const double SelectionBarHeight = 32;

    private readonly List<Stroke> _selected = new();
    private SelectDrag _selectDrag;
    private Point _selectLast;
    private Point _selectStart;
    private Vector _moveTotal;
    private bool _syncingSelectionUi;

    /// <summary>Attributes of the selection before the current color/width adjustment began.</summary>
    private List<AttributeSnapshot>? _attributesBefore;
    private DispatcherTimer? _selectionEditTimer;

    private bool HasSelection => _selected.Count > 0;

    /// <summary>The color the swatches and wheel show: the selection's while something is selected, else the pen's.</summary>
    private Color DisplayColor => HasSelection ? _selected[0].DrawingAttributes.Color : _penColor;

    private double CurrentScale => Math.Max(0.05, CanvasScale.ScaleX);

    // ----- Selection state -----

    private void SetSelection(IEnumerable<Stroke> strokes)
    {
        FlushSelectionEdit();
        _selected.Clear();
        _selected.AddRange(strokes);
        OnSelectionChanged();
    }

    private void ClearSelection()
    {
        FlushSelectionEdit();
        if (_selectDrag != SelectDrag.None)
        {
            _selectDrag = SelectDrag.None;
            MarqueeBox.Visibility = Visibility.Collapsed;
            if (SelectionLayer.IsMouseCaptured)
            {
                SelectionLayer.ReleaseMouseCapture();
            }
        }

        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        UpdateSelectionVisuals(rebuildMarks: true);
        UpdateToolPanels();

        // Make the color and width controls reflect the selection (or go back to the pen's values).
        _syncingSelectionUi = true;
        try
        {
            var width = HasSelection ? _selected[0].DrawingAttributes.Width : InkSurface.DefaultDrawingAttributes.Width;
            PenSizeSlider.Value = Math.Clamp(width, PenSizeSlider.Minimum, PenSizeSlider.Maximum);
            EditorColorPicker.SetColor(DisplayColor);
        }
        finally
        {
            _syncingSelectionUi = false;
        }

        UpdateSwatchSelection();
    }

    /// <summary>The pen controls and hints depend on the tool and on whether a selection exists.</summary>
    private void UpdateToolPanels()
    {
        var erasing = _tool == SketchTool.Eraser;
        var selecting = _tool == SketchTool.Select;
        PenSection.Visibility = erasing || (selecting && !HasSelection) ? Visibility.Collapsed : Visibility.Visible;
        EraserHint.Visibility = erasing ? Visibility.Visible : Visibility.Collapsed;
        SelectHint.Visibility = selecting && !HasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Chrome (dashed box + floating delete bar) -----

    private void UpdateSelectionVisuals(bool rebuildMarks = false)
    {
        if (SelectionBox is null)
        {
            return;
        }

        if (rebuildMarks)
        {
            RebuildSelectionMarks();
        }
        else
        {
            // While dragging, the halo and end points ride along with the strokes instead of being rebuilt.
            SelectionMarks.RenderTransform = _moveTotal.LengthSquared > 0
                ? new TranslateTransform(_moveTotal.X, _moveTotal.Y)
                : Transform.Identity;
        }

        if (!HasSelection)
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = CurrentScale;
        var bounds = StrokeSelection.Bounds(_selected);
        if (bounds.IsEmpty)
        {
            SelectionBox.Visibility = Visibility.Collapsed;
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }

        // Chrome is drawn inside the zoomed canvas but sized in screen pixels.
        var pad = 3 / scale;
        bounds.Inflate(pad, pad);
        SelectionBox.StrokeThickness = 1.5 / scale;
        SelectionBox.StrokeDashArray = new DoubleCollection { 4, 3 };
        Canvas.SetLeft(SelectionBox, bounds.Left);
        Canvas.SetTop(SelectionBox, bounds.Top);
        SelectionBox.Width = Math.Max(1, bounds.Width);
        SelectionBox.Height = Math.Max(1, bounds.Height);
        SelectionBox.Visibility = Visibility.Visible;

        SelectionBar.RenderTransform = new ScaleTransform(1 / scale, 1 / scale);
        var gap = 6 / scale;
        var barTop = bounds.Top - gap - SelectionBarHeight / scale;
        if (barTop < 0)
        {
            barTop = bounds.Bottom + gap;
        }

        Canvas.SetLeft(SelectionBar, Math.Max(0, bounds.Left));
        Canvas.SetTop(SelectionBar, barTop);
        SelectionBar.Visibility = Visibility.Visible;
    }

    private Rect SelectionBounds => StrokeSelection.Bounds(_selected);

    private static readonly Brush HaloBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x4C, 0x8D, 0xFF)));
    private static readonly Brush StartMarkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly Brush EndMarkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Outlines every selected stroke with a halo and marks where each entity starts (green) and ends (blue), so it
    /// is clear exactly what the selection holds. A multi-stroke entity (an arrow) is marked once, on its first stroke.
    /// </summary>
    private void RebuildSelectionMarks()
    {
        SelectionMarks.Children.Clear();
        SelectionMarks.RenderTransform = Transform.Identity;
        if (!HasSelection)
        {
            return;
        }

        var k = 1 / CurrentScale;
        var markedGroups = new HashSet<string>();
        var ends = new List<(Point Start, Point End)>();
        foreach (var stroke in _selected)
        {
            SelectionMarks.Children.Add(new System.Windows.Shapes.Path
            {
                Data = stroke.GetGeometry(),
                Stroke = HaloBrush,
                StrokeThickness = 6 * k,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            });

            var group = StrokeGroups.Get(stroke);
            if ((group is null || markedGroups.Add(group)) && stroke.StylusPoints.Count > 0)
            {
                var points = stroke.StylusPoints;
                ends.Add((new Point(points[0].X, points[0].Y),
                    new Point(points[^1].X, points[^1].Y)));
            }
        }

        // Marks go on top of every halo.
        var radius = 5 * k;
        foreach (var (start, end) in ends)
        {
            if ((end - start).Length > radius * 2.5)
            {
                AddEndMark(end, EndMarkBrush, radius, k);
            }

            AddEndMark(start, StartMarkBrush, radius, k);
        }
    }

    private void AddEndMark(Point center, Brush fill, double radius, double k)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 1.5 * k,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, center.X - radius);
        Canvas.SetTop(dot, center.Y - radius);
        SelectionMarks.Children.Add(dot);
    }

    // ----- Pointer handling -----

    private void SelectionLayer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool != SketchTool.Select || _currentItem is null)
        {
            return;
        }

        var pos = e.GetPosition(SelectionLayer);
        var hit = StrokeSelection.HitTop(InkSurface.Strokes, pos, Math.Max(3, SelectHitRadius / CurrentScale));
        var additive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (additive)
        {
            // Shift+click toggles the clicked entity; it never starts a drag.
            if (hit is not null)
            {
                var entity = StrokeSelection.ExpandGroups(InkSurface.Strokes, new[] { hit });
                var next = new List<Stroke>(_selected);
                if (_selected.Contains(hit))
                {
                    next.RemoveAll(entity.Contains);
                }
                else
                {
                    next.AddRange(entity.Where(s => !next.Contains(s)));
                }

                SetSelection(next);
            }

            e.Handled = true;
            return;
        }

        _selectStart = pos;
        _selectLast = pos;
        if (hit is not null && !_selected.Contains(hit))
        {
            // Pressing on ink that isn't selected picks it (and its group), even inside another selection's box.
            SetSelection(StrokeSelection.ExpandGroups(InkSurface.Strokes, new[] { hit }));
            BeginMove();
        }
        else if (hit is not null || (HasSelection && SelectionBounds.Contains(pos)))
        {
            // On selected ink, or inside the selection's box: grab the selection.
            BeginMove();
        }
        else
        {
            ClearSelection();
            _selectDrag = SelectDrag.Marquee;
            MarqueeBox.Visibility = Visibility.Collapsed;
        }

        SelectionLayer.CaptureMouse();
        e.Handled = true;
    }

    private void BeginMove()
    {
        FlushSelectionEdit();
        _selectDrag = SelectDrag.Move;
        _moveTotal = new Vector();
    }

    private void SelectionLayer_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(SelectionLayer);
        switch (_selectDrag)
        {
            case SelectDrag.Move:
            {
                var delta = pos - _selectLast;
                _selectLast = pos;
                if (delta.LengthSquared > 0)
                {
                    TranslateStrokes(_selected, delta);
                    _moveTotal += delta;
                    UpdateSelectionVisuals();
                }

                break;
            }
            case SelectDrag.Marquee:
            {
                var rect = new Rect(_selectStart, pos);
                Canvas.SetLeft(MarqueeBox, rect.Left);
                Canvas.SetTop(MarqueeBox, rect.Top);
                MarqueeBox.Width = rect.Width;
                MarqueeBox.Height = rect.Height;
                MarqueeBox.StrokeThickness = 1 / CurrentScale;
                MarqueeBox.Visibility = Visibility.Visible;
                break;
            }
            default:
                SelectionLayer.Cursor = HasSelection && SelectionBounds.Contains(pos) ? Cursors.SizeAll : Cursors.Arrow;
                break;
        }
    }

    private void SelectionLayer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectDrag == SelectDrag.None)
        {
            return;
        }

        var pos = e.GetPosition(SelectionLayer);
        var drag = _selectDrag;
        _selectDrag = SelectDrag.None;
        if (SelectionLayer.IsMouseCaptured)
        {
            SelectionLayer.ReleaseMouseCapture();
        }

        if (drag == SelectDrag.Move)
        {
            CommitMove();
        }
        else
        {
            MarqueeBox.Visibility = Visibility.Collapsed;
            var rect = new Rect(_selectStart, pos);
            if (rect.Width >= 3 || rect.Height >= 3)
            {
                var touched = StrokeSelection.Intersecting(InkSurface.Strokes, rect);
                SetSelection(StrokeSelection.ExpandGroups(InkSurface.Strokes, touched));
            }
        }

        e.Handled = true;
    }

    /// <summary>Capture was taken away mid-drag (window deactivated, etc.): keep what was done so far.</summary>
    private void SelectionLayer_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_selectDrag == SelectDrag.None)
        {
            return;
        }

        var drag = _selectDrag;
        _selectDrag = SelectDrag.None;
        MarqueeBox.Visibility = Visibility.Collapsed;
        if (drag == SelectDrag.Move)
        {
            CommitMove();
        }
    }

    private static void TranslateStrokes(IEnumerable<Stroke> strokes, Vector delta)
    {
        var matrix = new Matrix(1, 0, 0, 1, delta.X, delta.Y);
        foreach (var stroke in strokes)
        {
            stroke.Transform(matrix, false);
        }
    }

    /// <summary>A finished drag is one undo step. Transforming strokes doesn't raise StrokesChanged, so record it here.</summary>
    private void CommitMove()
    {
        var total = _moveTotal;
        _moveTotal = new Vector();
        UpdateSelectionVisuals(rebuildMarks: true);
        if (total.LengthSquared < 0.01)
        {
            return;
        }

        var moved = _selected.ToList();
        MarkDirty();
        PushHistory(new HistoryEntry(
            Undo: () => TranslateStrokes(moved, -total),
            Redo: () => TranslateStrokes(moved, total)));
    }

    // ----- Delete -----

    private void SelectionDelete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        if (!HasSelection)
        {
            return;
        }

        var doomed = new StrokeCollection(_selected);
        ClearSelection();
        // Strokes_Changed records the undo step.
        InkSurface.Strokes.Remove(doomed);
    }

    // ----- Color and width -----

    /// <summary>A swatch or the wheel picked a color: it recolors the selection if there is one, else the pen.</summary>
    private void ApplyColorChoice(Color color)
    {
        if (_syncingSelectionUi)
        {
            return;
        }

        if (HasSelection)
        {
            BeginSelectionEdit();
            foreach (var stroke in _selected)
            {
                var attributes = stroke.DrawingAttributes.Clone();
                attributes.Color = color;
                stroke.DrawingAttributes = attributes;
            }

            MarkDirty();
            ScheduleSelectionEditFlush();
            UpdateSelectionVisuals();
            UpdatePenVisuals();
        }
        else
        {
            ApplyPenColor(color);
        }
    }

    private void ApplySelectionWidth(double size)
    {
        BeginSelectionEdit();
        foreach (var stroke in _selected)
        {
            var attributes = stroke.DrawingAttributes.Clone();
            attributes.Width = size;
            attributes.Height = size;
            stroke.DrawingAttributes = attributes;
        }

        MarkDirty();
        ScheduleSelectionEditFlush();
        UpdateSelectionVisuals(rebuildMarks: true);
    }

    private void BeginSelectionEdit()
    {
        _attributesBefore ??= _selected
            .Select(s => new AttributeSnapshot(s, s.DrawingAttributes.Color, s.DrawingAttributes.Width, s.DrawingAttributes.Height))
            .ToList();
    }

    private void ScheduleSelectionEditFlush()
    {
        if (_selectionEditTimer is null)
        {
            // A slider or wheel drag fires many changes; one undo step once it settles.
            _selectionEditTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _selectionEditTimer.Tick += (_, _) => FlushSelectionEdit();
        }

        _selectionEditTimer.Stop();
        _selectionEditTimer.Start();
    }

    /// <summary>Turns the pending color/width adjustment into a single undo step.</summary>
    private void FlushSelectionEdit()
    {
        _selectionEditTimer?.Stop();
        var before = _attributesBefore;
        _attributesBefore = null;
        if (before is null || _currentItem is null)
        {
            return;
        }

        var after = before
            .Select(b => new AttributeSnapshot(b.Stroke, b.Stroke.DrawingAttributes.Color,
                b.Stroke.DrawingAttributes.Width, b.Stroke.DrawingAttributes.Height))
            .ToList();
        var unchanged = before.Zip(after).All(p =>
            p.First.Color == p.Second.Color
            && Math.Abs(p.First.Width - p.Second.Width) < 0.001
            && Math.Abs(p.First.Height - p.Second.Height) < 0.001);
        if (unchanged)
        {
            return;
        }

        PushHistory(new HistoryEntry(
            Undo: () => RestoreAttributes(before),
            Redo: () => RestoreAttributes(after)));
    }

    private static void RestoreAttributes(IEnumerable<AttributeSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var attributes = snapshot.Stroke.DrawingAttributes.Clone();
            attributes.Color = snapshot.Color;
            attributes.Width = snapshot.Width;
            attributes.Height = snapshot.Height;
            snapshot.Stroke.DrawingAttributes = attributes;
        }
    }
}
