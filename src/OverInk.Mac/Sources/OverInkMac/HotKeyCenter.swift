import Carbon.HIToolbox
import Foundation
import OverInkCore

/// Registers system-wide hotkeys through Carbon `RegisterEventHotKey`. Unlike an `NSEvent` global
/// monitor this needs no Accessibility permission and swallows the keystroke.
@MainActor
final class HotKeyCenter {
    static let shared = HotKeyCenter()

    private static let signature = OSType(0x4F494E4B) // "OINK"

    private var handlers: [UInt32: () -> Void] = [:]
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var nextID: UInt32 = 1
    private var eventHandler: EventHandlerRef?

    private init() {
        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )
        InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, userData in
                guard let event, let userData else { return noErr }

                var identifier = EventHotKeyID()
                let status = GetEventParameter(
                    event,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &identifier
                )
                guard status == noErr, identifier.signature == HotKeyCenter.signature else {
                    return OSStatus(eventNotHandledErr)
                }

                let center = Unmanaged<HotKeyCenter>.fromOpaque(userData).takeUnretainedValue()
                let hotKeyID = identifier.id
                MainActor.assumeIsolated {
                    center.handlers[hotKeyID]?()
                }
                return noErr
            },
            1,
            &eventType,
            Unmanaged.passUnretained(self).toOpaque(),
            &eventHandler
        )
    }

    /// Returns an id for `unregister`, or `nil` if the system refused the combination
    /// (for example it is reserved or already taken).
    func register(_ chord: KeyChord, handler: @escaping () -> Void) -> UInt32? {
        let id = nextID
        nextID += 1
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(
            chord.keyCode,
            Self.carbonModifiers(chord.modifiers),
            EventHotKeyID(signature: Self.signature, id: id),
            GetApplicationEventTarget(),
            0,
            &ref
        )
        guard status == noErr, let ref else { return nil }
        refs[id] = ref
        handlers[id] = handler
        return id
    }

    func unregister(_ id: UInt32) {
        if let ref = refs.removeValue(forKey: id) {
            UnregisterEventHotKey(ref)
        }
        handlers.removeValue(forKey: id)
    }

    static func carbonModifiers(_ modifiers: KeyChord.Modifiers) -> UInt32 {
        var result: UInt32 = 0
        if modifiers.contains(.command) { result |= UInt32(cmdKey) }
        if modifiers.contains(.option) { result |= UInt32(optionKey) }
        if modifiers.contains(.control) { result |= UInt32(controlKey) }
        if modifiers.contains(.shift) { result |= UInt32(shiftKey) }
        return result
    }
}

/// One hotkey that can be enabled, disabled, and re-assigned without leaking registrations.
@MainActor
final class HotKeyBinding {
    private var registrationID: UInt32?
    private let action: () -> Void
    private let allowsBareKey: Bool

    private(set) var chord: KeyChord?
    private(set) var isEnabled: Bool
    /// False when the chord is set and enabled but the system refused to register it.
    private(set) var isRegistered = false

    /// - Parameter allowsBareKey: permit chords without modifiers (Escape, P, E, ...). Only use this
    ///   for hotkeys that are registered temporarily, such as while drawing, since the key is swallowed
    ///   system-wide while registered.
    init(
        chord: KeyChord?,
        enabled: Bool = true,
        allowsBareKey: Bool = false,
        action: @escaping () -> Void
    ) {
        self.allowsBareKey = allowsBareKey
        self.chord = chord
        self.isEnabled = enabled
        self.action = action
        refresh()
    }

    func update(chord: KeyChord?) {
        self.chord = chord
        refresh()
    }

    func setEnabled(_ enabled: Bool) {
        guard enabled != isEnabled else { return }
        isEnabled = enabled
        refresh()
    }

    func shutdown() {
        isEnabled = false
        refresh()
    }

    private func refresh() {
        if let registrationID {
            HotKeyCenter.shared.unregister(registrationID)
            self.registrationID = nil
        }
        isRegistered = false
        guard isEnabled, let chord, allowsBareKey || chord.isValidGlobalHotKey else { return }
        registrationID = HotKeyCenter.shared.register(chord, handler: action)
        isRegistered = registrationID != nil
    }
}
