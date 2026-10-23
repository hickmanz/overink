import CoreGraphics
import Foundation
import ImageIO
import OverInkCore
import UniformTypeIdentifiers

/// The iPad's view of the capture library, served over HTTP on the remote port.
///
/// Library data is too large for WebSocket messages and `<img>` tags cannot send headers, so every route
/// needs a per-connection capability token (`?k=`) that is only handed to the paired owner device.
/// Same routes and payloads as the Windows host, so the shared web client works with both.
actor RemoteLibraryService {
    struct Settings: Sendable {
        var folder: URL
        var enabled: Bool
    }

    private static let thumbEdge = 480
    private static let maxOriginalEdge = 4096

    private let settings: @Sendable () -> Settings
    private let tokens: LibraryTokenRegistry

    private var thumbs: [String: Data] = [:]
    private var sizes: [String: (w: Int, h: Int)] = [:]
    private var scaledOriginal: (key: String, data: Data)?
    private var lastRevisions: [String: String] = [:]

    init(tokens: LibraryTokenRegistry, settings: @escaping @Sendable () -> Settings) {
        self.tokens = tokens
        self.settings = settings
    }

    private var library: CaptureLibrary { CaptureLibrary(folder: settings().folder) }

    // MARK: Routing

    /// Handles a `/lib...` request; `nil` for any other path.
    func handle(_ request: HTTPRequest, peer: String) async -> HTTPResponse? {
        guard request.path == "/lib" || request.path.hasPrefix("/lib/") else { return nil }

        guard RemoteNetwork.isAllowedRemote(peer) else { return .empty(403) }
        guard settings().enabled, tokens.isValid(request.query["k"]) else { return .empty(401) }

        let parts = request.path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        let id = parts.count >= 2 ? parts[1] : ""
        let action = parts.count == 3 ? parts[2] : ""

        switch (request.method, parts.count, action) {
        case ("GET", 1, _): return list()
        case ("GET", 3, "thumb"): return thumbnail(id: id)
        case ("GET", 3, "original"): return original(id: id)
        case ("GET", 3, "strokes"): return strokes(id: id)
        case ("POST", 3, "strokes"): return await saveStrokes(id: id, body: request.body)
        case ("POST", 3, "duplicate"): return duplicate(id: id)
        case ("DELETE", 2, _): return delete(id: id)
        default: return .empty(404)
        }
    }

    // MARK: Routes

    private func list() -> HTTPResponse {
        .json(library.entries().map(item(for:)))
    }

    private func thumbnail(id: String) -> HTTPResponse {
        guard let entry = entry(id) else { return .empty(404) }
        let key = "\(entry.name)|\(CaptureStorage.revision(of: entry.url))"
        if thumbs[key] == nil {
            guard let data = Self.jpegThumbnail(of: entry.url, edge: Self.thumbEdge) else { return .empty(404) }
            if thumbs.count > 400 { thumbs.removeAll() }
            thumbs[key] = data
        }
        return binary(thumbs[key] ?? Data(), type: "image/jpeg", cache: "private, max-age=3600")
    }

    private func original(id: String) -> HTTPResponse {
        guard let entry = entry(id) else { return .empty(404) }
        let original = CapturePackagePaths.original(for: entry.url)
        let url = FileManager.default.fileExists(atPath: original.path) ? original : entry.url
        guard let (w, h) = Self.pixelSize(of: url) else { return .empty(404) }

        if max(w, h) <= Self.maxOriginalEdge {
            guard let data = try? Data(contentsOf: url) else { return .empty(404) }
            return binary(data, type: "image/png", cache: "private, max-age=3600")
        }

        // Very large captures are downscaled so Safari's canvas limits and the Wi-Fi link are not stressed.
        // Strokes travel in image pixels of the full original, and the client scales by the reported size.
        let key = "\(entry.name)|\(CaptureStorage.revision(of: entry.url))"
        if let cached = scaledOriginal, cached.key == key {
            return binary(cached.data, type: "image/png", cache: "private, max-age=3600")
        }
        guard let scaled = Self.scaledPNG(of: url, edge: Self.maxOriginalEdge) else { return .empty(404) }
        scaledOriginal = (key, scaled)
        return binary(scaled, type: "image/png", cache: "private, max-age=3600")
    }

    private func strokes(id: String) -> HTTPResponse {
        guard let entry = entry(id) else { return .empty(404) }
        guard let descriptor = try? CaptureStorage.describe(entry.url) else { return .empty(404) }
        return .json(RemoteLibraryStrokes(
            rev: CaptureStorage.revision(of: entry.url),
            w: descriptor.pixelWidth,
            h: descriptor.pixelHeight,
            strokes: RemoteLibraryConversion.strokes(
                from: descriptor.document,
                pixelWidth: descriptor.pixelWidth,
                pixelHeight: descriptor.pixelHeight
            ),
            veil: descriptor.document.veilOpacity
        ))
    }

    private struct Conflict: Encodable { let error = "conflict"; let rev: String }
    private struct Saved: Encodable { let rev: String }
    private struct Done: Encodable { let ok = true }

    private func saveStrokes(id: String, body: Data) async -> HTTPResponse {
        guard let request = try? JSONDecoder().decode(RemoteLibrarySave.self, from: body) else {
            return .text(400, "Malformed JSON.")
        }
        if let problem = RemoteLibraryConversion.validate(request.strokes) {
            return .text(400, problem)
        }
        guard let entry = entry(id) else { return .empty(404) }

        let current = CaptureStorage.revision(of: entry.url)
        guard current == request.baseRev else { return .json(Conflict(rev: current), status: 409) }

        do {
            let descriptor = try CaptureStorage.describe(entry.url)
            var document = descriptor.document
            document.strokes = RemoteLibraryConversion.inkStrokes(
                from: request.strokes,
                document: document,
                pixelWidth: descriptor.pixelWidth,
                pixelHeight: descriptor.pixelHeight
            )
            if let veil = request.veil, veil.isFinite {
                document.veilOpacity = min(max(veil, 0), RemoteLibraryConversion.maxVeil)
            }
            guard let source = CGImageSourceCreateWithURL(descriptor.sourceURL as CFURL, nil),
                  let background = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
                throw CaptureEditorError.imageUnavailable
            }
            let flattened = try CaptureRenderer.flattened(background: background, document: document)
            try CaptureSaveJob(
                url: entry.url, document: document, flattened: flattened,
                promoteOriginal: !descriptor.hasOriginal
            ).run()

            let revision = CaptureStorage.revision(of: entry.url)
            thumbs.removeAll()
            // Our own write must not echo back as "changed on the Mac".
            lastRevisions[entry.name] = revision
            return .json(Saved(rev: revision))
        } catch {
            NSLog("OverInk remote library save failed for %@: %@", id, error.localizedDescription)
            return .text(500, "Saving failed.")
        }
    }

    private func duplicate(id: String) -> HTTPResponse {
        guard let entry = entry(id) else { return .empty(404) }
        do {
            let copy = try library.duplicate(entry.url)
            let modified = (try? copy.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            return .json(item(for: CaptureEntry(url: copy, modified: modified ?? Date())))
        } catch {
            NSLog("OverInk remote library duplicate failed for %@: %@", id, error.localizedDescription)
            return .text(500, "Duplicating failed.")
        }
    }

    private func delete(id: String) -> HTTPResponse {
        guard let entry = entry(id) else { return .empty(404) }
        do {
            try library.delete(entry.url)
            thumbs.removeAll()
            return .json(Done())
        } catch {
            return .text(500, "Deleting failed.")
        }
    }

    // MARK: Change detection

    /// Re-reads revisions and reports what changed since the last scan (`listChanged`: captures were
    /// added or removed; `changed`: captures whose content changed).
    func scan() -> (listChanged: Bool, changed: [(id: String, rev: String)]) {
        var current: [String: String] = [:]
        for entry in library.entries() { current[entry.name] = CaptureStorage.revision(of: entry.url) }

        let listChanged = Set(current.keys) != Set(lastRevisions.keys)
        var changed: [(id: String, rev: String)] = []
        for (id, rev) in current {
            if let previous = lastRevisions[id], previous != rev { changed.append((id, rev)) }
        }
        lastRevisions = current
        if listChanged || !changed.isEmpty { thumbs.removeAll() }
        return (listChanged, changed)
    }

    /// Takes the current state as the baseline (call when serving starts or the folder changes).
    func resetBaseline() {
        _ = scan()
    }

    // MARK: Helpers

    private func entry(_ id: String) -> CaptureEntry? {
        library.entries().first { $0.name == id }
    }

    private func item(for entry: CaptureEntry) -> RemoteLibraryItem {
        let revision = CaptureStorage.revision(of: entry.url)
        let key = "\(entry.name)|\(revision)"
        var size = sizes[key]
        if size == nil {
            let original = CapturePackagePaths.original(for: entry.url)
            let url = FileManager.default.fileExists(atPath: original.path) ? original : entry.url
            size = Self.pixelSize(of: url) ?? (0, 0)
            if sizes.count > 2000 { sizes.removeAll() }
            sizes[key] = size
        }
        return RemoteLibraryItem(
            id: entry.name,
            name: entry.name,
            createdAt: Int64((entry.modified.timeIntervalSince1970 * 1000).rounded()),
            w: size?.w ?? 0,
            h: size?.h ?? 0,
            rev: revision,
            editable: entry.isEditable
        )
    }

    private func binary(_ data: Data, type: String, cache: String) -> HTTPResponse {
        HTTPResponse(status: 200, headers: [("Content-Type", type), ("Cache-Control", cache)], body: data)
    }

    private static func pixelSize(of url: URL) -> (w: Int, h: Int)? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let w = properties[kCGImagePropertyPixelWidth] as? Int,
              let h = properties[kCGImagePropertyPixelHeight] as? Int,
              w > 0, h > 0 else { return nil }
        return (w, h)
    }

    private static func thumbnailImage(of url: URL, edge: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: edge,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    private static func encode(_ image: CGImage, as type: UTType, quality: Double? = nil) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, type.identifier as CFString, 1, nil) else {
            return nil
        }
        var options: [CFString: Any] = [:]
        if let quality { options[kCGImageDestinationLossyCompressionQuality] = quality }
        CGImageDestinationAddImage(destination, image, options as CFDictionary)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    private static func jpegThumbnail(of url: URL, edge: Int) -> Data? {
        guard let image = thumbnailImage(of: url, edge: edge) else { return nil }
        // JPEG has no alpha: flatten on white so transparent captures do not turn black.
        guard let context = CGContext(
            data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
        ) else { return nil }
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let flat = context.makeImage() else { return nil }
        return encode(flat, as: .jpeg, quality: 0.8)
    }

    private static func scaledPNG(of url: URL, edge: Int) -> Data? {
        guard let image = thumbnailImage(of: url, edge: edge) else { return nil }
        return encode(image, as: .png)
    }
}
