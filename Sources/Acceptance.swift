import AppKit
import ApplicationServices

// Runs only with --native-acceptance. All edits and keys are bound to a new test document.
final class NativeAcceptance {
    let app: App
    let target: String?
    var prefix: String { target == "com.apple.Notes" ? "Shift Switcher QA 20261020\n" : "" }
    let output: URL
    let fixture: URL
    let originalAuto: Bool
    let originalShortcut: CorrectionShortcut
    let originalKeyboardUndo: Bool
    var results: [[String: Any]] = []
    var editor: NSRunningApplication?
    var originalInput: TISInputSource?

    init(app: App, target: String? = nil) {
        self.app = app; self.target = target
        let folder = Bundle.main.bundleURL.deletingLastPathComponent()
        output = folder.appendingPathComponent(target.map { "compatibility-" + $0 + ".json" } ?? "native-acceptance.json")
        fixture = folder.appendingPathComponent("ShiftSwitcher-native-test-\(Int(Date().timeIntervalSince1970)).txt")
        originalAuto = app.autoEnabled
        originalShortcut = app.shortcutPreferences.correction
        originalKeyboardUndo = app.shortcutPreferences.keyboardUndo
    }
    func start() {
        guard AXIsProcessTrusted(), CGPreflightPostEventAccess(), app.tap != nil else {
            record("permissions", false, "AX=\(AXIsProcessTrusted()), post=\(CGPreflightPostEventAccess()), tap=\(app.tap != nil)")
            finish(); return
        }
        originalInput = TISCopyCurrentKeyboardInputSource().takeRetainedValue()
        _ = app.shortcuts.setCorrection(.doubleShift)
        app.shortcutPreferences.correction = .doubleShift
        if let target = target {
            guard ["com.apple.Safari", "com.google.Chrome", "com.apple.Notes"].contains(target),
                  let running = NSRunningApplication.runningApplications(withBundleIdentifier: target).first else {
                record("fixture", false, "Approved test application is not running"); finish(); return
            }
            editor = running; app.autoEnabled = false
            later { self.selected() }; return
        }
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
        let title = attribute(window, kAXTitleAttribute) as? String ?? ""
        if target == "com.apple.Notes" {
            guard field.value.hasPrefix(prefix) else { return nil }
        } else if target != nil {
            guard title.contains("Shift Switcher — isolated compatibility fixture") else { return nil }
        } else {
            guard title.contains(fixture.deletingPathExtension().lastPathComponent) else { return nil }
        }
        guard !prefix.isEmpty else { return field }
        return Field(element: field.element, value: String(field.value.dropFirst(prefix.count)),
                     range: NSRange(location: max(0, field.range.location - (prefix as NSString).length), length: field.range.length), pid: field.pid)
    }
    func prepare(_ text: String, range: NSRange) -> Bool {
        guard let field = field() else {
            var detail = "front=\(NSWorkspace.shared.frontmostApplication?.bundleIdentifier == editor?.bundleIdentifier) secure=\(IsSecureEventInputEnabled())"
            if let editor = editor {
                let root = AXUIElementCreateApplication(editor.processIdentifier)
                if let raw = attribute(root, kAXFocusedUIElementAttribute), CFGetTypeID(raw) == AXUIElementGetTypeID() {
                    let element = unsafeBitCast(raw, to: AXUIElement.self)
                    detail += " appField=\(app.field() != nil) role=\(attribute(element,kAXRoleAttribute) as? String ?? "none") valueString=\(attribute(element,kAXValueAttribute) is String) range=\(String(describing: selection(element))) length=\((attribute(element,kAXValueAttribute) as? String ?? "").utf16.count)"
                }
            }
            if let editor = editor, let raw = attribute(AXUIElementCreateApplication(editor.processIdentifier), kAXFocusedWindowAttribute), CFGetTypeID(raw) == AXUIElementGetTypeID() {
                let window = unsafeBitCast(raw, to: AXUIElement.self)
                let title = attribute(window,kAXTitleAttribute) as? String ?? ""
                detail += " windowTitleMatches=\(title.contains("Shift Switcher — isolated compatibility fixture")) windowDocumentMatches=\((attribute(window,kAXDocumentAttribute) as? String) == "http://127.0.0.1:8766/")"
            }
            record("prepare", false, detail); return false
        }
        let value = prefix + text
        _ = AXUIElementSetAttributeValue(field.element, kAXValueAttribute as CFString, value as CFString)
        for _ in 0..<16 {
            if (attribute(field.element,kAXValueAttribute) as? String) == value { break }
            usleep(5000)
        }
        if (attribute(field.element,kAXValueAttribute) as? String) != value {
            guard target != nil, let current = self.field(), current.value == field.value,
                  let chunks = TextEvents.chunks(text),
                  setSelection(field.element, NSRange(location: prefix.utf16.count, length: current.value.utf16.count)) else {
                record("prepare", false, "Fixture cannot be prepared safely"); return false
            }
            usleep(30000)
            var prepared = ""
            for chunk in chunks {
                let units = Array(chunk.utf16)
                for down in [true, false] {
                    guard self.field() != nil else { record("prepare",false,"Fixture focus changed during preparation"); return false }
                    let event = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: down)!
                    event.flags = []
                    if down { units.withUnsafeBufferPointer { event.keyboardSetUnicodeString(stringLength: units.count, unicodeString: $0.baseAddress!) } }
                    event.post(tap: .cghidEventTap)
                }
                prepared += chunk
                for _ in 0..<20 {
                    if (attribute(field.element,kAXValueAttribute) as? String) == prefix + prepared { break }
                    usleep(5000)
                }
                guard (attribute(field.element,kAXValueAttribute) as? String) == prefix + prepared else {
                    record("prepare",false,"Fixture Unicode preparation did not settle"); return false
                }
            }
        }
        guard (attribute(field.element, kAXValueAttribute) as? String) == value,
              setSelection(field.element, NSRange(location: range.location + prefix.utf16.count, length: range.length)) else {
            record("prepare", false, "Fixture value or selection did not settle"); return false
        }
        for _ in 0..<16 {
            if self.field()?.range == range { break }
            usleep(5000)
        }
        app.detector.reset(); app.autoGeneration += 1; return true
    }
    func postShift(_ down: Bool) {
        guard !down || field() != nil else { return }
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
                self.later(0.04) { self.postShift(false); self.later(0.35) { self.resultWhenReady(done) } }
            }
        }
    }
    func resultWhenReady(_ done: @escaping () -> Void, attempts: Int = 30) {
        guard field() != nil else {
            if attempts > 0 { later(0.05) { self.resultWhenReady(done, attempts: attempts - 1) } }
            else { record("focus", false, "Fixture focus did not settle after input-source change"); finish() }
            return
        }
        done()
    }
    func selected() {
        editor?.activate(options: [.activateIgnoringOtherApps])
        if let editor = editor, target != nil, target != "com.apple.Notes",
           let windows = attribute(AXUIElementCreateApplication(editor.processIdentifier), kAXWindowsAttribute) as? [AXUIElement],
           let fixtureWindow = windows.first(where: { (attribute($0, kAXTitleAttribute) as? String ?? "").contains("Shift Switcher — isolated compatibility fixture") }) {
            AXUIElementPerformAction(fixtureWindow, kAXRaiseAction as CFString)
        }
        later { self.selectedWhenReady(40) }
    }
    func selectedWhenReady(_ attempts: Int) {
        if field() == nil, attempts > 0 {
            later(0.15) { self.selectedWhenReady(attempts - 1) }; return
        }
        postShift(false)
        guard prepare("ghbdtn", range: NSRange(location: 0, length: 6)) else { finish(); return }
        doubleShift {
            let field = self.field()
            let changed = field?.value == "привет" && field?.range == NSRange(location: 0, length: 6)
            self.record("double_shift_selection", changed, "value=\(field?.value ?? "<unavailable>"), range=\(String(describing: field?.range))")
            self.doubleShift {
                self.record("double_shift_selection_reverse", changed && self.field()?.value == "ghbdtn", "Repeated double Shift returns selected text")
                self.unselected()
            }
        }
    }
    func unselected() {
        guard prepare("test ghbdtn", range: NSRange(location: 11, length: 0)) else { finish(); return }
        doubleShift {
            let field = self.field()
            let changed = field?.value == "test привет" && field?.range == NSRange(location: 11, length: 0)
            self.record("double_shift_last_word", changed, "value=\(field?.value ?? "<unavailable>"), range=\(String(describing: field?.range))")
            self.doubleShift {
                self.record("double_shift_last_word_reverse", changed && self.field()?.value == "test ghbdtn", "Repeated double Shift returns last word")
                self.automatic()
            }
        }
    }
    func automatic() {
        guard prepare("ghbdtn", range: NSRange(location: 6, length: 0)) else { finish(); return }
        app.autoEnabled = true
        postSpace()
        later(0.8) {
            let field = self.field()
            let changed = field?.value == "привет "
            self.record("automatic_en_to_ru", changed, "value=\(field?.value ?? "<unavailable>")")
            self.doubleShift {
                self.record("automatic_undo_double_shift", changed && self.field()?.value == "ghbdtn ", "Automatic correction reversed; trailing space preserved")
                self.automaticReverse()
            }
        }
    }
    func automaticReverse() {
        guard prepare("руддщ", range: NSRange(location: 5, length: 0)) else { finish(); return }
        postSpace()
        later(0.8) {
            let field = self.field()
            let changed = field?.value == "hello "
            self.record("automatic_ru_to_en", changed, "value=\(field?.value ?? "<unavailable>")")
            guard self.app.shortcuts.setUndo(true) else {
                self.record("automatic_undo_registration", false, "Undo shortcut occupied"); self.finish(); return
            }
            self.app.shortcutPreferences.keyboardUndo = true
            self.postChord(6)
            self.later(0.6) {
                self.record("automatic_undo_hotkey", changed && self.field()?.value == "руддщ ", "Control+Option+Z restores automatic correction and space")
                self.validWord()
            }
        }
    }
    func validWord() {
        guard prepare("hello", range: NSRange(location: 5, length: 0)) else { finish(); return }
        postSpace()
        later(0.8) {
            self.record("valid_word_unchanged", self.field()?.value == "hello ", "value=\(self.field()?.value ?? "<unavailable>")")
            self.manualShortcut()
        }
    }
    func manualShortcut() {
        app.autoEnabled = false
        guard app.shortcuts.setCorrection(.controlOptionSpace), app.shortcuts.setUndo(true) else {
            record("hotkey_registration", false, "Test shortcuts occupied"); finish(); return
        }
        app.shortcutPreferences.correction = .controlOptionSpace
        app.shortcutPreferences.keyboardUndo = true
        guard prepare("ghbdtn", range: NSRange(location: 0, length: 6)) else { finish(); return }
        postChord(49)
        later(0.6) {
            let changed = self.field()?.value == "привет"
            self.record("hotkey_selection", changed, "Control+Option+Space in test fixture")
            self.postChord(6)
            self.later(0.6) {
                self.record("hotkey_undo", changed && self.field()?.value == "ghbdtn" && self.field()?.range == NSRange(location: 0, length: 6), "Control+Option+Z in same unchanged fixture")
                self.later(0.3) { self.longSelection() }
            }
        }
    }
    func awaitFixtureValue(_ expected: String, attempts: Int = 40, done: @escaping (Bool) -> Void) {
        if let current = field(), current.value == expected { done(true); return }
        guard attempts > 0 else { done(false); return }
        later(0.05) { self.awaitFixtureValue(expected, attempts: attempts - 1, done: done) }
    }
    func longSelection() {
        app.autoEnabled = false
        guard app.shortcuts.setCorrection(.doubleShift) else { record("long_selection", false, "Shortcut unavailable"); finish(); return }
        app.shortcutPreferences.correction = .doubleShift
        let input = String(repeating: "ghbdtn ", count: 8) + "😀 "
        guard prepare(input, range: NSRange(location: 0, length: input.utf16.count)) else { finish(); return }
        doubleShift {
            self.awaitFixtureValue(Layout.convert(input)) { changed in
                self.record("long_selection_with_emoji", changed, "Multi-event text replacement with intact emoji; value=" + (self.field()?.value ?? "<unavailable>"))
                guard changed else { self.finish(); return }
                self.postChord(6)
                self.awaitFixtureValue(input) { restored in
                    self.record("long_selection_undo", restored, "Restore complete selection without truncation; status=" + self.app.info.title)
                    self.changedTextUndo()
                }
            }
        }
    }
    func changedTextUndo() {
        guard prepare("ghbdtn", range: NSRange(location: 0, length: 6)) else { finish(); return }
        doubleShift {
            let changed = self.field()?.value == "привет"
            guard self.prepare("user edit", range: NSRange(location: 9, length: 0)) else { self.finish(); return }
            self.postChord(6)
            self.later(0.6) {
                self.record("undo_after_user_edit_is_refused", changed && self.field()?.value == "user edit", "New user text is preserved")
                self.mixedWords()
            }
        }
    }
    func mixedWords() {
        mixedCase("mesh wifi копbz", expected: "mesh wifi копия", automatic: false) {
            self.mixedCase("helдщ", expected: "hello", automatic: false) {
                self.mixedCase("копbz", expected: "копия", automatic: true) { self.finish() }
            }
        }
    }
    func mixedCase(_ input: String, expected: String, automatic: Bool, done: @escaping () -> Void) {
        app.autoEnabled = automatic
        guard prepare(input, range: NSRange(location: input.utf16.count, length: 0)) else { finish(); return }
        let name = automatic ? "mixed_automatic" : "mixed_manual_" + input
        let corrected = {
            let expectedValue = expected + (automatic ? " " : "")
            self.awaitFixtureValue(expectedValue) { changed in
                self.record(name, changed, "value=" + (self.field()?.value ?? "<unavailable>"))
                self.doubleShift {
                    self.record(name + "_undo", changed && self.field()?.value == input + (automatic ? " " : ""), "Exact mixed source restored")
                    done()
                }
            }
        }
        if automatic { postSpace(); later(0.8, corrected) } else { doubleShift(corrected) }
    }
    func postChord(_ key: CGKeyCode) {
        guard field() != nil else { record("focus", false, "Aborted shortcut outside fixture"); finish(); return }
        let down = CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: true)!
        down.flags = [.maskControl, .maskAlternate]; down.post(tap: .cghidEventTap)
        later(0.03) {
            guard self.field() != nil else { return }
            let up = CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: false)!
            up.flags = [.maskControl, .maskAlternate]; up.post(tap: .cghidEventTap)
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
        postShift(false) // Always release the modifier owned by this test.
        if app.shortcuts.setCorrection(originalShortcut) { app.shortcutPreferences.correction = originalShortcut }
        if app.shortcuts.setUndo(originalKeyboardUndo) { app.shortcutPreferences.keyboardUndo = originalKeyboardUndo }
        app.autoEnabled = originalAuto
        app.autoItem.state = originalAuto ? .on : .off
        app.detector.reset(); app.autoGeneration += 1
        if let input = originalInput { TISSelectInputSource(input) }
        let report: [String: Any] = ["testedAt": ISO8601DateFormatter().string(from: Date()), "fixture": target == nil ? fixture.path : "isolated fixture: " + (target ?? ""), "input": "CGEvent synthetic keyboard events; not physical keyboard", "accessibility": AXIsProcessTrusted(), "postEventAccess": CGPreflightPostEventAccess(), "results": results]
        if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) { try? data.write(to: output, options: .atomic) }
        app.report("Тест завершён: \(results.filter { $0["passed"] as? Bool == true }.count)/\(results.count)")
    }
}
import Carbon
