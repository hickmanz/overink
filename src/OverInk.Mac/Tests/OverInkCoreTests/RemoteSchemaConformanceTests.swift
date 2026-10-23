import Foundation
import Testing
@testable import OverInkCore

/// A validator for the small part of JSON Schema that `shared/protocol/remote-v1.schema.json` uses
/// (`$ref`, `type`, `const`, `enum`, `required`, `properties`, `items`, length/size/range limits,
/// `pattern`, `oneOf`, `anyOf`, `allOf`). Unknown keywords are ignored.
struct MiniSchema {
    let root: [String: Any]

    init(root: [String: Any]) {
        self.root = root
    }

    static func load() throws -> MiniSchema {
        let url = repositoryRoot().appendingPathComponent("shared/protocol/remote-v1.schema.json")
        let data = try Data(contentsOf: url)
        let object = try JSONSerialization.jsonObject(with: data)
        return MiniSchema(root: object as? [String: Any] ?? [:])
    }

    static func repositoryRoot() -> URL {
        // .../src/OverInk.Mac/Tests/OverInkCoreTests/<this file> -> repository root.
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }

    /// Problems found validating `json` against `#/$defs/<name>` (empty when valid).
    func validate(_ json: Data, against name: String) -> [String] {
        guard let value = try? JSONSerialization.jsonObject(with: json, options: [.fragmentsAllowed]) else {
            return ["not JSON"]
        }
        return validate(value, schema: ["$ref": "#/$defs/\(name)"], path: "$")
    }

    func definition(_ name: String) -> [String: Any]? {
        (root["$defs"] as? [String: Any])?[name] as? [String: Any]
    }

    func validate(_ value: Any, schema: [String: Any], path: String) -> [String] {
        var problems: [String] = []

        if let ref = schema["$ref"] as? String {
            guard ref.hasPrefix("#/$defs/"), let target = definition(String(ref.dropFirst("#/$defs/".count))) else {
                return ["\(path): unresolved \(ref)"]
            }
            problems += validate(value, schema: target, path: path)
        }

        if let type = schema["type"] {
            let names = (type as? [String]) ?? [type as? String ?? ""]
            if !names.contains(where: { matches(value, type: $0) }) {
                return problems + ["\(path): expected \(names.joined(separator: "|"))"]
            }
        }
        if let const = schema["const"], !equal(value, const) {
            problems.append("\(path): expected constant \(const)")
        }
        if let options = schema["enum"] as? [Any], !options.contains(where: { equal(value, $0) }) {
            problems.append("\(path): not one of the allowed values")
        }

        if let number = number(value) {
            if let minimum = schema["minimum"] as? Double, number < minimum { problems.append("\(path): below minimum") }
            if let maximum = schema["maximum"] as? Double, number > maximum { problems.append("\(path): above maximum") }
            if let exclusive = schema["exclusiveMinimum"] as? Double, number <= exclusive {
                problems.append("\(path): not above \(exclusive)")
            }
        }

        if let text = value as? String {
            if let minimum = schema["minLength"] as? Int, text.count < minimum { problems.append("\(path): too short") }
            if let maximum = schema["maxLength"] as? Int, text.count > maximum { problems.append("\(path): too long") }
            if let pattern = schema["pattern"] as? String,
               text.range(of: pattern, options: .regularExpression) == nil {
                problems.append("\(path): does not match \(pattern)")
            }
        }

        if let array = value as? [Any] {
            if let minimum = schema["minItems"] as? Int, array.count < minimum { problems.append("\(path): too few items") }
            if let maximum = schema["maxItems"] as? Int, array.count > maximum { problems.append("\(path): too many items") }
            if let items = schema["items"] as? [String: Any] {
                for (index, item) in array.enumerated() {
                    problems += validate(item, schema: items, path: "\(path)[\(index)]")
                }
            }
        }

        if let object = value as? [String: Any] {
            for key in (schema["required"] as? [String]) ?? [] where object[key] == nil {
                problems.append("\(path): missing \(key)")
            }
            for (key, sub) in (schema["properties"] as? [String: Any]) ?? [:] {
                if let child = object[key], let sub = sub as? [String: Any] {
                    problems += validate(child, schema: sub, path: "\(path).\(key)")
                }
            }
        }

        if let all = schema["allOf"] as? [[String: Any]] {
            for sub in all { problems += validate(value, schema: sub, path: path) }
        }
        if let any = schema["anyOf"] as? [[String: Any]],
           !any.contains(where: { validate(value, schema: $0, path: path).isEmpty }) {
            problems.append("\(path): matches none of anyOf")
        }
        if let one = schema["oneOf"] as? [[String: Any]] {
            let matching = one.filter { validate(value, schema: $0, path: path).isEmpty }.count
            if matching != 1 { problems.append("\(path): matches \(matching) of oneOf, expected exactly 1") }
        }
        return problems
    }

    private func matches(_ value: Any, type: String) -> Bool {
        switch type {
        case "string": return value is String
        case "boolean": return isBool(value)
        case "null": return value is NSNull
        case "array": return value is [Any]
        case "object": return value is [String: Any]
        case "number": return number(value) != nil
        case "integer":
            guard let n = number(value) else { return false }
            return n == n.rounded()
        default: return false
        }
    }

    private func isBool(_ value: Any) -> Bool {
        guard let number = value as? NSNumber else { return false }
        return CFGetTypeID(number) == CFBooleanGetTypeID()
    }

    private func number(_ value: Any) -> Double? {
        guard let number = value as? NSNumber, !isBool(value) else { return nil }
        return number.doubleValue
    }

    private func equal(_ a: Any, _ b: Any) -> Bool {
        if let a = number(a), let b = number(b) { return a == b }
        if let a = a as? String, let b = b as? String { return a == b }
        if isBool(a), isBool(b) { return (a as? Bool) == (b as? Bool) }
        return false
    }

    /// The `t` values a message group (`clientMessage` / `serverMessage`) allows.
    func messageTypes(in group: String) -> [String] {
        let refs = (definition(group)?["oneOf"] as? [[String: Any]]) ?? []
        return refs.compactMap { ref in
            guard let path = ref["$ref"] as? String,
                  let target = definition(String(path.dropFirst("#/$defs/".count))),
                  let properties = target["properties"] as? [String: Any],
                  let t = properties["t"] as? [String: Any] else { return nil }
            return t["const"] as? String
        }
    }
}

@Suite("Remote protocol schema conformance")
struct RemoteSchemaConformanceTests {
    private let schema: MiniSchema

    init() throws {
        schema = try MiniSchema.load()
    }

    // MARK: Fixtures

    private static let state = RemoteState(
        tool: "Pen", color: "#E53935", size: 4, mode: "Draw", screenW: 1728, screenH: 1117,
        regionActive: false, pressureSensitive: true, screenVeil: 0.2,
        customColors: ["#112233"], streamEpoch: 42,
        monitors: [RemoteMonitorRect(x: 0, y: 0, w: 0.5, h: 1, primary: true)]
    )

    private static let snapshot = RemoteStrokeSnapshot(
        id: RemoteID.wire(UUID()), color: "#43A047", size: 6, pts: [0.1, 0.2, 1, 0.3, 0.4, 0.5], ignorePressure: false
    )

    /// Every server message the host builds, as it goes out on the wire.
    private func hostMessages() throws -> [(type: String, json: Data)] {
        func wrap(_ type: String, _ payload: some Encodable) throws -> (String, Data) {
            (type, try RemoteJSON.wrap(type, payload: payload))
        }
        let shape = RemotePeerShape(
            author: "c1", id: RemoteID.wire(UUID()), tool: "Rectangle",
            x1: 0.1, y1: 0.1, x2: 0.4, y2: 0.3, color: "#1E88E5", size: 4
        )
        let pairs: [(String, Data)] = try [
            wrap("welcome", RemoteWelcome(
                clientId: "c1", deviceKey: "key", screenW: 1728, screenH: 1117,
                state: Self.state, inkRevision: 7, libToken: "tok"
            )),
            wrap("welcome", RemoteWelcome(clientId: "c2", screenW: 10, screenH: 10, state: Self.state, inkRevision: 0)),
            wrap("error", RemoteError(code: RemoteErrorCode.badToken.rawValue, message: "Not paired.")),
            wrap("state", Self.state),
            wrap("roster", RemoteRoster(participants: [RemoteRosterEntry(clientId: "c1", name: "iPad", color: "#E53935")])),
            wrap("ack", RemoteAck(id: "s1", inkRevision: 9)),
            wrap("pong", RemotePong(ms: 12)),
            wrap("strokesRemoved", RemoteStrokesRemoved(ids: [RemoteID.wire(UUID())])),
            wrap("strokesAdded", RemoteStrokesAdded(strokes: [Self.snapshot])),
            wrap("captured", RemoteCaptured(id: "OverInk-1")),
            wrap("captureFailed", RemoteCaptureFailed(message: "No display.")),
            wrap("libraryItemChanged", RemoteLibraryItemChanged(id: "OverInk-1", rev: "1-2.3-4.0-0")),
            wrap("peerStrokeBegin", RemotePeerStrokeBegin(author: "c1", id: "s1", color: "#1E88E5", size: 5, x: 0.1, y: 0.2, p: 0.7)),
            wrap("peerStrokePoints", RemotePeerStrokePoints(author: "c1", id: "s1", pts: [0.1, 0.2, 1])),
            wrap("peerStrokeEnd", RemotePeerStrokeID(author: "c1", id: "s1")),
            wrap("peerStrokeCancel", RemotePeerStrokeID(author: "c1", id: "s1")),
            wrap("peerShapePreview", shape),
            wrap("peerShapeCommit", shape),
            wrap("hover", RemoteHover(clientId: "c1", x: 0.5, y: 0.5)),
        ]
        var result = pairs.map { (type: $0.0, json: $0.1) }
        result.append(("cleared", RemoteJSON.wrap("cleared")))
        result.append(("libraryChanged", RemoteJSON.wrap("libraryChanged")))
        return result
    }

    /// Client messages as the web client sends them (plus the ones only another client might).
    private static let clientSamples: [String] = [
        ##"{"t":"hello","v":1,"deviceName":"iPad"}"##,
        ##"{"t":"hello","v":1,"deviceKey":"k","pair":null,"join":null,"deviceName":"iPad"}"##,
        ##"{"t":"strokeBegin","id":"a","color":"#1E88E5","size":6,"x":0.1,"y":0.2,"p":0.5}"##,
        ##"{"t":"strokeBegin","id":"a","x":0.1,"y":0.2}"##,
        ##"{"t":"strokePoints","id":"a","pts":[0.1,0.2,1,0.2,0.3,1]}"##,
        ##"{"t":"strokeEnd","id":"a"}"##,
        ##"{"t":"strokeCancel","id":"a"}"##,
        ##"{"t":"shapePreview","id":"b","tool":"Line","x1":0.1,"y1":0.1,"x2":0.5,"y2":0.5,"color":"#43A047","size":4}"##,
        ##"{"t":"shapeCommit","id":"b","tool":"Ellipse","x1":0.1,"y1":0.1,"x2":0.5,"y2":0.5,"color":"#43A047","size":4}"##,
        ##"{"t":"erase","pts":[0.1,0.1,0.2,0.2],"size":30,"whole":true}"##,
        ##"{"t":"erase","pts":[0.1,0.1],"size":30}"##,
        ##"{"t":"setTool","tool":"Eraser"}"##,
        ##"{"t":"setColor","color":"#8E24AA"}"##,
        ##"{"t":"saveColor","color":"#8E24AA"}"##,
        ##"{"t":"removeColor","color":"#8E24AA"}"##,
        ##"{"t":"setSize","size":8}"##,
        ##"{"t":"setPressure","enabled":false}"##,
        ##"{"t":"setVeil","value":0.3}"##,
        ##"{"t":"undo"}"##,
        ##"{"t":"redo"}"##,
        ##"{"t":"clear"}"##,
        ##"{"t":"ping","ms":1700000000000}"##,
        ##"{"t":"hover","clientId":"c1","x":0.4,"y":0.6}"##,
        ##"{"t":"viewMode","mode":"doc"}"##,
        ##"{"t":"viewMode","mode":"live"}"##,
        ##"{"t":"capture"}"##,
    ]

    // MARK: Tests

    @Test func schemaResolvesEveryReference() throws {
        func collectRefs(_ value: Any, into refs: inout Set<String>) {
            if let object = value as? [String: Any] {
                if let ref = object["$ref"] as? String { refs.insert(ref) }
                object.values.forEach { collectRefs($0, into: &refs) }
            } else if let array = value as? [Any] {
                array.forEach { collectRefs($0, into: &refs) }
            }
        }
        var refs: Set<String> = []
        collectRefs(schema.root, into: &refs)
        #expect(!refs.isEmpty)
        for ref in refs {
            #expect(schema.definition(String(ref.dropFirst("#/$defs/".count))) != nil, "unresolved \(ref)")
        }
    }

    @Test func everyServerMessageTheHostSendsMatchesTheSchema() throws {
        for message in try hostMessages() {
            let problems = schema.validate(message.json, against: "serverMessage")
            #expect(problems.isEmpty, "\(message.type): \(problems) in \(String(decoding: message.json, as: UTF8.self))")
        }
    }

    @Test func theHostBuildsEveryServerMessageTheSchemaDeclares() throws {
        let built = Set(try hostMessages().map(\.type))
        let declared = Set(schema.messageTypes(in: "serverMessage"))
        #expect(declared.subtracting(built).isEmpty, "schema declares messages the host never builds: \(declared.subtracting(built))")
        #expect(built.subtracting(declared).isEmpty, "host builds messages missing from the schema: \(built.subtracting(declared))")
    }

    @Test func clientSamplesMatchTheSchemaAndDecode() throws {
        for sample in Self.clientSamples {
            let data = Data(sample.utf8)
            let problems = schema.validate(data, against: "clientMessage")
            #expect(problems.isEmpty, "\(sample): \(problems)")

            // `hello` is handled by the handshake, everything else by the message decoder.
            if !sample.contains(##""t":"hello""##) {
                #expect(RemoteClientMessage.decode(data) != nil, "host cannot decode \(sample)")
            } else {
                #expect((try? RemoteJSON.decoder().decode(RemoteHello.self, from: data)) != nil, "bad hello \(sample)")
            }
        }
    }

    @Test func everyClientMessageInTheSchemaHasASample() {
        let covered = Set(Self.clientSamples.compactMap { sample -> String? in
            guard let object = try? JSONSerialization.jsonObject(with: Data(sample.utf8)) as? [String: Any] else { return nil }
            return object["t"] as? String
        })
        let declared = Set(schema.messageTypes(in: "clientMessage"))
        #expect(declared.subtracting(covered).isEmpty, "no sample for \(declared.subtracting(covered))")
        #expect(covered.subtracting(declared).isEmpty, "sample not in schema: \(covered.subtracting(declared))")
    }

    @Test func malformedMessagesAreRejectedBySchema() {
        let bad: [String] = [
            ##"{"t":"nope"}"##,
            ##"{"x":1}"##,
            ##"{"t":"strokeBegin","id":"a","y":0.2}"##,
            ##"{"t":"strokePoints","id":"a","pts":[]}"##,
            ##"{"t":"strokePoints","id":"a","pts":["x","y","z"]}"##,
            ##"{"t":"erase","pts":[0.1,0.1],"size":0}"##,
            ##"{"t":"setColor","color":"red"}"##,
            ##"{"t":"setVeil","value":2}"##,
            ##"{"t":"hello","v":2,"deviceName":"x"}"##,
            ##"{"t":"hello","v":1}"##,
            ##"{"t":"viewMode","mode":"sideways"}"##,
            ##"{"t":"shapeCommit","id":"b","tool":"Pen","x1":0,"y1":0,"x2":1,"y2":1}"##,
            ##"{"t":"setPressure","enabled":"yes"}"##,
        ]
        for sample in bad {
            #expect(!schema.validate(Data(sample.utf8), against: "clientMessage").isEmpty, "accepted \(sample)")
        }

        let badServer: [String] = [
            ##"{"t":"state","tool":"Pen","color":"red","size":4,"mode":"Draw","screenW":1,"screenH":1,"regionActive":false}"##,
            ##"{"t":"state","tool":"Pen","color":"#112233","size":4,"mode":"Sideways","screenW":1,"screenH":1,"regionActive":false}"##,
            ##"{"t":"error","code":"mystery","message":"x"}"##,
            ##"{"t":"ack","id":"a","inkRevision":-1}"##,
            ##"{"t":"strokesAdded","strokes":[{"id":"NOT-A-GUID","color":"#112233","size":4,"pts":[],"ignorePressure":false}]}"##,
        ]
        for sample in badServer {
            #expect(!schema.validate(Data(sample.utf8), against: "serverMessage").isEmpty, "accepted \(sample)")
        }
    }

    /// The shipped web client must only send what the schema allows, and only react to what the host can send.
    @Test func theWebClientAgreesWithTheSchema() throws {
        let appJS = MiniSchema.repositoryRoot().appendingPathComponent("shared/remote-web/app.js")
        let source = try String(contentsOf: appJS, encoding: .utf8)

        func matches(_ pattern: String) throws -> Set<String> {
            let regex = try NSRegularExpression(pattern: pattern)
            let range = NSRange(source.startIndex..., in: source)
            return Set(regex.matches(in: source, range: range).compactMap { match in
                Range(match.range(at: 1), in: source).map { String(source[$0]) }
            })
        }

        let sent = try matches(##"send\(\{\s*t:\s*'(\w+)'"##)
        #expect(sent.count >= 10, "found too few send() calls; did the client change shape? \(sent)")
        let clientTypes = Set(schema.messageTypes(in: "clientMessage"))
        #expect(sent.subtracting(clientTypes).isEmpty, "web client sends messages the schema does not know: \(sent.subtracting(clientTypes))")

        // The client's message switch handles these server messages.
        let handled = try matches(##"case '(\w+)':"##)
        let serverTypes = Set(schema.messageTypes(in: "serverMessage"))
        let handledServer = handled.intersection(serverTypes)
        let required: Set = [
            "welcome", "state", "ack", "strokesRemoved", "strokesAdded", "cleared", "pong", "error",
            "roster", "peerStrokeBegin", "peerStrokePoints", "peerStrokeEnd", "peerStrokeCancel",
            "peerShapePreview", "peerShapeCommit", "captured", "captureFailed",
            "libraryChanged", "libraryItemChanged",
        ]
        #expect(required.subtracting(handledServer).isEmpty, "web client no longer handles \(required.subtracting(handledServer))")
        #expect(required.subtracting(serverTypes).isEmpty, "schema is missing \(required.subtracting(serverTypes))")
    }
}
