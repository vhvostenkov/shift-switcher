import Foundation

enum AppCorrectionMode: String, Codable, CaseIterable {
    case automatic, manual, disabled
    var title: String {
        switch self {
        case .automatic: return "Автоматика разрешена"
        case .manual: return "Только двойной Shift"
        case .disabled: return "Не исправлять"
        }
    }
}

final class CorrectionPreferences {
    private struct State: Codable {
        var appModes: [String: AppCorrectionMode] = [:]
        var ignoredWords: [String] = []
    }
    private let defaults: UserDefaults
    private let key = "correctionPreferences.v1"
    private var state: State
    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        if let data = defaults.data(forKey: key), let decoded = try? JSONDecoder().decode(State.self, from: data) {
            state = decoded
        } else { state = State() }
    }
    var appModes: [String: AppCorrectionMode] { state.appModes }
    var ignoredWords: [String] { state.ignoredWords.sorted() }
    func mode(for bundle: String, defaultExclusions: Set<String>) -> AppCorrectionMode {
        state.appModes[bundle] ?? (defaultExclusions.contains(bundle) ? .manual : .automatic)
    }
    @discardableResult func setMode(_ mode: AppCorrectionMode?, for bundle: String) -> Bool {
        guard !bundle.isEmpty, bundle.count <= 255,
              bundle.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) || ".-_".unicodeScalars.contains($0) }) else { return false }
        state.appModes[bundle] = mode; persist(); return true
    }
    @discardableResult func addIgnoredWord(_ input: String) -> Bool {
        let word = input.trimmingCharacters(in: .whitespacesAndNewlines).precomposedStringWithCanonicalMapping.lowercased()
        guard !word.isEmpty, word.count <= 100, !word.contains(where: { $0.isWhitespace || $0.isNewline }) else { return false }
        if !state.ignoredWords.contains(word) { state.ignoredWords.append(word); persist() }
        return true
    }
    func removeIgnoredWord(_ word: String) { state.ignoredWords.removeAll { $0 == word }; persist() }
    func ignores(_ word: String) -> Bool { state.ignoredWords.contains(word.precomposedStringWithCanonicalMapping.lowercased()) }
    private func persist() {
        if let data = try? JSONEncoder().encode(state) { defaults.set(data, forKey: key) }
    }
}
