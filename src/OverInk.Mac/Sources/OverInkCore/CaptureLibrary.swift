import Foundation

/// File layout of one capture: the flattened PNG plus `.orig.png` and `.overink.json` siblings.
public enum CapturePackagePaths {
    public static func original(for flattened: URL) -> URL {
        flattened.deletingPathExtension().appendingPathExtension("orig.png")
    }

    public static func document(for flattened: URL) -> URL {
        flattened.deletingPathExtension().appendingPathExtension("overink.json")
    }

    /// Every file that belongs to the capture.
    public static func allFiles(for flattened: URL) -> [URL] {
        [flattened, original(for: flattened), document(for: flattened)]
    }

    public static func isFlattenedImage(_ url: URL) -> Bool {
        url.pathExtension.lowercased() == "png" && !url.lastPathComponent.lowercased().hasSuffix(".orig.png")
    }
}

public struct CaptureEntry: Equatable, Sendable {
    public var url: URL
    public var modified: Date

    public init(url: URL, modified: Date) {
        self.url = url
        self.modified = modified
    }

    public var name: String { url.deletingPathExtension().lastPathComponent }
    public var isEditable: Bool {
        FileManager.default.fileExists(atPath: CapturePackagePaths.document(for: url).path)
    }
}

public enum CaptureLibraryError: Error, Equatable {
    case emptyName
    case missing
}

/// Manual ordering of the library, persisted beside the captures. Names not listed sort after the
/// listed ones, newest first.
public struct LibraryOrder: Codable, Equatable, Sendable {
    public var names: [String]

    public init(names: [String] = []) {
        self.names = names
    }

    public func apply(to entries: [CaptureEntry]) -> [CaptureEntry] {
        let byName = Dictionary(entries.map { ($0.url.lastPathComponent, $0) }, uniquingKeysWith: { first, _ in first })
        let ordered = names.compactMap { byName[$0] }
        let listed = Set(ordered.map(\.url))
        let rest = entries
            .filter { !listed.contains($0.url) }
            .sorted { $0.modified > $1.modified }
        return ordered + rest
    }

    /// Records `current` (the displayed order) after moving `name` to `destination` (an index in `current`).
    public static func moving(_ name: String, to destination: Int, in current: [String]) -> LibraryOrder {
        var names = current
        guard let from = names.firstIndex(of: name) else { return LibraryOrder(names: names) }
        names.remove(at: from)
        let index = min(max(destination, 0), names.count)
        names.insert(name, at: index)
        return LibraryOrder(names: names)
    }

    /// Moves `name` so it sits just before the item currently at `index` (a drop position).
    public static func moving(_ name: String, before index: Int, in current: [String]) -> LibraryOrder {
        guard let from = current.firstIndex(of: name) else { return LibraryOrder(names: current) }
        return moving(name, to: from < index ? index - 1 : index, in: current)
    }

    public func renaming(_ old: String, to new: String) -> LibraryOrder {
        LibraryOrder(names: names.map { $0 == old ? new : $0 })
    }

    public func removing(_ name: String) -> LibraryOrder {
        LibraryOrder(names: names.filter { $0 != name })
    }

    /// Inserts `name` right after `after` (used by duplicate).
    public func inserting(_ name: String, after: String) -> LibraryOrder {
        var copy = names
        copy.removeAll { $0 == name }
        if let index = copy.firstIndex(of: after) {
            copy.insert(name, at: index + 1)
        } else {
            copy.insert(name, at: 0)
        }
        return LibraryOrder(names: copy)
    }
}

public struct CaptureLibrary: Sendable {
    public static let orderFileName = ".overink-order.json"

    public let folder: URL

    public init(folder: URL) {
        self.folder = folder
    }

    /// Resolves the library folder from the setting (`nil`/empty means `Pictures/OverInk`).
    public static func defaultFolder() -> URL {
        let pictures = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Pictures")
        return pictures.appendingPathComponent("OverInk", isDirectory: true)
    }

    public static func folder(forSetting path: String?) -> URL {
        guard let path, !path.trimmingCharacters(in: .whitespaces).isEmpty else { return defaultFolder() }
        return URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
    }

    public func ensureFolder() throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    // MARK: Listing

    public func entries() -> [CaptureEntry] {
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: folder,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        )) ?? []
        let found = urls.compactMap { url -> CaptureEntry? in
            guard CapturePackagePaths.isFlattenedImage(url) else { return nil }
            let date = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            return CaptureEntry(url: url, modified: date ?? .distantPast)
        }
        return loadOrder().apply(to: found)
    }

    public static func filter(_ entries: [CaptureEntry], matching query: String) -> [CaptureEntry] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return entries }
        return entries.filter { $0.name.localizedCaseInsensitiveContains(trimmed) }
    }

    // MARK: Order

    private var orderURL: URL { folder.appendingPathComponent(Self.orderFileName) }

    public func loadOrder() -> LibraryOrder {
        guard let data = try? Data(contentsOf: orderURL),
              let order = try? JSONDecoder().decode(LibraryOrder.self, from: data) else {
            return LibraryOrder()
        }
        return order
    }

    public func saveOrder(_ order: LibraryOrder) {
        guard let data = try? JSONEncoder().encode(order) else { return }
        try? data.write(to: orderURL, options: .atomic)
    }

    // MARK: File operations

    /// Strips path separators and trailing dots, so a typed name stays a single file in the folder.
    public static func sanitized(_ name: String) -> String {
        var cleaned = name
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if cleaned.lowercased().hasSuffix(".png") { cleaned = String(cleaned.dropLast(4)) }
        while cleaned.hasPrefix(".") { cleaned.removeFirst() }
        return cleaned.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// `base.png`, or `base-2.png`, `base-3.png`… when taken (also by a leftover sidecar).
    public func uniqueURL(base: String, excluding: URL? = nil) -> URL {
        var candidate = folder.appendingPathComponent("\(base).png")
        var counter = 2
        while Self.isTaken(candidate, excluding: excluding) {
            candidate = folder.appendingPathComponent("\(base)-\(counter).png")
            counter += 1
        }
        return candidate
    }

    private static func isTaken(_ url: URL, excluding: URL?) -> Bool {
        if let excluding, url.standardizedFileURL == excluding.standardizedFileURL { return false }
        return CapturePackagePaths.allFiles(for: url).contains {
            FileManager.default.fileExists(atPath: $0.path)
        }
    }

    @discardableResult
    public func rename(_ url: URL, to newName: String) throws -> URL {
        let name = Self.sanitized(newName)
        guard !name.isEmpty else { throw CaptureLibraryError.emptyName }
        guard FileManager.default.fileExists(atPath: url.path) else { throw CaptureLibraryError.missing }
        if name == url.deletingPathExtension().lastPathComponent { return url }

        let destination = uniqueURL(base: name, excluding: url)
        try moveOrCopy(url, to: destination, move: true)
        saveOrder(loadOrder().renaming(url.lastPathComponent, to: destination.lastPathComponent))
        return destination
    }

    @discardableResult
    public func duplicate(_ url: URL) throws -> URL {
        guard FileManager.default.fileExists(atPath: url.path) else { throw CaptureLibraryError.missing }
        let destination = uniqueURL(base: url.deletingPathExtension().lastPathComponent + " copy")
        try moveOrCopy(url, to: destination, move: false)
        saveOrder(entriesOrder().inserting(destination.lastPathComponent, after: url.lastPathComponent))
        return destination
    }

    /// Copies an image file into the library (unless it is already there). Returns the library URL.
    @discardableResult
    public func importImage(from source: URL) throws -> URL {
        if source.deletingLastPathComponent().standardizedFileURL == folder.standardizedFileURL { return source }
        guard CapturePackagePaths.isFlattenedImage(source) else { throw CaptureLibraryError.missing }
        try ensureFolder()
        let destination = uniqueURL(base: source.deletingPathExtension().lastPathComponent)
        try FileManager.default.copyItem(at: source, to: destination)
        return destination
    }

    public func delete(_ url: URL) throws {
        for file in CapturePackagePaths.allFiles(for: url) where FileManager.default.fileExists(atPath: file.path) {
            try FileManager.default.trashItem(at: file, resultingItemURL: nil)
        }
        saveOrder(loadOrder().removing(url.lastPathComponent))
    }

    /// The full displayed order (so an inserted name lands after its neighbour even if never reordered).
    private func entriesOrder() -> LibraryOrder {
        LibraryOrder(names: entries().map { $0.url.lastPathComponent })
    }

    private func moveOrCopy(_ source: URL, to destination: URL, move: Bool) throws {
        let pairs = zip(CapturePackagePaths.allFiles(for: source), CapturePackagePaths.allFiles(for: destination))
        for (from, to) in pairs where FileManager.default.fileExists(atPath: from.path) {
            if move {
                try FileManager.default.moveItem(at: from, to: to)
            } else {
                try FileManager.default.copyItem(at: from, to: to)
            }
        }
    }
}
