import AppKit
import CoreGraphics
import OverInkCore

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

    /// Composes per-display captures into one image covering `rect` (global points).
    /// The output uses the highest pixel density among the displays involved.
    static func stitched(
        pieces: [CapturePiece],
        images: [UInt32: CGImage],
        displays: [CaptureDisplay],
        rect: PixelRect
    ) throws -> CGImage {
        func scale(for displayID: UInt32) -> Double? {
            guard let image = images[displayID],
                  let display = displays.first(where: { $0.id == displayID }),
                  display.bounds.width > 0 else { return nil }
            return Double(image.width) / display.bounds.width
        }

        let outputScale = pieces.compactMap { scale(for: $0.displayID) }.max() ?? 1
        let width = max(1, Int((rect.width * outputScale).rounded()))
        let height = max(1, Int((rect.height * outputScale).rounded()))
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
        context.interpolationQuality = .high

        for piece in pieces {
            guard let image = images[piece.displayID],
                  let pieceScale = scale(for: piece.displayID) else { continue }
            let sourcePixels = CGRect(
                x: piece.source.x * pieceScale,
                y: piece.source.y * pieceScale,
                width: piece.source.width * pieceScale,
                height: piece.source.height * pieceScale
            ).integral
            guard let cropped = image.cropping(to: sourcePixels) else {
                throw CaptureSaveError.encodingFailed
            }
            // Destination is top-left based; CGContext is bottom-left based.
            let destination = CGRect(
                x: piece.destination.x * outputScale,
                y: Double(height) - (piece.destination.y + piece.destination.height) * outputScale,
                width: piece.destination.width * outputScale,
                height: piece.destination.height * outputScale
            )
            context.draw(cropped, in: destination)
        }

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
