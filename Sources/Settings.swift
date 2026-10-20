import AppKit
import SwiftUI
import ApplicationServices

final class SettingsModel: ObservableObject {
    weak var app: App?
    @Published var automatic = false
    @Published var paused = false
    @Published var accessibility = false
    @Published var monitoring = false
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
    }
    func setAutomatic(_ value: Bool) { if app?.autoEnabled != value { app?.toggleAuto() }; refresh() }
    func setPaused(_ value: Bool) { if app?.paused != value { app?.togglePause() }; refresh() }
    func openMonitoring() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!)
    }
}

private let accent = Color(red: 0.75, green: 0.70, blue: 1)
private let ink = Color(red: 0.085, green: 0.09, blue: 0.11)

struct SettingsView: View {
    @ObservedObject var model: SettingsModel
    @State private var converted = false
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
                Spacer()
                VStack(alignment: .leading, spacing: 9) {
                    Image(systemName: "lock.shield").font(.system(size: 20)).foregroundColor(accent)
                    Text("Только на вашем Mac").font(.system(size: 12, weight: .semibold))
                    Text("Без отправки текста\nи истории ввода.").font(.system(size: 11)).foregroundColor(.secondary).lineSpacing(3)
                }
                Divider().padding(.vertical, 20)
                Text("ВЕРСИЯ 0.2 · BETA").font(.system(size: 9, weight: .medium, design: .monospaced)).tracking(1.4).foregroundColor(.secondary)
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
