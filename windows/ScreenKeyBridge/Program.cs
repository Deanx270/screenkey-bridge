using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenKeyBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, "ScreenKeyBridge.User." + Environment.UserName, out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new BridgeContext());
    }
}

internal sealed class BridgeContext : ApplicationContext
{
    private const int Port = 38473;
    private readonly string token;
    private readonly NotifyIcon tray;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private int checking;
    private string status = "Starting";
    private readonly KeyboardRouter keyboard = new();
    private readonly string shortcutPath;
    private readonly string captureShortcutPath;
    private readonly string displayPreferencePath;
    private readonly string devicePath;
    private readonly string startupCommand;
    private readonly ToolStripMenuItem connectionItem = new("ADB: checking…") { Enabled = false };
    private readonly ToolStripMenuItem startupItem = new("Start at Windows sign-in");
    private readonly ToolStripMenuItem captureItem = new("Last screenshot: none") { Enabled = false };
    private readonly Control ui = new();
    private int shortcut = 11;
    private int captureShortcut = 10;
    private string captureStatus = "none";
    private volatile string? connectedSerial;
    private readonly object captureGate = new();
    private string selectedDisplay = "";
    private PendingCapture? pendingCapture;
    private int captureBusy;
    private sealed record PendingCapture(string Id, byte[] Image, TaskCompletionSource<bool> Completion);

    public BridgeContext()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeyBridge");
        Directory.CreateDirectory(dir);
        devicePath = Path.Combine(dir, "last-wireless-device.txt");
        startupCommand = GetStartupCommand();
        _ = ui.Handle;
        shortcutPath = Path.Combine(dir, "keyboard-shortcut.txt");
        captureShortcutPath = Path.Combine(dir, "capture-shortcut.txt");
        displayPreferencePath = Path.Combine(dir, "capture-display.txt");
        if (File.Exists(displayPreferencePath)) selectedDisplay = File.ReadAllText(displayPreferencePath).Trim();
        if (File.Exists(shortcutPath) && int.TryParse(File.ReadAllText(shortcutPath), out int saved) && saved is >= 1 and <= 11) shortcut = saved;
        if (File.Exists(captureShortcutPath) && int.TryParse(File.ReadAllText(captureShortcutPath), out int captureSaved) && captureSaved is >= 1 and <= 11) captureShortcut = captureSaved;
        if (captureShortcut == shortcut) captureShortcut = shortcut == 10 ? 9 : 10;
        keyboard.SetToggleFunctionKey(shortcut);
        keyboard.SetCaptureFunctionKey(captureShortcut);
        keyboard.CaptureRequested = () => _ = CaptureForPhone();
        string path = Path.Combine(dir, "pairing-key.txt");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        token = File.ReadAllText(path).Trim();
        var menu = new ContextMenuStrip();
        menu.Items.Add(connectionItem);
        menu.Items.Add(captureItem);
        menu.Items.Add("Show pairing key and status", null, (_, _) => ShowStatus());
        menu.Items.Add("Reconnect ADB", null, async (_, _) => await ConnectAdb());
        menu.Items.Add("Capture screen to phone", null, (_, _) => _ = Task.Run(CaptureForPhone));
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(startupItem);
        menu.Opening += (_, _) => RefreshStartup();
        menu.Items.Add("Toggle keyboard routing", null, (_, _) => keyboard.Toggle());
        var shortcutMenu = new ToolStripMenuItem("Keyboard shortcut (Ctrl+Alt)");
        for (int i = 1; i <= 11; i++)
        {
            int choice = i;
            var item = new ToolStripMenuItem($"F{i}") { Checked = i == shortcut };
            item.Click += (_, _) => {
                try {
                    if (choice == captureShortcut) { MessageBox.Show("Choose a different key from the screenshot shortcut.", "ScreenKey Bridge"); return; }
                    File.WriteAllText(shortcutPath, choice.ToString());
                    shortcut = choice; keyboard.SetToggleFunctionKey(choice);
                    foreach (ToolStripMenuItem entry in shortcutMenu.DropDownItems) entry.Checked = ReferenceEquals(entry, item);
                } catch (IOException ex) { MessageBox.Show($"Could not save shortcut: {ex.Message}", "ScreenKey Bridge"); }
            };
            shortcutMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(shortcutMenu);
        var captureMenu = new ToolStripMenuItem("Screenshot shortcut (Ctrl+Alt)");
        for (int i = 1; i <= 11; i++)
        {
            int choice = i;
            var item = new ToolStripMenuItem($"F{i}") { Checked = i == captureShortcut };
            item.Click += (_, _) => {
                try {
                    if (choice == shortcut) { MessageBox.Show("Choose a different key from the keyboard shortcut.", "ScreenKey Bridge"); return; }
                    File.WriteAllText(captureShortcutPath, choice.ToString());
                    captureShortcut = choice; keyboard.SetCaptureFunctionKey(choice);
                    foreach (ToolStripMenuItem entry in captureMenu.DropDownItems) entry.Checked = ReferenceEquals(entry, item);
                } catch (IOException ex) { MessageBox.Show($"Could not save shortcut: {ex.Message}", "ScreenKey Bridge"); }
            };
            captureMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(captureMenu);
        menu.Items.Add("Return keyboard to PC (Ctrl+Alt+F12)", null, (_, _) => keyboard.Disconnect());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Text = "ScreenKey Bridge", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowStatus();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { listener.Start(); _ = Serve(); }
        catch (HttpListenerException ex) { MessageBox.Show($"Could not start local server: {ex.Message}", "ScreenKey Bridge"); ExitThread(); return; }
        timer.Tick += async (_, _) => await ConnectAdb();
        timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _ = ConnectAdb();
    }

    private void ShowStatus() => MessageBox.Show($"{status}\nKeyboard: {keyboard.State()}\nLast screenshot: {captureStatus}\n\nCtrl+Alt+F{captureShortcut}: capture to phone\nCtrl+Alt+F{shortcut}: toggle keyboard routing\nCtrl+Alt+F12: return keyboard to PC\n\nEnter this pairing key in the phone app:\n\n{token}\n\nKeep this key private. The key is stored in your Windows user profile.", "ScreenKey Bridge");
    private void SetCaptureStatus(string value)
    {
        captureStatus = value;
        if (ui.IsDisposed || !ui.IsHandleCreated) return;
        if (ui.InvokeRequired) { try { ui.BeginInvoke(() => SetCaptureStatus(value)); } catch (InvalidOperationException) { } return; }
        captureItem.Text = "Last screenshot: " + value;
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect) keyboard.Disconnect(); }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ScreenKeyBridge";
    private static string GetStartupCommand()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the running executable.");
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = Path.Combine(AppContext.BaseDirectory, "ScreenKeyBridge.dll");
            if (!File.Exists(assembly)) throw new InvalidOperationException("Cannot find the running application.");
            return $"\"{exe}\" \"{assembly}\"";
        }
        return $"\"{exe}\"";
    }
    private void RefreshStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            string? value = key?.GetValue(RunValue) as string;
            startupItem.Checked = string.Equals(value, startupCommand, StringComparison.OrdinalIgnoreCase);
            startupItem.Text = value != null && !startupItem.Checked ? "Start at Windows sign-in (another location configured)" : "Start at Windows sign-in";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { startupItem.Checked = false; startupItem.Text = "Start at Windows sign-in (unavailable)"; }
    }
    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey) ?? throw new IOException("Could not open the sign-in settings.");
            string? value = key.GetValue(RunValue) as string;
            if (value != null && !string.Equals(value, startupCommand, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("A ScreenKey Bridge startup entry already points to another location. Remove that entry in Windows Startup settings before changing this one.", "ScreenKey Bridge");
                return;
            }
            if (value == null) key.SetValue(RunValue, startupCommand, RegistryValueKind.String);
            else key.DeleteValue(RunValue, false);
            RefreshStartup();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { MessageBox.Show($"Could not change sign-in startup: {ex.Message}", "ScreenKey Bridge"); }
    }
    private void SetConnectionStatus(string value)
    {
        status = value;
        if (ui.IsDisposed || !ui.IsHandleCreated) return;
        if (ui.InvokeRequired) { try { ui.BeginInvoke(() => SetConnectionStatus(value)); } catch (InvalidOperationException) { } return; }
        connectionItem.Text = "ADB: " + value;
        string tip = "ScreenKey Bridge — " + value;
        tray.Text = tip.Length > 63 ? tip[..60] + "…" : tip;
    }
    private static bool IsWirelessEndpoint(string value)
    {
        int colon = value.LastIndexOf(':');
        return colon > 0 && IPAddress.TryParse(value[..colon], out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && int.TryParse(value[(colon + 1)..], out int port) && port is >= 1 and <= 65535;
    }

    private async Task ConnectAdb()
    {
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        try
        {
            SetConnectionStatus("Checking connection…");
            string devices = await RunAdb("devices");
            var entries = ParseDevices(devices);
            var serials = entries.Where(parts => parts[1] == "device").Select(parts => parts[0]).ToArray();
            if (serials.Length == 0 && !entries.Any(parts => parts[1] == "unauthorized") && File.Exists(devicePath))
            {
                string saved = (await File.ReadAllTextAsync(devicePath)).Trim();
                if (IsWirelessEndpoint(saved))
                {
                    SetConnectionStatus("Reconnecting saved wireless device…");
                    await RunAdb($"connect {saved}");
                    entries = ParseDevices(await RunAdb("devices"));
                    serials = entries.Where(parts => parts[1] == "device").Select(parts => parts[0]).ToArray();
                }
            }
            if (serials.Length != 1)
            {
                string errorMessage = serials.Length > 1 ? "Multiple devices: connect only one." : entries.Any(parts => parts[1] == "unauthorized") ? "Phone unauthorized: approve USB debugging on phone." : entries.Any(parts => parts[1] == "offline") ? "Phone offline: check ADB and Wi-Fi." : "No ADB device: reconnect the phone.";
                connectedSerial = null; SetConnectionStatus(errorMessage); keyboard.Disconnect(); return;
            }
            await RunAdb($"-s {serials[0]} reverse tcp:{Port} tcp:{Port}");
            connectedSerial = serials[0];
            string message = $"Connected to {serials[0]}";
            if (IsWirelessEndpoint(serials[0]))
            {
                try
                {
                    if (!File.Exists(devicePath) || (await File.ReadAllTextAsync(devicePath)).Trim() != serials[0])
                        await File.WriteAllTextAsync(devicePath, serials[0]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { message += " (could not save for reconnect)"; }
            }
            SetConnectionStatus(message);
        }
        catch (Exception ex) { connectedSerial = null; SetConnectionStatus($"Connection error: {ex.Message}"); keyboard.Disconnect(); }
        finally { Interlocked.Exchange(ref checking, 0); }
    }

    private static string[][] ParseDevices(string output) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && parts[0] != "List" && parts[1] is "device" or "offline" or "unauthorized")
        .ToArray();

    private static async Task<string> RunAdb(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("adb", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("Could not start adb.");
        Task<string> output = p.StandardOutput.ReadToEndAsync();
        Task<string> error = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw new TimeoutException("ADB did not respond."); }
        if (p.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
        return await output;
    }

    private static byte[] CaptureBytes(string displayName)
    {
        Rectangle bounds;
        if (string.IsNullOrEmpty(displayName)) bounds = SystemInformation.VirtualScreen;
        else
        {
            var display = Screen.AllScreens.FirstOrDefault(screen => string.Equals(screen.DeviceName, displayName, StringComparison.Ordinal));
            if (display == null) throw new ArgumentException("Selected display is unavailable.");
            bounds = display.Bounds;
        }
        using var image = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        if (stream.Length > 100L * 1024 * 1024) throw new IOException("Screenshot is too large.");
        return stream.ToArray();
    }

    private async Task CaptureForPhone()
    {
        if (Interlocked.Exchange(ref captureBusy, 1) != 0) { SetCaptureStatus("capture already in progress"); return; }
        PendingCapture? request = null;
        try
        {
            string? serial = connectedSerial;
            if (serial == null) throw new InvalidOperationException("phone is not connected");
            string display;
            lock (captureGate) display = selectedDisplay;
            // This method runs on a worker queued directly by the keyboard hook. Capture before starting ADB.
            byte[] image = CaptureBytes(display);
            request = new PendingCapture(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), image, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (captureGate) pendingCapture = request;
            SetCaptureStatus("sending to phone…");
            string output = await RunAdb($"-s {serial} shell am start-foreground-service -n com.screenkeybridge.app/.CaptureService --es request_id {request.Id}");
            if (output.Contains("Error:", StringComparison.OrdinalIgnoreCase) || output.Contains("Exception", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("phone could not start capture service");
            if (await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(30))) != request.Completion.Task)
                throw new TimeoutException("phone did not confirm the save");
            SetCaptureStatus(await request.Completion.Task ? "saved to phone" : "phone could not save; check phone notification");
        }
        catch (Exception ex) { SetCaptureStatus("failed: " + ex.Message); }
        finally
        {
            lock (captureGate) { if (ReferenceEquals(pendingCapture, request)) pendingCapture = null; }
            Interlocked.Exchange(ref captureBusy, 0);
        }
    }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            _ = Handle(context);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            string given = context.Request.Headers["X-ScreenKey-Token"] ?? "";
            byte[] actual = Encoding.UTF8.GetBytes(given), expected = Encoding.UTF8.GetBytes(token);
            if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected)) { context.Response.StatusCode = 401; return; }
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 404; return; }
            string? route = context.Request.Url?.AbsolutePath;
            if (route == "/capture/request")
            {
                _ = Task.Run(CaptureForPhone);
                context.Response.StatusCode = 202;
                return;
            }
            if (route == "/capture/status")
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { state = captureStatus });
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = json.Length;
                await context.Response.OutputStream.WriteAsync(json); return;
            }
            if (route == "/capture/preference")
            {
                string display = context.Request.QueryString["display"] ?? "";
                if (display.Length > 256 || display.Contains('\r') || display.Contains('\n')) { context.Response.StatusCode = 400; return; }
                string existing; lock (captureGate) existing = selectedDisplay;
                if (!string.Equals(existing, display, StringComparison.Ordinal)) await File.WriteAllTextAsync(displayPreferencePath, display);
                lock (captureGate) selectedDisplay = display;
                return;
            }
            if (route == "/capture/pending" || route == "/capture/ack")
            {
                string? id = context.Request.QueryString["id"];
                PendingCapture? pending;
                lock (captureGate) pending = pendingCapture;
                if (pending == null || !string.Equals(pending.Id, id, StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
                if (route == "/capture/ack") { pending.Completion.TrySetResult(context.Request.QueryString["ok"] == "1"); return; }
                context.Response.ContentType = "image/png";
                context.Response.ContentLength64 = pending.Image.Length;
                await context.Response.OutputStream.WriteAsync(pending.Image);
                return;
            }
            if (route == "/displays")
            {
                var displays = Screen.AllScreens.Select((screen, index) => new {
                    id = screen.DeviceName,
                    label = $"Display {index + 1}{(screen.Primary ? " (primary)" : "")} — {screen.Bounds.Width} × {screen.Bounds.Height}"
                }).ToArray();
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { displays });
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload);
                return;
            }
            if (route is "/keyboard/state" or "/keyboard/toggle" or "/keyboard/poll" or "/keyboard/disconnect" or "/keyboard/unready")
            {
                string[] events = Array.Empty<string>();
                if (route == "/keyboard/toggle") keyboard.Toggle();
                if (route == "/keyboard/poll") events = keyboard.Heartbeat();
                if (route == "/keyboard/disconnect") keyboard.Disconnect();
                if (route == "/keyboard/unready") keyboard.InputUnavailable();
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { state = keyboard.State(), events });
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = json.Length;
                await context.Response.OutputStream.WriteAsync(json);
                return;
            }
            if (route != "/capture") { context.Response.StatusCode = 404; return; }
            byte[] png;
            try { png = CaptureBytes(context.Request.QueryString["display"] ?? ""); }
            catch (ArgumentException) { context.Response.StatusCode = 400; return; }
            context.Response.ContentType = "image/png";
            context.Response.ContentLength64 = png.Length;
            await context.Response.OutputStream.WriteAsync(png);
        }
        catch (Exception) { try { context.Response.StatusCode = 500; } catch (ObjectDisposedException) { } }
        finally { context.Response.Close(); }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch; keyboard.Dispose(); timer.Stop(); timer.Dispose(); stop.Cancel(); listener.Close(); tray.Visible = false; tray.Dispose(); ui.Dispose(); stop.Dispose();
        base.ExitThreadCore();
    }
}
