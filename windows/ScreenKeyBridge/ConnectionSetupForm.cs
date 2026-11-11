using System.Net;
using System.Net.Sockets;

namespace ScreenKeyBridge;

/// <summary>Explicit setup actions; the companion otherwise stays in the tray.</summary>
internal sealed class ConnectionSetupForm : Form
{
    private readonly Func<string, Task<string>> adb;
    private readonly Func<string, Task<string>> pair;
    private readonly ComboBox devices = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "Phone IP address, e.g. 192.168.1.10:5555" };
    private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Connect the phone with USB or enter its Wi-Fi address." };
    private readonly List<Button> actions = new();

    public ConnectionSetupForm(Func<string, Task<string>> adb, Func<string, Task<string>> pair)
    {
        this.adb = adb; this.pair = pair;
        Text = "Connect a phone — ScreenKey Bridge"; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 540); MinimumSize = new Size(480, 450);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, AutoScroll = true };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Install ScreenKey Bridge on the phone. Enable Developer options and USB debugging, then connect by USB and approve the phone's authorization prompt. USB can be used directly. If testing the side-by-side preview app, select it in the tray's Android app installation menu first." });
        layout.Controls.Add(new Label { AutoSize = true, Text = "Choose an authorized device:" });
        layout.Controls.Add(devices);
        AddButton(layout, "Refresh connected devices", RefreshDevices);
        AddButton(layout, "Connect and pair selected phone", async () => {
            if (devices.SelectedItem is not DeviceChoice choice) throw new InvalidOperationException("Select an authorized phone first, then try again.");
            result.Text = await pair(choice.Serial);
        });
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Optional Wi-Fi setup: with exactly one authorized phone connected by USB, enable its ADB TCP mode. Find the phone's Wi-Fi IP address in Android settings, then unplug USB before connecting below." });
        AddButton(layout, "Enable Wi-Fi ADB on USB phone (port 5555)", EnableTcp);
        layout.Controls.Add(endpoint);
        AddButton(layout, "Connect to phone over Wi-Fi", ConnectWifi);
        layout.Controls.Add(result);
        Controls.Add(layout);
        Shown += async (_, _) => { try { await RefreshDevices(); } catch (Exception ex) { result.Text = "Could not list devices: " + ex.Message; } };
    }

    private sealed record DeviceChoice(string Serial, string Description) { public override string ToString() => Description; }
    private async Task RefreshDevices()
    {
        string? previous = (devices.SelectedItem as DeviceChoice)?.Serial;
        var entries = BridgeContext.ParseDevices(await adb("devices -l"));
        devices.Items.Clear();
        foreach (var parts in entries.Where(parts => parts[1] == "device" && BridgeContext.IsSafeSerial(parts[0])))
        {
            string model = parts.FirstOrDefault(part => part.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ') ?? "Android device";
            devices.Items.Add(new DeviceChoice(parts[0], $"{model} — {parts[0]}"));
        }
        int selected = -1;
        for (int i = 0; i < devices.Items.Count; i++) if (devices.Items[i] is DeviceChoice item && item.Serial == previous) selected = i;
        if (selected < 0 && devices.Items.Count == 1) selected = 0;
        devices.SelectedIndex = selected;
        result.Text = devices.Items.Count == 0 ? "No authorized devices found. Approve USB debugging on the phone and refresh." : "Select a phone and choose Connect and pair. Pairing replaces that app's pairing with another PC.";
    }

    private void AddButton(Control parent, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Dock = DockStyle.Top };
        button.Click += async (_, _) => {
            foreach (Button item in actions) item.Enabled = false;
            try { result.Text = "Working…"; await action(); }
            catch (Exception ex) { result.Text = "Connection failed: " + ex.Message; }
            finally { foreach (Button item in actions) item.Enabled = true; }
        };
        actions.Add(button); parent.Controls.Add(button);
    }
    private async Task EnableTcp()
    {
        var lines = (await adb("devices")).Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[1] is "device" or "unauthorized" or "offline").ToArray();
        var usb = lines.Where(parts => parts[1] == "device" && !parts[0].Contains(':')).Select(parts => parts[0]).ToArray();
        if (usb.Length != 1 || lines.Length != 1 || !System.Text.RegularExpressions.Regex.IsMatch(usb[0], "^[A-Za-z0-9._-]+$")) throw new InvalidOperationException("Connect exactly one authorized USB phone and approve USB debugging on it.");
        string output = await adb($"-s {usb[0]} tcpip 5555");
        if (!output.Contains("TCP mode", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The phone did not confirm TCP mode. Check USB debugging and try again.");
        result.Text = "TCP mode enabled. Unplug USB, enter the phone's Wi-Fi IP and select Connect to phone over Wi-Fi.";
    }
    private async Task ConnectWifi()
    {
        string address = endpoint.Text.Trim();
        if (!address.Contains(':')) address += ":5555";
        int split = address.LastIndexOf(':');
        if (split <= 0 || !IPAddress.TryParse(address[..split], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !int.TryParse(address[(split + 1)..], out int port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Enter an IPv4 address with an optional port, such as 192.168.1.10:5555.");
        string output = await adb($"connect {address}");
        if (!output.Contains("connected to", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ADB could not connect. Check the IP, Wi-Fi, and debugging authorization.");
        await RefreshDevices();
        result.Text = "ADB connected. Approve any phone prompt, refresh the list, then select and pair the phone.";
    }
}
