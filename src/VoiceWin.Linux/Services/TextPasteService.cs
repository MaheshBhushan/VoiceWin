using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceWin.Services;

/// <summary>
/// Serializes Wayland clipboard writes and configurable paste keystrokes.
/// ydotool uses uinput; wtype is a fallback for compatible compositors.
/// </summary>
public class TextPasteService : IDisposable
{
    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern uint getgid();

    private readonly SemaphoreSlim _daemonLock = new(1, 1);
    private Process? _ydotoold;
    private string? _managedSocket;

    public event EventHandler<string>? ErrorOccurred;
    public string Shortcut { get; set; } = "ctrl-shift-v";
    private readonly object _queueLock = new();
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    public void PasteText(string text) => _ = PasteTextAsync(text);

    public Task PasteTextAsync(string text)
    {
        lock (_queueLock)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
            string shortcut = Shortcut;
            return _pending = PasteAfterAsync(_pending, text, shortcut);
        }
    }

    public Task FlushAsync()
    {
        lock (_queueLock) return _pending;
    }

    private async Task PasteAfterAsync(Task previous, string text, string shortcut)
    {
        await Task.Yield();
        await previous;
        try
        {
            await PrepareClipboardAsync(text, shortcut);
            if (!await TryYdotoolAsync(shortcut) && !await TryWtypeAsync(shortcut))
                throw new InvalidOperationException("Automatic paste failed. Text is on the clipboard; check ydotool/uinput permissions or paste manually.");
            // Let the receiving application request the clipboard before replacing it.
            await Task.Delay(200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Paste error: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Paste error: {ex.Message}");
        }
    }

    private static async Task<bool> CopyToClipboardAsync(string text, bool primary)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("wl-copy", primary
                ? "--primary --type text/plain;charset=utf-8"
                : "--type text/plain;charset=utf-8")
            {
                RedirectStandardInput = true,
                UseShellExecute = false
            });
            if (p == null) return false;
            await p.StandardInput.WriteAsync(text).WaitAsync(TimeSpan.FromSeconds(5));
            p.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static async Task PrepareClipboardAsync(string text, string shortcut)
    {
        if (shortcut is not ("ctrl-v" or "ctrl-shift-v" or "shift-insert"))
            throw new InvalidOperationException("Unknown paste shortcut; choose a shortcut in Settings.");
        if (!await CopyToClipboardAsync(text, primary: false))
            throw new InvalidOperationException("Could not copy transcript: check wl-copy and the Wayland session.");
        // Shift+Insert means PRIMARY in Ghostty/Alacritty, but CLIPBOARD in
        // some desktop apps. Populate both rather than pasting stale selection text.
        bool needsPrimary = shortcut == "shift-insert";
        if (needsPrimary && !await CopyToClipboardAsync(text, primary: true))
            throw new InvalidOperationException("Could not update the primary selection. Choose Ctrl+Shift+V or Ctrl+V instead.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (await ClipboardMatchesAsync(text, primary: false, timeout.Token) &&
                    (!needsPrimary || await ClipboardMatchesAsync(text, primary: true, timeout.Token)))
                    return;
                await Task.Delay(30, timeout.Token);
            }
        }
        catch (OperationCanceledException) { /* Report a failed verification below. */ }
        throw new InvalidOperationException("Clipboard verification failed; automatic paste cancelled to avoid inserting old text. Check wl-paste and your clipboard manager.");
    }

    private static async Task<bool> ClipboardMatchesAsync(string text, bool primary, CancellationToken token)
    {
        using var process = Process.Start(new ProcessStartInfo("wl-paste", primary
            ? "--primary --no-newline --type text/plain;charset=utf-8"
            : "--no-newline --type text/plain;charset=utf-8")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process == null) return false;
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            string actual = await output;
            await error;
            return process.ExitCode == 0 && string.Equals(actual, text, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private async Task<bool> TryYdotoolAsync(string shortcut)
    {
        var socket = FindExistingSocket() ?? await EnsureManagedDaemonAsync();
        if (socket == null) return false;

        // 29 = KEY_LEFTCTRL, 47 = KEY_V (layout-independent evdev keycodes).
        string keys = shortcut switch
        {
            "ctrl-v" => "29:1 47:1 47:0 29:0",
            "ctrl-shift-v" => "29:1 42:1 47:1 47:0 42:0 29:0",
            _ => "42:1 110:1 110:0 42:0"
        };
        return await RunAsync("ydotool", "key " + keys, ("YDOTOOL_SOCKET", socket));
    }

    /// <summary>
    /// Locate a ydotoold socket that is already up: an explicit YDOTOOL_SOCKET,
    /// one we started earlier, or the well-known default locations (e.g. a
    /// system/user ydotoold.service the user configured themselves).
    /// </summary>
    private string? FindExistingSocket()
    {
        var env = Environment.GetEnvironmentVariable("YDOTOOL_SOCKET");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        foreach (var candidate in new[]
        {
            _managedSocket,
            xdg != null ? $"{xdg}/.ydotool_socket" : null,
            "/tmp/ydotoold.sock",
            "/tmp/.ydotool_socket"
        })
        {
            if (candidate != null && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Start (once) a user-owned ydotoold and return its socket path, or null if
    /// ydotoold is missing or /dev/uinput is not writable by this user.
    /// </summary>
    private async Task<string?> EnsureManagedDaemonAsync()
    {
        await _daemonLock.WaitAsync();
        try
        {
            // A concurrent paste may already have brought it up.
            if (_managedSocket != null && File.Exists(_managedSocket) &&
                _ydotoold is { HasExited: false })
                return _managedSocket;

            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
            var path = $"{xdg}/.voicewin-ydotool-{Environment.ProcessId}.sock";
            try { File.Delete(path); } catch { }

            var psi = new ProcessStartInfo("ydotoold",
                $"--socket-path={path} --socket-own={getuid()}:{getgid()}")
            {
                UseShellExecute = false,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            };

            Process? proc;
            try { proc = Process.Start(psi); }
            catch { return null; } // ydotoold not installed

            if (proc == null) return null;
            _ydotoold = proc;
            _managedSocket = path;

            // Daemon opens /dev/uinput before creating the socket; give it a
            // moment. If it exits first, uinput almost certainly wasn't writable.
            for (int i = 0; i < 25; i++)
            {
                if (File.Exists(path)) { await Task.Delay(500); return path; }
                if (proc.HasExited) { _ydotoold = null; return null; }
                await Task.Delay(100);
            }
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
        finally { _daemonLock.Release(); }
    }

    private static Task<bool> TryWtypeAsync(string shortcut) =>
        RunAsync("wtype", shortcut switch
        {
            "ctrl-v" => "-M ctrl -P v -p v -m ctrl",
            "ctrl-shift-v" => "-M ctrl -M shift -P v -p v -m shift -m ctrl",
            _ => "-M shift -P Insert -p Insert -m shift"
        });

    private static async Task<bool> RunAsync(string cmd, string args, params (string, string)[] env)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                UseShellExecute = false,
                RedirectStandardError = false
            };
            foreach (var (k, v) in env) psi.Environment[k] = v;
            using var p = Process.Start(psi);
            if (p == null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_queueLock) _disposed = true;
        FlushAsync().GetAwaiter().GetResult();
        try
        {
            if (_ydotoold is { HasExited: false })
                _ydotoold.Kill(entireProcessTree: true);
            _ydotoold?.Dispose();
        }
        catch { }
        _daemonLock.Dispose();
    }
}
