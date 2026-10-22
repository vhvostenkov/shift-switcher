import Foundation

enum Layout {
    private static let en = Array("`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?")
    private static let ru = Array("ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,")
    private static let forward = Dictionary(uniqueKeysWithValues: zip(en, ru))
    private static let backward = Dictionary(uniqueKeysWithValues: zip(ru, en))

    static func isRussian(_ c: Character) -> Bool {
        c.unicodeScalars.contains { (0x0400...0x04ff).contains($0.value) }
    }
    static func language(in text: String) -> String? {
        for c in text.reversed() {
            if isRussian(c) { return "ru" }
            if c.isASCII && c.isLetter { return "en" }
        }
        return nil
    }
    static func permitsAutomaticConversion(_ before: String, to after: String) -> Bool {
        // Punctuation keys can be letters in the other layout (comma → б).
        before.count >= 2 && !after.isEmpty && after.allSatisfy { $0.isLetter }
    }
    static func convert(_ text: String) -> String {
        // The final typed alphabet sets the direction. In mixed words the other
        // alphabet is already correct: копbz → копия, helдщ → hello.
        var output = "", token = ""
        func flush() {
            let map = language(in: token) == "ru" ? backward : forward
            output += String(token.map { map[$0] ?? $0 }); token = ""
        }
        for c in text {
            if c.isWhitespace { flush(); output.append(c) } else { token.append(c) }
        }
        flush(); return output
    }
    static func lastWord(in value: String, caret: Int, skipWhitespace: Bool) -> NSRange? {
        let ns = value as NSString
        guard caret >= 0, caret <= ns.length else { return nil }
        var end = caret
        if skipWhitespace {
            while end > 0, CharacterSet.whitespacesAndNewlines.characterIsMember(ns.character(at: end - 1)) { end -= 1 }
        }
        var start = end
        while start > 0 {
            let u = ns.character(at: start - 1)
            guard let scalar = UnicodeScalar(u), CharacterSet.letters.contains(scalar) || "'`[];,.".unicodeScalars.contains(scalar) else { break }
            start -= 1
        }
        return end > start ? NSRange(location: start, length: end - start) : nil
    }
}

private extension CharacterSet {
    func characterIsMember(_ u: unichar) -> Bool { UnicodeScalar(u).map(contains) ?? false }
}

struct DoubleShift {
    private var pressed = Set<Int>()
    private var downAt: TimeInterval?
    private var lastTap: TimeInterval?
    private var chord = false
    mutating func reset() { pressed.removeAll(); downAt = nil; lastTap = nil; chord = false }
    mutating func otherKey() { chord = true; lastTap = nil }
    mutating func shift(code: Int, down: Bool, time: TimeInterval, otherModifiers: Bool) -> Bool {
        if down {
            if !pressed.isEmpty { chord = true; lastTap = nil }
            else { downAt = time; chord = otherModifiers }
            pressed.insert(code)
            return false
        }
        guard pressed.remove(code) != nil else { return false }
        guard pressed.isEmpty, !chord, !otherModifiers, let start = downAt, time - start < 0.30 else {
            lastTap = nil; return false
        }
        downAt = nil
        if let last = lastTap, time - last <= 0.35 {
            lastTap = nil; return true
        }
        lastTap = time; return false
    }
}

// Quartz input chunks keep composed characters and UTF-16 surrogate pairs intact.
enum TextEvents {
    static func chunks(_ text: String, limit: Int = 16) -> [String]? {
        var result: [String] = [], current = ""
        for character in text {
            let part = String(character)
            guard part.utf16.count <= limit else { return nil }
            if current.utf16.count + part.utf16.count > limit { result.append(current); current = "" }
            current += part
        }
        if !current.isEmpty { result.append(current) }
        return result
    }
}
