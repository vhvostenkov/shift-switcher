import AppKit
import Carbon
import ApplicationServices

func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
}
func selection(_ element: AXUIElement) -> NSRange? {
    guard let raw = attribute(element, kAXSelectedTextRangeAttribute), CFGetTypeID(raw) == AXValueGetTypeID() else { return nil }
    let value = unsafeBitCast(raw, to: AXValue.self)
    guard AXValueGetType(value) == .cfRange else { return nil }
    var range = CFRange()
    guard AXValueGetValue(value, .cfRange, &range), range.location >= 0, range.length >= 0 else { return nil }
    return NSRange(location: range.location, length: range.length)
}
@discardableResult func setSelection(_ element: AXUIElement, _ range: NSRange) -> Bool {
    var cfRange = CFRange(location: range.location, length: range.length)
    guard let value = AXValueCreate(.cfRange, &cfRange) else { return false }
    return AXUIElementSetAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, value) == .success
}
struct Field {
    let element: AXUIElement
    let value: String
    let range: NSRange
    let pid: pid_t
}
struct Edit {
    let element: AXUIElement
    let pid: pid_t
    let before: String
    let after: String
    let range: NSRange
    let originalSelection: NSRange
    let expectedValue: String
    let sourceLanguage: String?
}

final class App: NSObject, NSApplicationDelegate {
    let preferences = CorrectionPreferences()
    let license = LicenseManager()
    let shortcutPreferences = ShortcutPreferences(defaults: .standard)
    let shortcuts = GlobalShortcuts()
    private var lastShortcutTime: TimeInterval = 0
    var settingsWindow: SettingsWindow?
    var acceptance: NativeAcceptance?
    var status: NSStatusItem!
    var tap: CFMachPort?
    var source: CFRunLoopSource?
    var detector = DoubleShift()
    var autoItem: NSMenuItem!
    var pauseItem: NSMenuItem!
    var info: NSMenuItem!
    var autoEnabled = UserDefaults.standard.bool(forKey: "autoEnabled")
    var paused = false
    var lastEdit: Edit?
    var autoGeneration = 0
    private var pendingAutomaticField: Field?
    private var automaticUndoPrefix: Field?
    private var keyboardConflict = false
    private let diagnostics = ProcessInfo.processInfo.environment["SHIFT_SWITCHER_DIAGNOSTICS"] == "1"
    let excluded = Set(["com.apple.Terminal", "com.googlecode.iterm2", "com.microsoft.VSCode", "dev.zed.Zed", "com.jetbrains.intellij", "com.jetbrains.pycharm", "com.apple.dt.Xcode", "com.agilebits.onepassword7", "com.1password.1password", "com.apple.Passwords"])

    func applicationDidFinishLaunching(_ notification: Notification) {
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(updateIndicator), name: NSNotification.Name(kTISNotifySelectedKeyboardInputSourceChanged as String), object: nil)
        updateIndicator()
        let menu = NSMenu()
        info = NSMenuItem(title: "Исправление: выделение / последнее слово", action: nil, keyEquivalent: "")
        menu.addItem(info)
        menu.addItem(.separator())
        _ = item("Открыть Shift Switcher…", #selector(showSettings), menu)
        autoItem = item("Автоисправление RU ↔ EN", #selector(toggleAuto), menu)
        autoItem.state = autoEnabled ? .on : .off
        pauseItem = item("Пауза", #selector(togglePause), menu)
        _ = item("Исправить символ слева от курсора · ⌃⌥.", #selector(correctCharacter), menu)
        _ = item("Отменить последнее исправление", #selector(undo), menu)
        menu.addItem(.separator())
        _ = item("Разрешить управление текстом…", #selector(permissions), menu)
        _ = item("Подключить клавиатуру повторно", #selector(connect), menu)
        _ = item("Как пользоваться…", #selector(help), menu)
        menu.addItem(.separator())
        _ = item("Завершить Shift Switcher", #selector(quit), menu)
        status.menu = menu
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(checkKeyboardConflict), name: NSWorkspace.didLaunchApplicationNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(checkKeyboardConflict), name: NSWorkspace.didTerminateApplicationNotification, object: nil)
        checkKeyboardConflict()
        license.renewIfNeeded()
        shortcuts.perform = { [weak self] action in
            guard let self = self, !self.paused else { return }
            let now = ProcessInfo.processInfo.systemUptime
            guard now - self.lastShortcutTime >= 0.3 else { return }
            self.lastShortcutTime = now
            self.detector.reset(); self.autoGeneration += 1
            if action == 1 { self.correct(automatic: false) }
            else if action == 2 { self.undo() }
            else if action == 3 { self.correct(automatic: false, character: true) }
        }
        if !shortcuts.setCharacterCorrection() { report("⌃⌥. занято: исправление символа доступно в меню") }
        if !shortcuts.setCorrection(shortcutPreferences.correction) {
            shortcutPreferences.correction = .doubleShift
            report("Горячая клавиша занята — используйте двойной Shift")
        }
        if !shortcuts.setUndo(shortcutPreferences.keyboardUndo) {
            shortcutPreferences.keyboardUndo = false
            report("Клавиша отмены занята — отмена доступна в меню")
        }
        fputs("accessibility=\(AXIsProcessTrusted()) inputMonitoring=\(CGPreflightListenEventAccess())\n", stderr)
        connect()
        if let argument = CommandLine.arguments.first(where: { $0.hasPrefix("--compatibility-acceptance=") }) {
            let target = String(argument.dropFirst("--compatibility-acceptance=".count))
            acceptance = NativeAcceptance(app: self, target: target)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.acceptance?.start() }
        } else if CommandLine.arguments.contains("--native-acceptance") {
            acceptance = NativeAcceptance(app: self)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.acceptance?.start() }
        } else if !CommandLine.arguments.contains("--background") && NSAppleEventManager.shared().currentAppleEvent?.paramDescriptor(forKeyword: AEKeyword(keyAELaunchedAsLogInItem))?.booleanValue != true {
            showSettings()
        }
    }
    @objc func showSettings() {
        if settingsWindow == nil { settingsWindow = SettingsWindow(app: self) }
        settingsWindow?.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showSettings(); return true }
    func item(_ title: String, _ action: Selector, _ menu: NSMenu) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self; menu.addItem(item); return item
    }
    func report(_ message: String, failure: Bool = false) {
        fputs("[Shift Switcher] \(message)\n", stderr)
        info.title = message
        updateIndicator()
        status.button?.toolTip = (status.button?.toolTip ?? "") + "\n" + message
        if failure { NSSound.beep() }
    }
    @objc func checkKeyboardConflict() {
        keyboardConflict = !NSRunningApplication.runningApplications(withBundleIdentifier: "com.devdanpro.LangSwitcher").isEmpty
        if keyboardConflict { report("Конфликт клавиш: завершите оригинальный Lang Switcher") }
        else { updateIndicator() }
    }
    private func protectAutomaticPrefix(_ field: Field, value: String, end: Int) {
        automaticUndoPrefix = Field(element: field.element, value: (value as NSString).substring(to: end),
                                    range: NSRange(location: end, length: 0), pid: field.pid)
    }
    @objc func updateIndicator() {
        let input = TISCopyCurrentKeyboardInputSource().takeRetainedValue()
        var layout = "—"
        if let property = TISGetInputSourceProperty(input, kTISPropertyInputSourceLanguages) {
            let languages = Unmanaged<CFArray>.fromOpaque(property).takeUnretainedValue() as! [String]
            layout = languages.first?.uppercased() ?? "—"
        }
        let permitted = AXIsProcessTrusted() && CGPreflightListenEventAccess()
        let state = keyboardConflict ? "Конфликт: завершите Lang Switcher" : (!permitted ? "Нужны разрешения macOS" : (paused ? "Пауза" : (autoEnabled ? "Автоисправление включено" : "Ручное исправление")))
        status.button?.title = "⇧⇧ \(layout)"
        status.button?.image = NSImage(systemSymbolName: (!permitted || keyboardConflict) ? "exclamationmark.circle" : (paused ? "pause.circle" : (autoEnabled ? "bolt.circle.fill" : "hand.point.up")), accessibilityDescription: state)
        status.button?.imagePosition = .imageLeading
        status.button?.toolTip = "Shift Switcher · \(layout) · \(state)\nНажмите, чтобы открыть настройки и отмену"
        status.button?.setAccessibilityLabel("Shift Switcher, \(layout), \(state)")
    }
    @objc func connect() {
        guard tap == nil else { return }
        let mask = (CGEventMask(1) << CGEventType.flagsChanged.rawValue) | (CGEventMask(1) << CGEventType.keyDown.rawValue) | (CGEventMask(1) << CGEventType.leftMouseDown.rawValue)
        tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly, eventsOfInterest: mask, callback: { _, type, event, context in
            guard let context = context else { return Unmanaged.passUnretained(event) }
            let app = Unmanaged<App>.fromOpaque(context).takeUnretainedValue()
            app.handle(type, event)
            return Unmanaged.passUnretained(event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque())
        guard let tap = tap else { report("Нужен доступ: Мониторинг ввода", failure: true); return }
        source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        report(AXIsProcessTrusted() ? "Готово — \(shortcutPreferences.correction.title)" : "Нужен доступ: Универсальный доступ")
    }
    func handle(_ type: CGEventType, _ event: CGEvent) {
        guard event.getIntegerValueField(.eventSourceUserData) != 0x535357 else { return }
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            detector.reset(); autoGeneration += 1
            if let tap = tap { CGEvent.tapEnable(tap: tap, enable: true) }; return
        }
        guard !paused, !IsSecureEventInputEnabled() else { detector.reset(); autoGeneration += 1; return }
        if type == .leftMouseDown { detector.reset(); autoGeneration += 1; pendingAutomaticField = nil; return }
        let code = Int(event.getIntegerValueField(.keyboardEventKeycode))
        let modifiers = event.flags.intersection([.maskCommand, .maskControl, .maskAlternate, .maskSecondaryFn])
        let time = ProcessInfo.processInfo.systemUptime
        if type == .flagsChanged {
            if code == 56 || code == 60 {
                // Device-specific flags distinguish left/right releases when both are held.
                let deviceMask: UInt64 = code == 56 ? 0x00000002 : 0x00000004
                let down = event.flags.rawValue & deviceMask != 0
                if detector.shift(code: code, down: down, time: time, otherModifiers: !modifiers.isEmpty), shortcutPreferences.correction == .doubleShift {
                    autoGeneration += 1
                    DispatchQueue.main.async { self.correct(automatic: false) }
                }
            } else { detector.otherKey() }
        } else if type == .keyDown {
            detector.otherKey(); autoGeneration += 1
            // Respect the user's ordinary Undo even in hosts that keep their
            // own editing history. Protect only the exact expected restoration.
            if code == 6, modifiers == .maskCommand, !event.flags.contains(.maskShift),
               let edit = lastEdit, let current = sameField(Field(element: edit.element, value: edit.expectedValue, range: edit.range, pid: edit.pid)),
               current.value == edit.expectedValue {
                let restored = (edit.expectedValue as NSString).replacingCharacters(in: edit.range, with: edit.before)
                protectAutomaticPrefix(current, value: restored, end: edit.range.location + edit.before.utf16.count)
            }
            if diagnostics && code == 49 {
                fputs("[diagnostic] space auto=\(autoEnabled) pro=\(license.hasPro) modifiers=\(modifiers.rawValue) field=\(field() != nil) app=\(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "none")\n", stderr)
            }
            guard autoEnabled, license.hasPro, modifiers.isEmpty,
                  let bundle = NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
                  preferences.mode(for: bundle, defaultExclusions: excluded) == .automatic else {
                pendingAutomaticField = nil; return
            }
            let generation = autoGeneration
            if code == 49 {
                guard let field = field() else { pendingAutomaticField = nil; return }
                pendingAutomaticField = field
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.005) {
                    self.correctAfterSpace(field, generation: generation, attempts: 20)
                }
            } else if code <= 50, ![36, 48].contains(code), let field = pendingAutomaticField {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
                    guard self.autoGeneration == generation, !self.paused,
                          self.autoEnabled, let current = self.sameField(field),
                          current.range.length == 0 else { return }
                    self.pendingAutomaticField = nil
                    self.correct(automatic: true)
                }
            } else { pendingAutomaticField = nil }
        }
    }
    private func correctAfterSpace(_ field: Field, generation: Int, attempts: Int) {
        guard autoGeneration == generation, autoEnabled, !paused,
              let current = sameField(field) else { return }
        let expected = (field.value as NSString).replacingCharacters(in: field.range, with: " ")
        let caret = NSRange(location: field.range.location + 1, length: 0)
        if current.value == expected, current.range == caret {
            pendingAutomaticField = nil
            correct(automatic: true); return
        }
        // AX value and caret can settle separately after the key event. Only wait
        // for this exact insertion; any other input or field state cancels it.
        guard attempts > 0, current.value == field.value || current.value == expected,
              current.range == field.range || current.range == caret else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.01) {
            self.correctAfterSpace(field, generation: generation, attempts: attempts - 1)
        }
    }
    func field() -> Field? {
        guard AXIsProcessTrusted(), !IsSecureEventInputEnabled(),
              let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != getpid(),
              !CorrectionPreferences.protectedApps.contains(app.bundleIdentifier ?? "") else { return nil }
        let root = AXUIElementCreateApplication(app.processIdentifier)
        guard let raw = attribute(root, kAXFocusedUIElementAttribute), CFGetTypeID(raw) == AXUIElementGetTypeID() else { return nil }
        let element = unsafeBitCast(raw, to: AXUIElement.self)
        guard (attribute(element, kAXSubroleAttribute) as? String) != "AXSecureTextField",
              let value = attribute(element, kAXValueAttribute) as? String,
              let range = selection(element), NSMaxRange(range) <= (value as NSString).length else { return nil }
        return Field(element: element, value: value, range: range, pid: app.processIdentifier)
    }
    @objc func correctCharacter() { correct(automatic: false, character: true) }
    func correct(automatic: Bool, character: Bool = false) {
        if !automatic { pendingAutomaticField = nil }
        guard !keyboardConflict else {
            if !automatic { report("Сначала завершите оригинальный Lang Switcher", failure: true) }; return
        }
        guard !paused, let field = field() else {
            if !automatic { report("Поле недоступно. Проверьте Универсальный доступ", failure: true) }; return
        }
        guard let bundle = NSWorkspace.shared.frontmostApplication?.bundleIdentifier else { return }
        let mode = preferences.mode(for: bundle, defaultExclusions: excluded)
        guard mode != .disabled, !automatic || mode == .automatic else {
            if !automatic { report("Для этого приложения исправление отключено", failure: true) }; return
        }
        // A second manual correction restores the exact edit, including a mixed
        // source word. Re-converting a now-uniform word would change its prefix.
        if !automatic, !character, let edit = lastEdit, field.pid == edit.pid,
           CFEqual(field.element, edit.element), field.value == edit.expectedValue {
            let expectedSelection = edit.originalSelection.length > 0 ? edit.range : NSRange(
                location: edit.originalSelection.location + edit.after.utf16.count - edit.before.utf16.count, length: 0)
            if field.range == expectedSelection { undo(); return }
        }
        let ns = field.value as NSString
        let range: NSRange
        let after: String
        if automatic {
            var minimumLocation = 0
            if let undo = automaticUndoPrefix {
                if undo.pid == field.pid, CFEqual(undo.element, field.element), field.value.hasPrefix(undo.value) {
                    minimumLocation = undo.range.location
                } else { automaticUndoPrefix = nil }
            }
            guard license.hasPro, field.range.length == 0,
                  let plan = AutomaticCorrection.plan(in: field.value, caret: field.range.location, minimumLocation: minimumLocation,
                    valid: { self.validWord($0, language: $1) }, ignored: { self.preferences.ignores($0) }) else { return }
            range = plan.range; after = plan.text
        } else if field.range.length > 0 {
            range = field.range; after = Layout.convert(ns.substring(with: range), sourceLanguage: currentInputLanguage())
        } else if character {
            guard let symbol = Layout.previousCharacter(in: field.value, caret: field.range.location) else { return }
            range = symbol; after = Layout.convert(ns.substring(with: symbol), sourceLanguage: currentInputLanguage())
        } else {
            guard let word = Layout.lastWord(in: field.value, caret: field.range.location, skipWhitespace: true) else {
                report("Нет слова перед курсором", failure: true); return
            }
            range = word; after = Layout.convert(ns.substring(with: word))
        }
        let before = ns.substring(with: range)
        guard before != after else {
            if !automatic { report("Этот символ совпадает в RU/EN — заменять нечего") }; return
        }
        let sourceLanguage = Layout.language(in: before) ?? currentInputLanguage()
        let targetLanguage = Layout.language(in: after) ?? (sourceLanguage == "ru" ? "en" : "ru")
        guard replace(field: field, range: range, text: after) else {
            if !automatic { report("Не удалось безопасно заменить текст в этом поле", failure: true) }; return
        }
        let expected = ns.replacingCharacters(in: range, with: after)
        let newRange = NSRange(location: range.location, length: (after as NSString).length)
        if field.range.length > 0 { setSelection(field.element, newRange) }
        else {
            let caret = field.range.location + newRange.length - range.length
            setSelection(field.element, NSRange(location: caret, length: 0))
        }
        lastEdit = Edit(element: field.element, pid: field.pid, before: before, after: after, range: newRange, originalSelection: field.range, expectedValue: expected, sourceLanguage: sourceLanguage)
        if !automatic { protectAutomaticPrefix(field, value: expected, end: NSMaxRange(newRange)) }
        guard switchInput(for: after, language: targetLanguage) else {
            report("Текст исправлен, но macOS не переключила раскладку", failure: true); return
        }
        report(automatic ? "Автоматически исправлено · отмена в меню ⇧⇧" : "Раскладка исправлена · повторите \(shortcutPreferences.correction.title) для обратной замены")
    }
    private func sameField(_ field: Field) -> Field? {
        guard let current = self.field(), current.pid == field.pid, CFEqual(current.element, field.element) else { return nil }
        return current
    }
    private func awaitValue(_ expected: String, field: Field) -> Bool {
        for _ in 0..<16 {
            guard let current = sameField(field) else { return false }
            if current.value == expected { return true }
            usleep(5000)
        }
        return false
    }
    func replace(field: Field, range: NSRange, text: String) -> Bool {
        guard let current = sameField(field), current.value == field.value, current.range == field.range,
              setSelection(field.element, range) else { return false }
        for _ in 0..<16 {
            guard let selected = sameField(field), selected.value == field.value else { return false }
            if selected.range == range { break }
            usleep(5000)
        }
        guard let selected = sameField(field), selected.value == field.value, selected.range == range else { return false }
        let expected = (field.value as NSString).replacingCharacters(in: range, with: text)
        // Pick exactly one write path. An AX setter may apply late in a web
        // host; following it with Unicode insertion can duplicate the result.
        let hosts: Set<String> = ["com.apple.Safari", "com.google.Chrome", "com.apple.Notes", "com.openai.codex"]
        if !hosts.contains(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "") {
            var settable = DarwinBoolean(false)
            guard AXUIElementIsAttributeSettable(field.element, kAXSelectedTextAttribute as CFString, &settable) == .success,
                  settable.boolValue,
                  AXUIElementSetAttributeValue(field.element, kAXSelectedTextAttribute as CFString, text as CFString) == .success else {
                setSelection(field.element, field.range); return false
            }
            return awaitValue(expected, field: field)
        }
        guard hosts.contains(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? ""),
              CGPreflightPostEventAccess(), text.utf16.count <= 256, let chunks = TextEvents.chunks(text),
              let unchanged = sameField(field), unchanged.value == field.value, unchanged.range == range else {
            if let unchanged = sameField(field), unchanged.value == field.value, unchanged.range == range { setSelection(field.element, field.range) }
            return false
        }
        var value = field.value, replacement = range
        for chunk in chunks {
            guard let current = sameField(field), current.value == value, current.range == replacement else { return false }
            let units = Array(chunk.utf16)
            for down in [true, false] {
                guard sameField(field) != nil else { return false }
                let event = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: down)!
                event.flags = []
                event.setIntegerValueField(.eventSourceUserData, value: 0x535357)
                if down { units.withUnsafeBufferPointer { event.keyboardSetUnicodeString(stringLength: units.count, unicodeString: $0.baseAddress!) } }
                event.postToPid(field.pid)
            }
            value = (value as NSString).replacingCharacters(in: replacement, with: chunk)
            guard awaitValue(value, field: field) else { return false }
            replacement = NSRange(location: replacement.location + units.count, length: 0)
        }
        return value == expected
    }
    func validWord(_ word: String, language: String) -> Bool {
        let checker = NSSpellChecker.shared
        guard checker.availableLanguages.contains(language) else { return false }
        return checker.checkSpelling(of: word, startingAt: 0, language: language, wrap: false, inSpellDocumentWithTag: 0, wordCount: nil).location == NSNotFound
    }
    func currentInputLanguage() -> String? {
        let input = TISCopyCurrentKeyboardInputSource().takeRetainedValue()
        guard let property = TISGetInputSourceProperty(input, kTISPropertyInputSourceLanguages) else { return nil }
        return (Unmanaged<CFArray>.fromOpaque(property).takeUnretainedValue() as! [String]).first
    }
    @discardableResult func switchInput(for text: String, language override: String? = nil) -> Bool {
        guard let language = override ?? Layout.language(in: text) else { return true }
        func matches(_ input: TISInputSource) -> Bool {
            guard let property = TISGetInputSourceProperty(input, kTISPropertyInputSourceLanguages) else { return false }
            let languages = Unmanaged<CFArray>.fromOpaque(property).takeUnretainedValue() as! [String]
            return languages.first == language
        }
        if matches(TISCopyCurrentKeyboardInputSource().takeRetainedValue()) { updateIndicator(); return true }
        let filter = [kTISPropertyInputSourceIsSelectCapable as String: true,
                      kTISPropertyInputSourceIsEnabled as String: true,
                      kTISPropertyInputSourceCategory as String: kTISCategoryKeyboardInputSource as String] as CFDictionary
        let sources = TISCreateInputSourceList(filter, false).takeRetainedValue() as! [TISInputSource]
        for input in sources where matches(input) {
            if TISSelectInputSource(input) == noErr,
               matches(TISCopyCurrentKeyboardInputSource().takeRetainedValue()) {
                updateIndicator(); return true
            }
        }
        return false
    }
    @objc func undo() {
        guard let edit = lastEdit, let current = field(), current.pid == edit.pid,
              CFEqual(current.element, edit.element), current.value == edit.expectedValue else {
            report("Отмена недоступна: текст или поле изменились", failure: true); return
        }
        guard replace(field: current, range: edit.range, text: edit.before) else { report("Не удалось отменить", failure: true); return }
        setSelection(current.element, edit.originalSelection)
        let restored = (current.value as NSString).replacingCharacters(in: edit.range, with: edit.before) as NSString
        let end = edit.range.location + edit.before.utf16.count
        protectAutomaticPrefix(current, value: restored as String, end: end)
        switchInput(for: edit.before, language: edit.sourceLanguage); lastEdit = nil; report("Исправление отменено")
    }
    @objc func toggleAuto() {
        autoEnabled.toggle(); autoGeneration += 1
        UserDefaults.standard.set(autoEnabled, forKey: "autoEnabled")
        autoItem.state = autoEnabled ? .on : .off
        report(autoEnabled ? "Автоисправление включено" : "Автоисправление выключено")
    }
    @objc func togglePause() {
        paused.toggle(); detector.reset(); autoGeneration += 1
        pauseItem.state = paused ? .on : .off
        updateIndicator()
    }
    @objc func permissions() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    @objc func help() {
        let alert = NSAlert()
        alert.messageText = "Shift Switcher"
        alert.informativeText = "Выделите текст и используйте \(shortcutPreferences.correction.title). Без выделения исправится последнее слово перед курсором. Повторное сочетание возвращает раскладку. Настройка клавиш — в «Личные правила → Клавиши».\n\nАвтоматика проверяет до 12 завершённых слов текущей строки по пробелу и после короткой паузы. Соседние слова помогают различить одиночные буквы RU/EN; правильные слова, адреса и числа сохраняются. Анализ локальный, по словарям macOS, без облачного ИИ. Для терминалов, редакторов кода и менеджеров паролей она по умолчанию отключена.\n\nНужны разрешения «Универсальный доступ» и «Мониторинг ввода». После выдачи доступа перезапустите приложение.\n\nТекст обрабатывается в памяти, не отправляется в сеть и не записывается в файлы. Поля без поддержки Accessibility пропускаются. Используйте один переключатель раскладки одновременно."
        alert.runModal()
    }
    @objc func quit() { NSApp.terminate(nil) }
}
if NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "local.viktor.ShiftSwitcher").contains(where: { $0.processIdentifier != getpid() }) { exit(0) }
let app = NSApplication.shared
let delegate = App()
app.delegate = delegate
app.run()
