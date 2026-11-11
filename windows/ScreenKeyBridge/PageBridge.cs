using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ScreenKeyBridge;

// One on-demand request at a time; page content lives only in memory on the PC.
internal sealed class PageBridge
{
    private sealed record Request(string Id, TaskCompletionSource<string> Completion) { public bool Claimed; }
    private readonly object gate = new();
    private Request? pending;

    public async Task<string> Capture()
    {
        var request = new Request(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (gate) { if (pending != null) throw new InvalidOperationException("Another page save is in progress."); pending = request; }
        try {
            if (await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(35))) != request.Completion.Task)
                throw new TimeoutException("Edge extension did not respond. Check that it is installed and enabled.");
            return await request.Completion.Task;
        } finally { lock (gate) { if (ReferenceEquals(pending, request)) pending = null; } }
    }
    public string? Claim()
    {
        lock (gate) { if (pending == null || pending.Claimed) return null; pending.Claimed = true; return pending.Id; }
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

internal static class EdgeNativeHost
{
    internal const string ExtensionId = "phjkoppbccocbjlonppkkpcbmmackdlj";
    private const string HostName = "com.screenkeybridge.page";
    internal static bool IsHostInvocation(string[] args) => args.Length > 0 && args[0] == $"chrome-extension://{ExtensionId}/";

    public static void Setup()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the companion.");
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use the packaged Windows app to set up the Edge extension.");
        string extension = Path.Combine(AppContext.BaseDirectory, "edge-extension");
        if (!File.Exists(Path.Combine(extension, "manifest.json"))) throw new InvalidOperationException("Edge extension files are missing. Reinstall ScreenKey Bridge.");
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeyBridge");
        Directory.CreateDirectory(dir);
        string manifest = Path.Combine(dir, "edge-native-host.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { name = HostName, description = "ScreenKey Bridge page saving", path = exe, type = "stdio", allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" } }));
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName) ?? throw new IOException("Could not register the Edge host.");
        key.SetValue("", manifest);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{extension}\"") { UseShellExecute = true });
        MessageBox.Show("In Edge, open edge://extensions, turn on Developer mode, select Load unpacked, and choose the folder just opened. Keep the extension enabled.\n\nIt needs access to web pages to save the page you request.", "Set up page saving");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    private static bool EdgeIsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        try { using var process = Process.GetProcessById((int)processId); return process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    private static async Task<string?> ReadMessage(Stream input)
    {
        byte[] header = new byte[4];
        int first = await input.ReadAsync(header.AsMemory(0, 4));
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(first));
        int size = BitConverter.ToInt32(header);
        if (size is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Native message size invalid.");
        byte[] data = new byte[size]; await input.ReadExactlyAsync(data); return Encoding.UTF8.GetString(data);
    }
    private static async Task WriteMessage(Stream output, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await output.WriteAsync(BitConverter.GetBytes(bytes.Length)); await output.WriteAsync(bytes); await output.FlushAsync();
    }
    public static async Task Run()
    {
        try {
            using Stream input = Console.OpenStandardInput(), output = Console.OpenStandardOutput();
            if (await ReadMessage(input) == null) return;
            await WriteMessage(output, new { type = "ping" });
            string tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeyBridge", "pairing-key.txt");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("X-ScreenKey-Token", (await File.ReadAllTextAsync(tokenPath)).Trim());
            int ticks = 0;
            while (true) {
                try {
                    using var poll = await client.PostAsync("http://127.0.0.1:38473/browser/poll", null);
                    if (poll.IsSuccessStatusCode) {
                        using var doc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String) {
                            string id = idElement.GetString()!;
                            string result;
                            if (!EdgeIsForeground()) result = JsonSerializer.Serialize(new { id, error = "Bring Microsoft Edge to the foreground on the PC." });
                            else {
                                await WriteMessage(output, new { type = "capture", id });
                                result = await ReadMessage(input) ?? throw new EndOfStreamException();
                            }
                            using var data = new StringContent(result, Encoding.UTF8, "application/json");
                            await client.PostAsync("http://127.0.0.1:38473/browser/result", data);
                        }
                    }
                } catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (++ticks % 10 == 0) await WriteMessage(output, new { type = "ping" });
                await Task.Delay(1000);
            }
        } catch (Exception) { /* Browser restarts the host after disconnect. Never write to stdout. */ }
    }
}
