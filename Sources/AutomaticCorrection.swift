import Foundation

// Bounded local sentence context. Valid English words and unknown names remain
// unchanged; neighbouring unambiguous words only resolve single-letter tokens.
enum AutomaticCorrection {
    struct Plan {
        let range: NSRange
        let text: String
    }
    private struct Word {
        let range: NSRange
        let before: String
        let after: String
        let source: String
        let sourceValid: Bool
        let targetValid: Bool
        var target: String { source == "ru" ? "en" : "ru" }
    }
    static func plan(in value: String, caret: Int, minimumLocation: Int = 0, valid: (String, String) -> Bool,
                     ignored: (String) -> Bool) -> Plan? {
        let ns = value as NSString
        guard caret > 0, caret <= ns.length else { return nil }
        // Only completed tokens: an idle check must not convert the word still
        // being typed. Never read a previous paragraph or more than 512 characters.
        let prefix = ns.substring(to: caret)
        guard let boundary = prefix.lastIndex(where: { $0.isWhitespace }) else { return nil }
        let completed = String(prefix[...boundary])
        let line = completed.split(separator: "\n", omittingEmptySubsequences: false).last.map(String.init) ?? ""
        let window = String(line.suffix(512))
        let offset = completed.utf16.count - window.utf16.count
        let regex = try! NSRegularExpression(pattern: "\\S+")
        let matches = regex.matches(in: window, range: NSRange(location: 0, length: window.utf16.count)).suffix(12)
        let text = window as NSString
        var words: [Word] = []
        for match in matches {
            // A truncated first token is not a complete word.
            if match.range.location == 0 && line.utf16.count > window.utf16.count { continue }
            var token = text.substring(with: match.range)
            var range = NSRange(location: offset + match.range.location, length: match.range.length)
            guard !token.contains(where: { $0.isNumber || "@#:/\\=_-".contains($0) }) else { continue }
            // Prefer the full keyboard token (e.g. ghj,tkf). If it is not a
            // dictionary candidate, retry without trailing sentence punctuation.
            while !token.isEmpty {
                let after = Layout.convert(token)
                let source = Layout.language(in: token) ?? "en"
                let target = source == "ru" ? "en" : "ru"
                if ignored(token) || ignored(after) { break }
                let sourceValid = valid(token, source)
                let targetValid = !after.isEmpty && after.allSatisfy(\.isLetter) && valid(after, target)
                if token.allSatisfy(\.isLetter) || targetValid {
                    words.append(Word(range: range, before: token, after: after, source: source,
                                      sourceValid: sourceValid, targetValid: targetValid))
                    break
                }
                guard let last = token.last, ".,;:!?…\"»”)}".contains(last) else { break }
                range.length -= String(last).utf16.count
                token.removeLast()
            }
        }
        var votes: [String: Int] = [:]
        for word in words where word.before.count >= 2 {
            if word.sourceValid && !word.targetValid { votes[word.source, default: 0] += 1 }
            if !word.sourceValid && word.targetValid { votes[word.target, default: 0] += 1 }
        }
        let edits = words.filter { word in
            guard word.range.location >= minimumLocation, word.before != word.after, word.targetValid else { return false }
            if word.before.count >= 2 { return !word.sourceValid }
            let single = word.target == "ru" ? Set(["а", "в", "и", "к", "о", "с", "у", "я"]) : Set(["i", "I", "a", "A"])
            return single.contains(word.after.lowercased()) && votes[word.target, default: 0] >= 2 && votes[word.source, default: 0] == 0
        }
        guard let first = edits.first, let last = edits.last else { return nil }
        let range = NSRange(location: first.range.location, length: NSMaxRange(last.range) - first.range.location)
        let replacement = NSMutableString(string: ns.substring(with: range))
        for edit in edits.reversed() {
            replacement.replaceCharacters(in: NSRange(location: edit.range.location - range.location, length: edit.range.length), with: edit.after)
        }
        return Plan(range: range, text: replacement as String)
    }
}
