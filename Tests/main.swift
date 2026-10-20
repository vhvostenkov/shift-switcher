import Foundation
import CryptoKit
var count = 0
func check(_ condition: @autoclosure () -> Bool, _ label: String) {
    count += 1
    if !condition() { fatalError(label) }
}
check(Layout.convert("ghbdtn") == "привет", "en to ru")
check(Layout.convert("руддщ") == "hello", "ru to en")
check(Layout.convert("Ghbdtn Vbh") == "Привет Мир", "case")
check(Layout.convert("hello привет") == "руддщ ghbdtn", "mixed selection")
check(Layout.convert("123\n🙂") == "123\n🙂", "unicode and digits")
check(Layout.convert(Layout.convert("Hello Мир")) == "Hello Мир", "roundtrip")
check(Layout.lastWord(in: "🙂 ghbdtn ", caret: 10, skipWhitespace: true) == NSRange(location: 3, length: 6), "utf16 trailing space")
check(Layout.lastWord(in: "one two", caret: 3, skipWhitespace: false) == NSRange(location: 0, length: 3), "caret inside line")
check(Layout.lastWord(in: "", caret: 0, skipWhitespace: true) == nil, "empty")
var detector = DoubleShift()
check(!detector.shift(code: 56, down: true, time: 0, otherModifiers: false), "first down")
check(!detector.shift(code: 56, down: false, time: 0.05, otherModifiers: false), "first tap")
check(!detector.shift(code: 56, down: true, time: 0.1, otherModifiers: false), "second down")
check(detector.shift(code: 56, down: false, time: 0.15, otherModifiers: false), "double tap")
detector.reset()
_ = detector.shift(code: 56, down: true, time: 0, otherModifiers: false)
detector.otherKey()
check(!detector.shift(code: 56, down: false, time: 0.05, otherModifiers: false), "shift letter")
_ = detector.shift(code: 56, down: true, time: 0.1, otherModifiers: false)
check(!detector.shift(code: 56, down: false, time: 0.15, otherModifiers: false), "no false double after chord")
detector.reset()
_ = detector.shift(code: 56, down: true, time: 0, otherModifiers: false)
check(!detector.shift(code: 56, down: false, time: 0.5, otherModifiers: false), "hold")
detector.reset()
_ = detector.shift(code: 56, down: true, time: 0, otherModifiers: false)
_ = detector.shift(code: 60, down: true, time: 0.01, otherModifiers: false)
_ = detector.shift(code: 56, down: false, time: 0.04, otherModifiers: false)
check(!detector.shift(code: 60, down: false, time: 0.06, otherModifiers: false), "both shifts")


let suiteName = "ShiftSwitcher.tests." + UUID().uuidString
let isolatedDefaults = UserDefaults(suiteName: suiteName)!
let prefs = CorrectionPreferences(defaults: isolatedDefaults)
let terminalExclusions: Set<String> = ["com.apple.Terminal"]
check(prefs.mode(for: "com.apple.Terminal", defaultExclusions: terminalExclusions) == .manual, "terminal default is manual")
check(prefs.mode(for: "com.apple.TextEdit", defaultExclusions: terminalExclusions) == .automatic, "normal app default")
check(prefs.mode(for: "com.apple.Passwords", defaultExclusions: []) == .disabled, "password manager always disabled")
check(!prefs.setMode(.automatic, for: "com.1password.1password"), "password manager cannot enable automation")
check(!prefs.setMode(.manual, for: "com.agilebits.onepassword7"), "password manager cannot enable manual correction")
check(prefs.setMode(nil, for: "com.apple.Passwords"), "stale protected app preference can be cleared")
check(prefs.setMode(.disabled, for: "com.apple.TextEdit"), "disable app")
check(prefs.mode(for: "com.apple.TextEdit", defaultExclusions: terminalExclusions) == .disabled, "disabled retained")
check(prefs.setMode(.automatic, for: "com.apple.Terminal"), "explicit override")
check(prefs.mode(for: "com.apple.Terminal", defaultExclusions: terminalExclusions) == .automatic, "override terminal default")
check(prefs.addIgnoredWord("  Winston  "), "normalize word")
check(prefs.ignores("WINSTON"), "case insensitive exception")
check(!prefs.addIgnoredWord("two words"), "reject phrases")
check(!prefs.addIgnoredWord(""), "reject empty exception")
check(!prefs.setMode(.disabled, for: "../invalid/path"), "reject invalid bundle")
let loaded = CorrectionPreferences(defaults: isolatedDefaults)
check(loaded.ignores("winston"), "word survives reload")
check(loaded.mode(for: "com.apple.TextEdit", defaultExclusions: []) == .disabled, "profile survives reload")
loaded.removeIgnoredWord("winston")
check(!loaded.ignores("Winston"), "remove exception")
_ = loaded.setMode(nil, for: "com.apple.Terminal")
check(loaded.mode(for: "com.apple.Terminal", defaultExclusions: terminalExclusions) == .manual, "restore app defaults")
isolatedDefaults.set(Data("invalid JSON".utf8), forKey: "correctionPreferences.v1")
let recovered = CorrectionPreferences(defaults: isolatedDefaults)
check(recovered.ignoredWords.isEmpty && recovered.appModes.isEmpty, "corrupt settings fallback")
let shortcutPrefs = ShortcutPreferences(defaults: isolatedDefaults)
check(shortcutPrefs.correction == .doubleShift, "double shift remains default")
check(!shortcutPrefs.keyboardUndo, "keyboard undo opt in")
shortcutPrefs.correction = .controlOptionSpace
shortcutPrefs.keyboardUndo = true
let loadedShortcuts = ShortcutPreferences(defaults: isolatedDefaults)
check(loadedShortcuts.correction == .controlOptionSpace, "shortcut persists")
check(loadedShortcuts.keyboardUndo, "undo shortcut persists")
isolatedDefaults.set("unknownShortcut", forKey: "correctionShortcut.v1")
check(shortcutPrefs.correction == .doubleShift, "unknown shortcut falls back safely")
shortcutPrefs.correction = .doubleShift
shortcutPrefs.keyboardUndo = false
check(loadedShortcuts.correction == .doubleShift && !loadedShortcuts.keyboardUndo, "shortcut reset survives reload")
isolatedDefaults.removePersistentDomain(forName: suiteName)

let testSigner = Curve25519.Signing.PrivateKey()
let testDevice = String(repeating: "a", count: 64)
let testNow: Int64 = 1_800_000_000
func encoded(_ data: Data) -> String { data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
func certificate(_ changes: [String: Any] = [:]) -> String {
    var body: [String: Any] = ["schema": 1, "product": "shift-switcher-pro-v1", "license": "test-order", "device": testDevice, "issued_at": testNow, "valid_until": testNow + 30 * 86400, "key_id": "test", "environment": "DEMO"]
    for (key, value) in changes { body[key] = value }
    let message = "ss1." + encoded(try! JSONSerialization.data(withJSONObject: body, options: [.sortedKeys]))
    return message + "." + encoded(try! testSigner.signature(for: Data(message.utf8)))
}
let testKeys = ["DEMO:test": testSigner.publicKey.rawRepresentation.base64EncodedString()]
check(LicenseCheck.verify(certificate(), device: testDevice, keys: testKeys, environment: "DEMO", now: testNow) != nil, "valid Ed25519 certificate")
check(LicenseCheck.verify(certificate(), device: String(repeating: "b", count: 64), keys: testKeys, environment: "DEMO", now: testNow) == nil, "certificate bound to device")
check(LicenseCheck.verify(certificate(), device: testDevice, keys: testKeys, environment: "LIVE", now: testNow) == nil, "demo certificate cannot activate live app")
check(LicenseCheck.verify(certificate(["environment": "LIVE"]), device: testDevice, keys: testKeys, environment: "LIVE", now: testNow) == nil, "demo signing key cannot mint live certificate")
check(LicenseCheck.verify(certificate(["valid_until": testNow]), device: testDevice, keys: testKeys, environment: "DEMO", now: testNow) == nil, "expired certificate")
check(LicenseCheck.verify(certificate(["issued_at": testNow + 86400]), device: testDevice, keys: testKeys, environment: "DEMO", now: testNow) == nil, "future certificate rejected")
check(LicenseCheck.verify(certificate() + "tampered", device: testDevice, keys: testKeys, environment: "DEMO", now: testNow) == nil, "tampered signature rejected")
var trialTest = TrialState(started: testNow, latest: testNow)
check(trialTest.daysRemaining(now: testNow) == 14, "full 14 day trial")
check(trialTest.daysRemaining(now: testNow + 13 * 86400) == 1, "trial last day")
check(trialTest.daysRemaining(now: testNow) == 0, "trial rollback rejected")
check(trialTest.daysRemaining(now: testNow + 15 * 86400) == 0, "trial expired")

let storeFolder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
let localStore = LicenseStore(folder: storeFolder)
check(localStore.write("installation", "test-installation"), "private license storage saves")
let storedTestCertificate = certificate()
check(localStore.write("certificate", storedTestCertificate), "private license storage saves certificate")
let reopenedStore = LicenseStore(folder: storeFolder)
check(reopenedStore.read("installation") == "test-installation", "installation survives restart")
check(reopenedStore.read("certificate") == storedTestCertificate, "certificate survives restart")
check((try! FileManager.default.attributesOfItem(atPath: storeFolder.path)[.posixPermissions] as! NSNumber).intValue == 0o700, "license folder is private")
check((try! FileManager.default.attributesOfItem(atPath: storeFolder.appendingPathComponent("license-state.json").path)[.posixPermissions] as! NSNumber).intValue == 0o600, "license file is private")

check(TextEvents.chunks("привет hello")?.joined() == "привет hello", "Unicode chunks preserve text")
let emojiText = String(repeating: "😀привет ", count: 8)
check(TextEvents.chunks(emojiText)?.joined() == emojiText, "Unicode chunks preserve surrogate pairs")
check(TextEvents.chunks(emojiText)?.allSatisfy { $0.utf16.count <= 16 } == true, "Unicode event chunks stay bounded")
check(TextEvents.chunks("a" + String(repeating: "\u{0301}", count: 30)) == nil, "oversized composed character is rejected before input")

print("PASS: \(count) checks")
