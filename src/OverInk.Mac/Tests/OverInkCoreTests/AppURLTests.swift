import Foundation
import Testing
@testable import OverInkCore

@Suite struct AppURLTests {
    private func parse(_ string: String) -> AppURLAction? {
        AppURL.parse(URL(string: string)!)
    }

    @Test func parsesKnownActions() {
        #expect(parse("overink://editor") == .editor(capture: nil))
        #expect(parse("overink://library") == .library)
        #expect(parse("overink://settings") == .settings)
        #expect(parse("overink://draw") == .draw)
        #expect(parse("OVERINK://Editor") == .editor(capture: nil))
        #expect(parse("overink:///library") == .library)
    }

    @Test func editorCarriesCaptureName() {
        #expect(parse("overink://editor?capture=shot.png") == .editor(capture: "shot.png"))
        #expect(parse("overink://editor?path=%2FUsers%2Fme%2Fa.png") == .editor(capture: "/Users/me/a.png"))
        #expect(parse("overink://editor?capture=") == .editor(capture: nil))
    }

    @Test func rejectsUnknownAndForeignLinks() {
        #expect(parse("overink://capture") == nil)
        #expect(parse("overink://delete?capture=x.png") == nil)
        #expect(parse("https://example.com/editor") == nil)
        #expect(parse("overink://") == nil)
    }

    @Test func resolvesOnlyFilesInsideTheLibrary() throws {
        let library = FileManager.default.temporaryDirectory
            .appendingPathComponent("overink-url-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: library, withIntermediateDirectories: true)
        let shot = library.appendingPathComponent("shot.png")
        try Data("x".utf8).write(to: shot)
        let outside = library.deletingLastPathComponent().appendingPathComponent("outside-\(UUID().uuidString).png")
        try Data("x".utf8).write(to: outside)

        #expect(AppURL.resolveCapture("shot.png", in: library) == shot.standardizedFileURL)
        #expect(AppURL.resolveCapture(shot.path, in: library) == shot.standardizedFileURL)
        #expect(AppURL.resolveCapture("../\(outside.lastPathComponent)", in: library) == nil)
        #expect(AppURL.resolveCapture(outside.path, in: library) == nil)
        #expect(AppURL.resolveCapture("missing.png", in: library) == nil)
        #expect(AppURL.resolveCapture("shot.orig.png", in: library) == nil)
    }
}
