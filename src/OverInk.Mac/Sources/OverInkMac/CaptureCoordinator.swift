import AppKit
import Foundation
import OverInkCore

@MainActor
final class CaptureCoordinator {
    private let screenCapture: ScreenCaptureService
    private let annotationSession: AnnotationSession
    private let excludedWindowIDs: () -> Set<CGWindowID>

    init(
        screenCapture: ScreenCaptureService,
        annotationSession: AnnotationSession,
        excludedWindowIDs: @escaping () -> Set<CGWindowID>
    ) {
        self.screenCapture = screenCapture
        self.annotationSession = annotationSession
        self.excludedWindowIDs = excludedWindowIDs
    }

    func captureMainDisplay(copyToClipboard: Bool = true) async throws -> URL {
        guard let screen = NSScreen.main else {
            throw ScreenCaptureError.displayUnavailable
        }
        return try await captureDisplay(screen, copyToClipboard: copyToClipboard)
    }

    func captureDisplay(
        _ screen: NSScreen,
        copyToClipboard: Bool = true
    ) async throws -> URL {
        guard await screenCapture.requestAuthorizationIfNeeded() else {
            throw ScreenCaptureError.permissionDenied
        }
        guard let displayID = screen.overInkDisplayID else {
            throw ScreenCaptureError.displayUnavailable
        }
        return try await capture(
            screen: screen,
            displayID: displayID,
            region: nil,
            copyToClipboard: copyToClipboard
        )
    }

    func captureRegion(
        screen: NSScreen,
        rect: NSRect,
        copyToClipboard: Bool = true
    ) async throws -> URL {
        guard await screenCapture.requestAuthorizationIfNeeded() else {
            throw ScreenCaptureError.permissionDenied
        }
        guard let displayID = screen.overInkDisplayID else {
            throw ScreenCaptureError.displayUnavailable
        }
        return try await capture(
            screen: screen,
            displayID: displayID,
            region: rect,
            copyToClipboard: copyToClipboard
        )
    }

    private func capture(
        screen: NSScreen,
        displayID: CGDirectDisplayID,
        region: NSRect?,
        copyToClipboard: Bool
    ) async throws -> URL {
        let fullImage = try await screenCapture.captureDisplay(
            id: displayID,
            excludingWindowIDs: excludedWindowIDs()
        )
        let cleanImage: CGImage
        let document: CaptureDocument
        let outputSize: NSSize
        if let region {
            cleanImage = try crop(fullImage, to: region, on: screen)
            document = CaptureDocument(
                canvasWidth: region.width,
                canvasHeight: region.height,
                veilOpacity: annotationSession.veilOpacity,
                strokes: StrokeGeometry.crop(
                    annotationSession.strokes(for: displayID),
                    to: PixelRect(
                        x: region.minX,
                        y: region.minY,
                        width: region.width,
                        height: region.height
                    )
                )
            )
            outputSize = region.size
        } else {
            cleanImage = fullImage
            document = CaptureDocument(
                canvasWidth: screen.frame.width,
                canvasHeight: screen.frame.height,
                veilOpacity: annotationSession.veilOpacity,
                strokes: annotationSession.strokes(for: displayID)
            )
            outputSize = screen.frame.size
        }
        let flattened = try CaptureRenderer.flattened(
            background: cleanImage,
            document: document
        )
        let destination = try captureDestination()
        try CaptureRenderer.pngData(for: cleanImage).write(
            to: CapturePackagePaths.original(for: destination),
            options: .atomic
        )
        try writeDocument(document, beside: destination)
        try CaptureRenderer.pngData(for: flattened).write(to: destination, options: .atomic)

        if copyToClipboard {
            let pasteboardImage = NSImage(cgImage: flattened, size: outputSize)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([pasteboardImage, destination as NSURL])
        }
        return destination
    }

    private func crop(_ image: CGImage, to region: NSRect, on screen: NSScreen) throws -> CGImage {
        let scaleX = Double(image.width) / screen.frame.width
        let scaleY = Double(image.height) / screen.frame.height
        let pixelRect = CGRect(
            x: region.minX * scaleX,
            y: region.minY * scaleY,
            width: region.width * scaleX,
            height: region.height * scaleY
        ).integral
        guard let cropped = image.cropping(to: pixelRect) else {
            throw CaptureSaveError.encodingFailed
        }
        return cropped
    }

    private func writeDocument(_ document: CaptureDocument, beside imageURL: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(document)
        try data.write(to: CapturePackagePaths.document(for: imageURL), options: .atomic)
    }

    private func captureDestination() throws -> URL {
        guard let pictures = FileManager.default.urls(
            for: .picturesDirectory,
            in: .userDomainMask
        ).first else {
            throw CaptureSaveError.picturesFolderUnavailable
        }

        let folder = pictures.appendingPathComponent("OverInk", isDirectory: true)
        try FileManager.default.createDirectory(
            at: folder,
            withIntermediateDirectories: true
        )

        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let filename = "OverInk-\(formatter.string(from: Date())).png"
        return uniqueURL(folder.appendingPathComponent(filename))
    }

    private func uniqueURL(_ proposed: URL) -> URL {
        guard FileManager.default.fileExists(atPath: proposed.path) else {
            return proposed
        }

        let stem = proposed.deletingPathExtension().lastPathComponent
        let folder = proposed.deletingLastPathComponent()
        for suffix in 2 ... 999 {
            let candidate = folder
                .appendingPathComponent("\(stem)-\(suffix)")
                .appendingPathExtension("png")
            if !FileManager.default.fileExists(atPath: candidate.path) {
                return candidate
            }
        }
        return proposed
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
