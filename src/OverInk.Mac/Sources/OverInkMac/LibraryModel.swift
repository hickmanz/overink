import AppKit
import OverInkCore

/// Watches a directory for added, removed, or renamed files and reports changes after a short debounce.
@MainActor
final class DirectoryWatcher {
    private var source: DispatchSourceFileSystemObject?
    private var pending: DispatchWorkItem?
    private let onChange: @MainActor () -> Void

    init?(url: URL, onChange: @escaping @MainActor () -> Void) {
        self.onChange = onChange
        let descriptor = open(url.path, O_EVTONLY)
        guard descriptor >= 0 else { return nil }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: descriptor,
            eventMask: [.write, .rename, .delete, .link, .extend],
            queue: .main
        )
        source.setEventHandler { [weak self] in
            MainActor.assumeIsolated { self?.scheduleChange() }
        }
        source.setCancelHandler { close(descriptor) }
        source.resume()
        self.source = source
    }

    private func scheduleChange() {
        pending?.cancel()
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated { self?.onChange() }
        }
        pending = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
    }

    func stop() {
        pending?.cancel()
        source?.cancel()
        source = nil
    }
}

/// The capture library as the editor and grid see it: the entry list (kept fresh by a folder watcher),
/// the search query, and file operations.
@MainActor
final class LibraryModel {
    private(set) var library: CaptureLibrary
    private(set) var allEntries: [CaptureEntry] = []
    private var observers: [() -> Void] = []
    private var watcher: DirectoryWatcher?

    var query = "" {
        didSet {
            guard query != oldValue else { return }
            notify()
        }
    }

    /// Entries matching the search query.
    var entries: [CaptureEntry] {
        CaptureLibrary.filter(allEntries, matching: query)
    }

    init(folder: URL) {
        library = CaptureLibrary(folder: folder)
        startWatching()
        reload()
    }

    func observe(_ observer: @escaping () -> Void) {
        observers.append(observer)
    }

    /// Points the library at a different folder (the library-folder setting changed).
    func retarget(folder: URL) {
        guard folder.standardizedFileURL != library.folder.standardizedFileURL else { return }
        watcher?.stop()
        library = CaptureLibrary(folder: folder)
        startWatching()
        reload()
    }

    func reload() {
        allEntries = library.entries()
        notify()
    }

    private func startWatching() {
        try? library.ensureFolder()
        watcher = DirectoryWatcher(url: library.folder) { [weak self] in
            self?.reload()
        }
    }

    private func notify() {
        observers.forEach { $0() }
    }

    func index(of url: URL, filtered: Bool = true) -> Int? {
        (filtered ? entries : allEntries).firstIndex { $0.url == url }
    }

    // MARK: Operations

    func move(_ url: URL, beforeIndex index: Int) {
        let names = allEntries.map { $0.url.lastPathComponent }
        library.saveOrder(LibraryOrder.moving(url.lastPathComponent, before: index, in: names))
        reload()
    }

    @discardableResult
    func rename(_ url: URL, to name: String) throws -> URL {
        let renamed = try library.rename(url, to: name)
        reload()
        return renamed
    }

    @discardableResult
    func duplicate(_ url: URL) throws -> URL {
        let copy = try library.duplicate(url)
        reload()
        return copy
    }

    func delete(_ url: URL) throws {
        try library.delete(url)
        reload()
    }

    func importImages(_ urls: [URL]) {
        for url in urls {
            _ = try? library.importImage(from: url)
        }
        reload()
    }
}
