import Foundation
import Testing
@testable import OverInkCore

@Suite struct CaptureLibraryTests {
    private func makeLibrary() throws -> CaptureLibrary {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("overink-lib-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return CaptureLibrary(folder: folder)
    }

    @discardableResult
    private func addCapture(_ library: CaptureLibrary, _ name: String, age: TimeInterval = 0, sidecars: Bool = true) throws -> URL {
        let url = library.folder.appendingPathComponent("\(name).png")
        try Data("png".utf8).write(to: url)
        if sidecars {
            try Data("orig".utf8).write(to: CapturePackagePaths.original(for: url))
            try Data("{}".utf8).write(to: CapturePackagePaths.document(for: url))
        }
        let date = Date(timeIntervalSinceNow: -age)
        try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: url.path)
        return url
    }

    @Test func listsFlattenedImagesNewestFirstAndIgnoresSidecars() throws {
        let library = try makeLibrary()
        try addCapture(library, "old", age: 100)
        try addCapture(library, "new", age: 1)
        try Data("x".utf8).write(to: library.folder.appendingPathComponent("notes.txt"))

        let names = library.entries().map(\.name)
        #expect(names == ["new", "old"])
    }

    @Test func manualOrderWinsThenNewestFirst() throws {
        let library = try makeLibrary()
        try addCapture(library, "a", age: 30)
        try addCapture(library, "b", age: 20)
        try addCapture(library, "c", age: 10)
        library.saveOrder(LibraryOrder(names: ["a.png"]))
        #expect(library.entries().map(\.name) == ["a", "c", "b"])
    }

    @Test func moveWithinOrder() {
        let order = LibraryOrder.moving("c.png", to: 0, in: ["a.png", "b.png", "c.png"])
        #expect(order.names == ["c.png", "a.png", "b.png"])
        let clamped = LibraryOrder.moving("a.png", to: 99, in: ["a.png", "b.png"])
        #expect(clamped.names == ["b.png", "a.png"])
    }

    @Test func dropBeforeIndexAccountsForRemoval() {
        let names = ["a.png", "b.png", "c.png", "d.png"]
        #expect(LibraryOrder.moving("a.png", before: 3, in: names).names == ["b.png", "c.png", "a.png", "d.png"])
        #expect(LibraryOrder.moving("d.png", before: 1, in: names).names == ["a.png", "d.png", "b.png", "c.png"])
        #expect(LibraryOrder.moving("b.png", before: 4, in: names).names == ["a.png", "c.png", "d.png", "b.png"])
    }

    @Test func importCopiesPlainPngsOnce() throws {
        let library = try makeLibrary()
        let elsewhere = FileManager.default.temporaryDirectory.appendingPathComponent("drop-\(UUID().uuidString).png")
        try Data("png".utf8).write(to: elsewhere)
        let imported = try library.importImage(from: elsewhere)
        #expect(imported.deletingLastPathComponent().standardizedFileURL == library.folder.standardizedFileURL)
        #expect(try library.importImage(from: imported) == imported)
        #expect(library.entries().count == 1)
        #expect(throws: CaptureLibraryError.self) {
            try library.importImage(from: URL(fileURLWithPath: "/tmp/not-an-image.txt"))
        }
    }

    @Test func renameMovesWholePackageAndOrder() throws {
        let library = try makeLibrary()
        let url = try addCapture(library, "shot")
        library.saveOrder(LibraryOrder(names: ["shot.png"]))

        let renamed = try library.rename(url, to: "Bug report")
        #expect(renamed.lastPathComponent == "Bug report.png")
        #expect(FileManager.default.fileExists(atPath: CapturePackagePaths.original(for: renamed).path))
        #expect(FileManager.default.fileExists(atPath: CapturePackagePaths.document(for: renamed).path))
        #expect(!FileManager.default.fileExists(atPath: url.path))
        #expect(library.loadOrder().names == ["Bug report.png"])
    }

    @Test func renameAvoidsCollisionsAndSanitizes() throws {
        let library = try makeLibrary()
        let first = try addCapture(library, "one")
        try addCapture(library, "two")
        let renamed = try library.rename(first, to: "two")
        #expect(renamed.lastPathComponent == "two-2.png")
        let slashed = try library.rename(renamed, to: "a/b.png")
        #expect(slashed.lastPathComponent == "a-b.png")
    }

    @Test func renameRejectsEmptyName() throws {
        let library = try makeLibrary()
        let url = try addCapture(library, "one")
        #expect(throws: CaptureLibraryError.emptyName) { try library.rename(url, to: "  ") }
    }

    @Test func renameWithSameNameIsNoOp() throws {
        let library = try makeLibrary()
        let url = try addCapture(library, "same")
        #expect(try library.rename(url, to: "same") == url)
    }

    @Test func duplicateCopiesPackageNextToOriginal() throws {
        let library = try makeLibrary()
        let url = try addCapture(library, "shot", age: 5)
        try addCapture(library, "other", age: 1)
        let copy = try library.duplicate(url)
        #expect(copy.lastPathComponent == "shot copy.png")
        #expect(FileManager.default.fileExists(atPath: CapturePackagePaths.document(for: copy).path))
        #expect(FileManager.default.fileExists(atPath: url.path))
        #expect(library.entries().map(\.name) == ["other", "shot", "shot copy"])
    }

    @Test func plainPngHasNoDocument() throws {
        let library = try makeLibrary()
        try addCapture(library, "plain", sidecars: false)
        let entry = try #require(library.entries().first)
        #expect(!entry.isEditable)
    }

    @Test func filterMatchesCaseInsensitively() {
        let entries = [
            CaptureEntry(url: URL(fileURLWithPath: "/x/Bug Report.png"), modified: .now),
            CaptureEntry(url: URL(fileURLWithPath: "/x/Lunch.png"), modified: .now)
        ]
        #expect(CaptureLibrary.filter(entries, matching: "bug").map(\.name) == ["Bug Report"])
        #expect(CaptureLibrary.filter(entries, matching: "  ").count == 2)
    }

    @Test func folderSettingResolution() {
        #expect(CaptureLibrary.folder(forSetting: nil) == CaptureLibrary.defaultFolder())
        #expect(CaptureLibrary.folder(forSetting: "  ") == CaptureLibrary.defaultFolder())
        #expect(CaptureLibrary.folder(forSetting: "/tmp/caps").path == "/tmp/caps")
    }
}

@Suite struct DocumentVersionTests {
    @Test func versionOneDocumentsStillLoadWithoutGroups() throws {
        let json = """
        {"version":1,"canvasWidth":100,"canvasHeight":50,"veilOpacity":0.2,
         "strokes":[{"id":"\(UUID().uuidString)","colorHex":"#FF0000","width":4,
         "points":[{"x":1,"y":2,"pressure":0.5}],"ignoresPressure":false}]}
        """
        var document = try JSONDecoder().decode(CaptureDocument.self, from: Data(json.utf8))
        #expect(document.version == 1)
        #expect(document.strokes[0].groupId == nil)
        document.upgradeToCurrentVersion()
        #expect(document.version == 2)
    }

    @Test func groupIdRoundTrips() throws {
        let group = UUID()
        let doc = CaptureDocument(
            canvasWidth: 10, canvasHeight: 10,
            strokes: [InkStroke(colorHex: "#000000", width: 2, points: [], groupId: group)]
        )
        let decoded = try JSONDecoder().decode(CaptureDocument.self, from: JSONEncoder().encode(doc))
        #expect(decoded.strokes[0].groupId == group)
        #expect(decoded.version == 2)
    }
}
