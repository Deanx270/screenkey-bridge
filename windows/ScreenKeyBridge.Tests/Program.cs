using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Exercise the real hook callback without installing another system-wide hook.
if (args.Length != 1) throw new ArgumentException("Pass the path to ScreenKeyBridge.dll.");
var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("ScreenKeyBridge.KeyboardRouter", true)!;
var router = RuntimeHelpers.GetUninitializedObject(type);
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
void Set(string name, object value) => type.GetField(name, flags)!.SetValue(router, value);
Set("gate", new object()); Set("queue", new Queue<string>());
Set("held", new HashSet<int>()); Set("swallowed", new HashSet<int>());
Set("armed", true); Set("phoneReady", true); Set("lastHeartbeat", Environment.TickCount64); Set("toggleKey", 0x7A); Set("captureKey", 0x79);
using var captured = new ManualResetEventSlim();
type.GetProperty("CaptureRequested")!.SetValue(router, (Action)(() => captured.Set()));
var keyInfo = type.GetNestedType("Kbd", BindingFlags.NonPublic)!;
var hook = type.GetMethod("OnKey", flags)!;
void Key(uint key, bool down)
{
    var info = Activator.CreateInstance(keyInfo)!;
    keyInfo.GetField("vkCode")!.SetValue(info, key);
    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(keyInfo));
    try
    {
        Marshal.StructureToPtr(info, pointer, false);
        var result = (IntPtr)hook.Invoke(router, new object[] { 0, (IntPtr)(down ? 0x100 : 0x101), pointer })!;
        if (result != (IntPtr)1) throw new Exception($"Key {key} was passed to Windows.");
    }
    finally { Marshal.FreeHGlobal(pointer); }
}
void Press(uint key) { Key(key, true); Key(key, false); }
Key(0xA2, true); // Ctrl
foreach (uint key in new uint[] { 0x41, 0x56, 0x5A, 0x08 }) Press(key);
Key(0xA0, true); Press(0x25); Key(0xA0, false); // Ctrl+Shift+Left
Key(0xA4, true); Press(0x4B); // An unsupported Ctrl+Alt combination must not change the destination.
string state = (string)type.GetMethod("State")!.Invoke(router, null)!;
var events = (string[])type.GetMethod("Heartbeat")!.Invoke(router, null)!;
string[] expected = { "<ctrl:a>", "<ctrl:v>", "<ctrl:z>", "<ctrl:backspace>", "<ctrl:shift:left>", "<unsupported>" };
if (state != "phone" || !events.SequenceEqual(expected)) throw new Exception($"Unexpected mode or events: {state}, {string.Join(", ", events)}");
Press(0x79); // Ctrl+Alt+F10 captures without changing destination.
if (!captured.Wait(2000) || (string)type.GetMethod("State")!.Invoke(router, null)! != "phone") throw new Exception("Capture shortcut did not fire in phone mode.");
Press(0x7B); // Ctrl+Alt+F12
if ((string)type.GetMethod("State")!.Invoke(router, null)! != "pc") throw new Exception("Emergency shortcut did not return to PC.");
captured.Reset();
Press(0x79); // The capture shortcut also works while typing stays on the PC.
if (!captured.Wait(2000) || (string)type.GetMethod("State")!.Invoke(router, null)! != "pc") throw new Exception("Capture shortcut did not fire in PC mode.");
Console.WriteLine("Keyboard routing regression checks passed.");
