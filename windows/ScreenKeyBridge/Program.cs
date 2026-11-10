using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

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
    private bool checking;
    private string status = "Starting";

    public BridgeContext()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeyBridge");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pairing-key.txt");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        token = File.ReadAllText(path).Trim();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show pairing key and status", null, (_, _) => ShowStatus());
        menu.Items.Add("Reconnect ADB", null, async (_, _) => await ConnectAdb());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Text = "ScreenKey Bridge", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowStatus();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { listener.Start(); _ = Serve(); }
        catch (HttpListenerException ex) { MessageBox.Show($"Could not start local server: {ex.Message}", "ScreenKey Bridge"); ExitThread(); return; }
        timer.Tick += async (_, _) => await ConnectAdb();
        timer.Start();
        _ = ConnectAdb();
    }

    private void ShowStatus() => MessageBox.Show($"{status}\n\nEnter this pairing key in the phone app:\n\n{token}\n\nKeep this key private. The key is stored in your Windows user profile.", "ScreenKey Bridge");

    private async Task ConnectAdb()
    {
        if (checking) return;
        checking = true;
        try
        {
            string devices = await RunAdb("devices");
            var serials = devices.Split('\n').Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[1] == "device").Select(parts => parts[0]).ToArray();
            if (serials.Length != 1) { status = serials.Length == 0 ? "No authorized ADB device found." : "Connect exactly one ADB device."; return; }
            await RunAdb($"-s {serials[0]} reverse tcp:{Port} tcp:{Port}");
            status = $"ADB connected: {serials[0]}";
        }
        catch (Exception ex) { status = $"ADB error: {ex.Message}"; }
        finally { checking = false; }
    }

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
            if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != "/capture") { context.Response.StatusCode = 404; return; }
            Rectangle bounds = SystemInformation.VirtualScreen;
            using var image = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            context.Response.ContentType = "image/png";
            context.Response.ContentLength64 = stream.Length;
            stream.Position = 0;
            await stream.CopyToAsync(context.Response.OutputStream);
        }
        catch (Exception) { try { context.Response.StatusCode = 500; } catch (ObjectDisposedException) { } }
        finally { context.Response.Close(); }
    }

    protected override void ExitThreadCore()
    {
        timer.Stop(); timer.Dispose(); stop.Cancel(); listener.Close(); tray.Visible = false; tray.Dispose(); stop.Dispose();
        base.ExitThreadCore();
    }
}
