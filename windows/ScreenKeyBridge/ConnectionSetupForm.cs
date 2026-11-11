using System.Net;
using System.Net.Sockets;

namespace ScreenKeyBridge;

/// <summary>Explicit setup actions; the companion otherwise stays in the tray.</summary>
internal sealed class ConnectionSetupForm : Form
{
    private readonly Func<string, Task<string>> adb;
    private readonly Func<Task> refresh;
    private readonly Func<Task> pair;
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "Phone IP address, e.g. 192.168.1.10:5555" };
    private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Connect the phone with USB or enter its Wi-Fi address." };
    private readonly List<Button> actions = new();

    public ConnectionSetupForm(Func<string, Task<string>> adb, Func<Task> refresh, Func<Task> pair)
    {
        this.adb = adb; this.refresh = refresh; this.pair = pair;
        Text = "Connect a phone — ScreenKey Bridge"; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 480); MinimumSize = new Size(480, 420);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, AutoScroll = true };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Install ScreenKey Bridge on the phone. Enable Developer options and USB debugging, then connect by USB and approve the phone's authorization prompt. USB can be used directly. If testing the side-by-side preview app, select it in the tray's Android app installation menu first." });
        AddButton(layout, "Refresh connected devices", async () => { await refresh(); result.Text = "Check the ADB status in the tray, then choose Pair connected phone."; });
        AddButton(layout, "Pair connected phone", async () => { await refresh(); await pair(); result.Text = "See the pairing result shown above."; });
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Optional Wi-Fi setup: with exactly one authorized phone connected by USB, enable its ADB TCP mode. Find the phone's Wi-Fi IP address in Android settings, then unplug USB before connecting below." });
        AddButton(layout, "Enable Wi-Fi ADB on USB phone (port 5555)", EnableTcp);
        layout.Controls.Add(endpoint);
        AddButton(layout, "Connect to phone over Wi-Fi", ConnectWifi);
        layout.Controls.Add(result);
        Controls.Add(layout);
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
        await refresh();
        result.Text = "ADB connected. Approve any prompt on the phone, then select Pair connected phone. If two devices are listed, unplug USB and refresh.";
    }
}
