import Foundation
import Testing
@testable import OverInkCore

@Suite struct RemoteLibraryTests {
    private func document(strokes: [InkStroke]) -> CaptureDocument {
        // A 1000x500-point canvas stored for a 2000x1000-pixel (2x Retina) image.
        CaptureDocument(canvasWidth: 1000, canvasHeight: 500, veilOpacity: 0.2, strokes: strokes)
    }

    @Test func strokesScaleFromCanvasPointsToImagePixels() throws {
        let stroke = InkStroke(
            colorHex: "#112233",
            width: 4,
            points: [InkPoint(x: 10, y: 20, pressure: 0.5), InkPoint(x: 100, y: 200, pressure: 1)],
            ignoresPressure: true
        )
        let result = RemoteLibraryConversion.strokes(from: document(strokes: [stroke]), pixelWidth: 2000, pixelHeight: 1000)

        let dto = try #require(result.first)
        #expect(dto.pts == [20, 40, 0.5, 200, 400, 1])
        #expect(dto.size == 8)
        #expect(dto.color == "#112233")
        #expect(dto.ignorePressure)
        #expect(dto.group == nil)
    }

    @Test func roundTripReturnsTheOriginalStrokes() throws {
        let group = UUID()
        let stroke = InkStroke(
            colorHex: "#E53935",
            width: 6,
            points: [InkPoint(x: 1.5, y: 2.5, pressure: 0.25), InkPoint(x: 300, y: 120, pressure: 0.75)],
            groupId: group
        )
        let doc = document(strokes: [stroke])
        let dtos = RemoteLibraryConversion.strokes(from: doc, pixelWidth: 2000, pixelHeight: 1000)
        let back = RemoteLibraryConversion.inkStrokes(from: dtos, document: doc, pixelWidth: 2000, pixelHeight: 1000)

        let restored = try #require(back.first)
        #expect(restored.colorHex == "#E53935")
        #expect(restored.width == 6)
        #expect(restored.groupId == group)
        #expect(restored.points.count == 2)
        for (a, b) in zip(restored.points, stroke.points) {
            #expect(abs(a.x - b.x) < 1e-4)
            #expect(abs(a.y - b.y) < 1e-4)
            #expect(a.pressure == b.pressure)
        }
    }

    @Test func emptyStrokesAreNotSent() {
        let doc = document(strokes: [InkStroke(colorHex: "#000000", width: 2, points: [])])
        #expect(RemoteLibraryConversion.strokes(from: doc, pixelWidth: 2000, pixelHeight: 1000).isEmpty)
    }

    @Test func incomingStrokesAreSanitized() {
        let doc = document(strokes: [])
        let incoming = [
            RemoteLibraryStroke(
                color: "red",
                size: 100_000,
                pts: [10, 10, 0.5, .nan, 5, 0.5, 20, 20, 7],
                ignorePressure: false
            ),
            RemoteLibraryStroke(color: "#FFFFFF", size: 4, pts: [.nan, .nan, 1], ignorePressure: false)
        ]
        let result = RemoteLibraryConversion.inkStrokes(from: incoming, document: doc, pixelWidth: 2000, pixelHeight: 1000)

        #expect(result.count == 1)
        #expect(result[0].colorHex == "#E53935")
        #expect(result[0].width == 200)
        #expect(result[0].points.count == 2)
        #expect(result[0].points[1].pressure == 1)
    }

    @Test func sharedGroupStringsMapToOneGroupId() {
        let doc = document(strokes: [])
        func stroke(_ group: String?) -> RemoteLibraryStroke {
            RemoteLibraryStroke(color: "#000000", size: 2, pts: [1, 1, 1], ignorePressure: false, group: group)
        }
        let result = RemoteLibraryConversion.inkStrokes(
            from: [stroke("g1"), stroke("g1"), stroke("g2"), stroke(nil)],
            document: doc, pixelWidth: 1000, pixelHeight: 500
        )
        #expect(result[0].groupId != nil)
        #expect(result[0].groupId == result[1].groupId)
        #expect(result[2].groupId != result[0].groupId)
        #expect(result[3].groupId == nil)
    }

    @Test func zeroCanvasSizeDoesNotDivideByZero() {
        let doc = CaptureDocument(canvasWidth: 0, canvasHeight: 0, strokes: [])
        let dto = RemoteLibraryStroke(color: "#000000", size: 2, pts: [5, 6, 1], ignorePressure: false)
        let result = RemoteLibraryConversion.inkStrokes(from: [dto], document: doc, pixelWidth: 100, pixelHeight: 100)
        #expect(result.first?.points.first?.x == 5)
    }

    @Test func validationRejectsBadStrokes() {
        func stroke(pts: [Float], size: Double = 4) -> RemoteLibraryStroke {
            RemoteLibraryStroke(color: "#000000", size: size, pts: pts, ignorePressure: false)
        }
        #expect(RemoteLibraryConversion.validate([stroke(pts: [1, 2, 3])]) == nil)
        #expect(RemoteLibraryConversion.validate([stroke(pts: [1, 2])]) == "Invalid stroke points.")
        #expect(RemoteLibraryConversion.validate([stroke(pts: [1, 2, 3, 4])]) == "Invalid stroke points.")
        #expect(RemoteLibraryConversion.validate([stroke(pts: [1, 2, 3], size: .infinity)]) == "Invalid stroke size.")
        #expect(RemoteLibraryConversion.validate([stroke(pts: [1, 2, 3], size: .nan)]) == "Invalid stroke size.")

        let many = Array(repeating: stroke(pts: [1, 2, 3]), count: RemoteLibraryConversion.maxStrokes + 1)
        #expect(RemoteLibraryConversion.validate(many) == "Too many strokes.")
    }

    @Test func saveBodyDecodesLikeTheWebClientSendsIt() throws {
        let json = ##"{"baseRev":"r1","veil":0.25,"strokes":[{"color":"#112233","size":8,"pts":[1,2,0.5],"ignorePressure":true,"group":"g"}]}"##
        let save = try JSONDecoder().decode(RemoteLibrarySave.self, from: Data(json.utf8))
        #expect(save.baseRev == "r1")
        #expect(save.veil == 0.25)
        #expect(save.strokes.first?.group == "g")

        let minimal = ##"{"baseRev":"r1","strokes":[{"size":4,"pts":[1,2,1]}]}"##
        let bare = try JSONDecoder().decode(RemoteLibrarySave.self, from: Data(minimal.utf8))
        #expect(bare.veil == nil)
        #expect(bare.strokes.first?.ignorePressure == false)
    }

    @Test func itemAndStrokesResponsesUseTheClientKeys() throws {
        let item = RemoteLibraryItem(id: "a", name: "A", createdAt: 5, w: 10, h: 20, rev: "r", editable: true)
        let object = try #require(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(item)) as? [String: Any]
        )
        #expect(Set(object.keys) == ["id", "name", "createdAt", "w", "h", "rev", "editable"])

        let strokes = RemoteLibraryStrokes(rev: "r", w: 1, h: 2, strokes: [], veil: 0.1)
        let keys = try #require(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(strokes)) as? [String: Any]
        ).keys
        #expect(Set(keys) == ["rev", "w", "h", "strokes", "veil"])
    }

    @Test func revisionChangesWithAnyFile() {
        let t = Date(timeIntervalSince1970: 100)
        let base = RemoteLibraryRevision.make(files: [(t, 10), (t, 20), (nil, nil)])
        #expect(base == "100000-10.100000-20.0-0")
        #expect(RemoteLibraryRevision.make(files: [(t, 10), (t, 21), (nil, nil)]) != base)
        #expect(RemoteLibraryRevision.make(files: [(t, 10), (t, 20), (t, 1)]) != base)
    }

    @Test func hexColorValidation() {
        #expect(HexColor.isValid("#aBc123"))
        #expect(!HexColor.isValid("#abc"))
        #expect(!HexColor.isValid("abc123"))
        #expect(!HexColor.isValid("#GGGGGG"))
        #expect(HexColor.normalized("#aabbcc") == "#AABBCC")
        #expect(HexColor.normalized("nope") == "#E53935")
    }
}
