import Foundation
import Testing
@testable import OverInkCore

@Suite struct RemoteStreamingTests {
    // Two side-by-side displays: a 1440x900 Retina primary and a 1920x1080 external on its right.
    private let left = StreamDisplay(
        id: "LEFT", bounds: PixelRect(x: 0, y: 0, width: 1440, height: 900), isPrimary: true, backingScale: 2
    )
    private let right = StreamDisplay(
        id: "RIGHT", bounds: PixelRect(x: 1440, y: -90, width: 1920, height: 1080), isPrimary: false, backingScale: 1
    )
    private var displays: [StreamDisplay] { [left, right] }

    private func rect(_ r: PixelRect) -> [Double] { [r.x, r.y, r.width, r.height] }

    // MARK: Target

    @Test func followsTheToolbarDisplayElsePrimary() {
        let settings = RemoteSettings()
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "RIGHT") == right.bounds)
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: nil) == left.bounds)
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "gone") == left.bounds)
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "right") == right.bounds)
    }

    @Test func specificDisplayFallsBackToFollowThenPrimary() {
        var settings = RemoteSettings()
        settings.streamTarget = .specificDisplay
        settings.streamDisplay = "RIGHT"
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "LEFT") == right.bounds)
        settings.streamDisplay = "unplugged"
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "RIGHT") == right.bounds)
        #expect(StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: nil) == left.bounds)
    }

    @Test func allDisplaysIsTheUnion() {
        var settings = RemoteSettings()
        settings.streamTarget = .allDisplays
        let union = StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: nil)
        #expect(rect(union) == [0, -90, 3360, 1080])
    }

    @Test func noDisplaysGivesAnEmptyRect() {
        #expect(StreamTargetResolver.streamRect(settings: RemoteSettings(), displays: [], followDisplay: nil).isEmpty)
    }

    @Test func monitorRectsOnlyWithTwoOrMoreVisible() throws {
        var settings = RemoteSettings()
        settings.streamTarget = .allDisplays
        let union = StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: nil)
        let rects = StreamTargetResolver.monitorRects(stream: union, displays: displays)

        #expect(rects.count == 2)
        #expect(rects[0].primary)
        #expect(!rects[1].primary)
        #expect(abs(rects[0].x) < 1e-9)
        #expect(abs(rects[0].w - 1440.0 / 3360) < 1e-9)
        #expect(abs(rects[1].x - 1440.0 / 3360) < 1e-9)
        #expect(abs(rects[1].h - 1) < 1e-9)
        #expect(abs(rects[0].y - 90.0 / 1080) < 1e-9)

        #expect(StreamTargetResolver.monitorRects(stream: left.bounds, displays: displays).isEmpty)
    }

    @Test func epochChangesWithRectAndIsDeterministic() {
        let a = StreamTargetResolver.epoch(of: left.bounds)
        #expect(a == StreamTargetResolver.epoch(of: left.bounds))
        #expect(a != StreamTargetResolver.epoch(of: right.bounds))
        #expect(a != StreamTargetResolver.epoch(of: PixelRect(x: 0, y: 0, width: 1440, height: 901)))
        #expect(a >= 0)
    }

    // MARK: Region

    @Test func regionAppliesFractionsToItsDisplay() {
        var settings = RemoteSettings()
        settings.regionEnabled = true
        settings.regionDisplay = "RIGHT"
        settings.regionX = 0.25
        settings.regionY = 0.5
        settings.regionW = 0.5
        settings.regionH = 0.25
        let region = StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "LEFT")
        #expect(rect(region) == [1440 + 480, -90 + 540, 960, 270])
    }

    @Test func regionEnforcesMinimumsAndStaysInsideTheDisplay() {
        var settings = RemoteSettings()
        settings.regionEnabled = true
        settings.regionDisplay = "LEFT"
        settings.regionX = 0.98
        settings.regionY = 0.98
        settings.regionW = 0.5
        settings.regionH = 0.001
        let region = StreamTargetResolver.regionToPoints(settings, displayBounds: left.bounds)
        #expect(region.x >= 0)
        #expect(region.width >= StreamTargetResolver.minRegionPoints)
        #expect(region.height >= StreamTargetResolver.minRegionPoints)
        #expect(region.x + min(region.width, 1440 - region.x) <= 1440 + 1e-9)
    }

    @Test func missingRegionDisplayUsesPrimary() {
        var settings = RemoteSettings()
        settings.regionEnabled = true
        settings.regionDisplay = "unplugged"
        settings.regionW = 0.5
        settings.regionH = 0.5
        let region = StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: "RIGHT")
        #expect(rect(region) == [0, 0, 720, 450])
    }

    @Test func pickedRegionIsStoredAgainstTheDisplayHoldingMostOfIt() {
        // Mostly on the right display, spilling 100pt onto the left one.
        let picked = PixelRect(x: 1340, y: 100, width: 600, height: 400)
        let settings = StreamTargetResolver.settingRegion(RemoteSettings(), to: picked, displays: displays)

        #expect(settings.regionEnabled)
        #expect(settings.regionDisplay == "RIGHT")
        #expect(abs(settings.regionX - 0) < 1e-9)
        #expect(abs(settings.regionY - (190.0 / 1080)) < 1e-9)
        #expect(abs(settings.regionW - 500.0 / 1920) < 1e-9)
    }

    @Test func clearingTheRegionResetsEverything() {
        var settings = StreamTargetResolver.settingRegion(
            RemoteSettings(), to: PixelRect(x: 10, y: 10, width: 100, height: 100), displays: displays
        )
        settings = StreamTargetResolver.clearingRegion(settings)
        #expect(!settings.regionEnabled)
        #expect(settings.regionDisplay == nil)
        #expect(settings.regionW == 1 && settings.regionH == 1)
    }

    @Test func clampInsideKeepsMinimumSizeAndBounds() {
        let area = PixelRect(x: 0, y: 0, width: 1000, height: 800)
        let clamped = StreamTargetResolver.clampInside(PixelRect(x: 990, y: 790, width: 5, height: 5), area: area)
        #expect(clamped.width == 40 && clamped.height == 40)
        #expect(clamped.maxX <= 1000 && clamped.maxY <= 800)
    }

    @Test func followDisplayIsTheOneUnderTheToolbarCentre() {
        let toolbar = PixelRect(x: 1500, y: 20, width: 300, height: 50)
        #expect(StreamTargetResolver.displayNearest(to: toolbar, in: displays)?.id == "RIGHT")
        let offscreen = PixelRect(x: -500, y: 100, width: 100, height: 40)
        #expect(StreamTargetResolver.displayNearest(to: offscreen, in: displays)?.id == "LEFT")
        #expect(StreamTargetResolver.displayNearest(to: PixelRect(x: 0, y: 0, width: 0, height: 0), in: displays) == nil)
    }

    // MARK: Plan

    @Test func singleDisplayPlanIsOnePieceAtNativeDensity() throws {
        let plan = try #require(StreamPlan.make(rect: left.bounds, displays: displays, maxEdge: 7680))
        #expect(plan.pieces.count == 1)
        #expect(plan.outputWidth == 2880 && plan.outputHeight == 1800)
        #expect(plan.pixelsPerPoint == 2)
        #expect(plan.pieces[0].source == PixelRect(x: 0, y: 0, width: 1440, height: 900))
        #expect(plan.pieces[0].destination == PixelRect(x: 0, y: 0, width: 2880, height: 1800))
    }

    @Test func maxEdgeScalesTheOutputDown() throws {
        let plan = try #require(StreamPlan.make(rect: left.bounds, displays: displays, maxEdge: 1440))
        #expect(plan.outputWidth == 1440 && plan.outputHeight == 900)
        #expect(plan.pixelsPerPoint == 1)
    }

    @Test func plansNeverUpscale() throws {
        let plan = try #require(StreamPlan.make(rect: right.bounds, displays: displays, maxEdge: 7680))
        #expect(plan.pixelsPerPoint == 1)
        #expect(plan.outputWidth == 1920)
    }

    @Test func multiDisplayPlanCutsPiecesAndLeavesGaps() throws {
        var settings = RemoteSettings()
        settings.streamTarget = .allDisplays
        let union = StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: nil)
        let plan = try #require(StreamPlan.make(rect: union, displays: displays, maxEdge: 3360))

        #expect(plan.pixelsPerPoint == 1)
        #expect(plan.outputWidth == 3360 && plan.outputHeight == 1080)
        #expect(plan.pieces.count == 2)
        let leftPiece = try #require(plan.pieces.first { $0.displayID == "LEFT" })
        #expect(leftPiece.destination == PixelRect(x: 0, y: 90, width: 1440, height: 900))
        let rightPiece = try #require(plan.pieces.first { $0.displayID == "RIGHT" })
        #expect(rightPiece.destination == PixelRect(x: 1440, y: 0, width: 1920, height: 1080))
    }

    @Test func regionPlanCropsTheSource() throws {
        let region = PixelRect(x: 100, y: 50, width: 400, height: 300)
        let plan = try #require(StreamPlan.make(rect: region, displays: displays, maxEdge: 7680))
        #expect(plan.pieces.count == 1)
        #expect(plan.pieces[0].source == region)
        #expect(plan.outputWidth == 800 && plan.outputHeight == 600)
    }

    @Test func emptyOrOffscreenRectsHaveNoPlan() {
        #expect(StreamPlan.make(rect: PixelRect(x: 0, y: 0, width: 0, height: 10), displays: displays, maxEdge: 100) == nil)
        #expect(StreamPlan.make(rect: PixelRect(x: 9000, y: 9000, width: 10, height: 10), displays: displays, maxEdge: 100) == nil)
    }

    // MARK: Presets and estimates

    @Test func presetsMatchTheWindowsValues() {
        #expect(RemoteStreamPreset.balanced(nativeLongEdge: 3000) == RemoteStreamPreset(maxEdge: 1920, fps: 10, quality: 75))
        #expect(RemoteStreamPreset.balanced(nativeLongEdge: 1280).maxEdge == 1280)
        #expect(RemoteStreamPreset.sharp(nativeLongEdge: 5000) == RemoteStreamPreset(maxEdge: 2560, fps: 12, quality: 82))
        #expect(RemoteStreamPreset.native(nativeLongEdge: 10000) == RemoteStreamPreset(maxEdge: 7680, fps: 10, quality: 95))
        #expect(RemoteStreamPreset.eco == RemoteStreamPreset(maxEdge: 1280, fps: 6, quality: 65))
    }

    @Test func applyingAPresetSanitizesSettings() {
        let applied = RemoteStreamPreset(maxEdge: 100, fps: 99, quality: 5).applying(to: RemoteSettings())
        #expect(applied.maxEdge == 320)
        #expect(applied.maxFps == 30)
        #expect(applied.jpegQuality == 30)
    }

    @Test func autoEdgeIsNativeButCappedForTheWholeDesktop() {
        var settings = RemoteSettings()
        settings.maxEdge = 0
        #expect(RemoteStreamEstimator.effectiveMaxEdge(settings: settings, stream: left.bounds, displays: displays) == 2880)

        settings.streamTarget = .allDisplays
        let union = StreamTargetResolver.streamRect(settings: settings, displays: displays, followDisplay: nil)
        #expect(RemoteStreamEstimator.effectiveMaxEdge(settings: settings, stream: union, displays: displays) == 3840)

        settings.maxEdge = 100_000
        #expect(RemoteStreamEstimator.effectiveMaxEdge(settings: settings, stream: union, displays: displays) == 7680)
        settings.maxEdge = 10
        #expect(RemoteStreamEstimator.effectiveMaxEdge(settings: settings, stream: union, displays: displays) == 320)
    }

    @Test func estimateReportsSizeRatioAndGuidance() {
        var settings = RemoteSettings()
        settings.maxEdge = 1440
        let estimate = RemoteStreamEstimator.estimate(settings: settings, stream: left.bounds, displays: displays)
        #expect(estimate.nativeLongEdge == 2880)
        #expect(estimate.streamWidth == 1440 && estimate.streamHeight == 900)
        #expect(abs(estimate.downscaleRatio - 0.5) < 1e-9)
        #expect(estimate.guidance.contains("50%"))
        #expect(estimate.targetLabel.contains("follows toolbar"))
        #expect(estimate.estimatedKbps > 0)

        settings.maxEdge = 0
        let native = RemoteStreamEstimator.estimate(settings: settings, stream: left.bounds, displays: displays)
        #expect(native.guidance.contains("full resolution"))
    }

    @Test func estimatedBandwidthGrowsWithFpsAndQuality() {
        var settings = RemoteSettings()
        let low = RemoteStreamEstimator.estimate(settings: settings, stream: left.bounds, displays: displays).estimatedKbps
        settings.maxFps = 20
        let fast = RemoteStreamEstimator.estimate(settings: settings, stream: left.bounds, displays: displays).estimatedKbps
        #expect(fast > low)
    }

    // MARK: Settings

    @Test func remoteSettingsDecodeWithDefaultsAndClampBadValues() throws {
        let empty = try JSONDecoder().decode(RemoteSettings.self, from: Data("{}".utf8))
        #expect(empty == RemoteSettings())

        let bad = ##"{"port":80,"maxFps":500,"jpegQuality":1,"maxEdge":5,"maxClients":0,"streamTarget":"bogus"}"##
        let decoded = try JSONDecoder().decode(RemoteSettings.self, from: Data(bad.utf8))
        #expect(decoded.port == 8787)
        #expect(decoded.maxFps == 30)
        #expect(decoded.jpegQuality == 30)
        #expect(decoded.maxEdge == 320)
        #expect(decoded.maxClients == 1)
        #expect(decoded.streamTarget == .followToolbar)
    }

    @Test func appSettingsRoundTripTheRemoteSection() throws {
        var settings = MacAppSettings()
        settings.remote.enabled = true
        settings.remote.streamTarget = .allDisplays
        let data = try JSONEncoder().encode(settings)
        #expect(try JSONDecoder().decode(MacAppSettings.self, from: data).remote == settings.remote)

        // Settings written before the remote section existed still load.
        let old = try JSONDecoder().decode(MacAppSettings.self, from: Data(##"{"penWidth":6}"##.utf8))
        #expect(old.remote == RemoteSettings())
    }

    // MARK: Revision timeline

    @Test func frameRevisionLagsBehindTheClock() {
        var timeline = RevisionTimeline()
        timeline.record(revision: 0, at: 0)
        timeline.record(revision: 1, at: 10)
        timeline.record(revision: 2, at: 10.03)

        // A frame captured 20 ms after revision 2 may not show it yet.
        #expect(timeline.revision(forFrameAt: 10.05, latency: 0.05) == 1)
        #expect(timeline.revision(forFrameAt: 10.2, latency: 0.05) == 2)
        #expect(timeline.revision(forFrameAt: 9.9, latency: 0.05) == 0)
        #expect(timeline.revision(forFrameAt: -1, latency: 0.05) == 0)
    }

    @Test func timelineIgnoresRepeatsAndStaysBounded() {
        var timeline = RevisionTimeline(capacity: 4)
        for revision in 0 ..< 20 {
            timeline.record(revision: Int64(revision), at: Double(revision))
            timeline.record(revision: Int64(revision), at: Double(revision) + 0.5)
        }
        #expect(timeline.revision(forFrameAt: 100, latency: 0) == 19)
        // Older history was dropped, so an old query falls back to 0.
        #expect(timeline.revision(forFrameAt: 3, latency: 0) == 0)
    }
}
