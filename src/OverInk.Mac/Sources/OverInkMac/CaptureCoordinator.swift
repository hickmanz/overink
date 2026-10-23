import AppKit
import Foundation
import OverInkCore

enum CaptureTarget {
    /// The display under the mouse pointer.
    case displayUnderCursor
    /// Every display, stitched into a single image.
    case allDisplays
    /// A rectangle in global point coordinates (top-left origin); may span displays.
    case region(CGRect)
}

@MainActor
final class CaptureCoordinator {
    private let screenCapture: ScreenCaptureService
    private let annotationSession: AnnotationSession
    private let excludedWindowIDs: () -> Set<CGWindowID>
    private let libraryFolder: () -> URL

    init(
        screenCapture: ScreenCaptureService,
        annotationSession: AnnotationSession,
        libraryFolder: @escaping () -> URL,
        excludedWindowIDs: @escaping () -> Set<CGWindowID>
    ) {
        self.libraryFolder = libraryFolder
        self.screenCapture = screenCapture
        self.annotationSession = annotationSession
        self.excludedWindowIDs = excludedWindowIDs
    }

    func capture(_ target: CaptureTarget, copyToClipboard: Bool = true) async throws -> URL {
        guard await screenCapture.requestAuthorizationIfNeeded() else {
            throw ScreenCaptureError.permissionDenied
        }

        let displays = Self.currentDisplays()
        guard !displays.isEmpty else { throw ScreenCaptureError.displayUnavailable }

        let rect: PixelRect
        switch target {
        case .displayUnderCursor:
            let mouse = RegionPickerController.globalPoint(fromCocoa: NSEvent.mouseLocation)
            guard let display = CaptureLayout.display(
                at: PixelPoint(x: mouse.x, y: mouse.y),
                in: displays
            ) else {
                throw ScreenCaptureError.displayUnavailable
            }
            rect = display.bounds
        case .allDisplays:
            guard let desktop = CaptureLayout.fullDesktop(displays) else {
                throw ScreenCaptureError.displayUnavailable
            }
            rect = desktop
        case let .region(region):
            rect = PixelRect(region)
        }

        let pieces = CaptureLayout.plan(rect: rect, displays: displays)
        guard !pieces.isEmpty else { throw ScreenCaptureError.displayUnavailable }

        // One screenshot per display that the capture touches.
        let excluded = excludedWindowIDs()
        var images: [UInt32: CGImage] = [:]
        for id in Set(pieces.map(\.displayID)) {
            images[id] = try await screenCapture.captureDisplay(id: id, excludingWindowIDs: excluded)
        }

        let cleanImage = try CaptureRenderer.stitched(
            pieces: pieces,
            images: images,
            displays: displays,
            rect: rect
        )

        var strokes: [InkStroke] = []
        for piece in pieces {
            guard let display = displays.first(where: { $0.id == piece.displayID }) else { continue }
            strokes += CaptureLayout.localize(
                annotationSession.strokes(for: piece.displayID),
                displayBounds: display.bounds,
                captureRect: rect
            )
        }
        let document = CaptureDocument(
            canvasWidth: rect.width,
            canvasHeight: rect.height,
            veilOpacity: annotationSession.veilOpacity,
            strokes: strokes
        )

        let flattened = try CaptureRenderer.flattened(background: cleanImage, document: document)
        let destination = try captureDestination()
        try CaptureRenderer.pngData(for: cleanImage).write(
            to: CapturePackagePaths.original(for: destination),
            options: .atomic
        )
        try writeDocument(document, beside: destination)
        try CaptureRenderer.pngData(for: flattened).write(to: destination, options: .atomic)

        if copyToClipboard {
            let pasteboardImage = NSImage(cgImage: flattened, size: NSSize(width: rect.width, height: rect.height))
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([pasteboardImage, destination as NSURL])
        }
        return destination
    }

    static func currentDisplays() -> [CaptureDisplay] {
        NSScreen.screens.compactMap { screen in
            guard let id = screen.overInkDisplayID else { return nil }
            return CaptureDisplay(id: id, bounds: PixelRect(CGDisplayBounds(id)))
        }
    }

    private func writeDocument(_ document: CaptureDocument, beside imageURL: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(document)
        try data.write(to: CapturePackagePaths.document(for: imageURL), options: .atomic)
    }

    private func captureDestination() throws -> URL {
        let library = CaptureLibrary(folder: libraryFolder())
        try library.ensureFolder()

        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return library.uniqueURL(base: "OverInk-\(formatter.string(from: Date()))")
    }
}

enum CaptureSaveError: LocalizedError {
    case picturesFolderUnavailable
    case encodingFailed

    var errorDescription: String? {
        switch self {
        case .picturesFolderUnavailable:
            "The Pictures folder is unavailable."
        case .encodingFailed:
            "The captured image could not be encoded as PNG."
        }
    }
}
