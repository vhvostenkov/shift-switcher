import AppKit
let application = NSApplication.shared
let first = GlobalShortcuts()
let second = GlobalShortcuts()
func require(_ passed: Bool, _ label: String) {
    guard passed else { fatalError(label) }
}
require(first.setCorrection(.controlOptionSpace), "cannot reserve correction shortcut")
require(!second.setCorrection(.controlOptionSpace), "conflict was not rejected")
require(first.setCorrection(.doubleShift), "cannot release correction shortcut")
require(second.setCorrection(.controlOptionSpace), "shortcut not reusable after release")
require(first.setUndo(true), "cannot reserve undo shortcut")
require(!second.setUndo(true), "undo conflict was not rejected")
require(first.setUndo(false), "cannot release undo shortcut")
require(second.setUndo(true), "undo not reusable after release")
require(second.setCorrection(.doubleShift) && second.setUndo(false), "cleanup failed")
print("PASS: 9 native hotkey registration checks (no keyboard events)")
