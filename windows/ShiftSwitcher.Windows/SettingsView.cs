using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShiftSwitcher.Windows;

internal static class Palette
{
    internal static Color Background => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(246, 247, 251);
    internal static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Color.White;
    internal static Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(31, 38, 56);
    internal static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(101, 110, 130);
    internal static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(103, 73, 205);
    internal static Color Soft => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(239, 234, 252);
    internal static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(226, 229, 238);
    internal static Color Green => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(17, 116, 80);
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal sealed class Card : Panel
{
    internal Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; Padding = new Padding(20); Margin = new Padding(0, 0, 0, 16);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Palette.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 14 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Palette.Surface); using var border = new Pen(Palette.Border);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class SoftButton : Button
{
    internal readonly bool Primary;
    internal SoftButton(string text, bool primary = false)
    {
        Text = text; Primary = primary; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Font = new Font("Segoe UI", 10, FontStyle.Bold); Cursor = Cursors.Hand;
        Size = new Size(156, 38); UseVisualStyleBackColor = false; BackColor = Palette.Surface;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Primary ? Palette.Accent : Palette.Soft);
        e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            Primary ? SystemColors.HighlightText : Palette.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class Toggle : CheckBox
{
    internal Toggle(string name)
    {
        Text = name; AccessibleName = name; AccessibleRole = AccessibleRole.CheckButton;
        Size = new Size(52, 32); Cursor = Cursors.Hand; AutoSize = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f; var track = new RectangleF(3 * scale, 5 * scale, 44 * scale, 24 * scale);
        using var path = Palette.Round(track, 12 * scale);
        using var brush = new SolidBrush(!Enabled ? Palette.Border : Checked ? Palette.Accent : Palette.Muted);
        e.Graphics.FillPath(brush, path);
        using var thumb = new SolidBrush(Palette.Surface);
        e.Graphics.FillEllipse(thumb, (Checked ? 25 : 6) * scale, 8 * scale, 18 * scale, 18 * scale);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}

internal sealed class SettingsView
{
    internal SettingsForm Window { get; }
    internal CheckBox Automatic { get; } = new Toggle("Автоисправление после пробела");
    internal CheckBox Startup { get; } = new Toggle("Запускать при входе в Windows");
    internal Button Pause { get; } = new SoftButton("Приостановить");
    internal Button SaveDictionary { get; } = new SoftButton("Сохранить словарь", true) { Width = 188 };
    internal TextBox Known { get; } = Editor("Знакомые названия");
    internal TextBox Ignored { get; } = Editor("Не исправлять");
    private readonly Label state = Text("Работает в фоне", 11, true);
    private readonly Label language = Text("RU ↔ EN", 9);
    private readonly Label sidebarState = Text("● Готов к работе", 9);
    private readonly List<(Button Button, Panel Page)> navigation = [];
    private readonly Panel pages = new() { Dock = DockStyle.Fill, BackColor = Palette.Background };
    private string? lastState;

    internal SettingsView(Label status, bool dictionaryReady, bool undoAvailable, bool characterAvailable)
    {
        Window = new SettingsForm
        {
            Text = Program.WindowTitle, ClientSize = new Size(940, 680), MinimumSize = new Size(720, 480),
            AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterScreen, BackColor = Palette.Background,
            Font = new Font("Segoe UI", 10), ForeColor = Palette.Ink,
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
        };
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 216, BackColor = Palette.Surface, Padding = new Padding(22, 28, 18, 20) };
        var brand = Grid(2); brand.Height = 58; brand.Dock = DockStyle.Top;
        brand.ColumnStyles.Clear(); brand.ColumnStyles.Add(new(SizeType.Absolute, 46)); brand.ColumnStyles.Add(new(SizeType.Percent, 100));
        var picture = new PictureBox { Image = Window.Icon?.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), AccessibleName = "Shift Switcher" };
        brand.Controls.Add(picture, 0, 0); brand.Controls.Add(Text("Shift\nSwitcher", 14, true), 1, 0);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 24, 0, 0) };
        var footer = Grid(1); footer.Dock = DockStyle.Bottom; footer.Height = 82; footer.BackColor = Palette.Surface;
        footer.RowStyles.Add(new(SizeType.Absolute, 30)); footer.RowStyles.Add(new(SizeType.Percent, 100));
        footer.Controls.Add(sidebarState); footer.Controls.Add(Text("VHVost\nWindows Preview · 0.1.1", 9));
        var sidebarLayout = Grid(1); sidebarLayout.Dock = DockStyle.Fill;
        sidebarLayout.RowStyles.Add(new(SizeType.Absolute, 72)); sidebarLayout.RowStyles.Add(new(SizeType.Absolute, 190));
        sidebarLayout.RowStyles.Add(new(SizeType.Percent, 100)); sidebarLayout.RowStyles.Add(new(SizeType.Absolute, 82));
        brand.Dock = footer.Dock = DockStyle.Fill;
        sidebarLayout.Controls.Add(brand, 0, 0); sidebarLayout.Controls.Add(nav, 0, 1); sidebarLayout.Controls.Add(footer, 0, 3);
        sidebar.Controls.Add(sidebarLayout);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background };
        status.Dock = DockStyle.Bottom; status.Height = 48; status.Padding = new Padding(28, 4, 28, 8);
        status.Font = new Font("Segoe UI", 9); status.ForeColor = Palette.Muted; status.BackColor = Palette.Background;
        body.Controls.Add(pages); body.Controls.Add(status); Window.Controls.Add(body); Window.Controls.Add(sidebar);

        var overview = Page("Печатайте. Раскладку поправим.", "Переключение RU ↔ EN, которое всегда под рукой.");
        var live = new Card { Height = 152, Dock = DockStyle.Top };
        var liveGrid = Grid(2); liveGrid.Dock = DockStyle.Fill;
        liveGrid.ColumnStyles.Clear(); liveGrid.ColumnStyles.Add(new(SizeType.Percent, 65)); liveGrid.ColumnStyles.Add(new(SizeType.Percent, 35));
        liveGrid.RowStyles.Add(new(SizeType.Absolute, 30)); liveGrid.RowStyles.Add(new(SizeType.Absolute, 44)); liveGrid.RowStyles.Add(new(SizeType.Percent, 100));
        state.ForeColor = Palette.Green; liveGrid.Controls.Add(state, 0, 0); language.TextAlign = ContentAlignment.MiddleRight; liveGrid.Controls.Add(language, 1, 0);
        var demo = Text("ghbdtn   →   привет", 22, true); liveGrid.Controls.Add(demo, 0, 1); liveGrid.SetColumnSpan(demo, 2);
        liveGrid.Controls.Add(Text("Дважды Shift — исправить. Ещё раз — отменить.", 9), 0, 2);
        Pause.Dock = DockStyle.Fill; liveGrid.Controls.Add(Pause, 1, 2); live.Controls.Add(liveGrid); Add(overview, live);
        Automatic.Enabled = dictionaryReady;
        var options = new Card { Height = 156, Dock = DockStyle.Top };
        var optionRows = Grid(1); optionRows.Dock = DockStyle.Fill;
        optionRows.RowStyles.Add(new(SizeType.Percent, 50)); optionRows.RowStyles.Add(new(SizeType.Percent, 50));
        optionRows.Controls.Add(Option("Автоисправление", dictionaryReady ? "После пробела — на нужной раскладке." : "Нужны языковые словари Windows RU и EN.", Automatic));
        optionRows.Controls.Add(Option("Запускать вместе с Windows", "В фоне, без лишних окон при входе.", Startup));
        options.Controls.Add(optionRows); Add(overview, options);
        var keys = new Card { Height = 118, Dock = DockStyle.Top };
        var keyRows = Grid(2); keyRows.Dock = DockStyle.Fill;
        keyRows.ColumnStyles.Clear(); keyRows.ColumnStyles.Add(new(SizeType.Percent, 65)); keyRows.ColumnStyles.Add(new(SizeType.Percent, 35));
        Shortcut(keyRows, "Слово или выделенный текст", "Shift × 2", 0);
        Shortcut(keyRows, "Отменить исправление", undoAvailable ? "Ctrl + Alt + Z" : "Сочетание занято", 1);
        Shortcut(keyRows, "Один символ слева от курсора", characterAvailable ? "Ctrl + Alt + ." : "Выделите символ", 2);
        keys.Controls.Add(keyRows); Add(overview, keys);
        Add(overview, Text("Текст остаётся на вашем компьютере. Буфер обмена не используется.", 9), 28);

        var dictionary = Page("Ваши слова — ваши правила.", "По одному слову в строке. Сохраните изменения, когда закончите.");
        Add(dictionary, EditorCard("Знакомые названия", "Бренды и имена, которые вы часто используете.", Known));
        Add(dictionary, EditorCard("Не исправлять", "Сокращения и слова, которые нужно оставить как есть.", Ignored));
        var saveRow = new Panel { Height = 44, Dock = DockStyle.Top, BackColor = Palette.Background };
        SaveDictionary.Dock = DockStyle.Right; saveRow.Controls.Add(SaveDictionary); Add(dictionary, saveRow);

        var about = Page("Меньше переключений. Больше дела.", "Shift Switcher для Windows · 0.1.1 Preview");
        var aboutCard = new Card { Height = 206, Dock = DockStyle.Top };
        var info = Grid(1); info.Dock = DockStyle.Fill;
        info.RowStyles.Add(new(SizeType.Percent, 25)); info.RowStyles.Add(new(SizeType.Percent, 35)); info.RowStyles.Add(new(SizeType.Percent, 40));
        info.Controls.Add(Text("Локально и без буфера обмена", 14, true));
        info.Controls.Add(Text("Исправление выполняется на этом ПК. Набранный текст не отправляется в сеть.", 10));
        info.Controls.Add(Text("Парольные и недоступные поля пропускаются. В терминалах и редакторах кода автоматика отключена.", 10));
        aboutCard.Controls.Add(info); Add(about, aboutCard);
        var preview = new Card { Height = 128, Dock = DockStyle.Top };
        var previewInfo = Grid(1); previewInfo.Dock = DockStyle.Fill;
        previewInfo.RowStyles.Add(new(SizeType.Absolute, 28)); previewInfo.RowStyles.Add(new(SizeType.Percent, 100));
        previewInfo.Controls.Add(Text("Ранний доступ", 12, true));
        previewInfo.Controls.Add(Text("Это техническая Preview. Все реализованные функции доступны без покупки и активации.", 10));
        preview.Controls.Add(previewInfo); Add(about, preview);

        foreach (var (title, page) in new[] { ("Обзор", overview), ("Личный словарь", dictionary), ("О приложении", about) })
        {
            var button = new Button { Text = title, AccessibleName = title, Width = 174, Height = 44, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), Margin = new Padding(0, 0, 0, 6),
                ForeColor = Palette.Muted, BackColor = Palette.Surface, Font = new Font("Segoe UI", 10), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0; button.Click += (_, _) => Select(page);
            nav.Controls.Add(button); pages.Controls.Add(page); navigation.Add((button, page));
        }
        Select(overview);
        Window.Shown += (_, _) =>
        {
            var area = Screen.FromControl(Window).WorkingArea;
            Window.Size = new Size(Math.Min(Window.Width, area.Width - 24), Math.Min(Window.Height, area.Height - 24));
            Window.Location = new Point(area.X + (area.Width - Window.Width) / 2, area.Y + (area.Height - Window.Height) / 2);
        };
    }
    internal void SetState(string currentLanguage, bool paused, bool automatic, bool connected)
    {
        var signature = $"{currentLanguage}/{paused}/{automatic}/{connected}"; if (lastState == signature) return; lastState = signature;
        state.Text = !connected ? "Требуется внимание" : paused ? "На паузе" : automatic ? "Автоисправление включено" : "Ручное исправление";
        state.ForeColor = connected && !paused ? Palette.Green : Palette.Muted;
        sidebarState.Text = "● " + (!connected ? "Нет подключения" : paused ? "На паузе" : "Работает в фоне"); sidebarState.ForeColor = state.ForeColor;
        language.Text = currentLanguage is "RU" or "EN" ? "Сейчас " + currentLanguage : "RU ↔ EN";
        Pause.Text = paused ? "Продолжить" : "Приостановить"; Pause.Invalidate();
    }
    private void Select(Panel target)
    {
        foreach (var (button, page) in navigation)
        {
            page.Visible = page == target; button.BackColor = page == target ? Palette.Soft : Palette.Surface;
            button.ForeColor = page == target ? Palette.Accent : Palette.Muted;
        }
        target.BringToFront();
    }
    private static Label Text(string text, float size = 10, bool bold = false) => new()
    { Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? Palette.Ink : Palette.Muted,
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, UseMnemonic = false };
    private static TextBox Editor(string name) => new()
    { Multiline = true, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Segoe UI", 11), BackColor = Palette.Surface, ForeColor = Palette.Ink, AccessibleName = name, AcceptsReturn = true };
    private static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel { ColumnCount = columns, BackColor = Palette.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new(SizeType.Percent, 100f / columns));
        return grid;
    }
    private static Panel Page(string heading, string subtitle)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 28, 28, 12), BackColor = Palette.Background };
        var content = Grid(1); content.Dock = DockStyle.Top; content.AutoSize = true; content.AutoSizeMode = AutoSizeMode.GrowAndShrink; content.BackColor = Palette.Background;
        page.Controls.Add(content);
        var header = Grid(1); header.BackColor = Palette.Background; header.Height = 82; header.Dock = DockStyle.Top;
        header.RowStyles.Add(new(SizeType.Absolute, 45)); header.RowStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(Text(heading, 20, true)); header.Controls.Add(Text(subtitle, 9)); Add(page, header, 82);
        return page;
    }
    private static void Add(Panel page, Control item, int? height = null)
    {
        var content = (TableLayoutPanel)page.Controls[0]; var row = content.RowCount++;
        item.Dock = DockStyle.Fill; content.RowStyles.Add(new(SizeType.Absolute, (height ?? item.Height) + item.Margin.Vertical)); content.Controls.Add(item, 0, row);
    }
    private static Control Option(string title, string description, CheckBox toggle)
    {
        var row = Grid(2); row.Dock = DockStyle.Fill; row.ColumnStyles.Clear(); row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 56));
        var copy = Grid(1); copy.Dock = DockStyle.Fill; copy.RowStyles.Add(new(SizeType.Percent, 50)); copy.RowStyles.Add(new(SizeType.Percent, 50));
        copy.Controls.Add(Text(title, 10, true)); copy.Controls.Add(Text(description, 9)); row.Controls.Add(copy, 0, 0); toggle.Anchor = AnchorStyles.Right; row.Controls.Add(toggle, 1, 0); return row;
    }
    private static Card EditorCard(string title, string description, TextBox editor)
    {
        var card = new Card { Height = 204, Dock = DockStyle.Top }; var grid = Grid(1); grid.Dock = DockStyle.Fill;
        grid.RowStyles.Add(new(SizeType.Absolute, 27)); grid.RowStyles.Add(new(SizeType.Absolute, 34)); grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.Controls.Add(Text(title, 12, true)); grid.Controls.Add(Text(description, 9)); grid.Controls.Add(editor); card.Controls.Add(grid); return card;
    }
    private static void Shortcut(TableLayoutPanel rows, string action, string keys, int index)
    {
        rows.RowStyles.Add(new(SizeType.Percent, 33.333f)); rows.Controls.Add(Text(action, 9), 0, index);
        var key = Text(keys, 9, true); key.TextAlign = ContentAlignment.MiddleRight; key.ForeColor = Palette.Accent; rows.Controls.Add(key, 1, index);
    }
}
