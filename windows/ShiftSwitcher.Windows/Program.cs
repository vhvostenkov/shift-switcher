using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Forms;
using ShiftSwitcher.Licensing;
using Microsoft.Win32;
using FormsApplication = System.Windows.Forms.Application;

namespace ShiftSwitcher.Windows;

internal static class Program
{
    internal const string WindowTitle = "Shift Switcher · Windows";
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--install-update")
        {
            Environment.ExitCode = Updater.Apply(args);
            return;
        }
        using var singleton = new Mutex(true, @"Local\VHVost.ShiftSwitcher.Windows", out var created);
        if (!created)
        {
            if (!args.Contains("--background") && !ShowExistingSettings())
                MessageBox.Show("Shift Switcher уже работает. Откройте его значок в трее.");
            return;
        }
        ApplicationConfiguration.Initialize();
        var failedUpdate = Array.IndexOf(args, "--update-failed");
        var failedVersion = failedUpdate >= 0 && args.Length > failedUpdate + 1 ? args[failedUpdate + 1] : null;
        using var context = new SwitcherContext(args.Contains("--background"), failedVersion);
        Updater.Finish(args);
        FormsApplication.Run(context);
    }
    private static bool ShowExistingSettings()
    {
        // The first process may still be creating its hidden settings window.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var handle = Native.FindWindowW(null, WindowTitle);
            if (handle != 0 && Native.GetWindowThreadProcessId(handle, out var processId) != 0)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                    using var current = System.Diagnostics.Process.GetCurrentProcess();
                    if (process.SessionId != current.SessionId ||
                        !string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
                    Native.AllowSetForegroundWindow(processId);
                    return SettingsForm.ShowMessage != 0 && Native.PostMessageW(handle, SettingsForm.ShowMessage, 0, 0);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
            }
            Thread.Sleep(25);
        }
        return false;
    }
}

internal sealed class SettingsForm : Form
{
    internal static readonly uint ShowMessage = Native.RegisterWindowMessageW("VHVost.ShiftSwitcher.ShowSettings.v1");
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (ShowMessage != 0 && (uint)message.Msg == ShowMessage)
        {
            Show(); WindowState = FormWindowState.Normal; Activate();
            return;
        }
        base.WndProc(ref message);
    }
}

internal sealed class SwitcherContext : ApplicationContext
{
    private readonly Settings settings = Settings.Load();
    private readonly LicenseClient license = CreateLicenseClient();
    private readonly Engine engine;
    private readonly KeyboardMonitor keyboard;
    private readonly Hotkeys hotkeys;
    private readonly NotifyIcon tray;
    private readonly Updater updater;
    private readonly System.Windows.Forms.Timer indicator = new() { Interval = 1000 };
    private readonly Form window;
    private readonly Label status = new() { AutoSize = false, Height = 48, Dock = DockStyle.Top };
    private readonly ToolStripMenuItem autoMenu = new("Автоисправление"), pauseMenu = new("Пауза");
    private Icon? generatedIcon;
    private CheckBox? automaticCheck;
    private SettingsView? view;
    private string? traySignature;
    private bool syncingAutomatic, exiting;

    private static LicenseClient CreateLicenseClient()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ShiftSwitcher.Windows.LicenseKeys");
        var keys = stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        var environment = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "LicenseEnvironment")?.Value ?? "LIVE";
        return new(new WindowsLicenseStore(WindowsLicenseStore.DefaultPath), keys, environment);
    }

    internal SwitcherContext(bool background, string? failedVersion)
    {
        engine = new(settings, () => license.HasPro);
        hotkeys = new();
        var dispatcher = new WindowsFormsSynchronizationContext();
        keyboard = new(engine.OnKey, engine.Cancel, dispatcher);
        if (!keyboard.Connected) hotkeys.Dispose();
        engine.UndoShortcutAvailable = hotkeys.UndoAvailable; engine.CharacterShortcutAvailable = hotkeys.CharacterAvailable;
        window = MakeWindow();
        license.Changed += () => { if (!exiting) { engine.Cancel(); UpdateIndicator(); } };
        _ = window.Handle; // Keep a discoverable window even for --background startup.
        engine.Status += text => status.Text = text;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки", null, (_, _) => { window.Show(); window.Activate(); });
        autoMenu.CheckOnClick = true; autoMenu.Enabled = engine.DictionaryReady; autoMenu.Checked = settings.Automatic && engine.DictionaryReady;
        autoMenu.Click += (_, _) => SetAutomatic(autoMenu.Checked);
        pauseMenu.Click += (_, _) => { engine.Pause(); UpdateIndicator(); };
        menu.Items.Add(autoMenu); menu.Items.Add(pauseMenu); menu.Items.Add(new ToolStripSeparator());
        updater = new Updater(settings.AutomaticUpdates, failedVersion)
        {
            Status = text => { status.Text = text; },
            CanRestart = () => !window.Visible && !license.Busy,
            ReadyToExit = ExitThread
        };
        menu.Items.Add("Проверить обновления…", null, async (_, _) => await updater.CheckAsync(true));
        var automaticUpdates = new ToolStripMenuItem("Автоматические обновления") { Checked = settings.AutomaticUpdates, CheckOnClick = true };
        automaticUpdates.Click += (_, _) =>
        {
            updater.Automatic = settings.AutomaticUpdates = automaticUpdates.Checked;
            SaveSettings();
        };
        menu.Items.Add(automaticUpdates);
        menu.Items.Add("Выйти", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "Shift Switcher · Windows" };
        tray.DoubleClick += (_, _) => { window.Show(); window.Activate(); };
        indicator.Tick += (_, _) => { UpdateIndicator(); _ = license.RenewIfNeededAsync(); }; indicator.Start(); UpdateIndicator();
        status.Text = keyboard.Connected ? "Готов к работе · двойной Shift" : "Не удалось подключить монитор клавиатуры";
        if (!background) window.Show();
    }
    private Form MakeWindow()
    {
        view = new SettingsView(status, engine.DictionaryReady, hotkeys.UndoAvailable, hotkeys.CharacterAvailable);
        var form = view.Window;
        var automatic = view.Automatic;
        automatic.Checked = settings.Automatic && engine.DictionaryReady;
        automaticCheck = automatic;
        automatic.CheckedChanged += (_, _) => { if (!syncingAutomatic) SetAutomatic(automatic.Checked); };
        var startup = view.Startup;
        var executable = Environment.ProcessPath;
        startup.Enabled = executable is not null && Path.GetFileNameWithoutExtension(executable).Equals("ShiftSwitcher", StringComparison.OrdinalIgnoreCase);
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            startup.Checked = run?.GetValue("VHVost.ShiftSwitcher") as string == $"\"{executable}\" --background";
        startup.CheckedChanged += (_, _) =>
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup.Checked) run.SetValue("VHVost.ShiftSwitcher", $"\"{executable}\" --background");
                else run.DeleteValue("VHVost.ShiftSwitcher", false);
                status.Text = startup.Checked ? "Автозапуск включён для этой копии" : "Автозапуск выключен";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { status.Text = "Не удалось изменить автозапуск"; }
        };
        view.Pause.Click += (_, _) => { engine.Pause(); UpdateIndicator(); };
        var known = view.Known; known.Text = string.Join(Environment.NewLine, settings.KnownWords);
        var ignored = view.Ignored; ignored.Text = string.Join(Environment.NewLine, settings.IgnoredWords);
        var save = view.SaveDictionary;
        save.Click += (_, _) =>
        {
            settings.KnownWords = known.Lines.Select(s => s.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant()).Where(s => s.Length is >= 2 and <= 100 && s.All(char.IsLetter) && (s.All(c => c is >= 'а' and <= 'я' or 'ё') || s.All(char.IsAsciiLetter))).ToHashSet();
            settings.IgnoredWords = ignored.Lines.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length is > 0 and <= 100 && !s.Any(char.IsWhiteSpace)).ToHashSet();
            SaveSettings(); engine.Cancel();
        };
        view.ActivateLicense.Click += async (_, _) => { await license.ActivateAsync(view.LicenseKey.Text); if (!exiting) UpdateIndicator(); };
        view.ImportLicense.Click += async (_, _) =>
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "Лицензия Shift Switcher|*.sslicense;*.json", Multiselect = false, Title = "Открыть лицензию" };
            if (dialog.ShowDialog(form) != DialogResult.OK) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 16384) { status.Text = "Файл лицензии слишком большой"; return; }
                await license.ImportAsync(File.ReadAllText(dialog.FileName));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = "Не удалось открыть файл лицензии"; }
            if (!exiting) UpdateIndicator();
        };
        view.ReleaseLicense.Click += async (_, _) => { await license.ReleaseAsync(); if (!exiting) UpdateIndicator(); };
        view.BuyLicense.Click += (_, _) => Process.Start(new ProcessStartInfo("https://vhvost.com/pay/?product=shift-switcher-pro-v1") { UseShellExecute = true });
        form.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.Hide(); } };
        return form;
    }
    private void SetAutomatic(bool enabled)
    {
        if (enabled && !license.HasPro) { status.Text = "Для автоматики нужен Pro. Ручное исправление доступно бесплатно."; UpdateIndicator(); return; }
        if (settings.Automatic == enabled && autoMenu.Checked == enabled && automaticCheck?.Checked == enabled) return;
        settings.Automatic = enabled; autoMenu.Checked = enabled;
        if (automaticCheck is not null) automaticCheck.Checked = enabled;
        engine.Cancel(); SaveSettings(); UpdateIndicator();
    }
    private void SaveSettings()
    {
        try { settings.Save(); status.Text = "Настройки сохранены на этом ПК"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "Не удалось сохранить настройки"; }
    }
    private void UpdateIndicator()
    {
        license.Refresh();
        var language = Native.Language(Native.GetForegroundWindow())?.ToUpperInvariant() ?? "?";
        var automatic = settings.Automatic && engine.DictionaryReady && license.HasPro;
        syncingAutomatic = true;
        try
        {
            autoMenu.Enabled = engine.DictionaryReady && license.HasPro; autoMenu.Checked = automatic;
            if (automaticCheck is not null) { automaticCheck.Enabled = autoMenu.Enabled; automaticCheck.Checked = automatic; }
        }
        finally { syncingAutomatic = false; }
        view?.SetLicense(license.Status, license.Message, license.HasPro, license.Ready, license.Busy, license.HasLicenseData, license.Device, license.Environment);
        var state = !keyboard.Connected ? "!" : engine.Paused ? "P" : automatic ? "A" : "M";
        tray.Text = $"Shift Switcher · {language} · {(engine.Paused ? "Пауза" : automatic ? "Авто" : "Вручную")}";
        pauseMenu.Checked = engine.Paused;
        view?.SetState(language, engine.Paused, automatic, keyboard.Connected);
        var signature = language + state;
        if (traySignature == signature) return;
        traySignature = signature;
        using var bitmap = new Bitmap(32, 32);
        using (var canvas = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(!keyboard.Connected ? Color.FromArgb(167, 89, 20) : engine.Paused ? Palette.Muted : Palette.Accent))
        using (var font = new Font("Segoe UI", 12, FontStyle.Bold))
        {
            canvas.Clear(Color.Transparent); canvas.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var shape = Palette.Round(new RectangleF(1, 1, 30, 30), 8);
            canvas.FillPath(brush, shape);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            canvas.DrawString(!keyboard.Connected ? "!" : engine.Paused ? "II" : language, font, Brushes.White, new RectangleF(0, 0, 32, 29), format);
            if (automatic && !engine.Paused && keyboard.Connected)
            {
                using var dot = new SolidBrush(Color.FromArgb(95, 225, 166));
                canvas.FillEllipse(dot, 23, 23, 8, 8);
            }
        }
        var handle = bitmap.GetHicon();
        using var borrowed = Icon.FromHandle(handle);
        var next = (Icon)borrowed.Clone(); DestroyIcon(handle);
        var previous = generatedIcon; generatedIcon = next; tray.Icon = next; previous?.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    protected override void ExitThreadCore()
    {
        exiting = true; updater.Dispose(); license.Dispose();
        indicator.Stop(); keyboard.Dispose(); hotkeys.Dispose(); engine.Dispose(); tray.Visible = false; tray.Dispose();
        generatedIcon?.Dispose(); indicator.Dispose(); window.Dispose(); base.ExitThreadCore();
    }
}
