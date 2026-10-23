import Foundation
import OverInkCore

/// The single in-memory copy of the persisted settings. UI code reads `value`, changes it through
/// `update`, and observes changes; every update is written to disk.
@MainActor
final class SettingsModel {
    private(set) var value: MacAppSettings
    private let store: MacSettingsStore
    private var observers: [() -> Void] = []

    init(store: MacSettingsStore = MacSettingsStore()) {
        self.store = store
        value = store.load()
    }

    func update(_ change: (inout MacAppSettings) -> Void) {
        var copy = value
        change(&copy)
        guard copy != value else { return }
        value = copy
        store.save(copy)
        observers.forEach { $0() }
    }

    /// Calls `observer` after every settings change.
    func observe(_ observer: @escaping () -> Void) {
        observers.append(observer)
    }
}
