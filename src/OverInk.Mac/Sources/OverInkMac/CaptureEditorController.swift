import AppKit
import OverInkCore

@MainActor
final class CaptureEditorController: NSObject {
    private var window: NSWindow?
    private var canvas: CaptureEditorCanvas?
    private var currentURL: URL?
    private var originalImage: CGImage?
    private var onSave: (() -> Void)?
    private let veilSlider = NSSlider(value: 0, minValue: 0, maxValue: 0.6, target: nil, action: nil)
    private let colorWell = NSColorWell()
    private let widthSlider = NSSlider(value: 4, minValue: 1, maxValue: 24, target: nil, action: nil)

    func open(_ flattenedURL: URL, onSave: @escaping () -> Void) {
        do {
            let loaded = try load(flattenedURL)
            currentURL = flattenedURL
            originalImage = loaded.cgImage
            self.onSave = onSave
            buildWindowIfNeeded()
            canvas?.load(image: loaded.image, document: loaded.document)
            veilSlider.doubleValue = loaded.document.veilOpacity
            window?.title = flattenedURL.deletingPathExtension().lastPathComponent
            window?.makeKeyAndOrderFront(nil)
            NSApplication.shared.activate(ignoringOtherApps: true)
        } catch {
            showError(error)
        }
    }

    private func buildWindowIfNeeded() {
        guard window == nil else { return }

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 980, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.center()

        let root = NSVisualEffectView()
        root.material = .underWindowBackground
        root.blendingMode = .behindWindow
        root.state = .active

        let canvas = CaptureEditorCanvas()
        canvas.translatesAutoresizingMaskIntoConstraints = false
        self.canvas = canvas

        let toolbar = makeToolbar()
        toolbar.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(toolbar)
        root.addSubview(canvas)
        NSLayoutConstraint.activate([
            toolbar.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16),
            toolbar.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16),
            toolbar.topAnchor.constraint(equalTo: root.safeAreaLayoutGuide.topAnchor, constant: 10),
            toolbar.heightAnchor.constraint(equalToConstant: 44),

            canvas.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            canvas.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            canvas.topAnchor.constraint(equalTo: toolbar.bottomAnchor, constant: 8),
            canvas.bottomAnchor.constraint(equalTo: root.bottomAnchor)
        ])

        window.contentView = root
        self.window = window
    }

    private func makeToolbar() -> NSView {
        let pen = toolbarButton(icon: .pencil, label: "Pen", action: #selector(selectPen))
        let eraser = toolbarButton(icon: .eraser, label: "Eraser", action: #selector(selectEraser))
        let undo = toolbarButton(icon: .undo, label: "Undo", action: #selector(undo))
        let redo = toolbarButton(icon: .redo, label: "Redo", action: #selector(redo))

        colorWell.color = NSColor(hex: "#E53935")
        colorWell.controlSize = .small
        colorWell.target = self
        colorWell.action = #selector(colorChanged)

        widthSlider.controlSize = .small
        widthSlider.widthAnchor.constraint(equalToConstant: 90).isActive = true
        widthSlider.target = self
        widthSlider.action = #selector(widthChanged)

        veilSlider.controlSize = .small
        veilSlider.widthAnchor.constraint(equalToConstant: 90).isActive = true
        veilSlider.target = self
        veilSlider.action = #selector(veilChanged)

        let veilLabel = NSTextField(labelWithString: "Veil")
        veilLabel.font = .systemFont(ofSize: 11, weight: .medium)
        veilLabel.textColor = .secondaryLabelColor

        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let save = NSButton(title: "Save", target: self, action: #selector(save))
        save.bezelStyle = .rounded
        save.keyEquivalent = "s"
        save.keyEquivalentModifierMask = .command
        let copy = NSButton(title: "Copy", target: self, action: #selector(copyFlattened))
        copy.bezelStyle = .rounded

        let stack = NSStackView(
            views: [
                pen, eraser, undo, redo,
                colorWell, widthSlider,
                veilLabel, veilSlider,
                spacer, copy, save
            ]
        )
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8

        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.contentView = stack
            glass.cornerRadius = 15
            glass.style = .regular
            if #available(macOS 27.0, *) {
                glass.effectIsInteractive = true
            }
            return glass
        }

        let material = NSVisualEffectView()
        material.material = .popover
        material.blendingMode = .withinWindow
        material.state = .active
        stack.translatesAutoresizingMaskIntoConstraints = false
        material.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: material.leadingAnchor, constant: 10),
            stack.trailingAnchor.constraint(equalTo: material.trailingAnchor, constant: -10),
            stack.topAnchor.constraint(equalTo: material.topAnchor, constant: 4),
            stack.bottomAnchor.constraint(equalTo: material.bottomAnchor, constant: -4)
        ])
        return material
    }

    private func toolbarButton(icon: LucideIcon, label: String, action: Selector) -> NSButton {
        let button = NSButton()
        button.image = icon.image(label: label)
        button.toolTip = label
        button.bezelStyle = .texturedRounded
        button.target = self
        button.action = action
        return button
    }

    private func load(_ flattenedURL: URL) throws -> (image: NSImage, cgImage: CGImage, document: CaptureDocument) {
        let originalURL = CapturePackagePaths.original(for: flattenedURL)
        let sourceURL = FileManager.default.fileExists(atPath: originalURL.path)
            ? originalURL
            : flattenedURL
        guard let image = NSImage(contentsOf: sourceURL) else {
            throw CaptureEditorError.imageUnavailable
        }
        var proposedRect = NSRect(origin: .zero, size: image.size)
        guard let cgImage = image.cgImage(
            forProposedRect: &proposedRect,
            context: nil,
            hints: nil
        ) else {
            throw CaptureEditorError.imageUnavailable
        }

        let documentURL = CapturePackagePaths.document(for: flattenedURL)
        let document: CaptureDocument
        if sourceURL == originalURL,
           let data = try? Data(contentsOf: documentURL),
           let decoded = try? JSONDecoder().decode(CaptureDocument.self, from: data) {
            document = decoded
        } else {
            document = CaptureDocument(
                canvasWidth: image.size.width,
                canvasHeight: image.size.height
            )
        }
        return (image, cgImage, document)
    }

    @objc private func selectPen() {
        canvas?.tool = .pen
    }

    @objc private func selectEraser() {
        canvas?.tool = .eraser
    }

    @objc private func undo() {
        canvas?.undo()
    }

    @objc private func redo() {
        canvas?.redo()
    }

    @objc private func colorChanged() {
        canvas?.colorHex = colorWell.color.hexRGB
    }

    @objc private func widthChanged() {
        canvas?.penWidth = widthSlider.doubleValue
    }

    @objc private func veilChanged() {
        canvas?.setVeil(veilSlider.doubleValue)
    }

    @objc private func save() {
        do {
            _ = try persist()
            onSave?()
        } catch {
            showError(error)
        }
    }

    @objc private func copyFlattened() {
        do {
            let image = try renderCurrent()
            let nsImage = NSImage(cgImage: image, size: canvas?.documentSize ?? .zero)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([nsImage])
        } catch {
            showError(error)
        }
    }

    @discardableResult
    private func persist() throws -> URL {
        guard let currentURL, let canvas else {
            throw CaptureEditorError.noDocument
        }
        let image = try renderCurrent()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(canvas.document).write(
            to: CapturePackagePaths.document(for: currentURL),
            options: .atomic
        )
        try CaptureRenderer.pngData(for: image).write(to: currentURL, options: .atomic)
        return currentURL
    }

    private func renderCurrent() throws -> CGImage {
        guard let originalImage, let document = canvas?.document else {
            throw CaptureEditorError.noDocument
        }
        return try CaptureRenderer.flattened(background: originalImage, document: document)
    }

    private func showError(_ error: Error) {
        let alert = NSAlert()
        alert.messageText = "Editor error"
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        alert.runModal()
    }
}

@MainActor
private final class CaptureEditorCanvas: NSView {
    private(set) var document = CaptureDocument(canvasWidth: 1, canvasHeight: 1)
    private var background: NSImage?
    private var activeStroke: InkStroke?
    private var undoStates: [[InkStroke]] = []
    private var redoStates: [[InkStroke]] = []

    var tool = SketchTool.pen
    var colorHex = "#E53935"
    var penWidth = 4.0
    var documentSize: NSSize {
        NSSize(width: document.canvasWidth, height: document.canvasHeight)
    }

    override var isFlipped: Bool { true }

    func load(image: NSImage, document: CaptureDocument) {
        background = image
        self.document = document
        undoStates.removeAll()
        redoStates.removeAll()
        activeStroke = nil
        needsDisplay = true
    }

    func setVeil(_ value: Double) {
        document.veilOpacity = min(max(value, 0), 0.6)
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        guard let point = documentPoint(for: convert(event.locationInWindow, from: nil)) else {
            return
        }
        if tool == .eraser {
            erase(near: point)
            return
        }
        activeStroke = InkStroke(
            colorHex: colorHex,
            width: penWidth,
            points: [InkPoint(x: point.x, y: point.y, pressure: pressure(event))]
        )
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let point = documentPoint(for: convert(event.locationInWindow, from: nil)) else {
            return
        }
        if tool == .eraser {
            erase(near: point)
        } else {
            activeStroke?.points.append(
                InkPoint(x: point.x, y: point.y, pressure: pressure(event))
            )
            needsDisplay = true
        }
    }

    override func mouseUp(with event: NSEvent) {
        guard let stroke = activeStroke else { return }
        pushUndo()
        document.strokes.append(stroke)
        activeStroke = nil
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard let background else { return }
        let target = imageRect
        background.draw(
            in: target,
            from: .zero,
            operation: .copy,
            fraction: 1,
            respectFlipped: true,
            hints: [.interpolation: NSImageInterpolation.high]
        )
        if document.veilOpacity > 0 {
            NSColor.white.withAlphaComponent(document.veilOpacity).setFill()
            NSBezierPath(rect: target).fill()
        }
        document.strokes.forEach { draw($0, in: target) }
        if let activeStroke {
            draw(activeStroke, in: target)
        }
    }

    func undo() {
        guard let previous = undoStates.popLast() else { return }
        redoStates.append(document.strokes)
        document.strokes = previous
        needsDisplay = true
    }

    func redo() {
        guard let next = redoStates.popLast() else { return }
        undoStates.append(document.strokes)
        document.strokes = next
        needsDisplay = true
    }

    private var imageRect: NSRect {
        guard document.canvasWidth > 0, document.canvasHeight > 0 else { return .zero }
        let available = bounds.insetBy(dx: 28, dy: 24)
        let scale = min(
            available.width / document.canvasWidth,
            available.height / document.canvasHeight
        )
        let size = NSSize(
            width: document.canvasWidth * scale,
            height: document.canvasHeight * scale
        )
        return NSRect(
            x: available.midX - size.width / 2,
            y: available.midY - size.height / 2,
            width: size.width,
            height: size.height
        )
    }

    private func documentPoint(for point: NSPoint) -> NSPoint? {
        let rect = imageRect
        guard rect.contains(point), rect.width > 0, rect.height > 0 else { return nil }
        return NSPoint(
            x: (point.x - rect.minX) / rect.width * document.canvasWidth,
            y: (point.y - rect.minY) / rect.height * document.canvasHeight
        )
    }

    private func draw(_ stroke: InkStroke, in rect: NSRect) {
        guard let first = stroke.points.first else { return }
        let scaleX = rect.width / document.canvasWidth
        let scaleY = rect.height / document.canvasHeight
        let path = NSBezierPath()
        path.lineCapStyle = .round
        path.lineJoinStyle = .round
        path.lineWidth = stroke.width * (scaleX + scaleY) / 2
        path.move(to: mapped(first, in: rect))
        stroke.points.dropFirst().forEach { path.line(to: mapped($0, in: rect)) }
        NSColor(hex: stroke.colorHex).setStroke()
        path.stroke()
    }

    private func mapped(_ point: InkPoint, in rect: NSRect) -> NSPoint {
        NSPoint(
            x: rect.minX + point.x / document.canvasWidth * rect.width,
            y: rect.minY + point.y / document.canvasHeight * rect.height
        )
    }

    private func pressure(_ event: NSEvent) -> Float {
        event.pressure > 0 ? event.pressure : 1
    }

    private func erase(near point: NSPoint) {
        let radius = max(8, penWidth * 2)
        guard let index = document.strokes.lastIndex(where: { stroke in
            stroke.points.contains {
                hypot($0.x - point.x, $0.y - point.y) <= radius + stroke.width / 2
            }
        }) else { return }
        pushUndo()
        document.strokes.remove(at: index)
        needsDisplay = true
    }

    private func pushUndo() {
        undoStates.append(document.strokes)
        redoStates.removeAll()
    }
}

enum CaptureEditorError: LocalizedError {
    case imageUnavailable
    case noDocument

    var errorDescription: String? {
        switch self {
        case .imageUnavailable: "The capture image could not be opened."
        case .noDocument: "No capture is open."
        }
    }
}

private extension NSColor {
    var hexRGB: String {
        let converted = usingColorSpace(.sRGB) ?? self
        return String(
            format: "#%02X%02X%02X",
            Int(round(converted.redComponent * 255)),
            Int(round(converted.greenComponent * 255)),
            Int(round(converted.blueComponent * 255))
        )
    }
}
