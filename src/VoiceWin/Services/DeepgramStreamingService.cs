using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceWin.Services;

public class DeepgramStreamingService : IDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly object _lock = new();
    private bool _isConnected;
    private Task _sendTask = Task.CompletedTask;
    private bool _closing;
    private bool _isFlux;
    private int _fluxTurn = -1;
    private int _lastCommittedTurn = -1;
    private string _fluxTranscript = "";
    private readonly StringBuilder _confirmedTranscript = new();

    internal static Uri BuildUri(string model, string language)
    {
        if (model.StartsWith("flux-", StringComparison.Ordinal))
        {
            string hint = model == "flux-general-multi" && language != "multi"
                ? "&language_hint=" + Uri.EscapeDataString(language) : "";
            return new Uri($"wss://api.deepgram.com/v2/listen?model={Uri.EscapeDataString(model)}&encoding=linear16&sample_rate=16000&eot_timeout_ms=1000{hint}");
        }
        return new Uri($"wss://api.deepgram.com/v1/listen?model={Uri.EscapeDataString(model)}&language={Uri.EscapeDataString(language)}&encoding=linear16&sample_rate=16000&channels=1&punctuate=true&smart_format=true&endpointing=300&interim_results=true&vad_events=true&utterance_end_ms=1000");
    }

    public event EventHandler<string>? TranscriptReceived;
    public event EventHandler<string>? TranscriptPreviewChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? ConnectionClosed;
    public event EventHandler? SpeechStarted;
    public event EventHandler? UtteranceEnded;

    public bool IsConnected => _isConnected;

    public async Task<bool> ConnectAsync(string apiKey, string model = "flux-general-en", string language = "multi")
    {
        try
        {
            await CloseAsync();
            _cts?.Dispose();
            _webSocket?.Dispose();
            _closing = false;
            _sendTask = Task.CompletedTask;
            _receiveTask = null;
            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            _webSocket.Options.SetRequestHeader("Authorization", $"Token {apiKey}");
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);

            _isFlux = model.StartsWith("flux-", StringComparison.Ordinal);
            _fluxTurn = -1;
            _lastCommittedTurn = -1;
            _fluxTranscript = "";
            _confirmedTranscript.Clear();
            PublishPreview("");
            var uri = BuildUri(model, language);

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectCts.Token);
            
            await _webSocket.ConnectAsync(uri, linkedCts.Token);
            _isConnected = true;

            _receiveTask = Task.Run(ReceiveLoop);

            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorOccurred?.Invoke(this, "Connection timed out");
            return false;
        }
        catch (WebSocketException wsEx)
        {
            ErrorOccurred?.Invoke(this, $"WebSocket error: {wsEx.Message} (Code: {wsEx.WebSocketErrorCode})");
            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Connection failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void SendAudio(byte[] audioData, int length)
    {
        lock (_lock)
        {
            if (!_isConnected || _closing || _webSocket == null || _cts == null) return;
            // Capture owns and reuses its buffer; each queued send needs its own copy.
            byte[] copy = audioData.AsSpan(0, length).ToArray();
            _sendTask = SendAfterAsync(_sendTask, _webSocket, copy, _cts.Token);
        }
    }

    private async Task SendAfterAsync(Task previous, ClientWebSocket socket, byte[] data, CancellationToken token)
    {
        await previous;
        if (token.IsCancellationRequested || socket.State != WebSocketState.Open) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, timeout.Token);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            if (!token.IsCancellationRequested)
                ErrorOccurred?.Invoke(this, $"Send failed: {ex.Message}");
        }
    }

    public async Task<bool> CloseAsync()
    {
        Task pending;
        lock (_lock)
        {
            _closing = true;
            pending = _sendTask;
        }
        if (_webSocket == null) return true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pending.WaitAsync(timeout.Token);
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}")),
                    WebSocketMessageType.Text, true, timeout.Token);
                if (_receiveTask != null) await _receiveTask.WaitAsync(timeout.Token);
            }
            if (_webSocket.State == WebSocketState.CloseReceived)
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Close failed: {ex.Message}");
            return false;
        }
        finally
        {
            _isConnected = false;
            _cts?.Cancel();
            _webSocket.Abort();
            if (_receiveTask != null) await _receiveTask;
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 1024 * 1024)
                        throw new InvalidDataException("Streaming response exceeded 1 MB.");
                    if (!result.EndOfMessage) continue;
                    var json = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            // Flux CloseStream may end the transport without a WebSocket close frame.
            if (!(_isFlux && _closing && ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely))
                ErrorOccurred?.Invoke(this, $"Stream disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Receive error: {ex.Message}");
        }
        finally
        {
            _isConnected = false;
            if (_isFlux && _closing) CommitFluxTurn();
            ConnectionClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return;

            var type = typeElement.GetString();

            switch (type)
            {
                case "TurnInfo":
                    ProcessFluxTurn(root);
                    break;
                case "Error":
                    ErrorOccurred?.Invoke(this, root.TryGetProperty("description", out var description)
                        ? description.GetString() ?? "Deepgram stream error" : "Deepgram stream error");
                    break;
                case "Results":
                    bool isFinal = root.TryGetProperty("is_final", out var finalElement) && finalElement.GetBoolean();

                    if (root.TryGetProperty("channel", out var channel) &&
                        channel.TryGetProperty("alternatives", out var alternatives) &&
                        alternatives.ValueKind == JsonValueKind.Array &&
                        alternatives.GetArrayLength() > 0 &&
                        alternatives[0].TryGetProperty("transcript", out var transcriptElement))
                    {
                        var transcript = transcriptElement.GetString();
                        if (isFinal && !string.IsNullOrWhiteSpace(transcript))
                            CommitTranscript(transcript);
                        else
                            PublishPreview(isFinal ? "" : transcript ?? "");
                    }
                    break;
                case "SpeechStarted":
                    SpeechStarted?.Invoke(this, EventArgs.Empty);
                    break;
                case "UtteranceEnd":
                    UtteranceEnded?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Parse error: {ex.Message}");
        }
    }

    private void ProcessFluxTurn(JsonElement root)
    {
        if (!root.TryGetProperty("turn_index", out var index) ||
            !root.TryGetProperty("event", out var eventElement)) return;
        int turn = index.GetInt32();
        if (turn <= _lastCommittedTurn || turn < _fluxTurn) return;
        if (turn != _fluxTurn)
        {
            _fluxTurn = turn;
            _fluxTranscript = "";
        }
        if (root.TryGetProperty("transcript", out var transcript))
            _fluxTranscript = transcript.GetString() ?? "";
        string? eventName = eventElement.GetString();
        if (eventName is "StartOfTurn" or "TurnResumed" ||
            (eventName == "Update" && !string.IsNullOrWhiteSpace(_fluxTranscript)))
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        // EagerEndOfTurn can be revised: only commit confirmed turns.
        if (eventName == "EndOfTurn")
        {
            CommitFluxTurn();
            UtteranceEnded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            PublishPreview(_fluxTranscript);
        }
    }

    private void CommitFluxTurn()
    {
        if (_fluxTurn <= _lastCommittedTurn) return;
        _lastCommittedTurn = _fluxTurn;
        string transcript = _fluxTranscript;
        _fluxTranscript = "";
        if (!string.IsNullOrWhiteSpace(transcript))
            CommitTranscript(transcript);
        else
            PublishPreview("");
    }

    private void CommitTranscript(string transcript)
    {
        if (_confirmedTranscript.Length > 0) _confirmedTranscript.Append(' ');
        _confirmedTranscript.Append(transcript.Trim());
        PublishPreview("");
        TranscriptReceived?.Invoke(this, transcript);
    }

    private void PublishPreview(string interim)
    {
        string confirmed = _confirmedTranscript.ToString();
        string current = interim.Trim();
        TranscriptPreviewChanged?.Invoke(this,
            confirmed.Length == 0 ? current : current.Length == 0 ? confirmed : confirmed + " " + current);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _webSocket?.Dispose();
        _cts?.Dispose();
    }
}
