import Foundation

/// A parsed HTTP/1.1 request. Only what the remote server needs.
public struct HTTPRequest: Equatable, Sendable {
    public var method: String
    /// The percent-decoded path, without the query string.
    public var path: String
    public var query: [String: String]
    /// Header names are lowercased.
    public var headers: [String: String]
    public var body: Data

    public init(
        method: String,
        path: String,
        query: [String: String] = [:],
        headers: [String: String] = [:],
        body: Data = Data()
    ) {
        self.method = method
        self.path = path
        self.query = query
        self.headers = headers
        self.body = body
    }

    public func header(_ name: String) -> String? { headers[name.lowercased()] }
}

public enum HTTPParseResult: Equatable, Sendable {
    /// The buffer does not hold a whole request yet.
    case needMore
    /// A request, and how many bytes of the buffer it used.
    case request(HTTPRequest, consumed: Int)
    /// Malformed or unsupported; answer with this status and close.
    case invalid(status: Int)
}

public enum HTTPRequestParser {
    public static let maxHeaderBytes = 16 * 1024
    public static let maxBodyBytes = 32 * 1024 * 1024

    private static let terminator = Data("\r\n\r\n".utf8)

    /// Parses one request from the start of `buffer`. Call again with more data on `.needMore`.
    public static func parse(_ buffer: Data) -> HTTPParseResult {
        guard let end = buffer.range(of: terminator) else {
            return buffer.count > maxHeaderBytes ? .invalid(status: 431) : .needMore
        }
        guard end.lowerBound <= maxHeaderBytes else { return .invalid(status: 431) }

        guard let head = String(data: buffer[buffer.startIndex ..< end.lowerBound], encoding: .utf8) else {
            return .invalid(status: 400)
        }
        var lines = head.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ", omittingEmptySubsequences: true)
        guard requestLine.count == 3, requestLine[2].hasPrefix("HTTP/1.") else { return .invalid(status: 400) }

        var headers: [String: String] = [:]
        for line in lines where !line.isEmpty {
            guard let colon = line.firstIndex(of: ":") else { return .invalid(status: 400) }
            let name = line[line.startIndex ..< colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty else { return .invalid(status: 400) }
            headers[name] = value
        }
        if headers["transfer-encoding"] != nil { return .invalid(status: 501) }

        var bodyLength = 0
        if let raw = headers["content-length"] {
            guard let length = Int(raw), length >= 0 else { return .invalid(status: 400) }
            guard length <= maxBodyBytes else { return .invalid(status: 413) }
            bodyLength = length
        }
        let bodyStart = end.upperBound
        let available = buffer.endIndex - bodyStart
        guard available >= bodyLength else { return .needMore }

        let target = String(requestLine[1])
        let (path, query) = splitTarget(target)
        guard path.hasPrefix("/"), !path.contains("\0") else { return .invalid(status: 400) }

        let request = HTTPRequest(
            method: String(requestLine[0]).uppercased(),
            path: path,
            query: query,
            headers: headers,
            body: Data(buffer[bodyStart ..< bodyStart + bodyLength])
        )
        return .request(request, consumed: bodyStart - buffer.startIndex + bodyLength)
    }

    /// Splits `/a%20b?x=1&y=2` into a decoded path and decoded query items.
    static func splitTarget(_ target: String) -> (String, [String: String]) {
        let parts = target.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let path = String(parts[0]).removingPercentEncoding ?? String(parts[0])
        var query: [String: String] = [:]
        if parts.count > 1 {
            for pair in parts[1].split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
                let key = String(kv[0]).replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? String(kv[0])
                let raw = kv.count > 1 ? String(kv[1]) : ""
                query[key] = raw.replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? raw
            }
        }
        return (path, query)
    }
}

public struct HTTPResponse: Equatable, Sendable {
    public var status: Int
    public var headers: [(String, String)]
    public var body: Data

    public init(status: Int, headers: [(String, String)] = [], body: Data = Data()) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    public static func == (lhs: HTTPResponse, rhs: HTTPResponse) -> Bool {
        lhs.status == rhs.status && lhs.body == rhs.body
            && lhs.headers.map { "\($0.0):\($0.1)" } == rhs.headers.map { "\($0.0):\($0.1)" }
    }

    public static func text(_ status: Int, _ text: String) -> HTTPResponse {
        HTTPResponse(
            status: status,
            headers: [("Content-Type", "text/plain; charset=utf-8"), ("Cache-Control", "no-store")],
            body: Data(text.utf8)
        )
    }

    public static func json(_ value: some Encodable, status: Int = 200) -> HTTPResponse {
        let body = (try? RemoteJSON.encoder().encode(value)) ?? Data("null".utf8)
        return HTTPResponse(
            status: status,
            headers: [("Content-Type", "application/json; charset=utf-8"), ("Cache-Control", "no-store")],
            body: body
        )
    }

    public static func empty(_ status: Int) -> HTTPResponse {
        HTTPResponse(status: status, headers: [("Cache-Control", "no-store")])
    }

    public static func reason(for status: Int) -> String {
        switch status {
        case 101: "Switching Protocols"
        case 200: "OK"
        case 304: "Not Modified"
        case 400: "Bad Request"
        case 401: "Unauthorized"
        case 403: "Forbidden"
        case 404: "Not Found"
        case 405: "Method Not Allowed"
        case 409: "Conflict"
        case 413: "Payload Too Large"
        case 431: "Request Header Fields Too Large"
        case 500: "Internal Server Error"
        case 501: "Not Implemented"
        default: "Status"
        }
    }

    /// Status line, headers (with `Content-Length` and `Connection: close`) and body.
    public func serialized(includeBody: Bool = true) -> Data {
        var head = "HTTP/1.1 \(status) \(Self.reason(for: status))\r\n"
        for (name, value) in headers where name.lowercased() != "content-length" && name.lowercased() != "connection" {
            head += "\(name): \(value)\r\n"
        }
        head += "Content-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        var data = Data(head.utf8)
        if includeBody { data.append(body) }
        return data
    }
}

public enum MIMEType {
    public static func forPath(_ path: String) -> String {
        switch (path as NSString).pathExtension.lowercased() {
        case "html", "htm": "text/html; charset=utf-8"
        case "js", "mjs": "text/javascript; charset=utf-8"
        case "css": "text/css; charset=utf-8"
        case "json": "application/json; charset=utf-8"
        case "svg": "image/svg+xml"
        case "png": "image/png"
        case "jpg", "jpeg": "image/jpeg"
        case "ico": "image/x-icon"
        case "webmanifest": "application/manifest+json"
        default: "application/octet-stream"
        }
    }
}

/// Resolves a request path to a file inside the served folder without ever leaving it.
public enum StaticFileResolver {
    /// `nil` when the path escapes the root, has hidden components or is not a file in `allowed`.
    public static func relativePath(for requestPath: String, allowed: Set<String>) -> String? {
        var path = requestPath
        if path == "/" || path.isEmpty { path = "/index.html" }
        let parts = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard !parts.isEmpty,
              parts.allSatisfy({ !$0.hasPrefix(".") && !$0.contains("\\") && !$0.contains("\0") }) else {
            return nil
        }
        let relative = parts.joined(separator: "/")
        return allowed.contains(relative) ? relative : nil
    }
}
