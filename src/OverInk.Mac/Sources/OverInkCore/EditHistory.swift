import Foundation

/// An undoable list of items (strokes, shapes, ...) keyed by `Identifiable`.
///
/// Mirrors the Windows `UndoStack`: add / remove / replace / clear are single undo steps, `batch`
/// groups several edits into one step, and every edit may carry an `author` so a remote client's
/// undo only touches its own work.
public struct EditHistory<Item: Identifiable & Sendable>: Sendable where Item.ID: Sendable {
    private indirect enum Edit: Sendable {
        case add(Item)
        case remove(Item, index: Int)
        case replace(old: Item, new: Item)
        case clear([Item])
        case batch([Edit])
    }

    private struct Entry: Sendable {
        var edit: Edit
        var author: String?
    }

    public private(set) var items: [Item] = []
    private var undoStack: [Entry] = []
    private var redoStack: [Entry] = []

    public init() {}

    public var canUndo: Bool { !undoStack.isEmpty }
    public var canRedo: Bool { !redoStack.isEmpty }

    public func canUndo(author: String?) -> Bool {
        undoStack.contains { author == nil || $0.author == author }
    }

    public func canRedo(author: String?) -> Bool {
        redoStack.contains { author == nil || $0.author == author }
    }

    // MARK: Mutations (each is one undo step)

    public mutating func append(_ item: Item, author: String? = nil) {
        record(.add(item), author: author)
    }

    /// Removes the item with `id`. Returns the removed item, if present.
    @discardableResult
    public mutating func remove(id: Item.ID, author: String? = nil) -> Item? {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return nil }
        let item = items[index]
        record(.remove(item, index: index), author: author)
        return item
    }

    /// Removes every item whose id is in `ids` as a single undo step.
    @discardableResult
    public mutating func remove(ids: [Item.ID], author: String? = nil) -> [Item] {
        let targets = Set(ids)
        var edits: [Edit] = []
        var removed: [Item] = []
        // Remove back-to-front so recorded indexes restore correctly in reverse.
        for index in items.indices.reversed() where targets.contains(items[index].id) {
            edits.append(.remove(items[index], index: index))
            removed.append(items[index])
        }
        guard !edits.isEmpty else { return [] }
        record(edits.count == 1 ? edits[0] : .batch(edits), author: author)
        return removed.reversed()
    }

    /// Replaces the stored item that has the same id as `item`.
    @discardableResult
    public mutating func replace(_ item: Item, author: String? = nil) -> Bool {
        guard let index = items.firstIndex(where: { $0.id == item.id }) else { return false }
        record(.replace(old: items[index], new: item), author: author)
        return true
    }

    /// Replaces several items (matched by id) as a single undo step.
    @discardableResult
    public mutating func replaceAll(_ replacements: [Item], author: String? = nil) -> Bool {
        var edits: [Edit] = []
        for item in replacements {
            if let index = items.firstIndex(where: { $0.id == item.id }) {
                edits.append(.replace(old: items[index], new: item))
            }
        }
        guard !edits.isEmpty else { return false }
        record(edits.count == 1 ? edits[0] : .batch(edits), author: author)
        return true
    }

    /// Removes `ids` and appends `added` as a single undo step (an erase that leaves pieces behind).
    /// Returns the removed items.
    @discardableResult
    public mutating func edit(removing ids: [Item.ID], adding added: [Item], author: String? = nil) -> [Item] {
        let targets = Set(ids)
        var edits: [Edit] = []
        var removed: [Item] = []
        for index in items.indices.reversed() where targets.contains(items[index].id) {
            edits.append(.remove(items[index], index: index))
            removed.append(items[index])
        }
        edits.append(contentsOf: added.map { Edit.add($0) })
        guard !edits.isEmpty else { return [] }
        record(edits.count == 1 ? edits[0] : .batch(edits), author: author)
        return removed.reversed()
    }

    public mutating func clear(author: String? = nil) {
        guard !items.isEmpty else { return }
        record(.clear(items), author: author)
    }

    // MARK: Undo / redo

    /// Undoes the most recent edit (by `author` when given). Returns whether anything changed.
    @discardableResult
    public mutating func undo(author: String? = nil) -> Bool {
        guard let position = undoStack.lastIndex(where: { author == nil || $0.author == author }) else {
            return false
        }
        let entry = undoStack.remove(at: position)
        applyInverse(entry.edit)
        redoStack.append(entry)
        return true
    }

    @discardableResult
    public mutating func redo(author: String? = nil) -> Bool {
        guard let position = redoStack.lastIndex(where: { author == nil || $0.author == author }) else {
            return false
        }
        let entry = redoStack.remove(at: position)
        apply(entry.edit)
        undoStack.append(entry)
        return true
    }

    /// Drops all history without touching the items (e.g. after loading a document).
    public mutating func resetHistory() {
        undoStack.removeAll()
        redoStack.removeAll()
    }

    /// Replaces the items without recording history and clears undo/redo.
    public mutating func load(_ newItems: [Item]) {
        items = newItems
        resetHistory()
    }

    // MARK: Internals

    private mutating func record(_ edit: Edit, author: String?) {
        apply(edit)
        undoStack.append(Entry(edit: edit, author: author))
        redoStack.removeAll()
    }

    private mutating func apply(_ edit: Edit) {
        switch edit {
        case let .add(item):
            items.append(item)
        case let .remove(item, _):
            items.removeAll { $0.id == item.id }
        case let .replace(old, new):
            if let index = items.firstIndex(where: { $0.id == old.id }) {
                items[index] = new
            }
        case .clear:
            items.removeAll()
        case let .batch(edits):
            edits.forEach { apply($0) }
        }
    }

    private mutating func applyInverse(_ edit: Edit) {
        switch edit {
        case let .add(item):
            items.removeAll { $0.id == item.id }
        case let .remove(item, index):
            items.insert(item, at: min(index, items.count))
        case let .replace(old, new):
            if let index = items.firstIndex(where: { $0.id == new.id }) {
                items[index] = old
            }
        case let .clear(removed):
            items = removed
        case let .batch(edits):
            edits.reversed().forEach { applyInverse($0) }
        }
    }
}
