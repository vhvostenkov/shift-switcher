import AppKit
import SwiftUI
import ApplicationServices
import ServiceManagement

final class SettingsModel: ObservableObject {
    weak var app: App?
    @Published var automatic = false
    @Published var paused = false
    @Published var accessibility = false
    @Published var monitoring = false
    @Published var appModes: [String: AppCorrectionMode] = [:]
    @Published var ignoredWords: [String] = []
    @Published var launchAtLogin = false
    @Published var loginNeedsApproval = false
    private var timer: Timer?
    init(app: App) {
        self.app = app
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.refresh() }
    }
    deinit { timer?.invalidate() }
    func refresh() {
        guard let app = app else { return }
        automatic = app.autoEnabled; paused = app.paused
        accessibility = AXIsProcessTrusted(); monitoring = CGPreflightListenEventAccess()
        appModes = app.preferences.appModes; ignoredWords = app.preferences.ignoredWords
        launchAtLogin = SMAppService.mainApp.status == .enabled
        loginNeedsApproval = SMAppService.mainApp.status == .requiresApproval
    }
    func setAutomatic(_ value: Bool) { if app?.autoEnabled != value { app?.toggleAuto() }; refresh() }
    func setPaused(_ value: Bool) { if app?.paused != value { app?.togglePause() }; refresh() }
    func setMode(_ raw: String, bundle: String) {
        _ = app?.preferences.setMode(AppCorrectionMode(rawValue: raw), for: bundle)
        app?.autoGeneration += 1
        refresh()
    }
    func addWord(_ word: String) -> Bool {
        let result = app?.preferences.addIgnoredWord(word) ?? false
        refresh(); return result
    }
    func removeWord(_ word: String) { app?.preferences.removeIgnoredWord(word); refresh() }
    func setLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
        } catch {
            let alert = NSAlert(); alert.messageText = "Не удалось изменить автозапуск"
            alert.informativeText = "Проверьте раздел «Основные → Объекты входа» в настройках macOS."; alert.runModal()
        }
        refresh()
    }
    func openMonitoring() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!)
    }
}

private let accent = Color(red: 0.75, green: 0.70, blue: 1)
private let ink = Color(red: 0.085, green: 0.09, blue: 0.11)

struct SettingsView: View {
    @ObservedObject var model: SettingsModel
    @State private var converted = false
    @State private var showRules = false
    var ready: Bool { model.accessibility && model.monitoring }
    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 10) {
                    Image(systemName: "shift.fill").font(.system(size: 23, weight: .semibold)).foregroundColor(accent)
                    Text("Shift\nSwitcher").font(.system(size: 16, weight: .bold)).lineSpacing(0)
                }.padding(.bottom, 46)
                Label("Обзор", systemImage: "square.grid.2x2.fill")
                    .font(.system(size: 13, weight: .semibold)).foregroundColor(accent)
                    .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                    .background(accent.opacity(0.10)).cornerRadius(10)
                Button { showRules = true } label: {
                    Label("Личные правила", systemImage: "slider.horizontal.3")
                        .font(.system(size: 12)).padding(12).frame(maxWidth: .infinity, alignment: .leading)
                }.buttonStyle(.plain).foregroundColor(.secondary).padding(.top, 8)
                Spacer()
                VStack(alignment: .leading, spacing: 9) {
                    Image(systemName: "lock.shield").font(.system(size: 20)).foregroundColor(accent)
                    Text("Только на вашем Mac").font(.system(size: 12, weight: .semibold))
                    Text("Без отправки текста\nи истории ввода.").font(.system(size: 11)).foregroundColor(.secondary).lineSpacing(3)
                }
                Divider().padding(.vertical, 20)
                Text("\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "") · PREVIEW").font(.system(size: 9, weight: .medium, design: .monospaced)).tracking(1.4).foregroundColor(.secondary)
            }.padding(.horizontal, 22).padding(.top, 38).padding(.bottom, 26).frame(width: 188)
                .background(Color.black.opacity(0.18))
            VStack(alignment: .leading, spacing: 19) {
                HStack {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Раскладка под контролем.").font(.system(size: 25, weight: .semibold)).tracking(-0.7)
                        Text("Пишите. Двойной Shift исправит остальное.").font(.system(size: 12)).foregroundColor(.secondary)
                    }
                    Spacer()
                    HStack(spacing: 6) {
                        Circle().fill(ready && !model.paused ? Color.green : Color.orange).frame(width: 6, height: 6)
                        Text(model.paused ? "Пауза" : ready ? "Готов к работе" : "Нужен доступ").font(.system(size: 10, weight: .medium))
                    }.padding(.horizontal, 10).padding(.vertical, 7).background(Color.white.opacity(0.05)).cornerRadius(20)
                }
                VStack(alignment: .leading, spacing: 20) {
                    HStack(alignment: .top) {
                        VStack(alignment: .leading, spacing: 7) {
                            Text("ДВА НАЖАТИЯ. НУЖНЫЕ БУКВЫ.").font(.system(size: 9, weight: .semibold, design: .monospaced)).tracking(1.5).foregroundColor(accent)
                            Text(converted ? "привет, мир" : "ghbdtn, vbh").font(.system(size: 32, weight: .medium, design: .monospaced)).tracking(-1)
                        }
                        Spacer()
                        Button { withAnimation(.easeInOut(duration: 0.18)) { converted.toggle() } } label: {
                            VStack(spacing: 4) {
                                Image(systemName: "shift").font(.system(size: 28, weight: .medium))
                                Text("× 2").font(.system(size: 10, weight: .semibold, design: .monospaced))
                            }.foregroundColor(accent).frame(width: 68, height: 68)
                                .background(Color.white.opacity(0.06)).cornerRadius(14)
                                .overlay(RoundedRectangle(cornerRadius: 14).stroke(accent.opacity(0.25), lineWidth: 1))
                        }.buttonStyle(.plain).help("Показать пример преобразования")
                    }
                    HStack(spacing: 22) {
                        Label("Выделение → весь фрагмент", systemImage: "selection.pin.in.out")
                        Label("Без выделения → последнее слово", systemImage: "text.cursor")
                    }.font(.system(size: 10)).foregroundColor(.secondary)
                }.padding(22).frame(maxWidth: .infinity, alignment: .leading)
                    .background(LinearGradient(colors: [accent.opacity(0.13), Color.white.opacity(0.025)], startPoint: .topLeading, endPoint: .bottomTrailing))
                    .cornerRadius(17).overlay(RoundedRectangle(cornerRadius: 17).stroke(accent.opacity(0.15), lineWidth: 1))
                VStack(spacing: 0) {
                    setting("Автоисправление", subtitle: "RU ↔ EN после пробела. Можно выключить в любой момент.", icon: "sparkles", value: Binding(get: { model.automatic }, set: model.setAutomatic))
                    Divider().padding(.leading, 54)
                    setting("Поставить на паузу", subtitle: "Временно отключить исправление и двойной Shift.", icon: "pause.circle", value: Binding(get: { model.paused }, set: model.setPaused))
                }.background(Color.white.opacity(0.035)).cornerRadius(14)
                HStack {
                    Text("РАЗРЕШЕНИЯ MACOS").font(.system(size: 9, weight: .medium, design: .monospaced)).tracking(1.3).foregroundColor(.secondary)
                    Spacer()
                    Text("Текст остаётся на устройстве").font(.system(size: 10)).foregroundColor(.secondary)
                }.padding(.top, 1)
                HStack(spacing: 10) {
                    permission("Универсальный доступ", subtitle: "Чтобы исправлять текст", enabled: model.accessibility) { model.app?.permissions() }
                    permission("Мониторинг ввода", subtitle: "Чтобы распознавать ⇧⇧", enabled: model.monitoring) { model.openMonitoring() }
                }
                HStack {
                    Text("Парольные поля пропускаются. Автоматика отключена в терминалах и редакторах кода.")
                        .font(.system(size: 9)).foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Инструкция ↗") { model.app?.help() }.buttonStyle(.plain).font(.system(size: 10)).foregroundColor(accent)
                }
            }.padding(30).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }.frame(width: 872, height: 628).background(ink).preferredColorScheme(.dark)
            .sheet(isPresented: $showRules) { RulesView(model: model) { showRules = false } }
    }
    func setting(_ title: String, subtitle: String, icon: String, value: Binding<Bool>) -> some View {
        HStack(spacing: 13) {
            Image(systemName: icon).font(.system(size: 18)).foregroundColor(accent).frame(width: 25)
            VStack(alignment: .leading, spacing: 5) {
                Text(title).font(.system(size: 13, weight: .medium))
                Text(subtitle).font(.system(size: 10)).foregroundColor(.secondary)
            }
            Spacer()
            Toggle(title, isOn: value).labelsHidden().toggleStyle(.switch).tint(accent)
        }.padding(16)
    }
    func permission(_ title: String, subtitle: String, enabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: enabled ? "checkmark.circle.fill" : "plus.circle").foregroundColor(enabled ? .green : accent)
                VStack(alignment: .leading, spacing: 4) {
                    Text(title).font(.system(size: 11, weight: .medium)).foregroundColor(.primary)
                    Text(enabled ? "Доступ разрешён" : subtitle).font(.system(size: 10)).foregroundColor(.secondary)
                }
                Spacer()
                if !enabled { Image(systemName: "arrow.up.right").font(.system(size: 10)).foregroundColor(.secondary) }
            }.padding(13).frame(maxWidth: .infinity, alignment: .leading).background(Color.white.opacity(0.035)).cornerRadius(11)
        }.buttonStyle(.plain)
    }
}

final class SettingsWindow: NSWindowController {
    init(app: App) {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 872, height: 628), styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "Shift Switcher"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.contentView = NSHostingView(rootView: SettingsView(model: SettingsModel(app: app)))
        window.appearance = NSAppearance(named: .darkAqua)
        window.center()
        window.isReleasedWhenClosed = false
        super.init(window: window)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}


private struct RulesView: View {
    @ObservedObject var model: SettingsModel
    let close: () -> Void
    @State private var page = 0
    @State private var selectedBundle = ""
    @State private var word = ""
    @State private var wordError = false
    private var runningApps: [(id: String, name: String)] {
        var unique: [String: String] = [:]
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            if let bundle = app.bundleIdentifier, bundle != Bundle.main.bundleIdentifier { unique[bundle] = app.localizedName ?? bundle }
        }
        for bundle in model.appModes.keys where unique[bundle] == nil { unique[bundle] = bundle }
        return unique.map { (id: $0.key, name: $0.value) }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                VStack(alignment: .leading, spacing: 5) {
                    Text("Личные правила").font(.system(size: 24, weight: .semibold))
                    Text("Настройте исправление под свой рабочий день.").font(.system(size: 12)).foregroundColor(.secondary)
                }
                Spacer()
                Button("Готово", action: close).keyboardShortcut(.defaultAction)
            }
            Picker("Раздел", selection: $page) {
                Text("Программы").tag(0); Text("Слова").tag(1); Text("Запуск").tag(2)
            }.pickerStyle(.segmented)
            if page == 0 {
                Text("Выберите запущенную программу и задайте её режим. Глобальный переключатель автоматики всё равно должен быть включён.").font(.system(size: 12)).foregroundColor(.secondary)
                Picker("Программа", selection: $selectedBundle) {
                    Text("Выберите программу").tag("")
                    ForEach(runningApps, id: \.id) { item in Text(item.name).tag(item.id) }
                }
                if !selectedBundle.isEmpty {
                    Picker("Режим", selection: Binding(get: { model.appModes[selectedBundle]?.rawValue ?? "default" }, set: { model.setMode($0, bundle: selectedBundle) })) {
                        Text("По умолчанию").tag("default")
                        ForEach(AppCorrectionMode.allCases, id: \.rawValue) { Text($0.title).tag($0.rawValue) }
                    }
                    Text(selectedBundle).font(.system(size: 10, design: .monospaced)).foregroundColor(.secondary)
                }
                Divider()
                Text("Сохранённые правила").font(.system(size: 12, weight: .semibold))
                ScrollView {
                    VStack(spacing: 12) {
                        ForEach(model.appModes.keys.sorted(), id: \.self) { bundle in
                            HStack {
                                Text(bundle).font(.system(size: 11)).lineLimit(1)
                                Spacer(); Text(model.appModes[bundle]?.title ?? "").font(.system(size: 10)).foregroundColor(.secondary)
                                Button("Сбросить") { model.setMode("default", bundle: bundle) }.font(.system(size: 10))
                            }
                        }
                        if model.appModes.isEmpty { Text("Пока используются стандартные режимы.").font(.system(size: 12)).foregroundColor(.secondary) }
                    }
                }
            } else if page == 1 {
                Text("Автоматика не будет менять эти слова. Ручное исправление двойным Shift остаётся доступным.").font(.system(size: 12)).foregroundColor(.secondary)
                HStack {
                    TextField("Например, название компании", text: $word).textFieldStyle(.roundedBorder).onSubmit(addWord)
                    Button("Добавить", action: addWord)
                }
                if wordError { Text("Введите одно слово без пробелов, не длиннее 100 символов.").font(.system(size: 11)).foregroundColor(.orange) }
                ScrollView {
                    VStack(spacing: 12) {
                        ForEach(model.ignoredWords, id: \.self) { item in
                            HStack { Text(item).font(.system(size: 13)); Spacer(); Button("Удалить") { model.removeWord(item) }.font(.system(size: 11)) }
                        }
                        if model.ignoredWords.isEmpty { Text("Личный словарь пока пуст.").font(.system(size: 12)).foregroundColor(.secondary) }
                    }
                }
            } else {
                Toggle("Запускать вместе с Mac", isOn: Binding(get: { model.launchAtLogin }, set: model.setLogin)).toggleStyle(.switch).tint(accent)
                Text("При входе приложение останется в строке меню.").font(.system(size: 12)).foregroundColor(.secondary)
                if model.loginNeedsApproval {
                    Text("Подтвердите автозапуск в настройках macOS → Основные → Объекты входа.").font(.system(size: 12)).foregroundColor(.orange)
                }
            }
            Spacer(minLength: 0)
            Text("Правила и словарь хранятся только на этом Mac. Парольные поля и Secure Input всегда пропускаются.").font(.system(size: 10)).foregroundColor(.secondary)
        }.padding(28).frame(width: 650, height: 490).background(ink).preferredColorScheme(.dark)
    }
    private func addWord() {
        wordError = !model.addWord(word)
        if !wordError { word = "" }
    }
}
