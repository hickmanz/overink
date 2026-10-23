import Testing
@testable import OverInkCore

@Suite struct WindowPlacementTests {
    private let main = PixelRect(x: 0, y: 0, width: 1440, height: 900)
    private let right = PixelRect(x: 1440, y: 0, width: 1920, height: 1080)

    @Test func visibleWindowIsUntouched() {
        let frame = PixelRect(x: 100, y: 100, width: 700, height: 52)
        #expect(WindowPlacement.recover(frame: frame, visibleFrames: [main]) == frame)
    }

    @Test func windowOnRemovedDisplayMovesToNearestRemainingOne() {
        // Was on a display to the right of `main` that is now gone.
        let frame = PixelRect(x: 2500, y: 200, width: 700, height: 52)
        let recovered = WindowPlacement.recover(frame: frame, visibleFrames: [main])
        #expect(recovered == PixelRect(x: 740, y: 200, width: 700, height: 52))
    }

    @Test func windowPartlyOffEdgeIsLeftAloneIfEnoughIsVisible() {
        let frame = PixelRect(x: 1400, y: 100, width: 700, height: 52)
        #expect(WindowPlacement.recover(frame: frame, visibleFrames: [main, right]) == frame)
    }

    @Test func windowAlmostEntirelyOffscreenIsPulledBack() {
        let frame = PixelRect(x: 1430, y: -500, width: 700, height: 52)
        let recovered = WindowPlacement.recover(frame: frame, visibleFrames: [main])
        #expect(main.intersection(recovered) == recovered)
    }

    @Test func clampPrefersTopLeftWhenWindowIsLargerThanBounds() {
        let big = PixelRect(x: 50, y: 50, width: 2000, height: 100)
        let clamped = WindowPlacement.clamp(big, inside: main)
        #expect(clamped.x == 0)
    }

    @Test func noDisplaysReturnsFrame() {
        let frame = PixelRect(x: 5, y: 5, width: 10, height: 10)
        #expect(WindowPlacement.recover(frame: frame, visibleFrames: []) == frame)
    }
}
