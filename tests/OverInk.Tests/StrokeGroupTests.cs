using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OverInk.Editor;
using OverInk.Overlay;
using OverInk.Remote;
using Xunit;

namespace OverInk.Tests;

public sealed class StrokeGroupTests
{
    private static void RunSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA work failed", error);
        }
    }

    private static Stroke Line(double x1, double y1, double x2, double y2, double thickness = 4)
    {
        var points = new StylusPointCollection { new StylusPoint(x1, y1), new StylusPoint(x2, y2) };
        return new Stroke(points, new DrawingAttributes
        {
            Width = thickness,
            Height = thickness,
            FitToCurve = false,
            IgnorePressure = true
        });
    }

    private static Canvas ArrowElement()
    {
        var canvas = new Canvas();
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 10, Y1 = 10, X2 = 110, Y2 = 10,
            Stroke = Brushes.Red,
            StrokeThickness = 3
        });
        canvas.Children.Add(new Polygon
        {
            Points = new PointCollection { new Point(110, 10), new Point(98, 4), new Point(98, 16) },
            Stroke = Brushes.Red,
            StrokeThickness = 3
        });
        return canvas;
    }

    [Fact]
    public void Arrow_conversion_gives_shaft_and_head_one_group()
    {
        RunSta(() =>
        {
            var strokes = ShapeStrokeConverter.ToStrokes(ArrowElement()).ToList();

            Assert.Equal(2, strokes.Count);
            var group = StrokeGroups.Get(strokes[0]);
            Assert.False(string.IsNullOrEmpty(group));
            Assert.Equal(group, StrokeGroups.Get(strokes[1]));
        });
    }

    [Fact]
    public void Separate_shapes_get_different_groups()
    {
        RunSta(() =>
        {
            var a = ShapeStrokeConverter.ToStrokes(ArrowElement()).First();
            var b = ShapeStrokeConverter.ToStrokes(ArrowElement()).First();

            Assert.NotEqual(StrokeGroups.Get(a), StrokeGroups.Get(b));
        });
    }

    [Fact]
    public void Group_survives_isf_save_and_load()
    {
        RunSta(() =>
        {
            var strokes = new StrokeCollection(ShapeStrokeConverter.ToStrokes(ArrowElement()));
            var plain = Line(0, 50, 80, 50);
            strokes.Add(plain);
            var expected = StrokeGroups.Get(strokes[0]);

            using var stream = new MemoryStream();
            strokes.Save(stream);
            stream.Position = 0;
            var loaded = new StrokeCollection(stream);

            Assert.Equal(3, loaded.Count);
            Assert.Equal(expected, StrokeGroups.Get(loaded[0]));
            Assert.Equal(expected, StrokeGroups.Get(loaded[1]));
            Assert.Null(StrokeGroups.Get(loaded[2]));
        });
    }

    [Fact]
    public void Group_round_trips_through_dtos()
    {
        RunSta(() =>
        {
            var strokes = new StrokeCollection(ShapeStrokeConverter.ToStrokes(ArrowElement()));
            strokes.Add(Line(0, 50, 80, 50));

            var dtos = RemoteLibraryService.ToDtos(strokes);
            Assert.Equal(StrokeGroups.Get(strokes[0]), dtos[0].Group);
            Assert.Equal(dtos[0].Group, dtos[1].Group);
            Assert.Null(dtos[2].Group);

            var json = RemoteJson.Serialize(new RemoteLibrarySave("rev", dtos));
            var save = RemoteJson.Deserialize<RemoteLibrarySave>(json)!;
            var rebuilt = RemoteLibraryService.BuildStrokes(save.Strokes);

            Assert.Equal(StrokeGroups.Get(strokes[0]), StrokeGroups.Get(rebuilt[0]));
            Assert.Equal(StrokeGroups.Get(strokes[1]), StrokeGroups.Get(rebuilt[1]));
            Assert.Null(StrokeGroups.Get(rebuilt[2]));
        });
    }

    [Fact]
    public void Dto_without_group_still_deserializes()
    {
        const string json = """{"baseRev":"r","strokes":[{"color":"#FF0000","size":4,"pts":[1,2,0.5,3,4,0.5],"ignorePressure":false}]}""";

        var save = RemoteJson.Deserialize<RemoteLibrarySave>(json)!;

        Assert.Single(save.Strokes);
        Assert.Null(save.Strokes[0].Group);
        RunSta(() => Assert.Null(StrokeGroups.Get(RemoteLibraryService.BuildStrokes(save.Strokes)[0])));
    }

    [Fact]
    public void Dto_with_group_keeps_it()
    {
        const string json = """{"baseRev":"r","strokes":[{"color":"#FF0000","size":4,"pts":[1,2,0.5,3,4,0.5],"ignorePressure":false,"group":"abc"}]}""";

        var save = RemoteJson.Deserialize<RemoteLibrarySave>(json)!;

        Assert.Equal("abc", save.Strokes[0].Group);
        RunSta(() => Assert.Equal("abc", StrokeGroups.Get(RemoteLibraryService.BuildStrokes(save.Strokes)[0])));
    }

    // ------------------------------------------------------------ StrokeSelection

    [Fact]
    public void ExpandGroups_adds_every_stroke_in_the_group_only()
    {
        RunSta(() =>
        {
            var a = Line(0, 0, 50, 0);
            var b = Line(0, 20, 50, 20);
            var c = Line(0, 40, 50, 40);
            StrokeGroups.Set(a, "g");
            StrokeGroups.Set(b, "g");
            var all = new StrokeCollection { a, b, c };

            var fromA = StrokeSelection.ExpandGroups(all, new[] { a });
            Assert.Equal(new[] { a, b }, fromA);

            var fromC = StrokeSelection.ExpandGroups(all, new[] { c });
            Assert.Equal(new[] { c }, fromC);
        });
    }

    [Fact]
    public void HitTop_finds_the_stroke_near_the_point_and_prefers_the_topmost()
    {
        RunSta(() =>
        {
            var below = Line(0, 100, 200, 100);
            var above = Line(100, 0, 100, 200);
            var all = new StrokeCollection { below, above };

            Assert.Same(above, StrokeSelection.HitTop(all, new Point(100, 100), 4));
            Assert.Same(below, StrokeSelection.HitTop(all, new Point(20, 101), 4));
            Assert.Null(StrokeSelection.HitTop(all, new Point(20, 20), 4));
        });
    }

    [Fact]
    public void Intersecting_selects_strokes_the_marquee_touches()
    {
        RunSta(() =>
        {
            var inside = Line(10, 10, 30, 10);
            var crossing = Line(40, 0, 40, 100);
            var outside = Line(300, 300, 340, 300);
            var all = new StrokeCollection { inside, crossing, outside };

            var hit = StrokeSelection.Intersecting(all, new Rect(0, 0, 50, 50));

            Assert.Contains(inside, hit);
            Assert.Contains(crossing, hit);
            Assert.DoesNotContain(outside, hit);
        });
    }
}
