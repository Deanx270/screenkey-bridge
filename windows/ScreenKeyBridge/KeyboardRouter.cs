using System.Runtime.InteropServices;

namespace ScreenKeyBridge;

// The hook only reads state and enqueues bounded, in-memory messages; it never performs I/O.
internal sealed class KeyboardRouter : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<string> queue = new();
    private readonly HashSet<int> swallowed = new();
    private readonly HashSet<int> held = new();
    private readonly HookProc callback;
    private IntPtr hook;
    private bool armed;
    private long lastHeartbeat;
    private long armedAt;
    private bool phoneReady;
    private int toggleKey = 0x7A;
    private int captureKey = 0x79;
    public Action? CaptureRequested { get; set; }
    private const int LeaseMs = 2000;
    private const int ArmMs = 30000;

    public KeyboardRouter()
    {
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private void Expire()
    {
        if (armed && ((phoneReady && Environment.TickCount64 - lastHeartbeat > LeaseMs) || (!phoneReady && Environment.TickCount64 - armedAt > ArmMs))) Reset();
    }
    private void Reset() { armed = false; phoneReady = false; queue.Clear(); }
    public string State()
    {
        lock (gate) { Expire(); return armed ? (phoneReady ? "phone" : "waiting") : "pc"; }
    }
    public string Toggle()
    {
        lock (gate)
        {
            Expire();
            if (armed) Reset();
            else { armed = true; armedAt = Environment.TickCount64; }
            return armed ? "waiting" : "pc";
        }
    }
    public void SetToggleFunctionKey(int number)
    {
        if (number < 1 || number > 11) throw new ArgumentOutOfRangeException(nameof(number));
        lock (gate) toggleKey = 0x6F + number;
    }
    public void SetCaptureFunctionKey(int number)
    {
        if (number < 1 || number > 11) throw new ArgumentOutOfRangeException(nameof(number));
        lock (gate) captureKey = 0x6F + number;
    }
    public string[] Heartbeat()
    {
        lock (gate)
        {
            Expire();
            lastHeartbeat = Environment.TickCount64;
            if (armed) phoneReady = true;
            if (!armed) return Array.Empty<string>();
            var events = queue.ToArray(); queue.Clear(); return events;
        }
    }
    public void Disconnect() { lock (gate) Reset(); }
    public void InputUnavailable() { lock (gate) { Expire(); if (phoneReady) Reset(); } }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        int type = message.ToInt32();
        bool down = type is 0x100 or 0x104, up = type is 0x101 or 0x105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);
        var info = Marshal.PtrToStructure<Kbd>(data);
        int key = (int)info.vkCode;
        // Ignore injected events, including our own test harness and accessibility tools.
        if ((info.flags & 0x10) != 0) return CallNextHookEx(hook, code, message, data);
        lock (gate)
        {
            Expire();
            bool firstDown = down && held.Add(key);
            if (up) held.Remove(key);
            if (up && swallowed.Remove(key)) return (IntPtr)1;
            bool ctrl = held.Contains(0x11) || held.Contains(0xA2) || held.Contains(0xA3);
            bool alt = held.Contains(0x12) || held.Contains(0xA4) || held.Contains(0xA5);
            if (down && ctrl && alt && key == captureKey)
            {
                if (firstDown) ThreadPool.QueueUserWorkItem(_ => CaptureRequested?.Invoke());
                swallowed.Add(key); return (IntPtr)1;
            }
            if (down && ctrl && alt && (key == toggleKey || key == 0x7B))
            {
                if (firstDown) { if (key == 0x7B) Reset(); else Toggle(); }
                swallowed.Add(key); return (IntPtr)1;
            }
            if (!armed || !phoneReady) return CallNextHookEx(hook, code, message, data);
            if (up) return CallNextHookEx(hook, code, message, data);
            if (IsModifier(key)) { swallowed.Add(key); return (IntPtr)1; }
            bool shift = held.Contains(0x10) || held.Contains(0xA0) || held.Contains(0xA1);
            bool caps = (GetKeyState(0x14) & 1) != 0;
            string value = (ctrl || alt ? TranslateShortcut(key, ctrl, alt, shift) : Translate(key, shift, caps)) ?? "<unsupported>";
            // If delivery cannot keep up, fail open instead of buffering without bound.
            if (queue.Count >= 128) { Reset(); return CallNextHookEx(hook, code, message, data); }
            queue.Enqueue(value); swallowed.Add(key); return (IntPtr)1;
        }
    }

    private static bool IsModifier(int k) => k is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
    private static string? TranslateShortcut(int key, bool ctrl, bool alt, bool shift)
    {
        if (!ctrl || alt) return null;
        string? name = key switch {
            0x41 => "a", 0x43 => "c", 0x56 => "v", 0x58 => "x", 0x59 => "y", 0x5A => "z",
            0x08 => "backspace", 0x2E => "delete", 0x25 => "left", 0x26 => "up", 0x27 => "right", 0x28 => "down", 0x24 => "home", 0x23 => "end", _ => null };
        if (shift && key is not (0x5A or 0x25 or 0x26 or 0x27 or 0x28 or 0x24 or 0x23)) return null;
        return name == null ? null : $"<ctrl:{(shift ? "shift:" : "")}{name}>";
    }
    private static string? Translate(int k, bool shift, bool caps)
    {
        if (k >= 0x41 && k <= 0x5A) return ((char)((shift ^ caps) ? k : k + 32)).ToString();
        if (k >= 0x30 && k <= 0x39) return (shift ? ")!@#$%^&*("[k - 0x30] : (char)k).ToString();
        return k switch {
            0x20 => " ", 0x08 => "<backspace>", 0x0D => "<enter>",
            0x25 => shift ? "<shift:left>" : "<left>", 0x26 => shift ? "<shift:up>" : "<up>", 0x27 => shift ? "<shift:right>" : "<right>", 0x28 => shift ? "<shift:down>" : "<down>",
            0x24 => shift ? "<shift:home>" : "<home>", 0x23 => shift ? "<shift:end>" : "<end>", 0x2E => "<delete>", 0x09 => shift ? "<shift:tab>" : "<tab>",
            0xBA => shift ? ":" : ";", 0xBB => shift ? "+" : "=", 0xBC => shift ? "<" : ",", 0xBD => shift ? "_" : "-", 0xBE => shift ? ">" : ".", 0xBF => shift ? "?" : "/", 0xC0 => shift ? "~" : "`", 0xDB => shift ? "{" : "[", 0xDC => shift ? "|" : "\\", 0xDD => shift ? "}" : "]", 0xDE => shift ? "\"" : "'", _ => null };
    }
    public void Dispose() { lock (gate) { Reset(); if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } } }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Kbd { public uint vkCode, scanCode, flags, time; public UIntPtr extraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
