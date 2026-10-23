import AppKit

@MainActor
final class CaptureLibraryController: NSObject, NSCollectionViewDataSource, NSCollectionViewDelegate {
    private struct Entry {
        let url: URL
        let image: NSImage
        let modified: Date
    }

    private var window: NSWindow?
    private let collectionView = NSCollectionView()
    private let emptyLabel = NSTextField(labelWithString: "No captures yet")
    private let editorController = CaptureEditorController()
    private var entries: [Entry] = []

    func show() {
        if window == nil {
            buildWindow()
        }
        reload()
        window?.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    func reload() {
        entries = loadEntries()
        emptyLabel.isHidden = !entries.isEmpty
        collectionView.reloadData()
    }

    func numberOfSections(in collectionView: NSCollectionView) -> Int {
        1
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        numberOfItemsInSection section: Int
    ) -> Int {
        entries.count
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        itemForRepresentedObjectAt indexPath: IndexPath
    ) -> NSCollectionViewItem {
        let item = collectionView.makeItem(
            withIdentifier: CaptureCollectionItem.identifier,
            for: indexPath
        )
        guard let captureItem = item as? CaptureCollectionItem else { return item }
        let entry = entries[indexPath.item]
        captureItem.configure(image: entry.image, title: entry.url.deletingPathExtension().lastPathComponent)
        return captureItem
    }

    private func buildWindow() {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 820, height: 560),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "Capture Library"
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.center()

        let background = NSVisualEffectView()
        background.material = .underWindowBackground
        background.blendingMode = .behindWindow
        background.state = .active

        let header = makeHeader()
        header.translatesAutoresizingMaskIntoConstraints = false

        let layout = NSCollectionViewFlowLayout()
        layout.itemSize = NSSize(width: 184, height: 148)
        layout.minimumInteritemSpacing = 12
        layout.minimumLineSpacing = 14
        layout.sectionInset = NSEdgeInsets(top: 12, left: 18, bottom: 18, right: 18)

        collectionView.collectionViewLayout = layout
        collectionView.dataSource = self
        collectionView.delegate = self
        collectionView.isSelectable = true
        collectionView.allowsMultipleSelection = false
        collectionView.backgroundColors = [.clear]
        collectionView.register(
            CaptureCollectionItem.self,
            forItemWithIdentifier: CaptureCollectionItem.identifier
        )
        let doubleClick = NSClickGestureRecognizer(target: self, action: #selector(openSelected))
        doubleClick.numberOfClicksRequired = 2
        collectionView.addGestureRecognizer(doubleClick)

        let scroll = NSScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.documentView = collectionView
        scroll.translatesAutoresizingMaskIntoConstraints = false

        emptyLabel.font = .systemFont(ofSize: 15, weight: .medium)
        emptyLabel.textColor = .secondaryLabelColor
        emptyLabel.alignment = .center
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false

        background.addSubview(header)
        background.addSubview(scroll)
        background.addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: background.leadingAnchor, constant: 18),
            header.trailingAnchor.constraint(equalTo: background.trailingAnchor, constant: -18),
            header.topAnchor.constraint(equalTo: background.safeAreaLayoutGuide.topAnchor, constant: 12),
            header.heightAnchor.constraint(equalToConstant: 40),

            scroll.leadingAnchor.constraint(equalTo: background.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: background.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 8),
            scroll.bottomAnchor.constraint(equalTo: background.bottomAnchor),

            emptyLabel.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: scroll.centerYAnchor)
        ])

        window.contentView = background
        self.window = window
    }

    private func makeHeader() -> NSView {
        let title = NSTextField(labelWithString: "Captures")
        title.font = .systemFont(ofSize: 22, weight: .bold)

        let open = headerButton(icon: .images, label: "Open", action: #selector(openSelected))
        let copy = headerButton(icon: .camera, label: "Copy", action: #selector(copySelected))
        let reveal = headerButton(icon: .pointer, label: "Reveal", action: #selector(revealSelected))
        let delete = headerButton(icon: .trash, label: "Delete", action: #selector(deleteSelected))

        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let stack = NSStackView(views: [title, spacer, open, copy, reveal, delete])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        return stack
    }

    private func headerButton(icon: LucideIcon, label: String, action: Selector) -> NSButton {
        let button = NSButton(title: label, target: self, action: action)
        button.image = icon.image(size: 15, label: label)
        button.imagePosition = .imageLeading
        button.bezelStyle = .rounded
        button.controlSize = .small
        return button
    }

    private func loadEntries() -> [Entry] {
        guard let folder = captureFolder(),
              let urls = try? FileManager.default.contentsOfDirectory(
                  at: folder,
                  includingPropertiesForKeys: [.contentModificationDateKey],
                  options: [.skipsHiddenFiles]
              ) else {
            return []
        }

        return urls.compactMap { url in
            guard url.pathExtension.lowercased() == "png",
                  !url.lastPathComponent.hasSuffix(".orig.png"),
                  let image = NSImage(contentsOf: url) else {
                return nil
            }
            let values = try? url.resourceValues(forKeys: [.contentModificationDateKey])
            return Entry(url: url, image: image, modified: values?.contentModificationDate ?? .distantPast)
        }
        .sorted { $0.modified > $1.modified }
    }

    private func captureFolder() -> URL? {
        FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)
            .first?
            .appendingPathComponent("OverInk", isDirectory: true)
    }

    private var selectedEntry: Entry? {
        guard let index = collectionView.selectionIndexPaths.first?.item,
              entries.indices.contains(index) else {
            return nil
        }
        return entries[index]
    }

    @objc private func openSelected() {
        guard let entry = selectedEntry else { return }
        editorController.open(entry.url) { [weak self] in
            self?.reload()
        }
    }

    @objc private func copySelected() {
        guard let entry = selectedEntry else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([entry.image, entry.url as NSURL])
    }

    @objc private func revealSelected() {
        guard let entry = selectedEntry else { return }
        NSWorkspace.shared.activateFileViewerSelecting([entry.url])
    }

    @objc private func deleteSelected() {
        guard let entry = selectedEntry else { return }
        let alert = NSAlert()
        alert.messageText = "Delete this capture?"
        alert.informativeText = entry.url.lastPathComponent
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Delete")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        try? FileManager.default.removeItem(at: entry.url)
        try? FileManager.default.removeItem(at: CapturePackagePaths.original(for: entry.url))
        try? FileManager.default.removeItem(at: CapturePackagePaths.document(for: entry.url))
        reload()
    }
}

@MainActor
private final class CaptureCollectionItem: NSCollectionViewItem {
    static let identifier = NSUserInterfaceItemIdentifier("CaptureCollectionItem")

    private let thumbnail = NSImageView()
    private let titleLabel = NSTextField(labelWithString: "")

    override func loadView() {
        let card = NSView()
        card.wantsLayer = true
        card.layer?.cornerRadius = 14
        card.layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.55).cgColor
        card.layer?.borderWidth = 0.5
        card.layer?.borderColor = NSColor.separatorColor.cgColor

        thumbnail.imageScaling = .scaleProportionallyUpOrDown
        thumbnail.wantsLayer = true
        thumbnail.layer?.cornerRadius = 10
        thumbnail.layer?.masksToBounds = true
        thumbnail.translatesAutoresizingMaskIntoConstraints = false

        titleLabel.font = .systemFont(ofSize: 11, weight: .medium)
        titleLabel.textColor = .secondaryLabelColor
        titleLabel.lineBreakMode = .byTruncatingMiddle
        titleLabel.translatesAutoresizingMaskIntoConstraints = false

        card.addSubview(thumbnail)
        card.addSubview(titleLabel)
        NSLayoutConstraint.activate([
            thumbnail.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 8),
            thumbnail.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -8),
            thumbnail.topAnchor.constraint(equalTo: card.topAnchor, constant: 8),
            thumbnail.heightAnchor.constraint(equalToConstant: 108),

            titleLabel.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 10),
            titleLabel.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -10),
            titleLabel.topAnchor.constraint(equalTo: thumbnail.bottomAnchor, constant: 8)
        ])
        view = card
    }

    override var isSelected: Bool {
        didSet {
            view.layer?.borderWidth = isSelected ? 2 : 0.5
            view.layer?.borderColor = isSelected
                ? NSColor.systemBlue.cgColor
                : NSColor.separatorColor.cgColor
        }
    }

    func configure(image: NSImage, title: String) {
        thumbnail.image = image
        titleLabel.stringValue = title
    }
}
