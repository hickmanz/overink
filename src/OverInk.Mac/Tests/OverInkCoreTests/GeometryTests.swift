import Testing
@testable import OverInkCore

@Suite struct GeometryTests {
    private func monitor(
        _ id: String,
        pixels: PixelRect,
        points: PixelRect? = nil,
        scale: Double = 1,
        primary: Bool = false
    ) -> MonitorGeometry {
        MonitorGeometry(
            id: id,
            pixels: pixels,
            points: points ?? pixels,
            scaleX: scale,
            scaleY: scale,
            isPrimary: primary
        )
    }

    @Test func virtualDesktopSupportsNegativeOrigins() {
        let monitors = [
            monitor("left", pixels: PixelRect(x: -1280, y: 0, width: 1280, height: 1024)),
            monitor(
                "primary",
                pixels: PixelRect(x: 0, y: 0, width: 2560, height: 1440),
                points: PixelRect(x: 0, y: 0, width: 1280, height: 720),
                scale: 2,
                primary: true
            )
        ]

        #expect(
            DesktopLayout.virtualDesktop(monitors)
                == PixelRect(x: -1280, y: 0, width: 3840, height: 1440)
        )
    }

    @Test func normalizedCoordinatesClampWhenMappingToPixels() {
        let area = PixelRect(x: 100, y: 200, width: 800, height: 600)

        #expect(
            DesktopLayout.normalizedToPixels(PixelPoint(x: -1, y: 2), in: area)
                == PixelPoint(x: 100, y: 800)
        )
        #expect(
            DesktopLayout.pixelsToNormalized(PixelPoint(x: 500, y: 500), in: area)
                == PixelPoint(x: 0.5, y: 0.5)
        )
    }

    @Test func nearestMonitorHandlesDesktopGaps() {
        let left = monitor("left", pixels: PixelRect(x: 0, y: 0, width: 100, height: 100), primary: true)
        let right = monitor("right", pixels: PixelRect(x: 200, y: 0, width: 100, height: 100))

        #expect(
            DesktopLayout.nearestMonitor(to: PixelPoint(x: 175, y: 50), in: [left, right])?.id
                == "right"
        )
    }
}
