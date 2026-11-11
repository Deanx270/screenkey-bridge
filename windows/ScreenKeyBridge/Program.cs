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
    private readonly Icon appIcon;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private int checking;
    private int disconnecting;
    private string status = "Starting";
    private readonly KeyboardRouter keyboard = new();
    private readonly PageBridge pages = new();
    private volatile string? browserToken;
    private readonly string shortcutPath;
    private readonly string captureShortcutPath;
    private readonly string htmlShortcutPath;
    private readonly string pdfShortcutPath;
    private readonly string displayPreferencePath;
    private readonly string androidTargetPath;
    private readonly string devicePath;
    private readonly string selectedDevicePath;
    private readonly string autoReconnectPath;
    private readonly string startupCommand;
    private readonly ToolStripMenuItem connectionItem = new("ADB: checking…") { Enabled = false };
    private readonly ToolStripMenuItem startupItem = new("Start at Windows sign-in");
    private readonly ToolStripMenuItem captureItem = new("Last screenshot: none") { Enabled = false };
    private readonly ToolStripMenuItem pageItem = new("Last page save: none") { Enabled = false };
    private readonly Control ui = new();
    private int shortcut = 11;
    private int captureShortcut = 10;
    private int htmlShortcut = 8;
    private int pdfShortcut = 9;
    private string captureStatus = "none";
    private volatile string? connectedSerial;
    private volatile string? preferredSerial;
    private volatile bool reconnectPaused;
    private volatile bool autoReconnect;
    private volatile string androidPackage = "com.screenkeybridge.app";
    private readonly object captureGate = new();
    private string selectedDisplay = "";
    private PendingCapture? pendingCapture;
    private int captureBusy;
    private readonly object pageGate = new();
    private PendingPage? pendingPage;
    private int pageBusy;
    private sealed record PendingCapture(string Id, byte[] Image, TaskCompletionSource<string> Completion);
    private sealed record PendingPage(string Id, string Json, string Format, TaskCompletionSource<string> Completion);

    public BridgeContext()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeyBridge");
        Directory.CreateDirectory(dir);
        devicePath = Path.Combine(dir, "last-wireless-device.txt");
        selectedDevicePath = Path.Combine(dir, "selected-device.txt");
        autoReconnectPath = Path.Combine(dir, "auto-reconnect.txt");
        autoReconnect = File.Exists(autoReconnectPath) && File.ReadAllText(autoReconnectPath).Trim() == "on";
        if (File.Exists(selectedDevicePath)) { string savedDevice = File.ReadAllText(selectedDevicePath).Trim(); if (IsSafeSerial(savedDevice)) preferredSerial = savedDevice; }
        androidTargetPath = Path.Combine(dir, "android-target.txt");
        if (File.Exists(androidTargetPath) && File.ReadAllText(androidTargetPath).Trim() == "com.screenkeybridge.app.preview") androidPackage = "com.screenkeybridge.app.preview";
        startupCommand = GetStartupCommand();
        _ = ui.Handle;
        shortcutPath = Path.Combine(dir, "keyboard-shortcut.txt");
        captureShortcutPath = Path.Combine(dir, "capture-shortcut.txt");
        htmlShortcutPath = Path.Combine(dir, "html-shortcut.txt");
        pdfShortcutPath = Path.Combine(dir, "pdf-shortcut.txt");
        displayPreferencePath = Path.Combine(dir, "capture-display.txt");
        if (File.Exists(displayPreferencePath)) selectedDisplay = File.ReadAllText(displayPreferencePath).Trim();
        if (File.Exists(shortcutPath) && int.TryParse(File.ReadAllText(shortcutPath), out int saved) && saved is >= 1 and <= 11) shortcut = saved;
        if (File.Exists(captureShortcutPath) && int.TryParse(File.ReadAllText(captureShortcutPath), out int captureSaved) && captureSaved is >= 1 and <= 11) captureShortcut = captureSaved;
        captureShortcut = FreeShortcut(captureShortcut, shortcut);
        if (File.Exists(htmlShortcutPath) && int.TryParse(File.ReadAllText(htmlShortcutPath), out int htmlSaved)) htmlShortcut = htmlSaved;
        if (File.Exists(pdfShortcutPath) && int.TryParse(File.ReadAllText(pdfShortcutPath), out int pdfSaved)) pdfShortcut = pdfSaved;
        htmlShortcut = FreeShortcut(htmlShortcut, shortcut, captureShortcut);
        pdfShortcut = FreeShortcut(pdfShortcut, shortcut, captureShortcut, htmlShortcut);
        keyboard.SetToggleFunctionKey(shortcut);
        keyboard.SetCaptureFunctionKey(captureShortcut);
        keyboard.SetHtmlFunctionKey(htmlShortcut); keyboard.SetPdfFunctionKey(pdfShortcut);
        keyboard.CaptureRequested = () => _ = CaptureForPhone();
        keyboard.HtmlRequested = () => _ = SavePageForPhone("html");
        keyboard.PdfRequested = () => _ = SavePageForPhone("pdf");
        string path = Path.Combine(dir, "pairing-key.txt");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        token = File.ReadAllText(path).Trim();
        try { browserToken = EdgeBrowserLink.Configure(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { browserToken = null; }
        var menu = new ContextMenuStrip();
        menu.Items.Add(connectionItem);
        menu.Items.Add(captureItem);
        menu.Items.Add(pageItem);
        menu.Items.Add("Show status", null, (_, _) => ShowStatus());
        menu.Items.Add("Connect and pair a phone…", null, (_, _) => OpenSetup());
        menu.Items.Add("Set up Edge page saving…", null, (_, _) => { try { browserToken = EdgeBrowserLink.Configure(); EdgeBrowserLink.Setup(); } catch (Exception ex) { MessageBox.Show(ex.Message, "ScreenKey Bridge"); } });
        var targetMenu = new ToolStripMenuItem("Android app installation");
        foreach (var target in new[] { (Name: "Normal app", Package: "com.screenkeybridge.app"), (Name: "Side-by-side preview", Package: "com.screenkeybridge.app.preview") })
        {
            var item = new ToolStripMenuItem(target.Name) { Checked = androidPackage == target.Package };
            item.Click += (_, _) => {
                try {
                    File.WriteAllText(androidTargetPath, target.Package);
                    androidPackage = target.Package; keyboard.Disconnect();
                    foreach (ToolStripMenuItem entry in targetMenu.DropDownItems) entry.Checked = ReferenceEquals(entry, item);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show($"Could not save Android app selection: {ex.Message}", "ScreenKey Bridge"); }
            };
            targetMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(targetMenu);
        menu.Items.Add("Reconnect ADB", null, async (_, _) => await ResumeConnection());
        menu.Items.Add("Disconnect wireless phone", null, async (_, _) => await DisconnectWireless());
        var autoReconnectItem = new ToolStripMenuItem("Automatically reconnect wireless ADB") { Checked = autoReconnect };
        autoReconnectItem.Click += (_, _) => {
            try {
                bool enabled = !autoReconnect;
                File.WriteAllText(autoReconnectPath, enabled ? "on" : "off");
                autoReconnect = enabled; autoReconnectItem.Checked = enabled;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show($"Could not save reconnect setting: {ex.Message}", "ScreenKey Bridge"); }
        };
        menu.Items.Add(autoReconnectItem);
        menu.Items.Add("Capture screen to phone", null, (_, _) => _ = Task.Run(CaptureForPhone));
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(startupItem);
        menu.Opening += async (_, _) => { RefreshStartup(); await ConnectAdb(); };
        menu.Items.Add("Toggle keyboard routing", null, (_, _) => keyboard.Toggle());
        var shortcutMenu = new ToolStripMenuItem("Keyboard shortcut (Ctrl+Alt)");
        for (int i = 1; i <= 11; i++)
        {
            int choice = i;
            var item = new ToolStripMenuItem($"F{i}") { Checked = i == shortcut };
            item.Click += (_, _) => {
                try {
                    if (choice == captureShortcut || choice == htmlShortcut || choice == pdfShortcut) { MessageBox.Show("Choose a key that is not used by another ScreenKey shortcut.", "ScreenKey Bridge"); return; }
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
                    if (choice == shortcut || choice == htmlShortcut || choice == pdfShortcut) { MessageBox.Show("Choose a key that is not used by another ScreenKey shortcut.", "ScreenKey Bridge"); return; }
                    File.WriteAllText(captureShortcutPath, choice.ToString());
                    captureShortcut = choice; keyboard.SetCaptureFunctionKey(choice);
                    foreach (ToolStripMenuItem entry in captureMenu.DropDownItems) entry.Checked = ReferenceEquals(entry, item);
                } catch (IOException ex) { MessageBox.Show($"Could not save shortcut: {ex.Message}", "ScreenKey Bridge"); }
            };
            captureMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(captureMenu);
        AddPageShortcutMenu(menu, "Save HTML shortcut (Ctrl+Alt)", false);
        AddPageShortcutMenu(menu, "Save PDF shortcut (Ctrl+Alt)", true);
        menu.Items.Add("Return keyboard to PC (Ctrl+Alt+F12)", null, (_, _) => keyboard.Disconnect());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        using (var iconStream = typeof(BridgeContext).Assembly.GetManifestResourceStream("ScreenKeyBridge.screenkey.ico") ?? throw new InvalidOperationException("Application icon is missing."))
        using (var icon = new Icon(iconStream)) appIcon = (Icon)icon.Clone();
        tray = new NotifyIcon { Text = "ScreenKey Bridge", Icon = appIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowStatus();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { listener.Start(); _ = Serve(); }
        catch (HttpListenerException ex) { MessageBox.Show($"Could not start local server: {ex.Message}", "ScreenKey Bridge"); ExitThread(); return; }
        timer.Tick += async (_, _) => await ConnectAdb();
        timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _ = ConnectAdb();
    }

    private static int FreeShortcut(int preferred, params int[] used) => preferred is >= 1 and <= 11 && !used.Contains(preferred) ? preferred : Enumerable.Range(1, 11).First(n => !used.Contains(n));
    private void AddPageShortcutMenu(ContextMenuStrip menu, string label, bool pdf)
    {
        var choices = new ToolStripMenuItem(label);
        for (int i = 1; i <= 11; i++) {
            int choice = i;
            var item = new ToolStripMenuItem($"F{i}") { Checked = i == (pdf ? pdfShortcut : htmlShortcut) };
            item.Click += (_, _) => {
                if (choice == shortcut || choice == captureShortcut || choice == (pdf ? htmlShortcut : pdfShortcut)) { MessageBox.Show("Choose a key that is not used by another ScreenKey shortcut.", "ScreenKey Bridge"); return; }
                try {
                    File.WriteAllText(pdf ? pdfShortcutPath : htmlShortcutPath, choice.ToString());
                    if (pdf) { pdfShortcut = choice; keyboard.SetPdfFunctionKey(choice); } else { htmlShortcut = choice; keyboard.SetHtmlFunctionKey(choice); }
                    foreach (ToolStripMenuItem entry in choices.DropDownItems) entry.Checked = ReferenceEquals(entry, item);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show($"Could not save shortcut: {ex.Message}", "ScreenKey Bridge"); }
            };
            choices.DropDownItems.Add(item);
        }
        menu.Items.Add(choices);
    }
    private void ShowStatus() => MessageBox.Show($"{status}\nAndroid app: {(androidPackage.EndsWith(".preview", StringComparison.Ordinal) ? "side-by-side preview" : "normal")}\nKeyboard: {keyboard.State()}\nLast screenshot: {captureStatus}\n\nCtrl+Alt+F{captureShortcut}: capture to phone\nCtrl+Alt+F{htmlShortcut}: save Edge page as HTML\nCtrl+Alt+F{pdfShortcut}: save Edge page as PDF\nCtrl+Alt+F{shortcut}: toggle keyboard routing\nCtrl+Alt+F12: return keyboard to PC\n\nTo connect or switch phones, choose Connect and pair a phone in the tray menu.", "ScreenKey Bridge");
    private void SetPageStatus(string value) { if (ui.IsDisposed || !ui.IsHandleCreated) return; if (ui.InvokeRequired) { try { ui.BeginInvoke(() => SetPageStatus(value)); } catch (InvalidOperationException) { } return; } pageItem.Text = "Last page save: " + value; }
    private void OpenSetup() { using var form = new ConnectionSetupForm(RunAdb, PairPhone); form.ShowDialog(); }
    private void SetCaptureStatus(string value)
    {
        captureStatus = value;
        if (ui.IsDisposed || !ui.IsHandleCreated) return;
        if (ui.InvokeRequired) { try { ui.BeginInvoke(() => SetCaptureStatus(value)); } catch (InvalidOperationException) { } return; }
        captureItem.Text = "Last screenshot: " + value;
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect) keyboard.Disconnect(); }

    private async Task<string> PairPhone(string serial)
    {
        if (!IsSafeSerial(serial)) throw new InvalidOperationException("Invalid device identifier. Refresh the device list.");
        if (Volatile.Read(ref disconnecting) != 0) throw new InvalidOperationException("Wait for disconnection to finish.");
        string? previous = preferredSerial;
        try
        {
            var serials = ParseDevices(await RunAdb("devices")).Where(parts => parts[1] == "device").Select(parts => parts[0]).ToArray();
            if (!serials.Contains(serial, StringComparer.Ordinal)) throw new InvalidOperationException("The selected device is no longer authorized or connected. Refresh the list.");
            preferredSerial = serial; connectedSerial = null; keyboard.Disconnect(); reconnectPaused = false;
            for (int i = 0; i < 60 && Volatile.Read(ref checking) != 0; i++) await Task.Delay(200);
            if (Volatile.Read(ref checking) != 0) throw new TimeoutException("ADB is busy. Try again.");
            await ConnectAdb();
            if (connectedSerial != serial) throw new InvalidOperationException("Could not establish the phone connection. Check the tray status.");
            string target = androidPackage;
            string result = await RunAdb($"-s {serial} shell am broadcast --include-stopped-packages -a com.screenkeybridge.action.PAIR -n {target}/com.screenkeybridge.app.PairingReceiver --es key {token}");
            if (!result.Contains("result=1", StringComparison.Ordinal)) throw new InvalidOperationException("The phone app did not confirm pairing. Install or update it and try again.");
            string message = $"Connected and paired {serial} with the {(target.EndsWith(".preview", StringComparison.Ordinal) ? "preview" : "normal")} app. Select its keyboard on the phone to use keyboard routing.";
            try { await File.WriteAllTextAsync(selectedDevicePath, serial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { message += " The device selection could not be saved for next time."; }
            return message;
        }
        catch { preferredSerial = previous; connectedSerial = null; keyboard.Disconnect(); SetConnectionStatus("Pairing failed; reconnect or choose a device."); throw; }
    }
    private async Task ResumeConnection()
    {
        if (Volatile.Read(ref disconnecting) != 0) { SetConnectionStatus("Wait for disconnection to finish."); return; }
        reconnectPaused = false;
        for (int i = 0; i < 60 && Volatile.Read(ref checking) != 0; i++) await Task.Delay(200);
        if (Volatile.Read(ref checking) != 0) { SetConnectionStatus("ADB is busy; try reconnecting again."); return; }
        await ConnectAdb(true);
    }
    private async Task DisconnectWireless()
    {
        if (Interlocked.Exchange(ref disconnecting, 1) != 0) return;
        reconnectPaused = true; connectedSerial = null; keyboard.Disconnect();
        try
        {
            for (int i = 0; i < 60 && Volatile.Read(ref checking) != 0; i++) await Task.Delay(200);
            if (Volatile.Read(ref checking) != 0) throw new TimeoutException("wait for ADB checking to finish and try again");
            var wireless = ParseDevices(await RunAdb("devices"))
                .Where(parts => IsWirelessEndpoint(parts[0])).Select(parts => parts[0]).ToArray();
            string? selected = preferredSerial != null ? (wireless.Contains(preferredSerial, StringComparer.Ordinal) ? preferredSerial : null) : wireless.Length == 1 ? wireless[0] : null;
            if (selected == null && wireless.Length > 1) { SetConnectionStatus("Reconnect paused; select a wireless phone first."); return; }
            if (selected != null) await RunAdb($"disconnect {selected}");
            SetConnectionStatus("Wireless connection paused. Use Reconnect ADB to resume.");
        }
        catch (Exception ex) { SetConnectionStatus("Disconnect failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref disconnecting, 0); }
    }

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
    internal static bool IsSafeSerial(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9._:-]+$");
    internal static string? ChooseSerial(string[] serials, string? preferred) => preferred == null ? (serials.Length == 1 ? serials[0] : null) : serials.Contains(preferred, StringComparer.Ordinal) ? preferred : null;
    internal static bool ShouldReconnect(bool automatic, bool requested, int authorizedCount, bool unauthorized) => (automatic || requested) && authorizedCount == 0 && !unauthorized;

    private async Task ConnectAdb(bool reconnectRequested = false)
    {
        if (reconnectPaused) return;
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        try
        {
            SetConnectionStatus("Checking connection…");
            string devices = await RunAdb("devices");
            if (reconnectPaused) return;
            var entries = ParseDevices(devices);
            var serials = entries.Where(parts => parts[1] == "device").Select(parts => parts[0]).ToArray();
            if (ShouldReconnect(autoReconnect, reconnectRequested, serials.Length, entries.Any(parts => parts[1] == "unauthorized")) && File.Exists(devicePath))
            {
                string saved = (await File.ReadAllTextAsync(devicePath)).Trim();
                if (IsWirelessEndpoint(saved))
                {
                    SetConnectionStatus("Reconnecting saved wireless device…");
                    await RunAdb($"connect {saved}");
                    if (reconnectPaused) return;
                    entries = ParseDevices(await RunAdb("devices"));
                    serials = entries.Where(parts => parts[1] == "device").Select(parts => parts[0]).ToArray();
                }
            }
            string? serial = ChooseSerial(serials, preferredSerial);
            if (serial == null)
            {
                string errorMessage = preferredSerial != null ? "Selected phone unavailable: reconnect it or select another." : serials.Length > 1 ? "Multiple devices: choose a phone in Connect and pair." : entries.Any(parts => parts[1] == "unauthorized") ? "Phone unauthorized: approve USB debugging on phone." : entries.Any(parts => parts[1] == "offline") ? "Phone offline: check ADB and Wi-Fi." : "No ADB device: reconnect the phone.";
                connectedSerial = null; SetConnectionStatus(errorMessage); keyboard.Disconnect(); return;
            }
            if (!IsSafeSerial(serial)) throw new InvalidOperationException("Invalid ADB device identifier.");
            await RunAdb($"-s {serial} reverse tcp:{Port} tcp:{Port}");
            if (reconnectPaused || ChooseSerial(serials, preferredSerial) != serial) return;
            connectedSerial = serial;
            string message = $"Connected to {serial}";
            if (IsWirelessEndpoint(serial))
            {
                try
                {
                    if (!File.Exists(devicePath) || (await File.ReadAllTextAsync(devicePath)).Trim() != serial)
                        await File.WriteAllTextAsync(devicePath, serial);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { message += " (could not save for reconnect)"; }
            }
            SetConnectionStatus(message);
        }
        catch (Exception ex) { connectedSerial = null; SetConnectionStatus($"Connection error: {ex.Message}"); keyboard.Disconnect(); }
        finally { Interlocked.Exchange(ref checking, 0); }
    }

    internal static string[][] ParseDevices(string output) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && parts[0] != "List" && parts[1] is "device" or "offline" or "unauthorized")
        .ToArray();

    private static async Task<string> RunAdb(string args)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        bool onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => { try { return File.Exists(Path.Combine(part.Trim().Trim('"'), "adb.exe")); } catch (ArgumentException) { return false; } });
        string executable = onPath || !File.Exists(bundled) ? "adb" : bundled;
        using var p = Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("Could not start adb.");
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
            request = new PendingCapture(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), image, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (captureGate) pendingCapture = request;
            SetCaptureStatus("sending to phone…");
            string target = androidPackage;
            string output = await RunAdb($"-s {serial} shell am start-foreground-service -n {target}/com.screenkeybridge.app.CaptureService --es request_id {request.Id}");
            if (output.Contains("Error:", StringComparison.OrdinalIgnoreCase) || output.Contains("Exception", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("phone could not start capture service");
            if (await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(30))) != request.Completion.Task)
                throw new TimeoutException("phone did not confirm the save");
            SetCaptureStatus(await request.Completion.Task);
        }
        catch (Exception ex) { SetCaptureStatus("failed: " + ex.Message); }
        finally
        {
            lock (captureGate) { if (ReferenceEquals(pendingCapture, request)) pendingCapture = null; }
            Interlocked.Exchange(ref captureBusy, 0);
        }
    }

    private async Task SavePageForPhone(string format)
    {
        if (Interlocked.Exchange(ref pageBusy, 1) != 0) { SetPageStatus("another page save is in progress"); return; }
        PendingPage? request = null;
        try {
            string? serial = connectedSerial;
            if (serial == null) throw new InvalidOperationException("phone is not connected");
            if (!EdgeBrowserLink.EdgeIsForeground()) throw new InvalidOperationException("bring Edge to the foreground");
            SetPageStatus("capturing Edge page…");
            string json = await pages.Capture();
            request = new PendingPage(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), json, format, new(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (pageGate) pendingPage = request;
            string target = androidPackage;
            string command = format == "html"
                ? $"-s {serial} shell am start-foreground-service -n {target}/com.screenkeybridge.app.PageSaveService --es request_id {request.Id}"
                : $"-s {serial} shell am start -n {target}/com.screenkeybridge.app.MainActivity -a com.screenkeybridge.action.PRINT_PAGE -f 0x24000000 --es request_id {request.Id}";
            string output = await RunAdb(command);
            if (output.Contains("Error:", StringComparison.OrdinalIgnoreCase) || output.Contains("Exception", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("phone could not start the page save");
            SetPageStatus("sending to phone…");
            if (await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(60))) != request.Completion.Task) throw new TimeoutException("phone did not confirm; unlock it and try again");
            SetPageStatus(await request.Completion.Task);
        } catch (Exception ex) { SetPageStatus("failed: " + ex.Message); }
        finally { lock (pageGate) { if (ReferenceEquals(pendingPage, request)) pendingPage = null; } Interlocked.Exchange(ref pageBusy, 0); }
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
            string? route = context.Request.Url?.AbsolutePath;
            bool extensionRoute = route is "/browser/poll" or "/browser/result";
            string origin = context.Request.Headers["Origin"] ?? "";
            if (extensionRoute && origin == EdgeBrowserLink.Origin) {
                context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-ScreenKey-Extension";
                context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                if (context.Request.HttpMethod == "OPTIONS") { context.Response.StatusCode = 204; return; }
            }
            if (extensionRoute && origin.Length != 0 && origin != EdgeBrowserLink.Origin) { context.Response.StatusCode = 403; return; }
            string expectedToken = extensionRoute ? browserToken ?? "" : token;
            string header = extensionRoute ? "X-ScreenKey-Extension" : "X-ScreenKey-Token";
            string given = context.Request.Headers[header] ?? "";
            byte[] actual = Encoding.UTF8.GetBytes(given), expected = Encoding.UTF8.GetBytes(expectedToken);
            if (extensionRoute ? !EdgeBrowserLink.Authorized(expectedToken, given, origin) : expected.Length == 0 || actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected)) { context.Response.StatusCode = 401; return; }
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 404; return; }
            if (route == "/page/save")
            {
                try {
                    if (!EdgeBrowserLink.EdgeIsForeground()) throw new InvalidOperationException("Bring Microsoft Edge to the foreground on the PC.");
                    string json = await pages.Capture();
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    context.Response.ContentType = "application/json"; context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload);
                } catch (Exception ex) {
                    context.Response.StatusCode = 409;
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { error = ex.Message });
                    context.Response.ContentType = "application/json"; context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload);
                }
                return;
            }
            if (route is "/page/pending" or "/page/ack")
            {
                string? id = context.Request.QueryString["id"];
                PendingPage? pending; lock (pageGate) pending = pendingPage;
                if (pending == null || !string.Equals(id, pending.Id, StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
                if (route == "/page/ack") {
                    pending.Completion.TrySetResult(context.Request.QueryString["ok"] == "1" ? (pending.Format == "pdf" ? "PDF ready on phone; finish saving there" : "HTML saved to phone") : "phone could not save the page"); return;
                }
                byte[] payload = Encoding.UTF8.GetBytes(pending.Json);
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload); return;
            }
            if (route == "/browser/poll")
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id = pages.Claim() });
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload); return;
            }
            if (route == "/browser/result")
            {
                if (context.Request.ContentLength64 > 16 * 1024 * 1024) { context.Response.StatusCode = 413; return; }
                using var data = new MemoryStream();
                byte[] buffer = new byte[32768]; int count;
                while ((count = await context.Request.InputStream.ReadAsync(buffer)) != 0) { if (data.Length + count > 16 * 1024 * 1024) { context.Response.StatusCode = 413; return; } data.Write(buffer, 0, count); }
                context.Response.StatusCode = pages.Complete(Encoding.UTF8.GetString(data.ToArray())) ? 200 : 404;
                return;
            }
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
                if (route == "/capture/ack")
                {
                    string result = context.Request.QueryString["ok"] != "1" ? "phone could not save; check phone notification" : context.Request.QueryString["clipboard"] switch {
                        "copied" => "saved to phone and copied to clipboard",
                        "failed" => "saved to phone; clipboard copy failed",
                        _ => "saved to phone; clipboard copy unverified"
                    };
                    pending.Completion.TrySetResult(result); return;
                }
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
        SystemEvents.SessionSwitch -= OnSessionSwitch; keyboard.Dispose(); timer.Stop(); timer.Dispose(); stop.Cancel(); listener.Close(); tray.Visible = false; tray.Dispose(); appIcon.Dispose(); ui.Dispose(); stop.Dispose();
        base.ExitThreadCore();
    }
}
