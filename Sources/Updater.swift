import AppKit
import CryptoKit
import ServiceManagement

private struct SignedUpdate: Decodable {
    let payload: Data
    let signature: Data
}

struct UpdateRelease: Decodable {
    static let feed = URL(string: "https://vhvost.com/shift-switcher/downloads/update.json")!
    static let bundleID = "local.viktor.ShiftSwitcher"
    private static let publicKey = "KWJ0pQmLV+QyNTbiGsi/mU4MGt/ungVsYNfegu4Zzpc="

    struct Package: Decodable {
        let bytes: Int
        let sha256: String
    }

    let schema: Int
    let version: String
    let mac: Package
    let windows: Package

    static func parse(_ data: Data, trustedKey: String? = nil) throws -> Self {
        guard data.count <= 8192 else { throw UpdateError.invalid }
        let envelope = try JSONDecoder().decode(SignedUpdate.self, from: data)
        let key = try Curve25519.Signing.PublicKey(rawRepresentation: Data(base64Encoded: trustedKey ?? publicKey)!)
        guard key.isValidSignature(envelope.signature, for: envelope.payload) else { throw UpdateError.invalid }
        let release = try JSONDecoder().decode(Self.self, from: envelope.payload)
        guard release.schema == 1, components(release.version) != nil,
              (1 ... 32 * 1024 * 1024).contains(release.mac.bytes),
              release.mac.sha256.utf8.count == 64 else { throw UpdateError.invalid }
        return release
    }

    static func components(_ version: String) -> [Int]? {
        let pieces = version.split(separator: ".", omittingEmptySubsequences: false)
        let numbers = pieces.compactMap { Int($0) }
        guard numbers.count == 3, pieces.count == 3,
              numbers.allSatisfy({ (0...999999).contains($0) }),
              numbers.map(String.init).joined(separator: ".") == version else { return nil }
        return numbers
    }

    func newer(than version: String) -> Bool {
        guard let current = Self.components(version), let next = Self.components(self.version) else { return false }
        return current.lexicographicallyPrecedes(next)
    }

    var macURL: URL {
        URL(string: "https://github.com/vhvostenkov/shift-switcher/releases/download/v\(version)/Shift-Switcher-Mac.zip")!
    }
}

enum UpdateError: Error { case invalid, commandFailed }

@MainActor
final class MacUpdater {
    private var busy = false
    private var downloaded: (version: String, archive: Data)?
    private var lastOffered: String?
    private var schedule: Timer?
    private let defaults = UserDefaults.standard
    private let autoKey = "checkForUpdatesAutomatically"

    var automatic: Bool { defaults.object(forKey: autoKey) as? Bool ?? true }

    func start() {
        guard Bundle.main.bundleIdentifier == UpdateRelease.bundleID else { return }
        Task {
            try? await Task.sleep(for: .seconds(30))
            if automatic { check(manual: false) }
        }
        schedule = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { _ in
            Task { @MainActor [weak self] in
                if let self, self.automatic { self.check(manual: false) }
            }
        }
    }

    func toggleAutomatic() {
        defaults.set(!automatic, forKey: autoKey)
        if automatic { check(manual: false) }
    }

    func check(manual: Bool) {
        guard !busy, Bundle.main.bundleIdentifier == UpdateRelease.bundleID else { return }
        busy = true
        Task {
            defer { busy = false }
            do {
                let feed = try await Self.fetch(UpdateRelease.feed, limit: 8192)
                let release = try UpdateRelease.parse(feed)
                let installed = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
                guard release.newer(than: installed) else {
                    if manual { message("У вас актуальная версия Shift Switcher.") }
                    return
                }
                if manual { offer(release, feed: feed) }
                else if lastOffered != release.version {
                    downloaded = (release.version, try await Self.verifiedArchive(release))
                    guard automatic else { return }
                    lastOffered = release.version
                    onAvailable?(release.version)
                }
            } catch {
                if manual { message("Не удалось проверить обновления. Попробуйте позже.") }
            }
        }
    }

    var onAvailable: ((String) -> Void)?

    private func offer(_ release: UpdateRelease, feed: Data) {
        let alert = NSAlert()
        alert.messageText = "Доступен Shift Switcher \(release.version)"
        alert.informativeText = "Обновление заменит приложение на месте и перезапустит его. Настройки и Pro сохранятся. После обновления macOS может попросить заново разрешить доступ к клавиатуре."
        alert.addButton(withTitle: "Обновить и перезапустить")
        alert.addButton(withTitle: "Позже")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Task {
            busy = true
            defer { busy = false }
            do { try await install(release, feed: feed) }
            catch {
                let fallback = NSAlert()
                fallback.messageText = "Не удалось установить обновление"
                fallback.informativeText = "Текущая версия остаётся доступной. Можно скачать новую сборку и заменить приложение вручную."
                fallback.addButton(withTitle: "Открыть страницу загрузки")
                fallback.addButton(withTitle: "Закрыть")
                if fallback.runModal() == .alertFirstButtonReturn {
                    NSWorkspace.shared.open(URL(string: "https://vhvost.com/shift-switcher/#download")!)
                }
            }
        }
    }

    private func message(_ text: String) {
        let alert = NSAlert()
        alert.messageText = text
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    nonisolated static func fetch(_ url: URL, limit: Int) async throws -> Data {
        let request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        let (stream, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
              http.url?.scheme == "https", http.expectedContentLength <= limit else { throw UpdateError.invalid }
        var data = Data()
        for try await byte in stream {
            guard data.count < limit else { throw UpdateError.invalid }
            data.append(byte)
        }
        return data
    }

    nonisolated static func verifiedArchive(_ release: UpdateRelease) async throws -> Data {
        let bytes = try await Self.fetch(release.macURL, limit: release.mac.bytes)
        guard bytes.count == release.mac.bytes,
              SHA256.hash(data: bytes).map({ String(format: "%02x", $0) }).joined() == release.mac.sha256 else {
            throw UpdateError.invalid
        }
        return bytes
    }

    private func install(_ release: UpdateRelease, feed: Data) async throws {
        // Keep the installation location and the registered login item path.
        let target = Bundle.main.bundleURL.resolvingSymlinksInPath()
        guard let executable = Bundle.main.executableURL else { throw UpdateError.invalid }
        let bytes: Data
        if let downloaded, downloaded.version == release.version,
           SHA256.hash(data: downloaded.archive).map({ String(format: "%02x", $0) }).joined() == release.mac.sha256 {
            bytes = downloaded.archive
        } else {
            bytes = try await Self.verifiedArchive(release)
        }
        let loginEnabled = SMAppService.mainApp.status == .enabled
        let parent = String(getpid())
        try await Task.detached {
            let files = FileManager.default
            let staging = target.deletingLastPathComponent().appendingPathComponent(".shift-switcher-update-" + UUID().uuidString)
            try files.createDirectory(at: staging, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            do {
                let archive = staging.appendingPathComponent("update.zip")
                try bytes.write(to: archive)
                try feed.write(to: staging.appendingPathComponent("signed-feed.json"))
                let helper = staging.appendingPathComponent("installer")
                try files.copyItem(at: executable, to: helper)
                let process = Process()
                process.executableURL = helper
                process.arguments = ["--install-update", parent, target.path, release.version, loginEnabled ? "login" : "manual"]
                process.currentDirectoryURL = staging
                try process.run()
                let started = staging.appendingPathComponent("started")
                for _ in 0..<300 where process.isRunning && !files.fileExists(atPath: started.path) {
                    try await Task.sleep(for: .milliseconds(50))
                }
                guard process.isRunning, files.fileExists(atPath: started.path) else {
                    if process.isRunning { process.terminate(); process.waitUntilExit() }
                    throw UpdateError.commandFailed
                }
            } catch {
                try? files.removeItem(at: staging)
                throw error
            }
        }.value
        NSApp.terminate(nil)
    }
}

private func runUpdateCommand(_ executable: String, _ arguments: [String]) throws {
    let task = Process()
    task.executableURL = URL(fileURLWithPath: executable)
    task.arguments = arguments
    try task.run()
    task.waitUntilExit()
    guard task.terminationStatus == 0 else { throw UpdateError.commandFailed }
}

private func validateUpdatedBundle(_ candidate: URL, version: String, bundleID: String) throws {
    guard let app = Bundle(url: candidate), app.bundleIdentifier == bundleID,
          app.infoDictionary?["CFBundleShortVersionString"] as? String == version,
          app.executableURL?.lastPathComponent == "Shift Switcher" else { throw UpdateError.invalid }
    try runUpdateCommand("/usr/bin/codesign", ["--verify", "--deep", "--strict", candidate.path])
}

// Only invoked by the old app from a private staging directory on the same volume.
func applyMacUpdate(_ arguments: [String], bundleID: String = UpdateRelease.bundleID, trustedKey: String? = nil) throws {
    guard arguments.count == 6, let oldPID = pid_t(arguments[2]), oldPID > 1 else { throw UpdateError.invalid }
    let files = FileManager.default
    let staging = URL(fileURLWithPath: arguments[0]).deletingLastPathComponent().resolvingSymlinksInPath()
    let target = URL(fileURLWithPath: arguments[3]).resolvingSymlinksInPath()
    guard staging.lastPathComponent.hasPrefix(".shift-switcher-update-"),
          staging.deletingLastPathComponent().path == target.deletingLastPathComponent().path,
          target.pathExtension == "app", Bundle(url: target)?.bundleIdentifier == bundleID else {
        throw UpdateError.invalid
    }
    let candidate = staging.appendingPathComponent("Shift Switcher.app")
    guard NSRunningApplication(processIdentifier: oldPID)?.bundleURL?.resolvingSymlinksInPath().path == target.path else {
        throw UpdateError.invalid
    }
    let feed = staging.appendingPathComponent("signed-feed.json")
    let feedSize = try feed.resourceValues(forKeys: [.fileSizeKey]).fileSize
    guard let feedSize, feedSize <= 8192 else { throw UpdateError.invalid }
    let release = try UpdateRelease.parse(Data(contentsOf: feed), trustedKey: trustedKey)
    let oldVersion = Bundle(url: target)?.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
    let archive = staging.appendingPathComponent("update.zip")
    let size = try archive.resourceValues(forKeys: [.fileSizeKey]).fileSize
    guard release.version == arguments[4], release.newer(than: oldVersion), size == release.mac.bytes,
          SHA256.hash(data: try Data(contentsOf: archive)).map({ String(format: "%02x", $0) }).joined() == release.mac.sha256 else {
        throw UpdateError.invalid
    }
    try runUpdateCommand("/usr/bin/ditto", ["-x", "-k", archive.path, staging.path])
    try validateUpdatedBundle(candidate, version: release.version, bundleID: bundleID)
    try Data().write(to: staging.appendingPathComponent("started"), options: .withoutOverwriting)
    for _ in 0..<150 where kill(oldPID, 0) == 0 { Thread.sleep(forTimeInterval: 0.1) }
    guard kill(oldPID, 0) != 0 else { throw UpdateError.commandFailed }

    // APFS can exchange two directories atomically. The previous app remains in staging.
    func exchange() -> Bool { renameatx_np(AT_FDCWD, target.path, AT_FDCWD, candidate.path, UInt32(RENAME_SWAP)) == 0 }
    guard exchange() else {
        try? runUpdateCommand("/usr/bin/open", ["-g", "-a", target.path, "--args", "--background"])
        throw UpdateError.commandFailed
    }
    let ready = staging.appendingPathComponent("ready")
    do {
        try runUpdateCommand("/usr/bin/open", ["-g", "-n", "-a", target.path, "--args", "--background", "--updated-from", staging.path, arguments[5]])
        for _ in 0..<100 where !files.fileExists(atPath: ready.path) { Thread.sleep(forTimeInterval: 0.1) }
        if files.fileExists(atPath: ready.path) {
            try? files.removeItem(at: staging)
            return
        }
        // Keep the backup if the replacement is still running but has not reported readiness.
        if NSRunningApplication.runningApplications(withBundleIdentifier: bundleID)
            .contains(where: { $0.bundleURL?.resolvingSymlinksInPath().path == target.path }) { return }
    } catch {}
    guard exchange() else { throw UpdateError.commandFailed }
    try runUpdateCommand("/usr/bin/open", ["-g", "-a", target.path, "--args", "--background"])
}

func finishMacUpdate(_ arguments: [String], bundleID: String = UpdateRelease.bundleID) {
    guard let flag = arguments.firstIndex(of: "--updated-from"), arguments.count == flag + 3 else { return }
    let staging = URL(fileURLWithPath: arguments[flag + 1]).resolvingSymlinksInPath()
    guard staging.lastPathComponent.hasPrefix(".shift-switcher-update-"),
          staging.deletingLastPathComponent().path == Bundle.main.bundleURL.resolvingSymlinksInPath().deletingLastPathComponent().path,
          Bundle(url: staging.appendingPathComponent("Shift Switcher.app"))?.bundleIdentifier == bundleID else { return }
    if arguments[flag + 2] == "login", SMAppService.mainApp.status == .notRegistered {
        try? SMAppService.mainApp.register()
    }
    try? Data().write(to: staging.appendingPathComponent("ready"), options: .withoutOverwriting)
}
