import Foundation

enum CorrectionShortcut: String, CaseIterable {
    case doubleShift, controlOptionSpace
    var title: String { self == .doubleShift ? "Двойной Shift" : "⌃⌥Пробел" }
}

// Separate keys preserve existing personal rules and allow safe fallback if a
// shortcut is already registered by another application.
struct ShortcutPreferences {
    let defaults: UserDefaults
    var correction: CorrectionShortcut {
        get { CorrectionShortcut(rawValue: defaults.string(forKey: "correctionShortcut.v1") ?? "") ?? .doubleShift }
        nonmutating set { defaults.set(newValue.rawValue, forKey: "correctionShortcut.v1") }
    }
    var keyboardUndo: Bool {
        get { defaults.bool(forKey: "keyboardUndo.v1") }
        nonmutating set { defaults.set(newValue, forKey: "keyboardUndo.v1") }
    }
}
