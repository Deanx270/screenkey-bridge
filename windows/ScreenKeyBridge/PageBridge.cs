using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenKeyBridge;

// One on-demand request at a time; captured pages remain in memory on the PC.
internal sealed class PageBridge
{
    private sealed record Request(string Id, TaskCompletionSource<string> Completion) { public bool Claimed; }
    private readonly object gate = new();
    private Request? pending;
    private long lastPoll;

    public async Task<string> Capture()
    {
        var request = new Request(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (gate) {
            if (pending != null) throw new InvalidOperationException("Another page save is in progress.");
            if (lastPoll == 0 || Environment.TickCount64 - lastPoll > 5000) throw new InvalidOperationException("Edge extension is not connected. Reload it in edge://extensions.");
            pending = request;
        }
        try {
            if (await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(15))) != request.Completion.Task)
                throw new TimeoutException("Edge did not finish saving the page. Check the extension and try again.");
            return await request.Completion.Task;
        } finally { lock (gate) { if (ReferenceEquals(pending, request)) pending = null; } }
    }
    public string? Claim()
    {
        lock (gate) { lastPoll = Environment.TickCount64; if (pending == null || pending.Claimed) return null; pending.Claimed = true; return pending.Id; }
    }
    public bool Complete(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return false;
        lock (gate) {
            if (pending == null || !pending.Claimed || id.GetString() != pending.Id) return false;
            if (doc.RootElement.TryGetProperty("error", out var error)) pending.Completion.TrySetException(new InvalidOperationException(error.GetString() ?? "Could not capture the page."));
            else if (doc.RootElement.TryGetProperty("html", out var html) && html.ValueKind == JsonValueKind.String && doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                pending.Completion.TrySetResult(json);
            else pending.Completion.TrySetException(new InvalidOperationException("Edge returned an invalid page."));
            return true;
        }
    }
}

internal static class EdgeBrowserLink
{
    internal const string ExtensionId = "phjkoppbccocbjlonppkkpcbmmackdlj";
    internal const string Origin = "chrome-extension://" + ExtensionId;
    public static bool Authorized(string? expected, string? provided, string? origin)
    {
        if (!string.IsNullOrEmpty(origin) && origin != Origin) return false;
        byte[] actual = Encoding.UTF8.GetBytes(provided ?? ""), wanted = Encoding.UTF8.GetBytes(expected ?? "");
        return wanted.Length != 0 && actual.Length == wanted.Length && CryptographicOperations.FixedTimeEquals(actual, wanted);
    }

    // This credential is separate from phone pairing. It never crosses ADB and
    // is installed only in the user's local extension directory and profile.
    public static string Configure()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeyBridge");
        Directory.CreateDirectory(dir);
        string tokenPath = Path.Combine(dir, "edge-browser-token.txt");
        string secret = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : "";
        if (!Regex.IsMatch(secret, "^[0-9A-F]{64}$")) { secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); File.WriteAllText(tokenPath, secret); }
        string extension = Path.Combine(AppContext.BaseDirectory, "edge-extension");
        if (File.Exists(Path.Combine(extension, "manifest.json"))) {
            string configPath = Path.Combine(extension, "bridge-config.js");
            string content = "globalThis.SCREENKEY_EDGE_TOKEN = " + JsonSerializer.Serialize(secret) + ";\n";
            if (!File.Exists(configPath) || File.ReadAllText(configPath) != content) File.WriteAllText(configPath, content);
        }
        return secret;
    }
    public static void Setup()
    {
        string extension = Path.Combine(AppContext.BaseDirectory, "edge-extension");
        if (!File.Exists(Path.Combine(extension, "manifest.json"))) throw new InvalidOperationException("Edge extension files are missing. Reinstall ScreenKey Bridge.");
        Configure();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{extension}\"") { UseShellExecute = true });
        MessageBox.Show("In Edge, open edge://extensions. If the ScreenKey Bridge extension is already installed, choose Reload. Otherwise turn on Developer mode, select Load unpacked, and choose the folder just opened.\n\nThe extension needs access to pages you request to save.", "Set up page saving");
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    public static bool EdgeIsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        try { using var process = Process.GetProcessById((int)processId); return process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}
