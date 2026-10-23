import AppKit
import CoreGraphics
import OverInkCore

enum CapturePackagePaths {
    static func original(for flattened: URL) -> URL {
        flattened
            .deletingPathExtension()
            .appendingPathExtension("orig.png")
    }

    static func document(for flattened: URL) -> URL {
        flattened
            .deletingPathExtension()
            .appendingPathExtension("overink.json")
    }
}

enum CaptureRenderer {
    static func flattened(background: CGImage, document: CaptureDocument) throws -> CGImage {
        let width = background.width
        let height = background.height
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw CaptureSaveError.encodingFailed
        }

        let bounds = CGRect(x: 0, y: 0, width: width, height: height)
        context.interpolationQuality = .high
        context.draw(background, in: bounds)

        if document.veilOpacity > 0 {
            context.setFillColor(
                NSColor.white.withAlphaComponent(document.veilOpacity).cgColor
            )
            context.fill(bounds)
        }

        let scaleX = document.canvasWidth > 0 ? Double(width) / document.canvasWidth : 1
        let scaleY = document.canvasHeight > 0 ? Double(height) / document.canvasHeight : 1
        // Strokes are stored top-left-origin in canvas points; flip and scale into the bitmap.
        context.saveGState()
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: scaleX, y: -scaleY)
        for stroke in document.strokes {
            StrokeRenderer.draw(stroke, in: context)
        }
        context.restoreGState()

        guard let image = context.makeImage() else {
            throw CaptureSaveError.encodingFailed
        }
        return image
    }

    static func pngData(for image: CGImage) throws -> Data {
        let bitmap = NSBitmapImageRep(cgImage: image)
        guard let png = bitmap.representation(using: .png, properties: [:]) else {
            throw CaptureSaveError.encodingFailed
        }
        return png
    }
}
