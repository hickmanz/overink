import AppKit
import OverInkCore

/// Builds the color menu (swatch grid, custom wheel, saved colors) for any UI that edits a color.
@MainActor
final class ColorMenuController: NSObject {
    private let settings: SettingsModel
    private let current: () -> String
    private let apply: (String) -> Void
    private let panelLevel: NSWindow.Level
    private var panelObserver: NSObjectProtocol?

    init(
        settings: SettingsModel,
        current: @escaping () -> String,
        apply: @escaping (String) -> Void,
        panelLevel: NSWindow.Level = .floating
    ) {
        self.settings = settings
        self.current = current
        self.apply = apply
        self.panelLevel = panelLevel
    }

    func makeMenu() -> NSMenu {
        let hex = current()
        let palette = CustomColorPalette(normalizing: settings.value.customColors)
        let menu = NSMenu()

        let grid = SwatchGridView(presets: PresetPalette.hexColors, custom: palette.colors, selected: hex)
        grid.onSelect = { [weak self, weak menu] picked in
            self?.apply(picked)
            menu?.cancelTracking()
        }
        let gridItem = NSMenuItem()
        gridItem.view = grid
        menu.addItem(gridItem)
        menu.addItem(.separator())

        let custom = NSMenuItem(title: "Custom color…", action: #selector(openColorPanel), keyEquivalent: "")
        custom.target = self
        menu.addItem(custom)

        let save = NSMenuItem(title: "Save current color", action: #selector(saveCurrentColor), keyEquivalent: "")
        save.target = self
        save.isEnabled = !palette.hasSwatch(hex) && palette.colors.count < CustomColorPalette.maxColors
        menu.addItem(save)

        if !palette.colors.isEmpty {
            let removeItem = NSMenuItem(title: "Remove saved color", action: nil, keyEquivalent: "")
            let submenu = NSMenu()
            for saved in palette.colors {
                let item = NSMenuItem(title: saved, action: #selector(removeSavedColor(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = saved
                item.image = ColorSwatchButton.swatchImage(color: NSColor(hex: saved), size: 14)
                submenu.addItem(item)
            }
            removeItem.submenu = submenu
            menu.addItem(removeItem)
        }
        return menu
    }

    @objc private func saveCurrentColor() {
        saveColor(current())
    }

    private func saveColor(_ hex: String) {
        settings.update {
            var palette = CustomColorPalette(normalizing: $0.customColors)
            palette.add(hex)
            $0.customColors = palette.colors
        }
    }

    @objc private func removeSavedColor(_ sender: NSMenuItem) {
        guard let hex = sender.representedObject as? String else { return }
        settings.update {
            var palette = CustomColorPalette(normalizing: $0.customColors)
            palette.remove(hex)
            $0.customColors = palette.colors
        }
    }

    @objc private func openColorPanel() {
        let colorPanel = NSColorPanel.shared
        colorPanel.mode = .wheel
        colorPanel.showsAlpha = false
        colorPanel.color = NSColor(hex: current())
        colorPanel.setTarget(self)
        colorPanel.setAction(#selector(colorPanelChanged(_:)))
        colorPanel.level = panelLevel
        colorPanel.orderFrontRegardless()

        // A color picked on the wheel is saved as a swatch when the picker closes.
        if let panelObserver { NotificationCenter.default.removeObserver(panelObserver) }
        panelObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification,
            object: colorPanel,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let hex = self.current()
                if !CustomColorPalette(normalizing: self.settings.value.customColors).hasSwatch(hex) {
                    self.saveColor(hex)
                }
                if let observer = self.panelObserver {
                    NotificationCenter.default.removeObserver(observer)
                    self.panelObserver = nil
                }
                NSColorPanel.shared.setTarget(nil)
                NSColorPanel.shared.setAction(nil)
            }
        }
    }

    @objc private func colorPanelChanged(_ sender: NSColorPanel) {
        apply(sender.color.hexRGB)
    }
}

/// Preset and saved color swatches for the color menu.
@MainActor
final class SwatchGridView: NSView {
    var onSelect: ((String) -> Void)?

    private let presets: [String]
    private let custom: [String]
    private let selected: String
    private let columns = 7
    private let cell: CGFloat = 26
    private let margin: CGFloat = 12
    private let sectionGap: CGFloat = 18

    init(presets: [String], custom: [String], selected: String) {
        self.presets = presets
        self.custom = custom
        self.selected = ColorHex.normalize(selected) ?? selected
        super.init(frame: .zero)
        frame = NSRect(origin: .zero, size: contentSize)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override var isFlipped: Bool { true }

    private func rows(_ count: Int) -> Int { (count + columns - 1) / columns }

    private var contentSize: NSSize {
        var height = margin * 2 + CGFloat(rows(presets.count)) * cell
        if !custom.isEmpty {
            height += sectionGap + CGFloat(rows(custom.count)) * cell
        }
        return NSSize(width: margin * 2 + CGFloat(columns) * cell, height: height)
    }

    private var customOriginY: CGFloat {
        margin + CGFloat(rows(presets.count)) * cell + sectionGap
    }

    private func rect(index: Int, originY: CGFloat) -> NSRect {
        NSRect(
            x: margin + CGFloat(index % columns) * cell,
            y: originY + CGFloat(index / columns) * cell,
            width: cell,
            height: cell
        ).insetBy(dx: 3, dy: 3)
    }

    override func draw(_ dirtyRect: NSRect) {
        func drawSwatch(_ hex: String, in frame: NSRect) {
            let color = NSColor(hex: hex)
            color.setFill()
            let circle = NSBezierPath(ovalIn: frame)
            circle.fill()
            NSColor.separatorColor.setStroke()
            circle.lineWidth = 0.75
            circle.stroke()
            if ColorHex.normalize(hex) == selected {
                NSColor.controlAccentColor.setStroke()
                let ring = NSBezierPath(ovalIn: frame.insetBy(dx: -2.5, dy: -2.5))
                ring.lineWidth = 2
                ring.stroke()
            }
        }
        for (index, hex) in presets.enumerated() {
            drawSwatch(hex, in: rect(index: index, originY: margin))
        }
        if !custom.isEmpty {
            let label = NSAttributedString(
                string: "Saved",
                attributes: [
                    .font: NSFont.systemFont(ofSize: 10, weight: .medium),
                    .foregroundColor: NSColor.secondaryLabelColor
                ]
            )
            label.draw(at: NSPoint(x: margin + 3, y: customOriginY - sectionGap + 2))
            for (index, hex) in custom.enumerated() {
                drawSwatch(hex, in: rect(index: index, originY: customOriginY))
            }
        }
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        for (index, hex) in presets.enumerated() where rect(index: index, originY: margin).contains(point) {
            onSelect?(hex)
            return
        }
        for (index, hex) in custom.enumerated()
            where rect(index: index, originY: customOriginY).contains(point) {
            onSelect?(hex)
            return
        }
    }
}

@MainActor
final class ColorSwatchButton: NSButton {
    var color = NSColor.systemRed {
        didSet { needsDisplay = true }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        title = ""
        isBordered = false
        bezelStyle = .regularSquare
        toolTip = "Color"
        widthAnchor.constraint(equalToConstant: 36).isActive = true
        heightAnchor.constraint(equalToConstant: 36).isActive = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let circle = bounds.insetBy(dx: 8, dy: 8)
        color.setFill()
        NSBezierPath(ovalIn: circle).fill()
        NSColor.white.withAlphaComponent(0.8).setStroke()
        let border = NSBezierPath(ovalIn: circle)
        border.lineWidth = 1.5
        border.stroke()
    }

    static func swatchImage(color: NSColor, size: CGFloat) -> NSImage {
        NSImage(size: NSSize(width: size, height: size), flipped: false) { bounds in
            color.setFill()
            NSBezierPath(ovalIn: bounds.insetBy(dx: 1, dy: 1)).fill()
            return true
        }
    }
}

