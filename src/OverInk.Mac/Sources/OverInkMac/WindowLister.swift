import CoreGraphics
import Foundation
import OverInkCore

enum WindowLister {
    /// Frames of on-screen, normal-level windows from other apps, ordered front to back, in global
    /// point coordinates with a top-left origin (the same space as `CGDisplayBounds`).
    /// Listing frames needs no Screen Recording permission.
    static func onScreenWindowFrames(excludingProcess pid: pid_t = getpid()) -> [PixelRect] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
            return []
        }
        return list.compactMap { info in
            guard let layer = info[kCGWindowLayer as String] as? Int, layer == 0,
                  let owner = info[kCGWindowOwnerPID as String] as? Int32, owner != pid,
                  (info[kCGWindowAlpha as String] as? Double ?? 1) > 0.01,
                  let boundsDictionary = info[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsDictionary as CFDictionary),
                  bounds.width >= 40, bounds.height >= 40 else {
                return nil
            }
            return PixelRect(x: bounds.minX, y: bounds.minY, width: bounds.width, height: bounds.height)
        }
    }
}

extension PixelRect {
    init(_ rect: CGRect) {
        self.init(x: rect.minX, y: rect.minY, width: rect.width, height: rect.height)
    }

    var cgRect: CGRect {
        CGRect(x: x, y: y, width: width, height: height)
    }
}
