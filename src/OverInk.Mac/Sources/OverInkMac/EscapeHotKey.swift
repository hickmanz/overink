import Carbon.HIToolbox
import Foundation

@MainActor
final class EscapeHotKey {
    private static let identifier = EventHotKeyID(
        signature: OSType(0x4F494E4B), // "OINK"
        id: 1
    )

    private var hotKey: EventHotKeyRef?
    private var eventHandler: EventHandlerRef?
    private let action: () -> Void

    init(action: @escaping () -> Void) {
        self.action = action

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
                guard status == noErr, identifier.id == EscapeHotKey.identifier.id else {
                    return status
                }

                let controller = Unmanaged<EscapeHotKey>
                    .fromOpaque(userData)
                    .takeUnretainedValue()
                MainActor.assumeIsolated {
                    controller.action()
                }
                return noErr
            },
            1,
            &eventType,
            Unmanaged.passUnretained(self).toOpaque(),
            &eventHandler
        )
    }

    func setEnabled(_ enabled: Bool) {
        if enabled {
            guard hotKey == nil else { return }
            RegisterEventHotKey(
                UInt32(kVK_Escape),
                0,
                Self.identifier,
                GetApplicationEventTarget(),
                0,
                &hotKey
            )
        } else if let hotKey {
            UnregisterEventHotKey(hotKey)
            self.hotKey = nil
        }
    }

    func shutdown() {
        setEnabled(false)
        if let eventHandler {
            RemoveEventHandler(eventHandler)
            self.eventHandler = nil
        }
    }
}
