using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceWin.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// Fake tools: never write the real clipboard or inject keystrokes.
string directory = Path.Combine(Path.GetTempPath(), "voicewin-test-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
string? originalPath = Environment.GetEnvironmentVariable("PATH");
string? originalSocket = Environment.GetEnvironmentVariable("YDOTOOL_SOCKET");
try
{
    Environment.SetEnvironmentVariable("VOICEWIN_TEST_DIR", directory);
    Environment.SetEnvironmentVariable("PATH", directory);
    string socket = Path.Combine(directory, "socket");
    File.WriteAllText(socket, "");
    Environment.SetEnvironmentVariable("YDOTOOL_SOCKET", socket);
    void Tool(string name, string body)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run these tests on Linux.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    const string copyScript = "selection=clipboard\nfor arg in \"$@\"; do [ \"$arg\" = --primary ] && selection=primary; done\n/bin/cat > \"$VOICEWIN_TEST_DIR/$selection\"\n";
    const string pasteScript = "selection=clipboard\nfor arg in \"$@\"; do [ \"$arg\" = --primary ] && selection=primary; done\n/bin/cat \"$VOICEWIN_TEST_DIR/$selection\"\n";
    Tool("wl-copy", copyScript);
    Tool("wl-paste", pasteScript);
    File.WriteAllText(Path.Combine(directory, "primary"), "previously selected text");
    Tool("ydotool", "selection=clipboard\ncase \"$*\" in *110:1*) selection=primary ;; esac\n/bin/sleep 0.05\n/bin/cat \"$VOICEWIN_TEST_DIR/$selection\" >> \"$VOICEWIN_TEST_DIR/output\"\nprintf '%s\\n' \"$*\" >> \"$VOICEWIN_TEST_DIR/keys\"\n");
    Tool("wtype", "exit 1\n");
    using (var paste = new TextPasteService { Shortcut = "shift-insert" })
    {
        var errors = new List<string>();
        paste.ErrorOccurred += (_, error) => errors.Add(error);
        var first = paste.PasteTextAsync("first ü ");
        paste.Shortcut = "ctrl-shift-v";
        var second = paste.PasteTextAsync("second ");
        paste.Shortcut = "ctrl-v";
        var third = paste.PasteTextAsync("third");
        await Task.WhenAll(first, second, third);
        Check(File.ReadAllText(Path.Combine(directory, "output")) == "first ü second third", "Paste order/content corrupted");
        string[] keys = File.ReadAllLines(Path.Combine(directory, "keys"));
        Check(keys.SequenceEqual(new[] { "key 42:1 110:1 110:0 42:0", "key 29:1 42:1 47:1 47:0 42:0 29:0", "key 29:1 47:1 47:0 29:0" }), "Shortcut selection failed");
        Check(errors.Count == 0, "Unexpected paste error");
        Tool("wl-copy", "exit 1\n");
        await paste.PasteTextAsync("must not paste");
        Check(errors.Count == 1, "Copy failure was swallowed");
        Check(File.ReadAllLines(Path.Combine(directory, "keys")).Length == 3, "Injected keys after failed copy");
        Tool("wl-copy", copyScript);
        // A successful wl-copy exit is not proof the compositor accepted it.
        Tool("wl-copy", "/bin/cat > /dev/null\n");
        await paste.PasteTextAsync("must not paste stale clipboard");
        Check(errors.Count == 2, "Stale clipboard was not reported");
        Check(File.ReadAllLines(Path.Combine(directory, "keys")).Length == 3, "Injected keys while clipboard remained stale");
        Tool("wl-copy", copyScript);
        Tool("wl-paste", "selection=clipboard\nfor arg in \"$@\"; do [ \"$arg\" = --primary ] && selection=primary; done\nif [ ! -f \"$VOICEWIN_TEST_DIR/read-delayed\" ]; then /bin/touch \"$VOICEWIN_TEST_DIR/read-delayed\"; printf stale; else /bin/cat \"$VOICEWIN_TEST_DIR/$selection\"; fi\n");
        await paste.PasteTextAsync("eventually ready");
        Check(errors.Count == 2 && File.ReadAllText(Path.Combine(directory, "output")).EndsWith("eventually ready"), "Readiness retry failed");
        Tool("wl-paste", pasteScript);
        Tool("ydotool", "exit 1\n");
        await paste.PasteTextAsync("manual fallback");
        Check(errors.Count == 3 && File.ReadAllText(Path.Combine(directory, "clipboard")) == "manual fallback", "Paste failure fallback broken");
    }
    using var stream = new DeepgramStreamingService();
    var transcripts = new List<string>();
    var previews = new List<string>();
    stream.TranscriptPreviewChanged += (_, text) => previews.Add(text);
    stream.TranscriptReceived += (_, text) => transcripts.Add(text);
    MethodInfo process = typeof(DeepgramStreamingService).GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("Parser missing");
    foreach (var (final, text) in new[] { (false, "guess"), (true, "first"), (true, ""), (false, "revision"), (true, "second") })
    {
        string json = JsonSerializer.Serialize(new { type = "Results", is_final = final, channel = new { alternatives = new[] { new { transcript = text } } } });
        process.Invoke(stream, new object[] { json });
    }
    Check(transcripts.SequenceEqual(new[] { "first", "second" }), "Interim text duplicated or finals lost");
    Check(previews.SequenceEqual(new[] { "guess", "first", "first", "first revision", "first second" }), "Nova preview must replace interim words and preserve confirmed text");
    Uri fluxUri = DeepgramStreamingService.BuildUri("flux-general-multi", "multi");
    Check(fluxUri.AbsolutePath == "/v2/listen" && !fluxUri.Query.Contains("language") && !fluxUri.Query.Contains("smart_format"), "Flux URI uses Nova parameters");
    Check(DeepgramStreamingService.BuildUri("flux-general-multi", "de").Query.Contains("language_hint=de"), "Flux language hint missing");
    Check(!DeepgramStreamingService.BuildUri("flux-general-en", "en").Query.Contains("language_hint"), "English Flux cannot use language hints");
    Check(DeepgramStreamingService.BuildUri("nova-3", "multi").AbsolutePath == "/v1/listen", "Nova routing changed");
    using var flux = new DeepgramStreamingService();
    var fluxTranscripts = new List<string>();
    var fluxPreviews = new List<string>();
    flux.TranscriptPreviewChanged += (_, text) => fluxPreviews.Add(text);
    flux.TranscriptReceived += (_, text) => fluxTranscripts.Add(text);
    foreach (var (turn, evt, text) in new[] {
        (0, "StartOfTurn", ""), (0, "Update", "guess"), (0, "EagerEndOfTurn", "guess"),
        (0, "TurnResumed", "revised"), (0, "EndOfTurn", "confirmed"),
        (0, "EndOfTurn", "confirmed"), (1, "Update", "next"), (1, "EndOfTurn", "next turn") })
        process.Invoke(flux, new object[] { JsonSerializer.Serialize(new { type = "TurnInfo", turn_index = turn, @event = evt, transcript = text }) });
    Check(fluxTranscripts.SequenceEqual(new[] { "confirmed", "next turn" }), "Flux revisions or duplicate turns were pasted");
    Check(fluxPreviews.SequenceEqual(new[] { "", "guess", "guess", "revised", "confirmed", "confirmed next", "confirmed next turn" }), "Flux preview duplicated turns or retained stale guesses");

    // Loopback WebSocket validates copied audio, ordered sends, fragmented UTF-8,
    // and the final transcript delivered after CloseStream.
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    Task<HttpListenerContext> accept = listener.GetContextAsync();
    using var client = new ClientWebSocket();
    Task connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), deadline.Token);
    HttpListenerContext context = await accept.WaitAsync(deadline.Token);
    HttpListenerWebSocketContext upgraded = await context.AcceptWebSocketAsync(null);
    using WebSocket server = upgraded.WebSocket;
    await connect;
    using var live = new DeepgramStreamingService();
    void SetField(string name, object value) =>
        (typeof(DeepgramStreamingService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception(name)).SetValue(live, value);
    SetField("_webSocket", client);
    SetField("_cts", new CancellationTokenSource());
    SetField("_isConnected", true);
    SetField("_isFlux", true);
    var finalReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    live.TranscriptReceived += (_, text) => finalReceived.TrySetResult(text);
    MethodInfo receive = typeof(DeepgramStreamingService).GetMethod("ReceiveLoop", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("Receive loop missing");
    Task receiving = (Task)(receive.Invoke(live, null) ?? throw new Exception("Receive task missing"));
    SetField("_receiveTask", receiving);
    byte[] audio = new byte[3200];
    for (int i = 1; i <= 12; i++)
    {
        Array.Fill(audio, (byte)i);
        live.SendAudio(audio, audio.Length);
    }
    Array.Fill(audio, (byte)99);
    byte[] received = new byte[4096];
    for (int i = 1; i <= 12; i++)
    {
        using var frame = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await server.ReceiveAsync(new ArraySegment<byte>(received), deadline.Token);
            frame.Write(received, 0, result.Count);
        } while (!result.EndOfMessage);
        Check(frame.Length == audio.Length && frame.ToArray().All(value => value == i), "Audio buffer reuse/order regression");
    }
    Task<bool> closing = live.CloseAsync();
    var closeMessage = await server.ReceiveAsync(new ArraySegment<byte>(received), deadline.Token);
    Check(Encoding.UTF8.GetString(received, 0, closeMessage.Count).Contains("CloseStream"), "Missing protocol close");
    byte[] finalJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "TurnInfo", turn_index = 0, @event = "Update", transcript = "last café" }));
    await server.SendAsync(new ArraySegment<byte>(finalJson, 0, 17), WebSocketMessageType.Text, false, deadline.Token);
    await server.SendAsync(new ArraySegment<byte>(finalJson, 17, finalJson.Length - 17), WebSocketMessageType.Text, true, deadline.Token);
    await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", deadline.Token);
    Check(await closing.WaitAsync(deadline.Token), "Graceful close failed");
    Check(await finalReceived.Task.WaitAsync(deadline.Token) == "last café", "Final fragmented transcript lost");
    Console.WriteLine("PASS: both clipboard selections, stale clipboard refusal, readiness retry, paste ordering, Unicode, shortcuts, errors/fallback, final-only delivery, audio ownership/order, fragmented responses, Flux routing/turn revisions, final Flux update on stop, cumulative interim previews.");

}
finally
{
    Environment.SetEnvironmentVariable("PATH", originalPath);
    Environment.SetEnvironmentVariable("YDOTOOL_SOCKET", originalSocket);
    Environment.SetEnvironmentVariable("VOICEWIN_TEST_DIR", null);
    Directory.Delete(directory, true);
}

// Optional real-service handshake using the existing configured key; no microphone or typing.
if (args.Contains("--live-flux"))
{
    string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceWin", "settings.json");
    using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
    string apiKey = settings.RootElement.GetProperty("DeepgramApiKey").GetString() ?? "";
    Check(!string.IsNullOrWhiteSpace(apiKey), "No Deepgram key configured for live smoke test");
    using var liveFlux = new DeepgramStreamingService();
    var errors = new List<string>();
    liveFlux.ErrorOccurred += (_, error) => errors.Add(error);
    Check(await liveFlux.ConnectAsync(apiKey, "flux-general-multi", "multi"), "Live Flux connection failed");
    liveFlux.SendAudio(new byte[16000], 16000);
    await Task.Delay(500);
    Check(await liveFlux.CloseAsync(), "Live Flux close failed");
    Check(errors.Count == 0, "Live Flux reported: " + string.Join("; ", errors));
    Console.WriteLine("PASS: real Flux Multilingual connection, silent PCM upload, and shutdown.");
}

if (args.Contains("--clipboard-smoke"))
{
    async Task<string> ClipboardToolAsync(string command, string arguments, string? input = null)
    {
        bool reads = command == "wl-paste";
        using var process = Process.Start(new ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = input != null,
            RedirectStandardOutput = reads,
            RedirectStandardError = reads
        }) ?? throw new Exception("Could not launch clipboard tool");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<string> output = reads ? process.StandardOutput.ReadToEndAsync(timeout.Token) : Task.FromResult("");
        Task<string> error = reads ? process.StandardError.ReadToEndAsync(timeout.Token) : Task.FromResult("");
        if (input != null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(timeout.Token);
        Check(process.ExitCode == 0, "Clipboard tool failed");
        await error;
        return await output;
    }
    // Preserve text selections in memory; never log their contents or send keys.
    string oldClipboard = await ClipboardToolAsync("wl-paste", "--no-newline --type text");
    string oldPrimary = await ClipboardToolAsync("wl-paste", "--primary --no-newline --type text");
    const string marker = "VoiceWin clipboard verification ü 你好 ";
    try
    {
        await TextPasteService.PrepareClipboardAsync(marker, "shift-insert");
        Check(await ClipboardToolAsync("wl-paste", "--no-newline --type text") == marker, "Real CLIPBOARD mismatch");
        Check(await ClipboardToolAsync("wl-paste", "--primary --no-newline --type text") == marker, "Real PRIMARY mismatch");
        await TextPasteService.PrepareClipboardAsync(marker, "ctrl-shift-v");
        Console.WriteLine("PASS: real Wayland CLIPBOARD/PRIMARY preparation and readback; no keystrokes sent.");
    }
    finally
    {
        if (await ClipboardToolAsync("wl-paste", "--no-newline --type text") == marker)
            await ClipboardToolAsync("wl-copy", "--type text/plain;charset=utf-8", oldClipboard);
        if (await ClipboardToolAsync("wl-paste", "--primary --no-newline --type text") == marker)
            await ClipboardToolAsync("wl-copy", "--primary --type text/plain;charset=utf-8", oldPrimary);
    }
}
