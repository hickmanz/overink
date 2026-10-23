import Foundation

/// What an `overink://` link asks the app to do.
public enum AppURLAction: Equatable, Sendable {
    /// Open the editor window. With `capture`, open that file (a path or a file name in the library).
    case editor(capture: String?)
    case library
    case settings
    /// Toggle drawing on or off.
    case draw
}

/// Parses `overink://` links. Only harmless navigation is accepted: links can be opened by any web
/// page, so there is deliberately no capture or file-writing action.
public enum AppURL {
    public static let scheme = "overink"

    public static func parse(_ url: URL) -> AppURLAction? {
        guard url.scheme?.lowercased() == scheme else { return nil }
        // `overink://editor` has the action as the host; `overink:///editor` has it as the path.
        let name = (url.host?.isEmpty == false ? url.host : url.pathComponents.dropFirst().first)?.lowercased()
        switch name {
        case "editor":
            let components = URLComponents(url: url, resolvingAgainstBaseURL: false)
            let value = components?.queryItems?.first { $0.name == "capture" || $0.name == "path" }?.value
            return .editor(capture: value?.isEmpty == false ? value : nil)
        case "library": return .library
        case "settings": return .settings
        case "draw": return .draw
        default: return nil
        }
    }

    /// Resolves a `capture` value to a file inside `library`. Anything outside the library (including
    /// `..` escapes) is rejected so a link cannot point the editor at arbitrary files.
    public static func resolveCapture(_ value: String, in library: URL) -> URL? {
        let root = library.standardizedFileURL
        let candidate: URL
        if value.hasPrefix("/") {
            candidate = URL(fileURLWithPath: value)
        } else {
            candidate = root.appendingPathComponent(value)
        }
        let resolved = candidate.standardizedFileURL
        guard resolved.deletingLastPathComponent() == root,
              CapturePackagePaths.isFlattenedImage(resolved),
              FileManager.default.fileExists(atPath: resolved.path) else { return nil }
        return resolved
    }
}
