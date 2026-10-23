import CoreGraphics
import Foundation
import ImageIO
import OverInkCore

/// Reading and writing one capture on disk, shared by the editor and the remote library.
enum CaptureStorage {
    /// What is known about a capture without decoding its pixels.
    struct Descriptor: Sendable {
        var document: CaptureDocument
        var pixelWidth: Int
        var pixelHeight: Int
        /// True when the untouched `.orig.png` exists (the capture is already editable).
        var hasOriginal: Bool
        /// The image to draw ink on: the original when there is one, otherwise the flattened PNG.
        var sourceURL: URL
    }

    static func describe(_ flattenedURL: URL) throws -> Descriptor {
        let manager = FileManager.default
        let originalURL = CapturePackagePaths.original(for: flattenedURL)
        let hasOriginal = manager.fileExists(atPath: originalURL.path)
        let sourceURL = hasOriginal ? originalURL : flattenedURL

        guard let source = CGImageSourceCreateWithURL(sourceURL as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0 else {
            throw CaptureEditorError.imageUnavailable
        }

        if hasOriginal,
           let data = try? Data(contentsOf: CapturePackagePaths.document(for: flattenedURL)),
           let document = try? JSONDecoder().decode(CaptureDocument.self, from: data) {
            return Descriptor(
                document: document, pixelWidth: width, pixelHeight: height,
                hasOriginal: true, sourceURL: sourceURL
            )
        }

        // A plain PNG (or a capture whose sidecar is gone): canvas points follow the file's DPI.
        var scale = 1.0
        if let dpi = properties[kCGImagePropertyDPIWidth] as? Double, dpi > 0 {
            scale = dpi / 72
        }
        let document = CaptureDocument(
            canvasWidth: Double(width) / scale,
            canvasHeight: Double(height) / scale
        )
        return Descriptor(
            document: document, pixelWidth: width, pixelHeight: height,
            hasOriginal: hasOriginal, sourceURL: sourceURL
        )
    }

    static func load(_ flattenedURL: URL) throws -> (image: CGImage, document: CaptureDocument, promote: Bool) {
        let descriptor = try describe(flattenedURL)
        guard let source = CGImageSourceCreateWithURL(descriptor.sourceURL as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw CaptureEditorError.imageUnavailable
        }
        return (image, descriptor.document, !descriptor.hasOriginal)
    }

    /// A short string that changes whenever any file of the capture changes.
    static func revision(of flattenedURL: URL) -> String {
        RemoteLibraryRevision.make(
            files: CapturePackagePaths.allFiles(for: flattenedURL).map { url in
                // A fresh URL: `URL` caches resource values per instance, which would hide a just-finished write.
                let values = try? URL(fileURLWithPath: url.path).resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
                return (values?.contentModificationDate, values?.fileSize.map(Int64.init))
            }
        )
    }
}

/// Everything written when a capture is saved. Runs off the main thread.
struct CaptureSaveJob: Sendable {
    let url: URL
    let document: CaptureDocument
    let flattened: CGImage
    let promoteOriginal: Bool

    func run() throws {
        let manager = FileManager.default
        let original = CapturePackagePaths.original(for: url)
        // A plain PNG becomes editable by keeping its untouched pixels as the original. The
        // original of an existing capture is never rewritten.
        if promoteOriginal, !manager.fileExists(atPath: original.path), manager.fileExists(atPath: url.path) {
            try manager.copyItem(at: url, to: original)
        }
        var document = document
        document.upgradeToCurrentVersion()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(document).write(to: CapturePackagePaths.document(for: url), options: .atomic)
        try CaptureRenderer.pngData(for: flattened).write(to: url, options: .atomic)
    }
}
