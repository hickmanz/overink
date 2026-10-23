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
        for stroke in document.strokes {
            draw(stroke, in: context, scaleX: scaleX, scaleY: scaleY, outputHeight: Double(height))
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

    private static func draw(
        _ stroke: InkStroke,
        in context: CGContext,
        scaleX: Double,
        scaleY: Double,
        outputHeight: Double
    ) {
        guard let first = stroke.points.first else { return }
        let color = NSColor(hex: stroke.colorHex).cgColor
        context.saveGState()
        context.setStrokeColor(color)
        context.setFillColor(color)
        context.setLineCap(.round)
        context.setLineJoin(.round)

        func mapped(_ point: InkPoint) -> CGPoint {
            CGPoint(
                x: point.x * scaleX,
                y: outputHeight - point.y * scaleY
            )
        }

        func width(_ point: InkPoint) -> Double {
            let base = stroke.width * (scaleX + scaleY) / 2
            guard !stroke.ignoresPressure else { return base }
            return base * (0.35 + 0.65 * Double(point.pressure))
        }

        if stroke.points.count == 1 {
            let diameter = width(first)
            let point = mapped(first)
            context.fillEllipse(
                in: CGRect(
                    x: point.x - diameter / 2,
                    y: point.y - diameter / 2,
                    width: diameter,
                    height: diameter
                )
            )
        } else {
            for (start, end) in zip(stroke.points, stroke.points.dropFirst()) {
                context.setLineWidth((width(start) + width(end)) / 2)
                context.beginPath()
                context.move(to: mapped(start))
                context.addLine(to: mapped(end))
                context.strokePath()
            }
        }
        context.restoreGState()
    }
}
