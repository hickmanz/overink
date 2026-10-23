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

    func captureMainDisplay() async throws -> URL {
        guard await screenCapture.requestAuthorizationIfNeeded() else {
            throw ScreenCaptureError.permissionDenied
        }
        guard let screen = NSScreen.main, let displayID = screen.overInkDisplayID else {
            throw ScreenCaptureError.displayUnavailable
        }

        let cleanImage = try await screenCapture.captureDisplay(
            id: displayID,
            excludingWindowIDs: excludedWindowIDs()
        )
        let document = CaptureDocument(
            canvasWidth: screen.frame.width,
            canvasHeight: screen.frame.height,
            veilOpacity: annotationSession.veilOpacity,
            strokes: annotationSession.strokes(for: displayID)
        )
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

        let pasteboardImage = NSImage(cgImage: flattened, size: screen.frame.size)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([pasteboardImage, destination as NSURL])
        return destination
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
