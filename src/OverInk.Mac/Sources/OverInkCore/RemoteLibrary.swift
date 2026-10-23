import Foundation

/// A library entry as the iPad's library view lists it.
public struct RemoteLibraryItem: Codable, Equatable, Sendable {
    public var id: String
    public var name: String
    /// Milliseconds since 1970.
    public var createdAt: Int64
    /// Pixel size of the original image.
    public var w: Int
    public var h: Int
    public var rev: String
    public var editable: Bool

    public init(id: String, name: String, createdAt: Int64, w: Int, h: Int, rev: String, editable: Bool) {
        self.id = id
        self.name = name
        self.createdAt = createdAt
        self.w = w
        self.h = h
        self.rev = rev
        self.editable = editable
    }
}

/// One stroke in image-pixel coordinates (`pts` are `x, y, pressure` triples).
public struct RemoteLibraryStroke: Codable, Equatable, Sendable {
    public var color: String
    public var size: Double
    public var pts: [Float]
    public var ignorePressure: Bool
    public var group: String?

    public init(color: String, size: Double, pts: [Float], ignorePressure: Bool, group: String? = nil) {
        self.color = color
        self.size = size
        self.pts = pts
        self.ignorePressure = ignorePressure
        self.group = group
    }

    private enum CodingKeys: String, CodingKey { case color, size, pts, ignorePressure, group }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        color = try c.decodeIfPresent(String.self, forKey: .color) ?? "#E53935"
        size = try c.decode(Double.self, forKey: .size)
        pts = try c.decode([Float].self, forKey: .pts)
        ignorePressure = try c.decodeIfPresent(Bool.self, forKey: .ignorePressure) ?? false
        group = try c.decodeIfPresent(String.self, forKey: .group)
    }
}

/// Response of `GET /lib/{id}/strokes`. `veil` is the capture's white wash (0-0.6); the original image is
/// served clean and the client applies it.
public struct RemoteLibraryStrokes: Codable, Equatable, Sendable {
    public var rev: String
    public var w: Int
    public var h: Int
    public var strokes: [RemoteLibraryStroke]
    public var veil: Double

    public init(rev: String, w: Int, h: Int, strokes: [RemoteLibraryStroke], veil: Double = 0) {
        self.rev = rev
        self.w = w
        self.h = h
        self.strokes = strokes
        self.veil = veil
    }
}

/// Body of `POST /lib/{id}/strokes`. `veil` is optional: when absent the capture's stored veil is kept.
public struct RemoteLibrarySave: Codable, Equatable, Sendable {
    public var baseRev: String
    public var strokes: [RemoteLibraryStroke]
    public var veil: Double?

    public init(baseRev: String, strokes: [RemoteLibraryStroke], veil: Double? = nil) {
        self.baseRev = baseRev
        self.strokes = strokes
        self.veil = veil
    }
}

public enum RemoteLibraryConversion {
    public static let maxStrokes = 20_000
    public static let maxPointsPerStroke = 200_000
    public static let maxVeil = 0.6

    /// Checks client-supplied ink so a bad request cannot create absurd strokes or exhaust memory.
    public static func validate(_ strokes: [RemoteLibraryStroke]) -> String? {
        if strokes.count > maxStrokes { return "Too many strokes." }
        for stroke in strokes {
            if stroke.pts.count < 3 || stroke.pts.count % 3 != 0 || stroke.pts.count / 3 > maxPointsPerStroke {
                return "Invalid stroke points."
            }
            if !stroke.size.isFinite { return "Invalid stroke size." }
        }
        return nil
    }

    /// A capture's strokes (canvas points) as image-pixel strokes for the iPad.
    public static func strokes(
        from document: CaptureDocument,
        pixelWidth: Int,
        pixelHeight: Int
    ) -> [RemoteLibraryStroke] {
        let (sx, sy) = scales(document, pixelWidth, pixelHeight)
        let widthScale = (sx + sy) / 2
        return document.strokes.compactMap { stroke in
            guard !stroke.points.isEmpty else { return nil }
            var pts: [Float] = []
            pts.reserveCapacity(stroke.points.count * 3)
            for point in stroke.points {
                pts.append(Float(point.x * sx))
                pts.append(Float(point.y * sy))
                pts.append(point.pressure)
            }
            return RemoteLibraryStroke(
                color: stroke.colorHex,
                size: stroke.width * widthScale,
                pts: pts,
                ignorePressure: stroke.ignoresPressure,
                group: stroke.groupId.map { RemoteID.wire($0) }
            )
        }
    }

    /// Image-pixel strokes from the iPad as canvas-point strokes. Points that are not finite are skipped and
    /// strokes left empty are dropped.
    public static func inkStrokes(
        from strokes: [RemoteLibraryStroke],
        document: CaptureDocument,
        pixelWidth: Int,
        pixelHeight: Int
    ) -> [InkStroke] {
        let (sx, sy) = scales(document, pixelWidth, pixelHeight)
        let widthScale = (sx + sy) / 2
        var groups: [String: UUID] = [:]
        return strokes.compactMap { stroke in
            var points: [InkPoint] = []
            var index = 0
            while index + 2 < stroke.pts.count {
                let x = stroke.pts[index], y = stroke.pts[index + 1], p = stroke.pts[index + 2]
                index += 3
                guard x.isFinite, y.isFinite else { continue }
                points.append(InkPoint(
                    x: Double(x) / sx,
                    y: Double(y) / sy,
                    pressure: p.isFinite ? min(max(p, 0), 1) : 0.5
                ))
            }
            guard !points.isEmpty else { return nil }
            let size = min(max(stroke.size / widthScale, 0.5), 200)
            var groupId: UUID?
            if let group = stroke.group, !group.isEmpty, group.count <= 64 {
                if let existing = groups[group] {
                    groupId = existing
                } else {
                    let created = RemoteID.uuid(group) ?? UUID()
                    groups[group] = created
                    groupId = created
                }
            }
            return InkStroke(
                colorHex: HexColor.isValid(stroke.color) ? stroke.color.uppercased() : "#E53935",
                width: size,
                points: points,
                ignoresPressure: stroke.ignorePressure,
                groupId: groupId
            )
        }
    }

    /// Pixels per canvas point on each axis.
    private static func scales(_ document: CaptureDocument, _ pixelWidth: Int, _ pixelHeight: Int) -> (Double, Double) {
        let sx = document.canvasWidth > 0 ? Double(pixelWidth) / document.canvasWidth : 1
        let sy = document.canvasHeight > 0 ? Double(pixelHeight) / document.canvasHeight : 1
        return (sx > 0 ? sx : 1, sy > 0 ? sy : 1)
    }
}

public enum HexColor {
    /// `#RRGGBB` (case-insensitive).
    public static func isValid(_ text: String) -> Bool {
        guard text.count == 7, text.first == "#" else { return false }
        return text.dropFirst().allSatisfy(\.isHexDigit)
    }

    public static func normalized(_ text: String, fallback: String = "#E53935") -> String {
        isValid(text) ? text.uppercased() : fallback
    }
}

public enum RemoteLibraryRevision {
    /// A short string that changes whenever any of a capture's files changes: modification time (ms) and size
    /// of each file, in a fixed order. Missing files contribute `0-0`.
    public static func make(files: [(modified: Date?, size: Int64?)]) -> String {
        files.map { file in
            let ms = file.modified.map { Int64(($0.timeIntervalSince1970 * 1000).rounded()) } ?? 0
            return "\(ms)-\(file.size ?? 0)"
        }.joined(separator: ".")
    }
}

/// Capability tokens for the `/lib` HTTP routes, one per connected paired device.
///
/// `<img>` tags cannot send headers, so the token travels as `?k=`. A device holds one token at a time: a
/// newer connection replaces the old one.
public final class LibraryTokenRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var clientByToken: [String: String] = [:]
    private var tokenByDevice: [String: String] = [:]

    public init() {}

    public func issue(deviceID: String, clientID: String) -> String {
        let token = PairingService.makeToken()
        lock.withLock {
            if let old = tokenByDevice[deviceID] { clientByToken[old] = nil }
            clientByToken[token] = clientID
            tokenByDevice[deviceID] = token
        }
        return token
    }

    public func isValid(_ token: String?) -> Bool {
        guard let token, !token.isEmpty else { return false }
        return lock.withLock { clientByToken[token] != nil }
    }

    public func revoke(clientID: String) {
        lock.withLock {
            let owned = clientByToken.filter { $0.value == clientID }.map(\.key)
            for token in owned {
                clientByToken[token] = nil
                tokenByDevice = tokenByDevice.filter { $0.value != token }
            }
        }
    }

    public func revokeAll() {
        lock.withLock {
            clientByToken.removeAll()
            tokenByDevice.removeAll()
        }
    }
}
