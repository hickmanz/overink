import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins

enum QRCode {
    /// A crisp QR code for `text` at roughly `size` points (nearest-neighbour scaling keeps the edges sharp).
    static func image(for text: String, size: CGFloat) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }

        let scale = max(1, (size * 2 / output.extent.width).rounded(.down))
        let scaled = output.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        guard let cgImage = CIContext().createCGImage(scaled, from: scaled.extent) else { return nil }
        return NSImage(cgImage: cgImage, size: NSSize(width: size, height: size))
    }
}
