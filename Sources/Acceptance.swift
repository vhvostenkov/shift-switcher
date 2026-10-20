import AppKit
import ApplicationServices

// Runs only with --native-acceptance. All edits and keys are bound to a new test document.
final class NativeAcceptance {
    let app: App
    let output: URL
    let fixture: URL
    let originalAuto: Bool
    var results: [[String: Any]] = []
    var editor: NSRunningApplication?
    var originalInput: TISInputSource?

    init(app: App) {
        self.app = app
        let folder = Bundle.main.bundleURL.deletingLastPathComponent()
        output = folder.appendingPathComponent("native-acceptance.json")
        fixture = folder.appendingPathComponent("ShiftSwitcher-native-test-\(Int(Date().timeIntervalSince1970)).txt")
        originalAuto = app.autoEnabled
    }
    func start() {
        guard AXIsProcessTrusted(), CGPreflightPostEventAccess(), app.tap != nil else {
            record("permissions", false, "AX=\(AXIsProcessTrusted()), post=\(CGPreflightPostEventAccess()), tap=\(app.tap != nil)")
            finish(); return
        }
        originalInput = TISCopyCurrentKeyboardInputSource().takeRetainedValue()
        do { try "Shift Switcher native acceptance\n".write(to: fixture, atomically: true, encoding: .utf8) }
        catch { record("fixture", false, error.localizedDescription); finish(); return }
        app.autoEnabled = false
        NSWorkspace.shared.open([fixture], withApplicationAt: URL(fileURLWithPath: "/System/Applications/TextEdit.app"), configuration: NSWorkspace.OpenConfiguration()) { running, error in
            DispatchQueue.main.async {
                self.editor = running
                if let error = error { self.record("open", false, error.localizedDescription); self.finish(); return }
                self.later(1) { self.selected() }
            }
        }
    }
    func later(_ delay: Double = 0.3, _ action: @escaping () -> Void) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: action)
    }
    func field() -> Field? {
        guard let editor = editor,
              NSWorkspace.shared.frontmostApplication?.processIdentifier == editor.processIdentifier,
              let field = app.field(), field.pid == editor.processIdentifier else { return nil }
        let root = AXUIElementCreateApplication(editor.processIdentifier)
        guard let raw = attribute(root, kAXFocusedWindowAttribute), CFGetTypeID(raw) == AXUIElementGetTypeID() else { return nil }
        let window = unsafeBitCast(raw, to: AXUIElement.self)
        guard (attribute(window, kAXTitleAttribute) as? String)?.contains(fixture.deletingPathExtension().lastPathComponent) == true else { return nil }
        return field
    }
    func prepare(_ text: String, range: NSRange) -> Bool {
        guard let field = field(), AXUIElementSetAttributeValue(field.element, kAXValueAttribute as CFString, text as CFString) == .success,
              (attribute(field.element, kAXValueAttribute) as? String) == text,
              setSelection(field.element, range) else {
            record("prepare", false, "Test document not focused or not writable"); return false
        }
        app.detector.reset(); app.autoGeneration += 1; return true
    }
    func postShift(_ down: Bool) {
        guard field() != nil else { return }
        let event = CGEvent(keyboardEventSource: nil, virtualKey: 56, keyDown: down)!
        event.type = .flagsChanged
        event.flags = down ? CGEventFlags(rawValue: CGEventFlags.maskShift.rawValue | 2) : []
        event.post(tap: .cghidEventTap)
    }
    func doubleShift(_ done: @escaping () -> Void) {
        guard field() != nil else { record("focus", false, "Aborted keys outside fixture"); finish(); return }
        postShift(true)
        later(0.04) { self.postShift(false)
            self.later(0.06) { self.postShift(true)
                self.later(0.04) { self.postShift(false); self.later(0.35, done) }
            }
        }
    }
    func selected() {
        editor?.activate(options: [.activateIgnoringOtherApps])
        later { guard self.prepare("ghbdtn", range: NSRange(location: 0, length: 6)) else { self.finish(); return }
            self.doubleShift {
                let field = self.field()
                self.record("double_shift_selection", field?.value == "привет" && field?.range == NSRange(location: 0, length: 6), "value=\(field?.value ?? "<unavailable>"), range=\(String(describing: field?.range))")
                self.unselected()
            }
        }
    }
    func unselected() {
        guard prepare("test ghbdtn", range: NSRange(location: 11, length: 0)) else { finish(); return }
        doubleShift {
            let field = self.field()
            self.record("double_shift_last_word", field?.value == "test привет" && field?.range == NSRange(location: 11, length: 0), "value=\(field?.value ?? "<unavailable>"), range=\(String(describing: field?.range))")
            self.automatic()
        }
    }
    func automatic() {
        guard prepare("ghbdtn", range: NSRange(location: 6, length: 0)) else { finish(); return }
        app.autoEnabled = true
        postSpace()
        later(0.8) {
            let field = self.field()
            self.record("automatic_en_to_ru", field?.value == "привет ", "value=\(field?.value ?? "<unavailable>")")
            self.automaticReverse()
        }
    }
    func automaticReverse() {
        guard prepare("руддщ", range: NSRange(location: 5, length: 0)) else { finish(); return }
        postSpace()
        later(0.8) {
            let field = self.field()
            self.record("automatic_ru_to_en", field?.value == "hello ", "value=\(field?.value ?? "<unavailable>")")
            self.validWord()
        }
    }
    func validWord() {
        guard prepare("hello", range: NSRange(location: 5, length: 0)) else { finish(); return }
        postSpace()
        later(0.8) {
            self.record("valid_word_unchanged", self.field()?.value == "hello ", "value=\(self.field()?.value ?? "<unavailable>")")
            self.finish()
        }
    }
    func postSpace() {
        guard field() != nil else { return }
        let down = CGEvent(keyboardEventSource: nil, virtualKey: 49, keyDown: true)!
        down.flags = []; down.post(tap: .cghidEventTap)
        later(0.03) {
            let up = CGEvent(keyboardEventSource: nil, virtualKey: 49, keyDown: false)!
            up.flags = []; up.post(tap: .cghidEventTap)
        }
    }
    func record(_ name: String, _ passed: Bool, _ detail: String) {
        results.append(["scenario": name, "passed": passed, "detail": detail])
    }
    func finish() {
        app.autoEnabled = originalAuto
        app.autoItem.state = originalAuto ? .on : .off
        app.detector.reset(); app.autoGeneration += 1
        if let input = originalInput { TISSelectInputSource(input) }
        let report: [String: Any] = ["testedAt": ISO8601DateFormatter().string(from: Date()), "fixture": fixture.path, "input": "CGEvent synthetic keyboard events; not physical keyboard", "accessibility": AXIsProcessTrusted(), "postEventAccess": CGPreflightPostEventAccess(), "results": results]
        if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) { try? data.write(to: output, options: .atomic) }
        app.report("Тест завершён: \(results.filter { $0["passed"] as? Bool == true }.count)/\(results.count)")
    }
}
import Carbon
