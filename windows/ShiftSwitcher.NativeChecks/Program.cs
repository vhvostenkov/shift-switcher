using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

// Do not run without explicit permission for this Windows SendInput test driver.
// CUA prepares and focuses the empty field; the driver never activates a window.
internal static class Program
{
    private const string Title = "Shift Switcher — isolated compatibility fixture - Google Chrome";
    private const nuint Marker = 0x535351; // Distinct from the product's 0x535357.
    private sealed record Scenario(string Keys, string Before, string After, int Source, int Destination);
    private static readonly Dictionary<string, Scenario> Scenarios = new()
    {
        ["en-phrase"] = new("bkb vbh ", "bkb vbh ", "или мир ", 0x409, 0x419),
        ["ru-phrase"] = new("hello world ", "руддщ цщкдв ", "hello world ", 0x419, 0x409),
        ["ru-repeat"] = new("hello hello ", "руддщ руддщ ", "hello hello ", 0x419, 0x409)
    };
    private static nint window;
    private static uint processId;
    private static int[] fieldId = [];
    private static string fieldName = "";
    private static readonly List<object> observations = [];

    [STAThread]
    private static int Main(string[] args)
    {
        // Only fixed harmless scenarios. No arbitrary input, hotkeys or target paths.
        if (args.Length != 2 || !Scenarios.TryGetValue(args[0], out var scenario) ||
            !int.TryParse(args[1], out var interval) || interval is not (10 or 25)) return 2;
        var report = Path.Combine(AppContext.BaseDirectory, $"qa-{args[0]}-{interval}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}.json");
        var times = new List<double>();
        var checks = new List<object>();
        string? error = null;
        var passed = false;
        try
        {
            window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out processId);
            using var process = Process.GetProcessById((int)processId);
            if (!process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Foreground process is not Chrome");
            var field = AutomationElement.FocusedElement ?? throw new InvalidOperationException("No focused field");
            fieldName = field.Current.Name;
            if (fieldName is not ("Тестовый текст" or "Тестовый поиск")) throw new InvalidOperationException("Not an allowed QA field");
            fieldId = field.GetRuntimeId();
            if (Read() != "" || Language() != scenario.Source || (GetKeyState(20) & 1) != 0)
                throw new InvalidOperationException("Requires an empty field, source layout and Caps Lock off");
            var started = Stopwatch.GetTimestamp();
            foreach (var c in scenario.Keys)
            {
                // UIA guard overhead is measured, never disguised as precise timing.
                var deadline = times.Count == 0 ? 0 : times[^1] + interval;
                while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < deadline) Thread.Sleep(1);
                Guard(); NoModifiers();
                var key = c == ' ' ? (ushort)32 : (ushort)char.ToUpperInvariant(c);
                var sent = Send([Key(key), Key(key, true)]);
                times.Add(Stopwatch.GetElapsedTime(started, sent).TotalMilliseconds);
            }
            WaitFor(scenario.After);
            Check("automatic text", scenario.After, Read(), checks);
            for (var attempt = 0; attempt < 40 && Language() != scenario.Destination; attempt++) Wait(25);
            Check("layout after correction", scenario.Destination.ToString("X4"), Language().ToString("X4"), checks);
            // CUA verifies undo and the next space separately after this process exits.
            passed = true;
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
        var timingWithinTolerance = gaps.Length > 0 && gaps.All(g => g >= interval - 1 && g <= interval + 5);
        try
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                scenario = args[0], requestedIntervalMs = interval, sendTimesMs = times, actualIntervalsMs = gaps,
                timingWithinTolerance, timingNote = "Synthetic virtual-key input, never physical keyboard proof. UIA guards may exceed requested intervals.",
                functionalPassed = passed, error, checks, observations, processId, fieldName,
                expectedUndoText = scenario.Before, undoTested = false,
                recordedUtc = DateTime.UtcNow, clipboardUsed = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { return 3; }
        return passed && timingWithinTolerance ? 0 : 1;
    }

    private static void Check(string name, string expected, string actual, List<object> checks)
    {
        var pass = actual == expected;
        checks.Add(new { name, expected, actual, pass });
        if (!pass) throw new InvalidOperationException(name + " did not match");
    }
    private static AutomationElement Guard()
    {
        if (GetForegroundWindow() != window) throw new InvalidOperationException("QA window lost focus; input stopped");
        var title = new StringBuilder(512); GetWindowTextW(window, title, title.Capacity);
        if (title.ToString() != Title) throw new InvalidOperationException("QA tab changed; input stopped");
        GetWindowThreadProcessId(window, out var currentProcess);
        var field = AutomationElement.FocusedElement ?? throw new InvalidOperationException("Field lost focus");
        if (currentProcess != processId || field.Current.ProcessId != processId ||
            field.Current.Name != fieldName || !fieldId.SequenceEqual(field.GetRuntimeId()) ||
            field.Current.IsPassword || !field.Current.IsEnabled)
            throw new InvalidOperationException("QA field changed; input stopped");
        if (!field.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ||
            ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false)
            throw new InvalidOperationException("Field is not writable TextPattern");
        return field;
    }
    private static string Read()
    {
        var field = Guard();
        var text = ((TextPattern)field.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(1024);
        observations.Add(new { elapsedUtc = DateTime.UtcNow, text });
        return text;
    }
    private static int Language()
    {
        Guard(); var thread = GetWindowThreadProcessId(window, out _);
        return (int)(GetKeyboardLayout(thread).ToInt64() & 0xffff);
    }
    private static void NoModifiers()
    {
        if (new[] { 16, 17, 18, 91, 92 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("Modifier held; input stopped");
    }
    private static void WaitFor(string expected)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (Read() == expected) { Wait(50); return; }
            Thread.Sleep(25);
        }
    }
    private static void Wait(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Guard(); Thread.Sleep(25); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0, Extra = Marker } } };
    private static long Send(Input[] inputs)
    {
        Guard();
        if (GetForegroundWindow() != window) throw new InvalidOperationException("QA window lost focus before dispatch");
        var sent = Stopwatch.GetTimestamp();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("SendInput incomplete; no retry");
        return sent;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
