import XCTest
@testable import OverInkCore

final class GeometryTests: XCTestCase {
    func testVirtualDesktopSupportsNegativeOrigins() {
        let monitors = [
            MonitorGeometry(
                id: "left",
                pixels: PixelRect(x: -1280, y: 0, width: 1280, height: 1024),
                points: PixelRect(x: -1280, y: 0, width: 1280, height: 1024),
                scaleX: 1,
                scaleY: 1,
                isPrimary: false
            ),
            MonitorGeometry(
                id: "primary",
                pixels: PixelRect(x: 0, y: 0, width: 2560, height: 1440),
                points: PixelRect(x: 0, y: 0, width: 1280, height: 720),
                scaleX: 2,
                scaleY: 2,
                isPrimary: true
            )
        ]

        XCTAssertEqual(
            DesktopLayout.virtualDesktop(monitors),
            PixelRect(x: -1280, y: 0, width: 3840, height: 1440)
        )
    }

    func testNormalizedCoordinatesClampWhenMappingToPixels() {
        let area = PixelRect(x: 100, y: 200, width: 800, height: 600)

        XCTAssertEqual(
            DesktopLayout.normalizedToPixels(PixelPoint(x: -1, y: 2), in: area),
            PixelPoint(x: 100, y: 800)
        )
        XCTAssertEqual(
            DesktopLayout.pixelsToNormalized(PixelPoint(x: 500, y: 500), in: area),
            PixelPoint(x: 0.5, y: 0.5)
        )
    }

    func testNearestMonitorHandlesDesktopGaps() {
        let left = MonitorGeometry(
            id: "left",
            pixels: PixelRect(x: 0, y: 0, width: 100, height: 100),
            points: PixelRect(x: 0, y: 0, width: 100, height: 100),
            scaleX: 1,
            scaleY: 1,
            isPrimary: true
        )
        let right = MonitorGeometry(
            id: "right",
            pixels: PixelRect(x: 200, y: 0, width: 100, height: 100),
            points: PixelRect(x: 200, y: 0, width: 100, height: 100),
            scaleX: 1,
            scaleY: 1,
            isPrimary: false
        )

        XCTAssertEqual(
            DesktopLayout.nearestMonitor(to: PixelPoint(x: 175, y: 50), in: [left, right])?.id,
            "right"
        )
    }
}
