import AppKit
import Carbon

final class GlobalShortcuts {
    private let signature: OSType = 0x53535743 // SSWC
    private var handler: EventHandlerRef?
    private var correctionRef: EventHotKeyRef?
    private var undoRef: EventHotKeyRef?
    var perform: ((UInt32) -> Void)?

    init() {
        var kind = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let event = event, let context = context else { return OSStatus(eventNotHandledErr) }
            let shortcuts = Unmanaged<GlobalShortcuts>.fromOpaque(context).takeUnretainedValue()
            var key = EventHotKeyID()
            let result = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &key)
            guard result == noErr, key.signature == shortcuts.signature else { return OSStatus(eventNotHandledErr) }
            shortcuts.perform?(key.id)
            return noErr
        }, 1, &kind, Unmanaged.passUnretained(self).toOpaque(), &handler)
    }

    // Register the requested binding before dropping the old one. A conflict
    // leaves the working configuration intact and is reported to the user.
    func setCorrection(_ shortcut: CorrectionShortcut) -> Bool {
        if shortcut == .doubleShift {
            if let ref = correctionRef { UnregisterEventHotKey(ref) }
            correctionRef = nil; return true
        }
        if correctionRef != nil { return true }
        guard handler != nil else { return false }
        return RegisterEventHotKey(UInt32(kVK_Space), UInt32(controlKey | optionKey), EventHotKeyID(signature: signature, id: 1), GetApplicationEventTarget(), 0, &correctionRef) == noErr
    }
    func setUndo(_ enabled: Bool) -> Bool {
        if !enabled {
            if let ref = undoRef { UnregisterEventHotKey(ref) }
            undoRef = nil; return true
        }
        if undoRef != nil { return true }
        guard handler != nil else { return false }
        return RegisterEventHotKey(UInt32(kVK_ANSI_Z), UInt32(controlKey | optionKey), EventHotKeyID(signature: signature, id: 2), GetApplicationEventTarget(), 0, &undoRef) == noErr
    }
    deinit {
        if let ref = correctionRef { UnregisterEventHotKey(ref) }
        if let ref = undoRef { UnregisterEventHotKey(ref) }
        if let handler = handler { RemoveEventHandler(handler) }
    }
}
