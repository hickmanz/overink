import AppKit
import OverInkCore

/// Everything about the iPad remote in one window: turn it on, pair a tablet with a QR code, see who is
/// connected, and choose what is streamed and how sharp it is.
@MainActor
final class RemoteWindowController: NSObject, NSWindowDelegate {
    private let host: RemoteHost
    private let settings: SettingsModel
    private let regionPicker = RegionPickerController()

    private var window: NSWindow?
    private let width: CGFloat = 560
    private let labelWidth: CGFloat = 130

    private let enableBox = ClosureCheckbox(title: "Draw on this Mac from an iPad")
    private let statusLabel = WindowChrome.label("", size: 12, secondary: true)
    private let issueLabel = WindowChrome.label("", size: 12)
    private let networkLabel = WindowChrome.label("", size: 12)

    private let pairingStack = NSStackView()
    private let qrView = NSImageView()
    private let urlLabel = WindowChrome.label("", size: 11, secondary: true)
    private let adapterPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let adapterWarning = WindowChrome.label("", size: 11)

    private let clientsStack = NSStackView()
    private let devicesStack = NSStackView()

    private let targetPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let displayPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let regionLabel = WindowChrome.label("", size: 12)
    private let presetPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let estimateLabel = WindowChrome.label("", size: 11, secondary: true)
    private let portField = NSTextField()
    private let maxClientsPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let approvalBox = ClosureCheckbox(title: "Ask before a guest joins")
    private let libraryBox = ClosureCheckbox(title: "Let paired devices browse and edit the capture library")

    private var shownURL: String?
    private var lastDeviceCount = -1
    private var localNetworkDenied = false
    private var codeTimer: Timer?
    private var rowObjects: [ClosureButton] = []

    private static let presetTitles = ["Balanced", "Sharp", "Native", "Eco", "Custom"]

    init(host: RemoteHost, settings: SettingsModel) {
        self.host = host
        self.settings = settings
        super.init()
        settings.observe { [weak self] in self?.refreshIfVisible() }
        let previousStatus = host.onStatusChange
        host.onStatusChange = { [weak self] in
            previousStatus?()
            self?.refreshIfVisible()
        }
        let previousClients = host.onClientsChange
        host.onClientsChange = { [weak self] in
            previousClients?()
            self?.refreshIfVisible()
        }
    }

    func show() {
        if window == nil { build() }
        host.refreshPairCode()
        shownURL = nil
        refresh()
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
        codeTimer?.invalidate()
        // A pairing code is good for 10 minutes; renew it before it lapses while the window is open.
        codeTimer = Timer.scheduledTimer(withTimeInterval: 8 * 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.host.refreshPairCode()
                self?.shownURL = nil
                self?.refreshIfVisible()
            }
        }
        if settings.value.remote.enabled {
            LocalNetworkPermission.check { [weak self] allowed in
                self?.localNetworkDenied = !allowed
                self?.refreshIfVisible()
            }
        }
    }

    /// Development aid: renders the window's contents to a PNG (for checking layout without a screen).
    func writeSnapshot(to url: URL) {
        show()
        guard let view = window?.contentView else { return }
        view.layoutSubtreeIfNeeded()
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    func windowWillClose(_ notification: Notification) {
        codeTimer?.invalidate()
        codeTimer = nil
    }

    func windowDidBecomeKey(_ notification: Notification) {
        refresh()
    }

    private func refreshIfVisible() {
        guard window?.isVisible == true else { return }
        refresh()
    }

    // MARK: Build

    private func build() {
        let window = WindowChrome.window(title: "iPad Remote", width: width)
        window.delegate = self

        let stack = NSStackView()
        stack.spacing = 8

        enableBox.onToggle = { [weak self] on in
            self?.settings.update { $0.remote.enabled = on }
            if on {
                self?.host.refreshPairCode()
                self?.shownURL = nil
                LocalNetworkPermission.check { allowed in
                    self?.localNetworkDenied = !allowed
                    self?.refreshIfVisible()
                }
            }
        }
        stack.addArrangedSubview(enableBox)
        stack.addArrangedSubview(statusLabel)
        stack.addArrangedSubview(issueLabel)
        stack.addArrangedSubview(networkLabel)
        issueLabel.textColor = .systemOrange
        networkLabel.textColor = .systemOrange
        for label in [statusLabel, issueLabel, networkLabel] { label.preferredMaxLayoutWidth = width - 48 }

        buildPairing(in: stack)

        addHeader("Connected now", to: stack)
        configure(clientsStack)
        stack.addArrangedSubview(clientsStack)

        addHeader("Paired devices", to: stack)
        configure(devicesStack)
        stack.addArrangedSubview(devicesStack)

        buildStreaming(in: stack)
        buildAccess(in: stack)

        WindowChrome.install(stack, in: window, root: WindowChrome.background(), width: width)
        window.center()
        self.window = window
    }

    private func configure(_ stack: NSStackView) {
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
    }

    private func buildPairing(in stack: NSStackView) {
        pairingStack.orientation = .vertical
        pairingStack.alignment = .leading
        pairingStack.spacing = 8

        let header = WindowChrome.label("Pair an iPad", size: 15, weight: .semibold)
        pairingStack.addArrangedSubview(header)
        pairingStack.setCustomSpacing(10, after: header)

        qrView.imageScaling = .scaleProportionallyUpOrDown
        qrView.widthAnchor.constraint(equalToConstant: 180).isActive = true
        qrView.heightAnchor.constraint(equalToConstant: 180).isActive = true
        qrView.wantsLayer = true
        qrView.layer?.backgroundColor = NSColor.white.cgColor
        qrView.layer?.cornerRadius = 6

        urlLabel.isSelectable = true
        urlLabel.preferredMaxLayoutWidth = width - 48 - 180 - 16
        urlLabel.lineBreakMode = .byCharWrapping

        let copy = ClosureButton(title: "Copy link")
        copy.onPress = { [weak self] in self?.copy(self?.host.pairingURL()) }
        let renew = ClosureButton(title: "New code")
        renew.onPress = { [weak self] in
            self?.host.refreshPairCode()
            self?.shownURL = nil
            self?.refresh()
        }
        let guest = ClosureButton(title: "Copy guest link")
        guest.onPress = { [weak self] in self?.copy(self?.host.guestURL()) }
        let reset = ClosureButton(title: "Reset guest link")
        reset.onPress = { [weak self] in self?.host.resetGuestLink() }

        let help = WindowChrome.label(
            "Scan with the iPad camera and open the link in Safari. The Mac and the iPad need to be on the same network. Pairing is remembered, so this is only needed once per iPad.",
            size: 11, secondary: true
        )
        help.preferredMaxLayoutWidth = width - 48 - 180 - 16

        let buttons = NSStackView(views: [copy, renew])
        buttons.spacing = 8
        let guestButtons = NSStackView(views: [guest, reset])
        guestButtons.spacing = 8
        let side = NSStackView(views: [help, urlLabel, buttons, guestButtons])
        side.orientation = .vertical
        side.alignment = .leading
        side.spacing = 8

        let line = NSStackView(views: [qrView, side])
        line.orientation = .horizontal
        line.alignment = .top
        line.spacing = 16
        pairingStack.addArrangedSubview(line)

        adapterPopup.target = self
        adapterPopup.action = #selector(adapterChosen(_:))
        adapterWarning.textColor = .systemOrange
        adapterWarning.preferredMaxLayoutWidth = width - 48 - labelWidth - 12
        row("Network", control: adapterPopup, in: pairingStack)
        let indent = NSStackView(views: [spacer(labelWidth), adapterWarning])
        indent.orientation = .horizontal
        indent.spacing = 12
        pairingStack.addArrangedSubview(indent)

        stack.addArrangedSubview(pairingStack)
        stack.setCustomSpacing(18, after: stack.arrangedSubviews.dropLast().last ?? pairingStack)
    }

    private func buildStreaming(in stack: NSStackView) {
        addHeader("Streaming", to: stack)

        targetPopup.addItems(withTitles: [
            "The display with the toolbar", "A specific display", "All displays",
        ])
        targetPopup.target = self
        targetPopup.action = #selector(targetChosen(_:))
        row("Show", control: targetPopup, in: stack)

        displayPopup.target = self
        displayPopup.action = #selector(displayChosen(_:))
        row("Display", control: displayPopup, in: stack)

        let choose = ClosureButton(title: "Select region…")
        choose.onPress = { [weak self] in self?.chooseRegion() }
        let clear = ClosureButton(title: "Clear")
        clear.onPress = { [weak self] in
            self?.settings.update { $0.remote = StreamTargetResolver.clearingRegion($0.remote) }
        }
        let regionLine = NSStackView(views: [regionLabel, choose, clear])
        regionLine.spacing = 8
        row("Region", control: regionLine, in: stack)

        presetPopup.addItems(withTitles: Self.presetTitles)
        presetPopup.target = self
        presetPopup.action = #selector(presetChosen(_:))
        row("Quality", control: presetPopup, note: nil, in: stack)
        estimateLabel.preferredMaxLayoutWidth = width - 48 - labelWidth - 12
        let indent = NSStackView(views: [spacer(labelWidth), estimateLabel])
        indent.orientation = .horizontal
        indent.spacing = 12
        stack.addArrangedSubview(indent)
    }

    private func buildAccess(in stack: NSStackView) {
        addHeader("Access", to: stack)

        portField.alignment = .right
        portField.target = self
        portField.action = #selector(portEntered(_:))
        portField.widthAnchor.constraint(equalToConstant: 80).isActive = true
        row("Port", control: portField, in: stack)

        maxClientsPopup.addItems(withTitles: (1...16).map(String.init))
        maxClientsPopup.target = self
        maxClientsPopup.action = #selector(maxClientsChosen(_:))
        row("Devices at once", control: maxClientsPopup, in: stack)

        approvalBox.onToggle = { [weak self] on in self?.settings.update { $0.remote.requireJoinApproval = on } }
        libraryBox.onToggle = { [weak self] on in self?.settings.update { $0.remote.libraryEnabled = on } }
        addIndented(approvalBox, to: stack)
        addIndented(libraryBox, to: stack)
    }

    private func addHeader(_ title: String, to stack: NSStackView) {
        let header = WindowChrome.label(title, size: 15, weight: .semibold)
        if let last = stack.arrangedSubviews.last { stack.setCustomSpacing(18, after: last) }
        stack.addArrangedSubview(header)
        stack.setCustomSpacing(10, after: header)
    }

    private func spacer(_ width: CGFloat) -> NSView {
        let view = NSView()
        view.widthAnchor.constraint(equalToConstant: width).isActive = true
        return view
    }

    private func addIndented(_ view: NSView, to stack: NSStackView) {
        let line = NSStackView(views: [spacer(labelWidth + 12), view])
        line.orientation = .horizontal
        line.spacing = 0
        stack.addArrangedSubview(line)
    }

    private func row(_ title: String, control: NSView, note: String? = nil, in stack: NSStackView) {
        let label = WindowChrome.label(title)
        label.alignment = .right
        label.widthAnchor.constraint(equalToConstant: labelWidth).isActive = true
        let line = NSStackView(views: [label, control])
        line.orientation = .horizontal
        line.alignment = .firstBaseline
        line.spacing = 12
        stack.addArrangedSubview(line)
        if let note {
            let text = WindowChrome.label(note, size: 11, secondary: true)
            text.preferredMaxLayoutWidth = width - 48 - labelWidth - 12
            let indent = NSStackView(views: [spacer(labelWidth), text])
            indent.orientation = .horizontal
            indent.spacing = 12
            stack.addArrangedSubview(indent)
        }
    }

    // MARK: Refresh

    private func refresh() {
        let remote = settings.value.remote
        enableBox.state = remote.enabled ? .on : .off

        switch host.status {
        case .stopped:
            statusLabel.stringValue = "Off. Turn this on to pair an iPad."
        case .running(let port):
            let count = host.clientCount
            statusLabel.stringValue = "On, listening on port \(port). \(count) device\(count == 1 ? "" : "s") connected."
        case .failed(let message):
            statusLabel.stringValue = message
        }
        issueLabel.stringValue = host.streamIssue ?? ""
        issueLabel.isHidden = host.streamIssue == nil
        networkLabel.stringValue = localNetworkDenied
            ? "Local Network access is off for OverInk, so iPads cannot connect. Turn it on in System Settings → Privacy & Security → Local Network."
            : ""
        networkLabel.isHidden = !localNetworkDenied

        pairingStack.isHidden = !host.isRunning
        if host.isRunning { refreshPairing(remote) }

        refreshClients()
        refreshDevices()
        refreshStreaming(remote)

        portField.stringValue = String(remote.port)
        maxClientsPopup.selectItem(withTitle: String(remote.maxClients))
        approvalBox.state = remote.requireJoinApproval ? .on : .off
        libraryBox.state = remote.libraryEnabled ? .on : .off

        fitWindow()
    }

    private func refreshPairing(_ remote: RemoteSettings) {
        let adapters = host.availableAdapters(includeAll: true)
        adapterPopup.removeAllItems()
        adapterPopup.addItem(withTitle: "Automatic")
        adapterPopup.lastItem?.representedObject = ""
        for adapter in adapters {
            adapterPopup.addItem(withTitle: "\(adapter.name) — \(adapter.address)")
            adapterPopup.lastItem?.representedObject = adapter.id
        }
        if let id = remote.adapterId, let index = adapters.firstIndex(where: { $0.id == id }) {
            adapterPopup.selectItem(at: index + 1)
        } else {
            adapterPopup.selectItem(at: 0)
        }

        let chosen = host.chosenAdapter()
        adapterWarning.stringValue = chosen?.warning
            ?? (chosen == nil ? "No network connection was found. Connect to Wi-Fi or Ethernet." : "")
        adapterWarning.isHidden = adapterWarning.stringValue.isEmpty

        // Someone just paired: the one-time code is spent, so show a fresh one.
        let deviceCount = host.pairing.pairedDevices.count
        if lastDeviceCount >= 0, deviceCount != lastDeviceCount {
            host.refreshPairCode()
            shownURL = nil
        }
        lastDeviceCount = deviceCount

        guard let url = host.pairingURL() else {
            qrView.image = nil
            urlLabel.stringValue = ""
            return
        }
        if url != shownURL {
            shownURL = url
            qrView.image = QRCode.image(for: url, size: 176)
            urlLabel.stringValue = url
        }
    }

    private func refreshClients() {
        clientsStack.arrangedSubviews.forEach { $0.removeFromSuperview() }
        let roster = host.isRunning ? host.roster : []
        if roster.isEmpty {
            clientsStack.addArrangedSubview(WindowChrome.label("Nobody yet.", size: 12, secondary: true))
            return
        }
        for entry in roster {
            let dot = NSView()
            dot.wantsLayer = true
            dot.layer?.backgroundColor = NSColor(hex: entry.color).cgColor
            dot.layer?.cornerRadius = 5
            dot.widthAnchor.constraint(equalToConstant: 10).isActive = true
            dot.heightAnchor.constraint(equalToConstant: 10).isActive = true
            let button = ClosureButton(title: "Disconnect")
            button.onPress = { [weak self] in self?.host.disconnect(entry.clientId) }
            let line = NSStackView(views: [dot, WindowChrome.label(entry.name), button])
            line.spacing = 8
            line.alignment = .centerY
            clientsStack.addArrangedSubview(line)
        }
    }

    private func refreshDevices() {
        devicesStack.arrangedSubviews.forEach { $0.removeFromSuperview() }
        let devices = host.pairing.pairedDevices.sorted { $0.lastSeen > $1.lastSeen }
        if devices.isEmpty {
            devicesStack.addArrangedSubview(WindowChrome.label("No paired iPads yet.", size: 12, secondary: true))
            return
        }
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        for device in devices.prefix(8) {
            let seen = formatter.localizedString(for: device.lastSeen, relativeTo: Date())
            let forget = ClosureButton(title: "Forget")
            forget.onPress = { [weak self] in self?.host.forgetDevice(device.id) }
            let name = WindowChrome.label(device.name)
            let when = WindowChrome.label("last seen \(seen)", size: 11, secondary: true)
            let line = NSStackView(views: [name, when, forget])
            line.spacing = 8
            line.alignment = .firstBaseline
            devicesStack.addArrangedSubview(line)
        }
        if devices.count > 8 {
            devicesStack.addArrangedSubview(
                WindowChrome.label("and \(devices.count - 8) more", size: 11, secondary: true)
            )
        }
        if devices.count > 1 {
            let all = ClosureButton(title: "Forget all")
            all.onPress = { [weak self] in self?.host.forgetAllDevices() }
            devicesStack.addArrangedSubview(all)
        }
    }

    private func refreshStreaming(_ remote: RemoteSettings) {
        targetPopup.selectItem(at: StreamTarget.allCases.firstIndex(of: remote.streamTarget) ?? 0)

        let displays = host.displays.map(\.stream)
        displayPopup.removeAllItems()
        for (index, display) in displays.enumerated() {
            displayPopup.addItem(withTitle: "Display \(index + 1)\(display.isPrimary ? " (main)" : "") — \(Int(display.bounds.width))×\(Int(display.bounds.height))")
            displayPopup.lastItem?.representedObject = display.id
        }
        if let chosen = StreamTargetResolver.find(displays, remote.streamDisplay),
           let index = displays.firstIndex(where: { $0.id == chosen.id }) {
            displayPopup.selectItem(at: index)
        }
        displayPopup.isEnabled = remote.streamTarget == .specificDisplay

        regionLabel.stringValue = remote.regionEnabled
            ? "\(Int(remote.regionW * 100))% × \(Int(remote.regionH * 100))% of a display"
            : "Whole target"

        let native = RemoteStreamEstimator.nativeLongEdge(stream: host.streamRect(), displays: displays)
        let presets: [RemoteStreamPreset] = [
            .balanced(nativeLongEdge: native), .sharp(nativeLongEdge: native),
            .native(nativeLongEdge: native), .eco,
        ]
        let match = presets.firstIndex { preset in
            preset.maxEdge == remote.maxEdge && preset.fps == remote.maxFps && preset.quality == remote.jpegQuality
        }
        presetPopup.selectItem(at: match ?? 4)

        let estimate = RemoteStreamEstimator.estimate(settings: remote, stream: host.streamRect(), displays: displays)
        estimateLabel.stringValue = "\(estimate.targetLabel) · about \(Int(estimate.estimatedKbps / 8)) KB/s. \(estimate.guidance)"
    }

    private func fitWindow() {
        guard let window, let stack = window.contentView?.subviews.first(where: { $0 is NSStackView }) as? NSStackView else {
            return
        }
        window.contentView?.layoutSubtreeIfNeeded()
        let height = stack.fittingSize.height + 24 * 2 + 18
        var frame = window.frame
        let newFrame = window.frameRect(forContentRect: NSRect(x: 0, y: 0, width: width, height: height))
        frame.origin.y += frame.height - newFrame.height
        frame.size.height = newFrame.height
        window.setFrame(frame, display: true)
    }

    // MARK: Actions

    private func copy(_ text: String?) {
        guard let text else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    @objc private func adapterChosen(_ sender: NSPopUpButton) {
        let id = (sender.selectedItem?.representedObject as? String) ?? ""
        settings.update { $0.remote.adapterId = id.isEmpty ? nil : id }
        shownURL = nil
    }

    @objc private func targetChosen(_ sender: NSPopUpButton) {
        let target = StreamTarget.allCases[min(max(sender.indexOfSelectedItem, 0), StreamTarget.allCases.count - 1)]
        settings.update {
            $0.remote.streamTarget = target
            if target == .specificDisplay, $0.remote.streamDisplay == nil {
                $0.remote.streamDisplay = self.host.displays.first(where: { $0.stream.isPrimary })?.stream.id
            }
        }
    }

    @objc private func displayChosen(_ sender: NSPopUpButton) {
        guard let id = sender.selectedItem?.representedObject as? String else { return }
        settings.update { $0.remote.streamDisplay = id }
    }

    private func chooseRegion() {
        window?.orderOut(nil)
        regionPicker.begin { [weak self] selection in
            guard let self else { return }
            if let selection {
                let rect = PixelRect(selection.rect)
                let displays = self.host.displays.map(\.stream)
                self.settings.update { $0.remote = StreamTargetResolver.settingRegion($0.remote, to: rect, displays: displays) }
            }
            self.show()
        }
    }

    @objc private func presetChosen(_ sender: NSPopUpButton) {
        let displays = host.displays.map(\.stream)
        let native = RemoteStreamEstimator.nativeLongEdge(stream: host.streamRect(), displays: displays)
        let preset: RemoteStreamPreset? = switch sender.indexOfSelectedItem {
        case 0: .balanced(nativeLongEdge: native)
        case 1: .sharp(nativeLongEdge: native)
        case 2: .native(nativeLongEdge: native)
        case 3: .eco
        default: nil
        }
        guard let preset else { return }
        settings.update { $0.remote = preset.applying(to: $0.remote) }
    }

    @objc private func portEntered(_ sender: NSTextField) {
        guard let port = Int(sender.stringValue), (1024...65535).contains(port) else {
            sender.stringValue = String(settings.value.remote.port)
            return
        }
        settings.update { $0.remote.port = port }
    }

    @objc private func maxClientsChosen(_ sender: NSPopUpButton) {
        guard let value = Int(sender.titleOfSelectedItem ?? "") else { return }
        settings.update { $0.remote.maxClients = value }
    }
}
