import Foundation

public struct InkPoint: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var pressure: Float

    public init(x: Double, y: Double, pressure: Float = 1) {
        self.x = x
        self.y = y
        self.pressure = min(max(pressure, 0), 1)
    }
}

public struct InkStroke: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID
    public var colorHex: String
    public var width: Double
    public var points: [InkPoint]
    public var ignoresPressure: Bool
    /// Strokes sharing a group id are selected and moved together.
    public var groupId: UUID?

    public init(
        id: UUID = UUID(),
        colorHex: String,
        width: Double,
        points: [InkPoint] = [],
        ignoresPressure: Bool = false,
        groupId: UUID? = nil
    ) {
        self.id = id
        self.colorHex = colorHex
        self.width = width
        self.points = points
        self.ignoresPressure = ignoresPressure
        self.groupId = groupId
    }
}

public enum SketchTool: String, Codable, CaseIterable, Sendable {
    case pen
    case eraser
    case line
    case arrow
    case rectangle
    case ellipse
}

public struct InkDocument: Codable, Equatable, Sendable {
    public static let currentVersion = 1

    public var version: Int
    public var strokes: [InkStroke]

    public init(version: Int = currentVersion, strokes: [InkStroke] = []) {
        self.version = version
        self.strokes = strokes
    }
}

public struct CaptureDocument: Codable, Equatable, Sendable {
    /// 2 added stroke `groupId`. Version 1 documents load unchanged.
    public static let currentVersion = 2

    public var version: Int
    public var canvasWidth: Double
    public var canvasHeight: Double
    public var veilOpacity: Double
    public var strokes: [InkStroke]

    public init(
        version: Int = currentVersion,
        canvasWidth: Double,
        canvasHeight: Double,
        veilOpacity: Double = 0,
        strokes: [InkStroke] = []
    ) {
        self.version = version
        self.canvasWidth = canvasWidth
        self.canvasHeight = canvasHeight
        self.veilOpacity = min(max(veilOpacity, 0), 0.6)
        self.strokes = strokes
    }

    /// Stamps the document with the current format version (call before saving a loaded document).
    public mutating func upgradeToCurrentVersion() {
        version = Self.currentVersion
    }
}

public struct InkHistory: Sendable {
    public private(set) var strokes: [InkStroke] = []
    private var redoStack: [InkStroke] = []

    public init() {}

    public mutating func append(_ stroke: InkStroke) {
        strokes.append(stroke)
        redoStack.removeAll()
    }

    @discardableResult
    public mutating func undo() -> InkStroke? {
        guard let stroke = strokes.popLast() else { return nil }
        redoStack.append(stroke)
        return stroke
    }

    @discardableResult
    public mutating func redo() -> InkStroke? {
        guard let stroke = redoStack.popLast() else { return nil }
        strokes.append(stroke)
        return stroke
    }

    public mutating func clear() {
        strokes.removeAll()
        redoStack.removeAll()
    }
}
