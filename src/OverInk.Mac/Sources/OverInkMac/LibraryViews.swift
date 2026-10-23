import AppKit
import OverInkCore

/// Rename / duplicate / delete / reveal / copy for library entries, shared by the strip and the grid.
@MainActor
final class LibraryActions {
    let model: LibraryModel
    /// Called before a file operation so the editor can save pending edits to that capture.
    var beforeChange: ((URL) -> Void)?
    var didRename: ((_ old: URL, _ new: URL) -> Void)?
    var didDelete: ((URL) -> Void)?

    init(model: LibraryModel) {
        self.model = model
    }

    func rename(_ entry: CaptureEntry) {
        let field = NSTextField(string: entry.name)
        field.frame = NSRect(x: 0, y: 0, width: 280, height: 24)
        let alert = NSAlert()
        alert.messageText = "Rename capture"
        alert.accessoryView = field
        alert.addButton(withTitle: "Rename")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        NSApplication.shared.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        beforeChange?(entry.url)
        do {
            let renamed = try model.rename(entry.url, to: field.stringValue)
            if renamed != entry.url { didRename?(entry.url, renamed) }
        } catch {
            present(error, title: "Could not rename")
        }
    }

    func duplicate(_ entry: CaptureEntry) {
        beforeChange?(entry.url)
        do {
            try model.duplicate(entry.url)
        } catch {
            present(error, title: "Could not duplicate")
        }
    }

    func delete(_ entry: CaptureEntry) {
        let alert = NSAlert()
        alert.messageText = "Move this capture to the Trash?"
        alert.informativeText = entry.url.lastPathComponent
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Move to Trash")
        alert.addButton(withTitle: "Cancel")
        NSApplication.shared.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do {
            try model.delete(entry.url)
            didDelete?(entry.url)
        } catch {
            present(error, title: "Could not delete")
        }
    }

    func reveal(_ entry: CaptureEntry) {
        NSWorkspace.shared.activateFileViewerSelecting([entry.url])
    }

    func copy(_ entry: CaptureEntry) {
        guard let image = NSImage(contentsOf: entry.url) else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([image, entry.url as NSURL])
    }

    private func present(_ error: Error, title: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        alert.runModal()
    }
}

// MARK: - Items

@MainActor
class CaptureThumbnailItem: NSCollectionViewItem {
    class var thumbnailHeight: CGFloat { 108 }
    class var identifier: NSUserInterfaceItemIdentifier { NSUserInterfaceItemIdentifier("CaptureThumbnailItem") }
    class var maxPixel: Int { 400 }

    private let thumbnail = NSImageView()
    private let titleLabel = NSTextField(labelWithString: "")
    private var token = UUID()
    private var isCurrent = false

    override func loadView() {
        let card = NSView()
        card.wantsLayer = true
        card.layer?.cornerRadius = 12
        card.layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.55).cgColor
        card.layer?.borderWidth = 0.5
        card.layer?.borderColor = NSColor.separatorColor.cgColor

        thumbnail.imageScaling = .scaleProportionallyUpOrDown
        thumbnail.wantsLayer = true
        thumbnail.layer?.cornerRadius = 8
        thumbnail.layer?.masksToBounds = true
        thumbnail.translatesAutoresizingMaskIntoConstraints = false

        titleLabel.font = .systemFont(ofSize: 11, weight: .medium)
        titleLabel.textColor = .secondaryLabelColor
        titleLabel.lineBreakMode = .byTruncatingMiddle
        titleLabel.translatesAutoresizingMaskIntoConstraints = false

        card.addSubview(thumbnail)
        card.addSubview(titleLabel)
        NSLayoutConstraint.activate([
            thumbnail.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 6),
            thumbnail.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -6),
            thumbnail.topAnchor.constraint(equalTo: card.topAnchor, constant: 6),
            thumbnail.heightAnchor.constraint(equalToConstant: Self.thumbnailHeight),

            titleLabel.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 8),
            titleLabel.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -8),
            titleLabel.topAnchor.constraint(equalTo: thumbnail.bottomAnchor, constant: 5)
        ])
        view = card
    }

    override var isSelected: Bool {
        didSet { updateBorder() }
    }

    func configure(_ entry: CaptureEntry, isCurrent: Bool) {
        self.isCurrent = isCurrent
        titleLabel.stringValue = entry.name
        view.toolTip = entry.name
        updateBorder()

        let maxPixel = Self.maxPixel
        token = UUID()
        let expected = token
        if let cached = ThumbnailStore.shared.cached(entry, maxPixel: maxPixel) {
            thumbnail.image = cached
            return
        }
        thumbnail.image = nil
        ThumbnailStore.shared.load(entry, maxPixel: maxPixel) { [weak self] image in
            guard let self, self.token == expected else { return }
            self.thumbnail.image = image
        }
    }

    private func updateBorder() {
        let highlighted = isSelected || isCurrent
        view.layer?.borderWidth = highlighted ? 2 : 0.5
        view.layer?.borderColor = highlighted
            ? NSColor.controlAccentColor.cgColor
            : NSColor.separatorColor.cgColor
    }
}

@MainActor
final class GridCaptureItem: CaptureThumbnailItem {
    override class var identifier: NSUserInterfaceItemIdentifier { NSUserInterfaceItemIdentifier("overink.grid.item") }

    // AppKit only instantiates a registered item class directly when the class itself declares loadView.
    override func loadView() {
        super.loadView()
    }
}

@MainActor
final class StripCaptureItem: CaptureThumbnailItem {
    override class var thumbnailHeight: CGFloat { 60 }
    override class var identifier: NSUserInterfaceItemIdentifier { NSUserInterfaceItemIdentifier("overink.strip.item") }
    override class var maxPixel: Int { 240 }

    override func loadView() {
        super.loadView()
    }
}

// MARK: - Collection

@MainActor
final class LibraryCollectionView: NSCollectionView {
    var menuProvider: ((IndexPath) -> NSMenu?)?

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        guard let indexPath = indexPathForItem(at: point) else { return nil }
        if !selectionIndexPaths.contains(indexPath) {
            selectionIndexPaths = [indexPath]
        }
        return menuProvider?(indexPath)
    }
}

/// One thumbnail collection (the editor's recent strip, or the full grid) over a `LibraryModel`.
@MainActor
final class LibraryCollectionController: NSObject, NSCollectionViewDataSource, NSCollectionViewDelegateFlowLayout {
    enum Style {
        case grid
        case strip
    }

    let style: Style
    let scrollView = NSScrollView()
    let collectionView = LibraryCollectionView()

    var onOpen: ((URL) -> Void)?
    /// The capture open in the editor (highlighted in the strip).
    var currentURL: URL? {
        didSet {
            guard currentURL != oldValue else { return }
            refreshVisibleItems()
            if style == .strip { scrollCurrentIntoView() }
        }
    }

    private let model: LibraryModel
    private let actions: LibraryActions
    private var draggedURL: URL?

    private var entries: [CaptureEntry] {
        style == .grid ? model.entries : model.allEntries
    }

    var selectedEntry: CaptureEntry? {
        guard let index = collectionView.selectionIndexPaths.first?.item, entries.indices.contains(index) else {
            return nil
        }
        return entries[index]
    }

    init(style: Style, model: LibraryModel, actions: LibraryActions) {
        self.style = style
        self.model = model
        self.actions = actions
        super.init()
        configure()
        model.observe { [weak self] in self?.modelChanged() }
    }

    private func configure() {
        let layout = NSCollectionViewFlowLayout()
        switch style {
        case .grid:
            layout.itemSize = NSSize(width: 184, height: 148)
            layout.minimumInteritemSpacing = 12
            layout.minimumLineSpacing = 14
            layout.sectionInset = NSEdgeInsets(top: 12, left: 18, bottom: 18, right: 18)
        case .strip:
            layout.scrollDirection = .horizontal
            layout.itemSize = NSSize(width: 118, height: 90)
            layout.minimumInteritemSpacing = 8
            layout.minimumLineSpacing = 8
            layout.sectionInset = NSEdgeInsets(top: 6, left: 12, bottom: 6, right: 12)
        }
        collectionView.collectionViewLayout = layout
        switch style {
        case .grid:
            collectionView.register(GridCaptureItem.self, forItemWithIdentifier: GridCaptureItem.identifier)
        case .strip:
            collectionView.register(StripCaptureItem.self, forItemWithIdentifier: StripCaptureItem.identifier)
        }
        collectionView.dataSource = self
        collectionView.delegate = self
        collectionView.isSelectable = true
        collectionView.allowsMultipleSelection = false
        collectionView.backgroundColors = [.clear]
        collectionView.registerForDraggedTypes([.fileURL])
        collectionView.setDraggingSourceOperationMask([.copy, .move], forLocal: true)
        collectionView.setDraggingSourceOperationMask(.copy, forLocal: false)

        let click = NSClickGestureRecognizer(target: self, action: #selector(clicked(_:)))
        click.numberOfClicksRequired = style == .strip ? 1 : 2
        click.delaysPrimaryMouseButtonEvents = false
        collectionView.addGestureRecognizer(click)
        collectionView.menuProvider = { [weak self] in self?.menu(for: $0) }

        scrollView.drawsBackground = false
        scrollView.documentView = collectionView
        switch style {
        case .grid:
            scrollView.hasVerticalScroller = true
        case .strip:
            scrollView.hasHorizontalScroller = true
            scrollView.horizontalScrollElasticity = .allowed
            scrollView.verticalScrollElasticity = .none
        }
    }

    private func modelChanged() {
        let keep = selectedEntry?.url
        collectionView.reloadData()
        if let keep, let index = entries.firstIndex(where: { $0.url == keep }) {
            collectionView.selectionIndexPaths = [IndexPath(item: index, section: 0)]
        }
    }

    private func refreshVisibleItems() {
        for case let item as CaptureThumbnailItem in collectionView.visibleItems() {
            guard let path = collectionView.indexPath(for: item), entries.indices.contains(path.item) else { continue }
            let entry = entries[path.item]
            item.configure(entry, isCurrent: entry.url == currentURL)
        }
    }

    private func scrollCurrentIntoView() {
        guard let currentURL, let index = entries.firstIndex(where: { $0.url == currentURL }) else { return }
        collectionView.scrollToItems(at: [IndexPath(item: index, section: 0)], scrollPosition: .centeredHorizontally)
    }

    @objc private func clicked(_ gesture: NSClickGestureRecognizer) {
        let point = gesture.location(in: collectionView)
        guard let path = collectionView.indexPathForItem(at: point), entries.indices.contains(path.item) else { return }
        onOpen?(entries[path.item].url)
    }

    func openSelected() {
        if let entry = selectedEntry { onOpen?(entry.url) }
    }

    // MARK: Data source

    func numberOfSections(in collectionView: NSCollectionView) -> Int { 1 }

    func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
        entries.count
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        itemForRepresentedObjectAt indexPath: IndexPath
    ) -> NSCollectionViewItem {
        let identifier = style == .grid ? GridCaptureItem.identifier : StripCaptureItem.identifier
        let item = collectionView.makeItem(withIdentifier: identifier, for: indexPath)
        if let thumbnail = item as? CaptureThumbnailItem {
            let entry = entries[indexPath.item]
            thumbnail.configure(entry, isCurrent: entry.url == currentURL)
        }
        return item
    }

    // MARK: Menu

    private func menu(for indexPath: IndexPath) -> NSMenu? {
        guard entries.indices.contains(indexPath.item) else { return nil }
        let entry = entries[indexPath.item]
        let menu = NSMenu()
        func add(_ title: String, _ handler: @escaping () -> Void) {
            let item = ClosureMenuItem(title: title, handler: handler)
            menu.addItem(item)
        }
        add("Open") { [weak self] in self?.onOpen?(entry.url) }
        add("Rename…") { [actions] in actions.rename(entry) }
        add("Duplicate") { [actions] in actions.duplicate(entry) }
        add("Copy") { [actions] in actions.copy(entry) }
        add("Reveal in Finder") { [actions] in actions.reveal(entry) }
        menu.addItem(.separator())
        add("Move to Trash") { [actions] in actions.delete(entry) }
        return menu
    }

    // MARK: Drag and drop

    func collectionView(_ collectionView: NSCollectionView, canDragItemsAt indexPaths: Set<IndexPath>, with event: NSEvent) -> Bool {
        true
    }

    func collectionView(_ collectionView: NSCollectionView, pasteboardWriterForItemAt indexPath: IndexPath) -> NSPasteboardWriting? {
        guard entries.indices.contains(indexPath.item) else { return nil }
        return entries[indexPath.item].url as NSURL
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        draggingSession session: NSDraggingSession,
        willBeginAt screenPoint: NSPoint,
        forItemsAt indexPaths: Set<IndexPath>
    ) {
        draggedURL = indexPaths.first.flatMap { entries.indices.contains($0.item) ? entries[$0.item].url : nil }
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        draggingSession session: NSDraggingSession,
        endedAt screenPoint: NSPoint,
        dragOperation operation: NSDragOperation
    ) {
        draggedURL = nil
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        validateDrop draggingInfo: any NSDraggingInfo,
        proposedIndexPath proposedDropIndexPath: AutoreleasingUnsafeMutablePointer<NSIndexPath>,
        dropOperation proposedDropOperation: UnsafeMutablePointer<NSCollectionView.DropOperation>
    ) -> NSDragOperation {
        proposedDropOperation.pointee = .before
        if (draggingInfo.draggingSource as? NSCollectionView) === collectionView {
            // Reordering is only meaningful on the full, unfiltered list.
            return model.query.isEmpty || style == .strip ? .move : []
        }
        return droppedImageURLs(draggingInfo).isEmpty ? [] : .copy
    }

    func collectionView(
        _ collectionView: NSCollectionView,
        acceptDrop draggingInfo: any NSDraggingInfo,
        indexPath: IndexPath,
        dropOperation: NSCollectionView.DropOperation
    ) -> Bool {
        if (draggingInfo.draggingSource as? NSCollectionView) === collectionView {
            guard let draggedURL else { return false }
            model.move(draggedURL, beforeIndex: indexPath.item)
            return true
        }
        let urls = droppedImageURLs(draggingInfo)
        guard !urls.isEmpty else { return false }
        model.importImages(urls)
        return true
    }

    private func droppedImageURLs(_ info: NSDraggingInfo) -> [URL] {
        let urls = info.draggingPasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        ) as? [URL] ?? []
        return urls.filter(CapturePackagePaths.isFlattenedImage)
    }
}

/// An `NSMenuItem` that runs a closure.
@MainActor
final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(title: String, keyEquivalent: String = "", handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: keyEquivalent)
        target = self
    }

    @available(*, unavailable)
    required init(coder: NSCoder) {
        fatalError("init(coder:) is unavailable")
    }

    @objc private func run() {
        handler()
    }
}
