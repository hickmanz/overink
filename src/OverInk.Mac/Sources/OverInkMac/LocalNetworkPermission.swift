import Foundation
import Network

/// macOS asks for Local Network access the first time an app browses the network, so a tablet can reach the
/// Mac's server. This browses for a service nothing advertises, only to raise the prompt and learn the answer.
@MainActor
final class LocalNetworkPermission {
    private static var current: LocalNetworkPermission?

    private let browser = NWBrowser(for: .bonjour(type: "_overink._tcp", domain: nil), using: .tcp)
    private var completion: (@MainActor (Bool) -> Void)?

    /// Calls `completion` once with `false` when access was refused, `true` when it was granted or nothing
    /// is blocking the app.
    static func check(completion: @escaping @MainActor (Bool) -> Void) {
        current?.finish(true)
        let probe = LocalNetworkPermission()
        probe.completion = completion
        current = probe
        probe.start()
    }

    private func start() {
        browser.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                Task { @MainActor in self?.finish(true) }
            case let .waiting(error), let .failed(error):
                // kDNSServiceErr_PolicyDenied: the user declined access.
                if case .dns(let code) = error, code == -65570 {
                    Task { @MainActor in self?.finish(false) }
                }
            default:
                break
            }
        }
        browser.start(queue: .main)
        // No refusal within a few seconds means nothing is blocking us.
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(4))
            self?.finish(true)
        }
    }

    private func finish(_ allowed: Bool) {
        guard let completion else { return }
        self.completion = nil
        browser.cancel()
        if Self.current === self { Self.current = nil }
        completion(allowed)
    }
}
