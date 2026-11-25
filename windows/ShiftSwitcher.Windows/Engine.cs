using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ShiftSwitcher.Core;

namespace ShiftSwitcher.Windows;

internal sealed class Settings
{
    internal static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VHVost", "ShiftSwitcher", "settings.json");
    public bool Automatic { get; set; }
    public HashSet<string> KnownWords { get; set; } = [];
    public HashSet<string> IgnoredWords { get; set; } = [];
    internal static Settings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            value.KnownWords ??= []; value.IgnoredWords ??= []; return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
    internal bool ValidKnown(string word) => KnownWords.Contains(word.ToLowerInvariant());
}

internal sealed class Engine : IDisposable
{
    private sealed record Edit(FieldTarget Target, string Before, string Expected, TextSpan Range,
        TextSpan OriginalSelection, string Source, long Generation);
    private sealed record Protection(FieldTarget Target, string Prefix, int End);
    private readonly Settings settings;
    private readonly Spelling spelling = new();
    private readonly DoubleShift detector = new();
    private readonly System.Windows.Forms.Timer idle = new() { Interval = 325 };
    private readonly System.Windows.Forms.Timer commandTimer = new() { Interval = 15 };
    private Action? command;
    private FieldTarget? commandTarget;
    private long commandGeneration;
    private int commandAttempts;
    private FieldTarget? pending;
    private FieldState? settled;
    private int attempts;
    private long generation;
    private Edit? edit;
    private Protection? protection;
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "keepass", "keepassxc", "1password", "bitwarden", "credentialuibroker", "logonui" };
    private static readonly HashSet<string> ManualOnly = new(StringComparer.OrdinalIgnoreCase) { "cmd", "powershell", "pwsh", "windowsterminal", "code", "devenv", "rider64", "idea64", "mintty" };
    internal bool Paused { get; private set; }
    internal bool DictionaryReady => spelling.Ready;
    internal bool UndoShortcutAvailable { get; set; }
    internal bool CharacterShortcutAvailable { get; set; }
    internal event Action<string>? Status;

    internal Engine(Settings settings)
    {
        this.settings = settings;
        idle.Tick += (_, _) => Idle();
        commandTimer.Tick += (_, _) => RunCommand();
    }
    private void Report(string text) => Status?.Invoke(text);
    internal void Cancel() { generation++; detector.Reset(); idle.Stop(); commandTimer.Stop(); command = null; pending = null; settled = null; }
    internal void Pause() { Paused = !Paused; Cancel(); Report(Paused ? "На паузе" : "Готов к работе"); }
    private static bool Permitted(FieldTarget target, bool automatic)
    {
        try
        {
            using var process = Process.GetProcessById(target.Process);
            return !Protected.Contains(process.ProcessName) && (!automatic || !ManualOnly.Contains(process.ProcessName));
        }
        catch (ArgumentException) { return false; }
    }
    internal void OnKey(KeyEvent input)
    {
        if (Paused) return;
        var modifiers = input.Control || input.Alt || input.Windows;
        if (input.Key is 16 or 160 or 161)
        {
            if (detector.Shift(input.Key, input.Down, input.Time, modifiers))
            { generation++; idle.Stop(); pending = null; ScheduleCommand(() => Correct(false)); }
            return;
        }
        if (!input.Down) return;
        detector.OtherKey(); generation++; idle.Stop(); settled = null;
        if (UndoShortcutAvailable && input.Control && input.Alt && !input.Windows && input.Key == 90) { ScheduleCommand(Undo); pending = null; return; }
        if (CharacterShortcutAvailable && input.Control && input.Alt && !input.Windows && input.Key == 190) { ScheduleCommand(() => Correct(false, true)); pending = null; return; }
        if (modifiers || !settings.Automatic || !spelling.Ready) { pending = null; return; }
        if (input.Key == 32) pending = TextFields.Capture();
        else if (input.Key is 8 or 9 or 13 or 27 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 46) pending = null;
        if (pending is null || !Permitted(pending, true)) return;
        attempts = 6; idle.Interval = 325; idle.Start();
    }
    private void ScheduleCommand(Action action)
    {
        commandTarget = TextFields.Capture(); command = action; commandGeneration = generation; commandAttempts = 100;
        commandTimer.Start();
    }
    private void RunCommand()
    {
        var focused = TextFields.Capture();
        if (Paused || commandGeneration != generation || commandTarget is null || focused is null || !commandTarget.Same(focused) || --commandAttempts <= 0)
        { commandTimer.Stop(); command = null; return; }
        // Never insert text with the user's Ctrl/Alt/Shift still held. Do not
        // synthesize modifier releases; wait for the actual key-up instead.
        if (Native.ModifiersDown()) return;
        var action = command; command = null; commandTimer.Stop(); action?.Invoke();
    }
    private void Idle()
    {
        idle.Stop();
        if (Paused || !settings.Automatic || pending is null || Native.ModifiersDown()) return;
        var current = TextFields.Read(pending);
        if (current is not null && current.Selection.Length != 0) { pending = null; return; }
        if (current is not null && settled is not null && current.Value == settled.Value && current.Selection == settled.Selection)
        { pending = null; settled = null; Correct(true); return; }
        settled = current;
        if (--attempts <= 0) { pending = null; return; }
        idle.Interval = 25; idle.Start();
    }
    private bool Valid(string word, string language) =>
        (settings.ValidKnown(word) && Layout.Language(word) == language) || spelling.Valid(word, language);
    internal void Correct(bool automatic, bool character = false)
    {
        if (Paused) return;
        var target = TextFields.Capture();
        var field = target is null ? null : TextFields.Read(target);
        if (field is null || target is null || !Permitted(target, automatic))
        { if (!automatic) Report("Поле недоступно: требуется поддержка UI Automation"); return; }
        if (!automatic && !character && edit is { } last && last.Target.Same(target) && field.Value == last.Expected)
        {
            var expected = last.OriginalSelection.Length > 0 ? last.Range : new TextSpan(last.OriginalSelection.Start + last.Range.Length - last.Before.Length, 0);
            if (field.Selection == expected || generation == last.Generation + 1) { Undo(); return; }
        }
        TextSpan range;
        string after;
        if (automatic)
        {
            var minimum = 0;
            if (protection is { } saved)
            {
                if (saved.Target.Same(target) && field.Value.StartsWith(saved.Prefix, StringComparison.Ordinal)) minimum = saved.End;
                else protection = null;
            }
            if (field.Selection.Length != 0) return;
            var plan = AutomaticCorrection.GetPlan(field.Value, field.Selection.Start, Valid,
                word => settings.IgnoredWords.Contains(word.ToLowerInvariant()), minimum);
            if (plan is null) return;
            range = plan.Range; after = plan.Text;
        }
        else
        {
            var candidate = field.Selection.Length > 0 ? field.Selection : character
                ? Layout.PreviousCharacter(field.Value, field.Selection.Start) : Layout.LastWord(field.Value, field.Selection.Start);
            if (candidate is not TextSpan chosen) { Report("Нет слова или символа для замены"); return; }
            range = chosen;
            after = Layout.Convert(field.Value.Substring(range.Start, range.Length), Native.Language(target.Window));
        }
        var before = field.Value.Substring(range.Start, range.Length);
        if (before == after) { if (!automatic) Report("В этих символах раскладки совпадают"); return; }
        var source = Layout.Language(before) ?? Native.Language(target.Window);
        var destination = Layout.Language(after) ?? (source == "ru" ? "en" : "ru");
        if (source is null || Native.FindLayout(destination) == 0)
        { Report("Добавьте русскую и английскую (США) раскладки Windows"); return; }
        if (!TextFields.Replace(field, range, after))
        { Report("Замена не подтверждена; повторная вставка не выполнялась"); return; }
        var expectedValue = Layout.Replace(field.Value, range, after);
        var changed = new TextSpan(range.Start, after.Length);
        var current = TextFields.Read(target);
        if (field.Selection.Length > 0 && current is not null && current.Value == expectedValue && current.Revision == field.Revision)
            TextFields.Select(current, changed);
        edit = new(target, before, expectedValue, changed, field.Selection, source, generation);
        if (!automatic)
        {
            protection = new(target, expectedValue[..changed.End], changed.End);
            if (before.Length >= 3 && before.All(char.IsLetter) && after.All(char.IsLetter) && !Valid(before, source) && !Valid(after, destination))
            { settings.KnownWords.Add(after.ToLowerInvariant()); TrySave(); }
        }
        Report(Native.SwitchLayout(target.Window, destination)
            ? "Исправлено · отмена ⇧⇧ или Ctrl+Alt+Z" : "Текст исправлен, но Windows не сменила раскладку");
    }
    private void TrySave() { try { settings.Save(); } catch (IOException) { Report("Не удалось сохранить настройки"); } catch (UnauthorizedAccessException) { Report("Нет доступа к файлу настроек"); } }
    private void Undo()
    {
        if (edit is not { } previous) { Report("Нет исправления для отмены"); return; }
        var field = TextFields.Read(previous.Target);
        if (field is null || field.Value != previous.Expected)
        { Report("Отмена недоступна: поле или текст изменились"); return; }
        if (!TextFields.Replace(field, previous.Range, previous.Before)) { Report("Не удалось подтвердить отмену"); return; }
        var restored = Layout.Replace(field.Value, previous.Range, previous.Before);
        var end = previous.Range.Start + previous.Before.Length;
        protection = new(previous.Target, restored[..end], end);
        var current = TextFields.Read(previous.Target);
        if (current is not null && current.Value == restored && current.Revision == field.Revision)
            TextFields.Select(current, previous.OriginalSelection);
        edit = null;
        Report(Native.SwitchLayout(previous.Target.Window, previous.Source) ? "Исправление отменено" : "Текст восстановлен, но Windows не сменила раскладку");
    }
    public void Dispose() { idle.Dispose(); commandTimer.Dispose(); spelling.Dispose(); }
}
