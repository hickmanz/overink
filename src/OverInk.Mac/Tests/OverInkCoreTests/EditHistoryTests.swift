import Foundation
import Testing
@testable import OverInkCore

private struct Thing: Identifiable, Equatable, Sendable {
    var id: Int
    var label: String
}

@Suite struct EditHistoryTests {
    @Test func addUndoRedo() {
        var history = EditHistory<Thing>()
        history.append(Thing(id: 1, label: "a"))
        history.append(Thing(id: 2, label: "b"))

        let result1 = history.undo()

        #expect(result1)
        #expect(history.items.map(\.id) == [1])
        let result2 = history.redo()
        #expect(result2)
        #expect(history.items.map(\.id) == [1, 2])
    }

    @Test func newEditClearsRedo() {
        var history = EditHistory<Thing>()
        history.append(Thing(id: 1, label: "a"))
        history.undo()
        history.append(Thing(id: 2, label: "b"))

        #expect(!history.canRedo)
        let result3 = history.redo()
        #expect(!result3)
    }

    @Test func removeRestoresOriginalIndex() {
        var history = EditHistory<Thing>()
        for id in 1 ... 3 { history.append(Thing(id: id, label: "")) }
        history.remove(id: 2)
        #expect(history.items.map(\.id) == [1, 3])

        history.undo()
        #expect(history.items.map(\.id) == [1, 2, 3])
    }

    @Test func replaceUndoRestoresOldValue() {
        var history = EditHistory<Thing>()
        history.append(Thing(id: 1, label: "old"))
        let result4 = history.replace(Thing(id: 1, label: "new"))
        #expect(result4)
        #expect(history.items.first?.label == "new")

        history.undo()
        #expect(history.items.first?.label == "old")
        history.redo()
        #expect(history.items.first?.label == "new")
    }

    @Test func clearIsOneUndoStep() {
        var history = EditHistory<Thing>()
        for id in 1 ... 3 { history.append(Thing(id: id, label: "")) }
        history.clear()
        #expect(history.items.isEmpty)

        history.undo()
        #expect(history.items.map(\.id) == [1, 2, 3])
    }

    @Test func batchRemoveIsOneStepAndRestoresOrder() {
        var history = EditHistory<Thing>()
        for id in 1 ... 5 { history.append(Thing(id: id, label: "")) }
        let removed = history.remove(ids: [2, 4, 5])
        #expect(removed.map(\.id) == [2, 4, 5])
        #expect(history.items.map(\.id) == [1, 3])

        history.undo()
        #expect(history.items.map(\.id) == [1, 2, 3, 4, 5])
        history.redo()
        #expect(history.items.map(\.id) == [1, 3])
    }

    @Test func replaceAllIsOneStep() {
        var history = EditHistory<Thing>()
        history.append(Thing(id: 1, label: "a"))
        history.append(Thing(id: 2, label: "b"))
        history.replaceAll([Thing(id: 1, label: "A"), Thing(id: 2, label: "B")])

        history.undo()
        #expect(history.items.map(\.label) == ["a", "b"])
    }

    @Test func authorScopedUndoOnlyTouchesThatAuthor() {
        var history = EditHistory<Thing>()
        history.append(Thing(id: 1, label: "mine"), author: "me")
        history.append(Thing(id: 2, label: "theirs"), author: "ipad")
        history.append(Thing(id: 3, label: "mine"), author: "me")

        let result5 = history.undo(author: "ipad")

        #expect(result5)
        #expect(history.items.map(\.id) == [1, 3])
        let result6 = history.undo(author: "ipad")
        #expect(!result6)
        #expect(history.canUndo(author: "me"))
    }

    @Test func loadResetsHistory() {
        var history = EditHistory<Thing>()
        history.append(Thing(id: 1, label: ""))
        history.load([Thing(id: 9, label: "")])

        #expect(history.items.map(\.id) == [9])
        #expect(!history.canUndo)
    }
}
