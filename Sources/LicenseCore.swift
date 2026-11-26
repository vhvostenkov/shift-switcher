import Foundation
import CryptoKit

// Private per-user storage survives ad-hoc rebuilds without password dialogs.
// Paid access requires a server-signed, device-bound certificate.
final class LicenseStore {
    private let folder: URL
    private let file: URL
    private var values: [String: String] = [:]
    private var valid = true
    init(folder: URL) {
        self.folder = folder; file = folder.appendingPathComponent("license-state.json")
        if FileManager.default.fileExists(atPath: file.path) {
            guard let data = try? Data(contentsOf: file), data.count <= 65536,
                  let decoded = try? JSONDecoder().decode([String: String].self, from: data) else { valid = false; return }
            values = decoded
        }
    }
    func read(_ key: String) -> String? { values[key] }
    @discardableResult func write(_ key: String, _ value: String) -> Bool {
        write([key: value])
    }
    @discardableResult func write(_ changes: [String: String]) -> Bool {
        guard valid else { return false }
        var updated = values
        for (key, value) in changes { updated[key] = value }
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)
            try JSONEncoder().encode(updated).write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
            values = updated; return true
        } catch { return false }
    }
}

struct LicensePayload: Decodable {
    let schema: Int
    let kind: String?
    let product: String
    let license: String
    let device: String
    let issued_at: Int64
    let valid_until: Int64
    let key_id: String
    let environment: String
}

enum LicenseCheck {
    static func validKey(_ key: String) -> Bool {
        (30...100).contains(key.utf8.count) && key.utf8.allSatisfy { (65...90).contains($0) || (97...122).contains($0) || (48...57).contains($0) || $0 == 45 || $0 == 95 }
    }
    static func data(_ value: String) -> Data? {
        let text = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        return Data(base64Encoded: text + String(repeating: "=", count: (4 - text.count % 4) % 4))
    }
    static func verify(_ certificate: String, device: String, keys: [String: String], environment: String, now: Int64) -> LicensePayload? {
        let parts = certificate.split(separator: ".", omittingEmptySubsequences: false).map(String.init)
        guard parts.count == 3, parts[0] == "ss1", let body = data(parts[1]), let signature = data(parts[2]),
              let payload = try? JSONDecoder().decode(LicensePayload.self, from: body),
              payload.schema == 1, payload.product == "shift-switcher-pro-v1", payload.device == device,
              payload.environment == environment, payload.issued_at <= now + 300, payload.valid_until > now,
              payload.valid_until > payload.issued_at,
              let encoded = keys[environment + ":" + payload.key_id], let raw = Data(base64Encoded: encoded),
              let publicKey = try? Curve25519.Signing.PublicKey(rawRepresentation: raw),
              publicKey.isValidSignature(signature, for: Data(("ss1." + parts[1]).utf8)) else { return nil }
        if payload.kind == "owner" {
            guard payload.license.hasPrefix("owner_"),
                  payload.valid_until - payload.issued_at <= 366 * 86400 else { return nil }
        } else {
            guard payload.kind == nil, payload.valid_until - payload.issued_at <= 31 * 86400 else { return nil }
        }
        return payload
    }
}

struct TrialState: Codable {
    let started: Int64
    var latest: Int64
    func validTime(now: Int64) -> Bool { started >= 0 && latest >= started && now >= 0 && now >= latest - 300 }
    mutating func daysRemaining(now: Int64) -> Int {
        guard validTime(now: now) else { return 0 }
        latest = max(latest, now)
        return max(0, Int(ceil(Double(started + 14 * 86400 - latest) / 86400)))
    }
}
