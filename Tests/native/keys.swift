import AppKit
import ApplicationServices
import Carbon
let target = NSWorkspace.shared.frontmostApplication
 guard target?.bundleIdentifier == "com.apple.TextEdit" else {
    fputs("Refusing: frontmost app is not TextEdit\n", stderr); exit(2)
}
let mode = CommandLine.arguments.dropFirst().first ?? "double-shift"
func post(_ code: CGKeyCode, down: Bool, flags: CGEventFlags, type: CGEventType? = nil) {
    let e = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)!
    e.flags = flags
    if let type = type { e.type = type }
    e.post(tap: .cghidEventTap)
}
if mode == "double-shift" {
    for _ in 0..<2 {
        post(56, down: true, flags: CGEventFlags(rawValue: CGEventFlags.maskShift.rawValue | 2), type: .flagsChanged)
        usleep(40000)
        post(56, down: false, flags: [], type: .flagsChanged)
        usleep(60000)
    }
} else if mode == "space" {
    post(49, down: true, flags: []); usleep(30000); post(49, down: false, flags: [])
} else { exit(3) }
print("Posted \(mode) to TextEdit, pid=\(target!.processIdentifier)")
