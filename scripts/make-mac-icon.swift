// Regenerates src/OverInk.Mac/Supporting/AppIcon.icns from the Windows icon artwork.
//   swift scripts/make-mac-icon.swift
// The artwork isn't square, so it is centred on a transparent square canvas (with the usual margin).
import AppKit

let root = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent().deletingLastPathComponent()
let source = root.appendingPathComponent("src/OverInk/Assets/app.png")
let output = root.appendingPathComponent("src/OverInk.Mac/Supporting/AppIcon.icns")
guard let art = NSImage(contentsOf: source) else { fatalError("Can't read \(source.path)") }

let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

func render(pixels: Int) -> Data {
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
        samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
        bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .high
    let canvas = CGFloat(pixels)
    let usable = canvas * 0.86
    let scale = usable / max(art.size.width, art.size.height)
    let size = NSSize(width: art.size.width * scale, height: art.size.height * scale)
    art.draw(
        in: NSRect(x: (canvas - size.width) / 2, y: (canvas - size.height) / 2, width: size.width, height: size.height),
        from: .zero, operation: .sourceOver, fraction: 1
    )
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for size in [16, 32, 128, 256, 512] {
    try render(pixels: size).write(to: iconset.appendingPathComponent("icon_\(size)x\(size).png"))
    try render(pixels: size * 2).write(to: iconset.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}

let process = Process()
process.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
process.arguments = ["-c", "icns", iconset.path, "-o", output.path]
try process.run()
process.waitUntilExit()
guard process.terminationStatus == 0 else { fatalError("iconutil failed") }
print("Wrote \(output.path)")
