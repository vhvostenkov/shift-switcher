using System.Runtime.InteropServices;

namespace ShiftSwitcher.Windows;

internal static class Native
{
    internal static readonly nuint OwnInput = 0x535357;
    private static long physicalRevision;
    internal static long PhysicalRevision => Interlocked.Read(ref physicalRevision);
    internal static void PhysicalInput() => Interlocked.Increment(ref physicalRevision);
    internal delegate nint HookProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData
    { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyInput
    { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    { [FieldOffset(0)] public KeyInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input
    { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }

    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern int GetKeyboardLayoutList(int count, [Out] nint[] layouts);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    internal static bool ModifiersDown() => new[] { 16, 17, 18, 91, 92 }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    internal static string? Language(nint window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        return ((long)GetKeyboardLayout(thread) & 0xffff) switch { 0x419 => "ru", 0x409 => "en", _ => null };
    }
    internal static nint FindLayout(string language)
    {
        var layouts = new nint[Math.Max(0, GetKeyboardLayoutList(0, []))];
        GetKeyboardLayoutList(layouts.Length, layouts);
        return layouts.FirstOrDefault(l => ((long)l & 0xffff) == (language == "ru" ? 0x419 : 0x409));
    }
    internal static bool SwitchLayout(nint window, string language)
    {
        if (Language(window) == language) return true;
        var layout = FindLayout(language);
        if (layout == 0 || GetForegroundWindow() != window) return false;
        if (!PostMessageW(window, 0x50, 0, layout)) return false;
        for (int i = 0; i < 30; i++)
        {
            if (GetForegroundWindow() != window) return false;
            if (Language(window) == language) return true;
            Thread.Sleep(10);
        }
        return false;
    }
    internal static bool Unicode(string text)
    {
        var events = text.SelectMany(c => new[] {
            new Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 4, Extra = OwnInput } } },
            new Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 6, Extra = OwnInput } } }
        }).ToArray();
        return events.Length > 0 && SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) == events.Length;
    }
}

internal sealed class Hotkeys : System.Windows.Forms.NativeWindow, IDisposable
{
    internal bool UndoAvailable { get; private set; }
    internal bool CharacterAvailable { get; private set; }
    private bool disposed;
    internal Hotkeys()
    {
        CreateHandle(new System.Windows.Forms.CreateParams { Caption = "Shift Switcher hotkeys", Parent = new nint(-3) });
        UndoAvailable = Native.RegisterHotKey(Handle, 1, 0x4003, 90); // Ctrl+Alt+Z, no repeat.
        CharacterAvailable = Native.RegisterHotKey(Handle, 2, 0x4003, 190);
    }
    // KeyboardMonitor observes the original key and schedules work after key-up.
    // Registration reserves the chord so the focused application cannot act on it too.
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (UndoAvailable) Native.UnregisterHotKey(Handle, 1);
        if (CharacterAvailable) Native.UnregisterHotKey(Handle, 2);
        UndoAvailable = CharacterAvailable = false;
        DestroyHandle();
    }
}

internal readonly record struct KeyEvent(int Key, bool Down, double Time, bool Control, bool Alt, bool Windows);

internal sealed class KeyboardMonitor : IDisposable
{
    private readonly Action<KeyEvent> key;
    private readonly Action click;
    private readonly SynchronizationContext dispatcher;
    private readonly Native.HookProc keyboardProc, mouseProc;
    private readonly HashSet<int> modifiers = [];
    private uint threadId;
    private readonly Thread thread;
    internal bool Connected { get; private set; }
    private readonly ManualResetEventSlim ready = new();

    internal KeyboardMonitor(Action<KeyEvent> key, Action click, SynchronizationContext dispatcher)
    {
        this.key = key; this.click = click; this.dispatcher = dispatcher;
        keyboardProc = Keyboard; mouseProc = Mouse;
        thread = new Thread(Run) { IsBackground = true, Name = "Shift Switcher keyboard monitor" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Wait(TimeSpan.FromSeconds(3));
    }
    private void Run()
    {
        threadId = Native.GetCurrentThreadId();
        foreach (var vk in new[] { 17, 162, 163, 18, 164, 165, 91, 92 })
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) modifiers.Add(vk);
        var module = Native.GetModuleHandleW(null);
        var keyboard = Native.SetWindowsHookExW(13, keyboardProc, module, 0);
        var mouse = Native.SetWindowsHookExW(14, mouseProc, module, 0);
        Native.PeekMessageW(out _, 0, 0, 0, 0); // Create the queue before signalling readiness.
        Connected = keyboard != 0 && mouse != 0; ready.Set();
        if (Connected) while (Native.GetMessageW(out _, 0, 0, 0) > 0) { }
        if (keyboard != 0) Native.UnhookWindowsHookEx(keyboard);
        if (mouse != 0) Native.UnhookWindowsHookEx(mouse);
        Connected = false;
    }
    private nint Keyboard(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            var value = Marshal.PtrToStructure<Native.KeyboardData>(data);
            if ((value.Flags & 0x10) == 0 && value.Extra != Native.OwnInput)
            {
                var down = message is 0x100 or 0x104;
                if (down) Native.PhysicalInput();
                var vk = (int)value.Key;
                // The hook runs before GetAsyncKeyState updates; track modifier events.
                if (vk is 17 or 162 or 163 or 18 or 164 or 165 or 91 or 92)
                { if (down) modifiers.Add(vk); else modifiers.Remove(vk); }
                var input = new KeyEvent(vk, down, value.Time / 1000d,
                    modifiers.Overlaps([17, 162, 163]), modifiers.Overlaps([18, 164, 165]), modifiers.Overlaps([91, 92]));
                dispatcher.Post(_ => key(input), null);
            }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }
    private nint Mouse(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x201 or 0x204 or 0x207)
        { Native.PhysicalInput(); dispatcher.Post(_ => click(), null); }
        return Native.CallNextHookEx(0, code, message, data);
    }
    public void Dispose()
    {
        if (threadId != 0) Native.PostThreadMessageW(threadId, 0x12, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2)); ready.Dispose();
    }
}
