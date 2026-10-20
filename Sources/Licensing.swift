import AppKit
import CryptoKit
import Security

final class LicenseManager {
    private let service = (Bundle.main.bundleIdentifier ?? "local.viktor.ShiftSwitcher") + ".licensing.v1"
    private let store = LicenseStore(folder: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent(Bundle.main.bundleIdentifier ?? "local.viktor.ShiftSwitcher", isDirectory: true))
    private var keys: [String: String] = [:]
    private var trial: TrialState?
    private var certificateCache: String?
    private var lastPersisted: Int64 = 0
    private var lastRenewal: Int64 = 0
    private var renewing = false
    private(set) var device = ""
    private(set) var days = 0
    private(set) var licensed = false
    private(set) var storageReady = false
    private(set) var message = ""
    private(set) var expires: Int64 = 0
    var hasPro: Bool { storageReady && (licensed || days > 0) }
    var environment: String { (Bundle.main.bundleIdentifier ?? "").hasSuffix(".Candidate") ? "DEMO" : "LIVE" }
    private var api: URL { URL(string: "https://vhvost.com/shift-switcher/api/")! }
    init() {
        if let url = Bundle.main.url(forResource: "license-public-keys", withExtension: "json"), let raw = try? Data(contentsOf: url),
           let decoded = try? JSONDecoder().decode([String: String].self, from: raw) { keys = decoded }
        // Legacy migration must never trigger an authentication dialog.
        if store.read("installation") == nil {
            for account in ["installation", "trial", "license_key", "certificate"] {
                if let value = legacyRead(account) { _ = store.write(account, value) }
            }
        }
        let id = read("installation") ?? UUID().uuidString
        guard write("installation", id) else { message = "Не удалось сохранить локальные данные лицензии"; return }
        device = SHA256.hash(data: Data(id.utf8)).map { String(format: "%02x", $0) }.joined()
        let now = Int64(Date().timeIntervalSince1970)
        if let raw = read("trial"), let data = raw.data(using: .utf8), let state = try? JSONDecoder().decode(TrialState.self, from: data) { trial = state }
        else if read("trial") == nil { trial = TrialState(started: now, latest: now) }
        certificateCache = read("certificate")
        storageReady = true
        refresh()
    }
    func read(_ account: String) -> String? { store.read(account) }
    private func legacyRead(_ account: String) -> String? {
        var result: CFTypeRef?
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
            kSecAttrAccount as String: account, kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne, kSecUseAuthenticationUI as String: kSecUseAuthenticationUIFail]
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
    @discardableResult func write(_ account: String, _ text: String) -> Bool { store.write(account, text) }
    func refresh() {
        let now = Int64(Date().timeIntervalSince1970)
        if var trial = trial {
            days = trial.daysRemaining(now: now)
            self.trial = trial
            if now - lastPersisted >= 60, let data = try? JSONEncoder().encode(trial), let text = String(data: data, encoding: .utf8) { storageReady = write("trial", text); lastPersisted = now }
        }
        licensed = false
        if let certificate = certificateCache, let payload = LicenseCheck.verify(certificate, device: device, keys: keys, environment: environment, now: now) {
            licensed = true; expires = payload.valid_until
        }
    }
    func activate(_ key: String, completion: @escaping () -> Void) {
        guard storageReady else { return }
        var request = URLRequest(url: api.appendingPathComponent("activate"))
        request.httpMethod = "POST"; request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["key": key, "device": device])
        URLSession.shared.dataTask(with: request) { data, response, _ in
            DispatchQueue.main.async {
                defer { completion() }
                if (response as? HTTPURLResponse)?.statusCode == 403, let data = data,
                   let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any], body["error"] as? String == "license_revoked" {
                    _ = self.write("certificate", ""); self.certificateCache = nil; self.refresh()
                    self.message = "Лицензия отозвана. Доступен Free и оставшийся пробный период."; return
                }
                guard (response as? HTTPURLResponse)?.statusCode == 200, let data = data, data.count <= 16384,
                      let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let certificate = body["certificate"] as? String,
                      LicenseCheck.verify(certificate, device: self.device, keys: self.keys, environment: self.environment, now: Int64(Date().timeIntervalSince1970)) != nil else {
                    self.message = "Активация не выполнена. Проверьте ключ, соединение и лимит двух Mac."; return
                }
                guard self.write("license_key", key), self.write("certificate", certificate) else {
                    self.message = "Не удалось сохранить лицензию на этом Mac"; return
                }
                self.certificateCache = certificate
                self.refresh(); self.message = "Лицензия активирована"
            }
        }.resume()
    }
    func renewIfNeeded() {
        let now = Int64(Date().timeIntervalSince1970)
        guard !renewing, expires < now + 7 * 86400, now - lastRenewal > 86400, read("license_key")?.isEmpty == false else { return }
        renewing = true; lastRenewal = now
        renew { self.renewing = false }
    }
    func renew(completion: @escaping () -> Void) {
        guard let key = read("license_key") else { return }
        activate(key, completion: completion)
    }
    func importFile(completion: @escaping () -> Void) {
        let panel = NSOpenPanel(); panel.allowedFileTypes = ["sslicense", "json"]; panel.allowsMultipleSelection = false
        panel.begin { result in
            guard result == .OK, let url = panel.url, let data = try? Data(contentsOf: url), data.count < 16384,
                  let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  body["product"] as? String == "shift-switcher-pro-v1", body["environment"] as? String == self.environment,
                  let key = body["license_key"] as? String else { self.message = "Файл лицензии не подходит этой сборке"; completion(); return }
            self.activate(key, completion: completion)
        }
    }
    func release(completion: @escaping () -> Void) {
        guard let key = read("license_key") else { return }
        var request = URLRequest(url: api.appendingPathComponent("release-device"))
        request.httpMethod = "POST"; request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["key": key, "device": device])
        URLSession.shared.dataTask(with: request) { _, response, _ in
            DispatchQueue.main.async {
                if (response as? HTTPURLResponse)?.statusCode == 200, self.write("certificate", ""), self.write("license_key", "") {
                    self.certificateCache = nil; self.refresh(); self.message = "Этот Mac освобождён"
                } else { self.message = "Не удалось освободить Mac. Проверьте соединение." }
                completion()
            }
        }.resume()
    }
    func buy() { NSWorkspace.shared.open(URL(string: "https://vhvost.com/shift-switcher/checkout")!) }
}
