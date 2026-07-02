using System.Diagnostics;

namespace VoiceWin.Services;

/// <summary>
/// Linux implementation of TextPasteService: copies text to the Wayland
/// clipboard (wl-copy) and simulates Ctrl+V in the focused window via ydotool
/// (uinput, compositor-independent) or wtype. Falls back to clipboard-only.
/// </summary>
public class TextPasteService
{
    public void PasteText(string text)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await CopyToClipboardAsync(text)) return;
                if (await TryYdotoolAsync()) return;
                await TryWtypeAsync();
            }
            catch { }
        });
    }

    private static async Task<bool> CopyToClipboardAsync(string text)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("wl-copy")
            {
                RedirectStandardInput = true,
                UseShellExecute = false
            });
            if (p == null) return false;
            await p.StandardInput.WriteAsync(text);
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> TryYdotoolAsync()
    {
        var socket = Environment.GetEnvironmentVariable("YDOTOOL_SOCKET");
        if (socket == null)
        {
            foreach (var candidate in new[]
            {
                "/tmp/ydotoold.sock",
                $"{Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")}/.ydotool_socket",
                "/tmp/.ydotool_socket"
            })
            {
                if (candidate != null && File.Exists(candidate)) { socket = candidate; break; }
            }
        }
        if (socket == null) return false;

        // 29 = KEY_LEFTCTRL, 47 = KEY_V
        return await RunAsync("ydotool", "key 29:1 47:1 47:0 29:0", ("YDOTOOL_SOCKET", socket));
    }

    private static Task<bool> TryWtypeAsync() =>
        RunAsync("wtype", "-M ctrl -P v -p v -m ctrl");

    private static async Task<bool> RunAsync(string cmd, string args, params (string, string)[] env)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                UseShellExecute = false,
                RedirectStandardError = true
            };
            foreach (var (k, v) in env) psi.Environment[k] = v;
            var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
