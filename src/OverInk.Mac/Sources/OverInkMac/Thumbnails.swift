import AppKit
import ImageIO
import OverInkCore

/// Decodes and caches capture thumbnails off the main thread.
@MainActor
final class ThumbnailStore {
    static let shared = ThumbnailStore()

    private let cache = NSCache<NSString, NSImage>()
    private let queue = DispatchQueue(label: "overink.thumbnails", qos: .userInitiated, attributes: .concurrent)

    private init() {
        cache.countLimit = 400
    }

    private func key(_ entry: CaptureEntry, _ maxPixel: Int) -> String {
        "\(entry.url.path)|\(entry.modified.timeIntervalSince1970)|\(maxPixel)"
    }

    func cached(_ entry: CaptureEntry, maxPixel: Int) -> NSImage? {
        cache.object(forKey: key(entry, maxPixel) as NSString)
    }

    func load(_ entry: CaptureEntry, maxPixel: Int, completion: @escaping @MainActor (NSImage?) -> Void) {
        let cacheKey = key(entry, maxPixel)
        if let image = cache.object(forKey: cacheKey as NSString) {
            completion(image)
            return
        }
        let url = entry.url
        queue.async {
            let cgImage = Self.makeThumbnail(url: url, maxPixel: maxPixel)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let cgImage else {
                        completion(nil)
                        return
                    }
                    let image = NSImage(
                        cgImage: cgImage,
                        size: NSSize(width: cgImage.width, height: cgImage.height)
                    )
                    self.cache.setObject(image, forKey: cacheKey as NSString)
                    completion(image)
                }
            }
        }
    }

    nonisolated private static func makeThumbnail(url: URL, maxPixel: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }
}
