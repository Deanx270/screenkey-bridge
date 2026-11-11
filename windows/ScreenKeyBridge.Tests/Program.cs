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

Exception? setupError = null;
var setupThread = new Thread(() => {
    try
    {
        var setupType = type.Assembly.GetType("ScreenKeyBridge.ConnectionSetupForm", true)!;
        var commands = new List<string>();
        string deviceListing = "List of devices attached\nUSB123\tdevice model:Test_Phone\n";
        Func<string, Task<string>> adb = command => {
            commands.Add(command);
            if (command == "devices" || command == "devices -l") return Task.FromResult(deviceListing);
            if (command == "-s USB123 tcpip 5555") return Task.FromResult("restarting in TCP mode port: 5555");
            if (command == "connect 192.168.1.10:5555") return Task.FromResult("connected to 192.168.1.10:5555");
            throw new Exception("Unexpected command: " + command);
        };
        Func<string, Task<string>> pair = serial => Task.FromResult("Paired " + serial);
        using var form = (System.Windows.Forms.Form)Activator.CreateInstance(setupType, adb, pair)!;
        var endpoint = (System.Windows.Forms.TextBox)setupType.GetField("endpoint", flags)!.GetValue(form)!;
        var connect = setupType.GetMethod("ConnectWifi", flags)!;
        endpoint.Text = "not-an-address";
        try { ((Task)connect.Invoke(form, null)!).GetAwaiter().GetResult(); throw new Exception("Invalid address was accepted."); }
        catch (InvalidOperationException) { }
        if (commands.Count != 0) throw new Exception("Invalid address invoked ADB.");
        endpoint.Text = "192.168.1.10";
        ((Task)connect.Invoke(form, null)!).GetAwaiter().GetResult();
        ((Task)setupType.GetMethod("EnableTcp", flags)!.Invoke(form, null)!).GetAwaiter().GetResult();
        if (!commands.SequenceEqual(new[] { "connect 192.168.1.10:5555", "devices -l", "devices", "-s USB123 tcpip 5555" })) throw new Exception("Unexpected setup commands.");
        deviceListing += "192.168.1.10:5555\tdevice model:Second_Phone\nUNAUTHORIZED\tunauthorized\n";
        var picker = (System.Windows.Forms.ComboBox)setupType.GetField("devices", flags)!.GetValue(form)!;
        ((Task)setupType.GetMethod("RefreshDevices", flags)!.Invoke(form, null)!).GetAwaiter().GetResult();
        if (picker.Items.Count != 2 || picker.SelectedIndex != 0 || !picker.Items[1]!.ToString()!.Contains("Second Phone")) throw new Exception("The device list did not preserve the chosen device or filter unauthorized devices.");
        picker.SelectedIndex = 1;
        ((Task)setupType.GetMethod("RefreshDevices", flags)!.Invoke(form, null)!).GetAwaiter().GetResult();
        if (picker.SelectedIndex != 1) throw new Exception("Refreshing the device list lost the selected device.");
        var bridge = type.Assembly.GetType("ScreenKeyBridge.BridgeContext", true)!;
        var choose = bridge.GetMethod("ChooseSerial", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? Choose(string[] serials, string? selected) => (string?)choose.Invoke(null, new object?[] { serials, selected });
        if (Choose(new[] { "USB123" }, null) != "USB123" || Choose(new[] { "USB123", "192.168.1.10:5555" }, null) != null || Choose(new[] { "USB123", "192.168.1.10:5555" }, "192.168.1.10:5555") != "192.168.1.10:5555" || Choose(new[] { "USB123" }, "other") != null) throw new Exception("Device selection did not preserve the chosen target.");
        Console.WriteLine("Guided ADB setup validation passed.");
    }
    catch (Exception ex) { setupError = ex; }
});
setupThread.SetApartmentState(ApartmentState.STA); setupThread.Start(); setupThread.Join();
if (setupError != null) throw new Exception("Connection setup test failed.", setupError);
