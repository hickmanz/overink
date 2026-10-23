import Foundation
import Testing
@testable import OverInkCore

@Suite struct CaptureLayoutTests {
    // Built-in display with an external one to its left (negative global origin) and one above.
    private let displays = [
        CaptureDisplay(id: 1, bounds: PixelRect(x: 0, y: 0, width: 1440, height: 900)),
        CaptureDisplay(id: 2, bounds: PixelRect(x: -1920, y: -180, width: 1920, height: 1080)),
        CaptureDisplay(id: 3, bounds: PixelRect(x: 0, y: -1080, width: 1920, height: 1080))
    ]

    @Test func rectOnOneDisplayYieldsOnePiece() {
        let pieces = CaptureLayout.plan(
            rect: PixelRect(x: 100, y: 50, width: 300, height: 200),
            displays: displays
        )
        #expect(pieces == [
            CapturePiece(
                displayID: 1,
                source: PixelRect(x: 100, y: 50, width: 300, height: 200),
                destination: PixelRect(x: 0, y: 0, width: 300, height: 200)
            )
        ])
    }

    @Test func rectSpanningTwoDisplaysSplitsIntoTwoPieces() {
        let rect = PixelRect(x: -100, y: 100, width: 300, height: 200)
        let pieces = CaptureLayout.plan(rect: rect, displays: displays)

        let left = pieces.first { $0.displayID == 2 }
        let main = pieces.first { $0.displayID == 1 }
        #expect(pieces.count == 2)
        // Left part: 100pt wide, taken from the right edge of the external display.
        #expect(left?.source == PixelRect(x: 1820, y: 280, width: 100, height: 200))
        #expect(left?.destination == PixelRect(x: 0, y: 0, width: 100, height: 200))
        #expect(main?.source == PixelRect(x: 0, y: 100, width: 200, height: 200))
        #expect(main?.destination == PixelRect(x: 100, y: 0, width: 200, height: 200))
    }

    @Test func rectOutsideEveryDisplayYieldsNothing() {
        let pieces = CaptureLayout.plan(
            rect: PixelRect(x: 5000, y: 5000, width: 10, height: 10),
            displays: displays
        )
        #expect(pieces.isEmpty)
        #expect(CaptureLayout.plan(rect: PixelRect(x: 0, y: 0, width: 0, height: 5), displays: displays).isEmpty)
    }

    @Test func fullDesktopCoversAllDisplays() {
        #expect(
            CaptureLayout.fullDesktop(displays)
                == PixelRect(x: -1920, y: -1080, width: 3840, height: 1980)
        )
    }

    @Test func displayLookupFallsBackToNearest() {
        #expect(CaptureLayout.display(at: PixelPoint(x: 10, y: 10), in: displays)?.id == 1)
        #expect(CaptureLayout.display(at: PixelPoint(x: -500, y: 0), in: displays)?.id == 2)
        // Below the built-in display: nearest is display 1.
        #expect(CaptureLayout.display(at: PixelPoint(x: 700, y: 2000), in: displays)?.id == 1)
    }

    @Test func strokesMoveIntoCaptureCoordinatesAndClip() {
        let onExternal = InkStroke(
            colorHex: "#000000",
            width: 2,
            points: [InkPoint(x: 1900, y: 300), InkPoint(x: 1910, y: 310)]
        )
        let farAway = InkStroke(
            colorHex: "#000000",
            width: 2,
            points: [InkPoint(x: 10, y: 10), InkPoint(x: 20, y: 20)]
        )
        let rect = PixelRect(x: -100, y: 100, width: 300, height: 200)

        let localized = CaptureLayout.localize(
            [onExternal, farAway],
            displayBounds: displays[1].bounds,
            captureRect: rect
        )

        // (1900, 300) on the external display is global (-20, 120): output (80, 20).
        #expect(localized.count == 1)
        #expect(localized[0].points[0].x == 80)
        #expect(localized[0].points[0].y == 20)
    }

    @Test func windowSnapPicksTheFrontmostWindowUnderThePoint() {
        let front = PixelRect(x: 100, y: 100, width: 200, height: 200)
        let back = PixelRect(x: 0, y: 0, width: 500, height: 500)

        #expect(WindowSnap.topmost(at: PixelPoint(x: 150, y: 150), in: [front, back]) == front)
        #expect(WindowSnap.topmost(at: PixelPoint(x: 400, y: 400), in: [front, back]) == back)
        #expect(WindowSnap.topmost(at: PixelPoint(x: 900, y: 900), in: [front, back]) == nil)
    }

    @Test func captureSettingsDefaultsAndRoundTrip() throws {
        let defaults = try JSONDecoder().decode(MacAppSettings.self, from: Data("{}".utf8))
        #expect(defaults.captureMode == .auto)
        #expect(defaults.captureHotkey == nil)
        #expect(!defaults.includeToolbarInCaptures)

        var settings = MacAppSettings()
        settings.captureMode = .fullScreen
        settings.captureHotkey = KeyChord(keyCode: 122) // F1, no modifier
        let data = try JSONEncoder().encode(settings)
        #expect(try JSONDecoder().decode(MacAppSettings.self, from: data) == settings)
    }
}
