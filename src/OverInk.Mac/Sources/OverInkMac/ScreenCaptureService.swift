import CoreGraphics
import Foundation
import ScreenCaptureKit

enum ScreenCaptureError: LocalizedError {
    case permissionDenied
    case displayUnavailable

    var errorDescription: String? {
        switch self {
        case .permissionDenied:
            "Screen Recording permission is required to capture or stream the desktop."
        case .displayUnavailable:
            "The selected display is no longer available."
        }
    }
}

actor ScreenCaptureService {
    private var didRequestAuthorization = false

    nonisolated var isAuthorized: Bool {
        CGPreflightScreenCaptureAccess()
    }

    nonisolated func requestAuthorization() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    func requestAuthorizationIfNeeded() -> Bool {
        if isAuthorized {
            return true
        }
        guard !didRequestAuthorization else {
            return false
        }
        didRequestAuthorization = true
        return CGRequestScreenCaptureAccess()
    }

    @available(macOS 14.0, *)
    func captureDisplay(
        id: CGDirectDisplayID,
        excludingWindowIDs: Set<CGWindowID> = []
    ) async throws -> CGImage {
        guard isAuthorized else {
            throw ScreenCaptureError.permissionDenied
        }

        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        guard let display = content.displays.first(where: { $0.displayID == id }) else {
            throw ScreenCaptureError.displayUnavailable
        }

        let excludedWindows = content.windows.filter {
            excludingWindowIDs.contains($0.windowID)
        }
        let filter = SCContentFilter(display: display, excludingWindows: excludedWindows)
        let configuration = SCStreamConfiguration()
        // Capture at the display's native pixel size; `SCDisplay.width/height` are in points and
        // would give a blurry half-resolution image on Retina displays.
        let mode = CGDisplayCopyDisplayMode(id)
        configuration.width = mode?.pixelWidth ?? display.width * 2
        configuration.height = mode?.pixelHeight ?? display.height * 2
        configuration.showsCursor = false
        configuration.capturesAudio = false

        return try await SCScreenshotManager.captureImage(
            contentFilter: filter,
            configuration: configuration
        )
    }
}
