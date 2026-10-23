import Foundation

/// Post-processes a freehand stroke so it looks smooth without drifting from what the user drew.
/// Port of the Windows `StrokeSmoother`:
///
/// 1. Drop near-duplicate samples (pointer jitter at rest).
/// 2. Fit a centripetal Catmull-Rom spline through the raw samples.
/// 3. Resample the spline at a fixed arc-length spacing.
/// 4. Low-pass filter the resampled path with repeated [1 2 1] passes. Endpoints stay pinned.
/// 5. Clamp how far any point may move from the spline so strong smoothing can't pull the line
///    off the intended path.
public enum StrokeSmoother {
    private static let minSampleDistance = 0.75
    private static let resampleStep = 1.5
    private static let minSigma = 5.0
    private static let maxSigma = 12.0
    private static let maxPasses = 200

    private struct Sample {
        var x: Double
        var y: Double
        var pressure: Double
    }

    /// Returns the smoothed points, or `nil` when the stroke is too short to smooth.
    public static func smooth(_ points: [InkPoint], penWidth: Double) -> [InkPoint]? {
        guard points.count >= 4 else { return nil }

        let raw = deduplicate(points)
        guard raw.count >= 4 else { return nil }

        let reference = resampleCatmullRom(raw)
        guard reference.count >= 4 else { return nil }

        let maxDeviation = min(max(penWidth * 0.6, 1.5), 3.0)
        let sigma = min(max(medianSpacing(raw) * 0.6, minSigma), maxSigma)
        let passes = Int(min(Double(maxPasses), (2 * sigma * sigma / (resampleStep * resampleStep)).rounded(.up)))
        let smoothed = lowPass(reference, maxDeviation: maxDeviation, passes: passes)

        return smoothed.map {
            InkPoint(x: $0.x, y: $0.y, pressure: Float(min(max($0.pressure, 0), 1)))
        }
    }

    /// Smooths `stroke` in place when possible.
    public static func apply(to stroke: inout InkStroke) {
        if let smoothed = smooth(stroke.points, penWidth: stroke.width) {
            stroke.points = smoothed
        }
    }

    // MARK: Pipeline

    private static func deduplicate(_ source: [InkPoint]) -> [Sample] {
        var list: [Sample] = []
        list.reserveCapacity(source.count)
        for point in source {
            let sample = Sample(x: point.x, y: point.y, pressure: Double(point.pressure))
            if let last = list.last, distance(last, sample) < minSampleDistance {
                // Keep the latest pressure but hold position.
                list[list.count - 1].pressure = sample.pressure
            } else {
                list.append(sample)
            }
        }

        // Make sure the stroke ends where the user lifted.
        if let lastSource = source.last, list.count > 1, let last = list.last,
           distance(last, Sample(x: lastSource.x, y: lastSource.y, pressure: 0)) > 0.01 {
            list[list.count - 1] = Sample(
                x: lastSource.x,
                y: lastSource.y,
                pressure: Double(lastSource.pressure)
            )
        }
        return list
    }

    private static func resampleCatmullRom(_ pts: [Sample]) -> [Sample] {
        // Phantom end points so the spline reaches the first and last sample.
        var padded: [Sample] = [reflect(pts[1], through: pts[0])]
        padded.append(contentsOf: pts)
        padded.append(reflect(pts[pts.count - 2], through: pts[pts.count - 1]))

        var dense: [Sample] = [pts[0]]
        dense.reserveCapacity(pts.count * 4)

        for i in 1 ..< padded.count - 2 {
            let p0 = padded[i - 1]
            let p1 = padded[i]
            let p2 = padded[i + 1]
            let p3 = padded[i + 2]

            let t0 = 0.0
            var t1 = t0 + distance(p0, p1).squareRoot()
            let t2 = t1 + distance(p1, p2).squareRoot()
            var t3 = t2 + distance(p2, p3).squareRoot()

            if t2 - t1 < 1e-9 { continue }
            if t1 - t0 < 1e-9 { t1 = t0 + 1e-6 }
            if t3 - t2 < 1e-9 { t3 = t2 + 1e-6 }

            let segmentLength = distance(p1, p2)
            let steps = max(1, Int((segmentLength / resampleStep).rounded(.up)))

            for s in 1 ... steps {
                let t = t1 + (t2 - t1) * Double(s) / Double(steps)

                let a1 = lerp(p0, p1, (t - t0) / (t1 - t0))
                let a2 = lerp(p1, p2, (t - t1) / (t2 - t1))
                let a3 = lerp(p2, p3, (t - t2) / (t3 - t2))
                let b1 = lerp(a1, a2, (t - t0) / (t2 - t0))
                let b2 = lerp(a2, a3, (t - t1) / (t3 - t1))
                var c = lerp(b1, b2, (t - t1) / (t2 - t1))

                c.pressure = p1.pressure + (p2.pressure - p1.pressure) * Double(s) / Double(steps)
                dense.append(c)
            }
        }
        return dense
    }

    private static func medianSpacing(_ pts: [Sample]) -> Double {
        var gaps = (0 ..< pts.count - 1).map { distance(pts[$0], pts[$0 + 1]) }
        gaps.sort()
        return gaps[gaps.count / 2]
    }

    private static func lowPass(_ reference: [Sample], maxDeviation: Double, passes: Int) -> [Sample] {
        let n = reference.count
        var x = reference.map(\.x)
        var y = reference.map(\.y)
        var pr = reference.map(\.pressure)
        var tx = x
        var ty = y
        var tp = pr

        for _ in 0 ..< passes {
            tx[0] = x[0]; ty[0] = y[0]; tp[0] = pr[0]
            tx[n - 1] = x[n - 1]; ty[n - 1] = y[n - 1]; tp[n - 1] = pr[n - 1]
            for i in 1 ..< n - 1 {
                tx[i] = 0.25 * x[i - 1] + 0.5 * x[i] + 0.25 * x[i + 1]
                ty[i] = 0.25 * y[i - 1] + 0.5 * y[i] + 0.25 * y[i + 1]
                tp[i] = 0.25 * pr[i - 1] + 0.5 * pr[i] + 0.25 * pr[i + 1]
            }
            swap(&x, &tx)
            swap(&y, &ty)
            swap(&pr, &tp)
        }

        return (0 ..< n).map { i in
            var dx = x[i] - reference[i].x
            var dy = y[i] - reference[i].y
            let d = hypot(dx, dy)
            if d > maxDeviation {
                let k = maxDeviation / d
                dx *= k
                dy *= k
            }
            return Sample(x: reference[i].x + dx, y: reference[i].y + dy, pressure: pr[i])
        }
    }

    // MARK: Helpers

    private static func reflect(_ other: Sample, through pivot: Sample) -> Sample {
        Sample(x: 2 * pivot.x - other.x, y: 2 * pivot.y - other.y, pressure: pivot.pressure)
    }

    private static func lerp(_ a: Sample, _ b: Sample, _ t: Double) -> Sample {
        Sample(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, pressure: a.pressure)
    }

    private static func distance(_ a: Sample, _ b: Sample) -> Double {
        hypot(b.x - a.x, b.y - a.y)
    }
}
