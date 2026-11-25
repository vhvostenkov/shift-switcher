using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using FormsApplication = System.Windows.Forms.Application;

namespace ShiftSwitcher.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleton = new Mutex(true, @"Local\VHVost.ShiftSwitcher.Windows", out var created);
        if (!created) { MessageBox.Show("Shift Switcher уже работает. Откройте его значок в трее."); return; }
        ApplicationConfiguration.Initialize();
        FormsApplication.Run(new SwitcherContext(args.Contains("--background")));
    }
}

internal sealed class SwitcherContext : ApplicationContext
{
    private readonly Settings settings = Settings.Load();
    private readonly Engine engine;
    private readonly KeyboardMonitor keyboard;
    private readonly Hotkeys hotkeys;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer indicator = new() { Interval = 1000 };
    private readonly Form window;
    private readonly Label status = new() { AutoSize = false, Height = 48, Dock = DockStyle.Top };
    private readonly ToolStripMenuItem autoMenu = new("Автоисправление"), pauseMenu = new("Пауза");
    private Icon? generatedIcon;
    private CheckBox? automaticCheck;

    internal SwitcherContext(bool background)
    {
        engine = new(settings);
        hotkeys = new();
        var dispatcher = new WindowsFormsSynchronizationContext();
        keyboard = new(engine.OnKey, engine.Cancel, dispatcher);
        if (!keyboard.Connected) hotkeys.Dispose();
        engine.UndoShortcutAvailable = hotkeys.UndoAvailable; engine.CharacterShortcutAvailable = hotkeys.CharacterAvailable;
        window = MakeWindow();
        engine.Status += text => status.Text = text;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки", null, (_, _) => { window.Show(); window.Activate(); });
        autoMenu.CheckOnClick = true; autoMenu.Enabled = engine.DictionaryReady; autoMenu.Checked = settings.Automatic && engine.DictionaryReady;
        autoMenu.Click += (_, _) => SetAutomatic(autoMenu.Checked);
        pauseMenu.Click += (_, _) => { engine.Pause(); UpdateIndicator(); };
        menu.Items.Add(autoMenu); menu.Items.Add(pauseMenu); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выйти", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = "Shift Switcher · Windows Preview" };
        tray.DoubleClick += (_, _) => { window.Show(); window.Activate(); };
        indicator.Tick += (_, _) => UpdateIndicator(); indicator.Start(); UpdateIndicator();
        status.Text = keyboard.Connected ? "Готов к работе · двойной Shift" : "Не удалось подключить монитор клавиатуры";
        if (!background) window.Show();
    }
    private Form MakeWindow()
    {
        var form = new Form { Text = "Shift Switcher · Windows Preview", ClientSize = new Size(570, 650), MinimumSize = new Size(540, 620), StartPosition = FormStartPosition.CenterScreen, BackColor = Color.FromArgb(245, 245, 250), Font = new Font("Segoe UI", 10) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(new Label { Text = "⇧⇧  Shift Switcher", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true });
        panel.Controls.Add(new Label { Text = "RU ↔ EN · раскладка под контролем", AutoSize = true, Margin = new Padding(0, 4, 0, 20) });
        status.Width = 490; panel.Controls.Add(status);
        panel.Controls.Add(new Label { Text = "Двойной Shift — выделение или последнее слово.\nПовторный ⇧⇧ — точная отмена. " + (hotkeys.UndoAvailable ? "Также Ctrl+Alt+Z." : "Ctrl+Alt+Z занят.") + "\n" + (hotkeys.CharacterAvailable ? "Ctrl+Alt+. — один символ слева от курсора." : "Ctrl+Alt+. занят; используйте выделение символа."), AutoSize = true, Margin = new Padding(0, 0, 0, 18) });
        var automatic = new CheckBox { Text = "Автоисправление после пробела и паузы", Checked = settings.Automatic && engine.DictionaryReady, Enabled = engine.DictionaryReady, AutoSize = true };
        automaticCheck = automatic;
        automatic.CheckedChanged += (_, _) => SetAutomatic(automatic.Checked);
        panel.Controls.Add(automatic);
        panel.Controls.Add(new Label { Text = engine.DictionaryReady ? "Словари RU/EN доступны в Windows." : "Для автоматики нужны языковые словари RU и EN в Windows.", AutoSize = true, ForeColor = Color.DimGray });
        var startup = new CheckBox { Text = "Запускать при входе в Windows", AutoSize = true };
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
        panel.Controls.Add(startup);
        var paused = new Button { Text = "Пауза / продолжить", AutoSize = true, Margin = new Padding(0, 12, 0, 12) };
        paused.Click += (_, _) => { engine.Pause(); UpdateIndicator(); }; panel.Controls.Add(paused);
        panel.Controls.Add(new Label { Text = "Знакомые названия · одно слово в строке", AutoSize = true });
        var known = new TextBox { Multiline = true, Width = 490, Height = 68, Text = string.Join(Environment.NewLine, settings.KnownWords), ScrollBars = ScrollBars.Vertical };
        panel.Controls.Add(known);
        panel.Controls.Add(new Label { Text = "Не исправлять · одно слово в строке", AutoSize = true });
        var ignored = new TextBox { Multiline = true, Width = 490, Height = 52, Text = string.Join(Environment.NewLine, settings.IgnoredWords), ScrollBars = ScrollBars.Vertical };
        panel.Controls.Add(ignored);
        var save = new Button { Text = "Сохранить словарь", AutoSize = true };
        save.Click += (_, _) =>
        {
            settings.KnownWords = known.Lines.Select(s => s.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant()).Where(s => s.Length is >= 2 and <= 100 && s.All(char.IsLetter) && (s.All(c => c is >= 'а' and <= 'я' or 'ё') || s.All(char.IsAsciiLetter))).ToHashSet();
            settings.IgnoredWords = ignored.Lines.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length is > 0 and <= 100 && !s.Any(char.IsWhiteSpace)).ToHashSet();
            SaveSettings(); engine.Cancel();
        }; panel.Controls.Add(save);
        panel.Controls.Add(new Label { Text = "Техническая Windows Preview · продажи Windows закрыты.\nТекст и буфер обмена не отправляются в сеть.\nПарольные поля пропускаются; неподдерживаемые поля не меняются.\nАвтоматика по умолчанию выключена в терминалах и редакторах кода.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 16, 0, 0) });
        form.Controls.Add(panel);
        form.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.Hide(); } };
        return form;
    }
    private void SetAutomatic(bool enabled)
    {
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
        var language = Native.Language(Native.GetForegroundWindow())?.ToUpperInvariant() ?? "?";
        var automatic = settings.Automatic && engine.DictionaryReady;
        var state = !keyboard.Connected ? "!" : engine.Paused ? "P" : automatic ? "A" : "M";
        tray.Text = $"Shift Switcher · {language} · {(engine.Paused ? "Пауза" : automatic ? "Авто" : "Вручную")}";
        pauseMenu.Checked = engine.Paused;
        using var bitmap = new Bitmap(32, 32);
        using (var canvas = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(engine.Paused ? Color.Gray : Color.FromArgb(109, 80, 230)))
        using (var font = new Font("Segoe UI", 11, FontStyle.Bold))
        {
            canvas.Clear(Color.Transparent); canvas.FillEllipse(brush, 0, 0, 31, 31);
            canvas.DrawString(language, font, Brushes.White, new PointF(2, 5));
            using var modeFont = new Font("Segoe UI", 6, FontStyle.Bold);
            canvas.DrawString(state, modeFont, Brushes.White, new PointF(23, 21));
        }
        var handle = bitmap.GetHicon();
        using var borrowed = Icon.FromHandle(handle);
        var next = (Icon)borrowed.Clone(); DestroyIcon(handle);
        var previous = generatedIcon; generatedIcon = next; tray.Icon = next; previous?.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    protected override void ExitThreadCore()
    {
        indicator.Stop(); keyboard.Dispose(); hotkeys.Dispose(); engine.Dispose(); tray.Visible = false; tray.Dispose();
        generatedIcon?.Dispose(); indicator.Dispose(); window.Dispose(); base.ExitThreadCore();
    }
}
