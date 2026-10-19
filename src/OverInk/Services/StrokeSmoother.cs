using System.Windows.Ink;
using System.Windows.Input;

namespace OverInk.Services;

/// <summary>
/// Post-processes a freehand stroke so it looks smooth without drifting away from what the user drew.
///
/// Pipeline:
///   1. Drop near-duplicate samples (mouse/pen jitter at rest).
///   2. Fit a centripetal Catmull-Rom spline through the raw samples. It passes through every point,
///      never loops or overshoots, and fills in the gaps when the pointer moved fast.
///   3. Resample the spline at a fixed arc-length spacing.
///   4. Low-pass filter the resampled path with repeated [1 2 1] passes (a Gaussian blur along the path).
///      This removes pixel stair-stepping and hand tremor. Endpoints stay pinned.
///   5. Clamp how far any point may move from the spline, so strong smoothing can't pull the line off
///      the intended path (the problem with WPF's FitToCurve).
/// </summary>
public static class StrokeSmoother
{
    private const double MinSampleDistance = 0.75;
    private const double ResampleStep = 1.5;
    private const double MinSigma = 5.0;
    private const double MaxSigma = 12.0;
    private const int MaxPasses = 200;

    public static void Apply(Stroke stroke)
    {
        var smoothed = Smooth(stroke.StylusPoints, stroke.DrawingAttributes.Width);
        if (smoothed is not null)
        {
            stroke.StylusPoints = smoothed;
        }
    }

    public static StylusPointCollection? Smooth(StylusPointCollection source, double penWidth)
    {
        if (source.Count < 4)
        {
            return null;
        }

        var raw = Deduplicate(source);
        if (raw.Count < 4)
        {
            return null;
        }

        var reference = ResampleCatmullRom(raw);
        if (reference.Count < 4)
        {
            return null;
        }

        var maxDeviation = Math.Clamp(penWidth * 0.6, 1.5, 3.0);

        // Sparse input (e.g. a Pencil sampled at ~60 Hz on a fast stroke) has a much longer noise
        // wavelength than dense mouse input, so the blur has to reach further to remove it.
        var sigma = Math.Clamp(MedianSpacing(raw) * 0.6, MinSigma, MaxSigma);
        var passes = (int)Math.Min(MaxPasses, Math.Ceiling(2 * sigma * sigma / (ResampleStep * ResampleStep)));
        var smoothed = LowPass(reference, maxDeviation, passes);

        var result = new StylusPointCollection();
        foreach (var p in smoothed)
        {
            result.Add(new StylusPoint(p.X, p.Y, (float)Math.Clamp(p.Pressure, 0.0, 1.0)));
        }

        return result;
    }

    private readonly record struct Sample(double X, double Y, double Pressure);

    private static List<Sample> Deduplicate(StylusPointCollection source)
    {
        var list = new List<Sample>(source.Count);
        foreach (var sp in source)
        {
            var s = new Sample(sp.X, sp.Y, sp.PressureFactor);
            if (list.Count == 0 || Distance(list[^1], s) >= MinSampleDistance)
            {
                list.Add(s);
            }
            else
            {
                // Keep the very last real position so the stroke ends where the user lifted.
                list[^1] = list[^1] with { Pressure = s.Pressure };
            }
        }

        var last = source[^1];
        if (list.Count > 1 && Distance(list[^1], new Sample(last.X, last.Y, 0)) > 0.01)
        {
            list[^1] = new Sample(last.X, last.Y, last.PressureFactor);
        }

        return list;
    }

    private static List<Sample> ResampleCatmullRom(List<Sample> pts)
    {
        // Phantom end points so the spline reaches the first and last sample.
        var padded = new List<Sample>(pts.Count + 2)
        {
            Reflect(pts[1], pts[0])
        };
        padded.AddRange(pts);
        padded.Add(Reflect(pts[^2], pts[^1]));

        var dense = new List<Sample>(pts.Count * 4) { pts[0] };

        for (var i = 1; i < padded.Count - 2; i++)
        {
            var p0 = padded[i - 1];
            var p1 = padded[i];
            var p2 = padded[i + 1];
            var p3 = padded[i + 2];

            var t0 = 0.0;
            var t1 = t0 + Math.Sqrt(Distance(p0, p1));
            var t2 = t1 + Math.Sqrt(Distance(p1, p2));
            var t3 = t2 + Math.Sqrt(Distance(p2, p3));

            if (t2 - t1 < 1e-9)
            {
                continue;
            }

            // Guard degenerate phantom segments.
            if (t1 - t0 < 1e-9) { t1 = t0 + 1e-6; }
            if (t3 - t2 < 1e-9) { t3 = t2 + 1e-6; }

            var segmentLength = Distance(p1, p2);
            var steps = Math.Max(1, (int)Math.Ceiling(segmentLength / ResampleStep));

            for (var s = 1; s <= steps; s++)
            {
                var t = t1 + (t2 - t1) * s / steps;

                var a1 = Lerp(p0, p1, (t - t0) / (t1 - t0));
                var a2 = Lerp(p1, p2, (t - t1) / (t2 - t1));
                var a3 = Lerp(p2, p3, (t - t2) / (t3 - t2));
                var b1 = Lerp(a1, a2, (t - t0) / (t2 - t0));
                var b2 = Lerp(a2, a3, (t - t1) / (t3 - t1));
                var c = Lerp(b1, b2, (t - t1) / (t2 - t1));

                // Pressure is interpolated linearly between the real samples.
                var pressure = p1.Pressure + (p2.Pressure - p1.Pressure) * s / steps;
                dense.Add(c with { Pressure = pressure });
            }
        }

        return dense;
    }

    private static double MedianSpacing(List<Sample> pts)
    {
        var d = new double[pts.Count - 1];
        for (var i = 0; i < d.Length; i++)
        {
            d[i] = Distance(pts[i], pts[i + 1]);
        }

        Array.Sort(d);
        return d[d.Length / 2];
    }

    private static Sample[] LowPass(List<Sample> reference, double maxDeviation, int passes)
    {
        var n = reference.Count;
        var x = new double[n];
        var y = new double[n];
        var pr = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = reference[i].X;
            y[i] = reference[i].Y;
            pr[i] = reference[i].Pressure;
        }

        var tx = new double[n];
        var ty = new double[n];
        var tp = new double[n];

        for (var pass = 0; pass < passes; pass++)
        {
            tx[0] = x[0]; ty[0] = y[0]; tp[0] = pr[0];
            tx[n - 1] = x[n - 1]; ty[n - 1] = y[n - 1]; tp[n - 1] = pr[n - 1];

            for (var i = 1; i < n - 1; i++)
            {
                tx[i] = 0.25 * x[i - 1] + 0.5 * x[i] + 0.25 * x[i + 1];
                ty[i] = 0.25 * y[i - 1] + 0.5 * y[i] + 0.25 * y[i + 1];
                tp[i] = 0.25 * pr[i - 1] + 0.5 * pr[i] + 0.25 * pr[i + 1];
            }

            (x, tx) = (tx, x);
            (y, ty) = (ty, y);
            (pr, tp) = (tp, pr);
        }

        var result = new Sample[n];
        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - reference[i].X;
            var dy = y[i] - reference[i].Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d > maxDeviation)
            {
                var k = maxDeviation / d;
                dx *= k;
                dy *= k;
            }

            result[i] = new Sample(reference[i].X + dx, reference[i].Y + dy, pr[i]);
        }

        return result;
    }

    private static Sample Reflect(Sample through, Sample pivot) =>
        new(2 * pivot.X - through.X, 2 * pivot.Y - through.Y, pivot.Pressure);

    private static Sample Lerp(Sample a, Sample b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Pressure);

    private static double Distance(Sample a, Sample b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
