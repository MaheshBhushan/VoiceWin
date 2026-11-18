using VoiceWin.Models;

namespace VoiceWin.Services;

public class TranscriptionOrchestrator : IDisposable
{
    private readonly AudioRecordingService _audioService;
    private readonly GroqTranscriptionService _groqService;
    private readonly DeepgramTranscriptionService _deepgramService;
    private readonly DeepgramStreamingService _streamingService;
    private readonly GroqLlmService _llmService;
    private readonly TextPasteService _pasteService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly SettingsService _settingsService;
    private readonly SoundService _soundService;

    private bool _pasteFailed;
    private bool _isProcessing;
    private bool _isStreaming;
    private bool _isAutoStopping;
    private bool _isConnecting;
    private bool _releasedDuringConnect;
    // Audio captured while the Deepgram socket is still opening; null once
    // the socket is live. Guards _isConnecting/_isStreaming hand-off too.
    private readonly object _connectLock = new();
    private List<byte[]>? _preConnectAudio;
    private DateTime _streamingStartTime;
    private DateTime _lastSpeechTime;

    internal static void Log(string message) =>
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingStopped;
    public event EventHandler<TranscriptionResult>? TranscriptionCompleted;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? TranscriptPreviewChanged;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler<bool>? SpeechDetected;

    public bool IsRecording => _audioService.IsRecording;

    // Linux additions: evdev may be unavailable (user not in the "input"
    // group); tray clicks and `voicewin --toggle` drive the hotkey instead.
    public bool IsHotkeyAvailable => _hotkeyService.IsAvailable;
    public void SimulateHotkeyToggle() => _hotkeyService.SimulateToggle();

    public TranscriptionOrchestrator(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _audioService = new AudioRecordingService();
        _groqService = new GroqTranscriptionService();
        _deepgramService = new DeepgramTranscriptionService();
        _streamingService = new DeepgramStreamingService();
        _llmService = new GroqLlmService();
        _pasteService = new TextPasteService { Shortcut = settingsService.Settings.LinuxPasteShortcut };
        _pasteService.ErrorOccurred += (s, error) =>
        {
            _pasteFailed = true;
            StatusChanged?.Invoke(this, error);
        };
        _hotkeyService = new GlobalHotkeyService();
        _soundService = new SoundService();

        _pasteService.Shortcut = _settingsService.Settings.LinuxPasteShortcut;
        _hotkeyService.TargetVirtualKey = _settingsService.Settings.HotkeyVirtualKey;
        _hotkeyService.TargetModifiers = _settingsService.Settings.HotkeyModifiers;
        _hotkeyService.Mode = _settingsService.Settings.HotkeyMode;

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        _audioService.RecordingStarted += (s, e) =>
        {
            if (_settingsService.Settings.PlaySoundFeedback)
                _soundService.PlayStartSound();
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        };
        _audioService.RecordingStopped += (s, e) =>
        {
            if (_settingsService.Settings.PlaySoundFeedback)
                _soundService.PlayEndSound();
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        };
        _audioService.AudioChunkAvailable += OnAudioChunkAvailable;

        _streamingService.TranscriptReceived += OnStreamingTranscriptReceived;
        _streamingService.TranscriptPreviewChanged += (s, text) => TranscriptPreviewChanged?.Invoke(this, text);
        _streamingService.ErrorOccurred += (s, err) =>
        {
            Log($"streaming error: {err}");
            StatusChanged?.Invoke(this, err);
        };
        _streamingService.SpeechStarted += (s, e) => _lastSpeechTime = DateTime.UtcNow;
        _streamingService.UtteranceEnded += (s, e) => _lastSpeechTime = DateTime.UtcNow;
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        Log($"hotkey pressed (processing={_isProcessing}, streaming={_isStreaming})");

        if (_isProcessing || _isStreaming || _isConnecting)
        {
            StatusChanged?.Invoke(this, "Busy, please wait...");
            _hotkeyService.CancelToggle();
            return;
        }

        var settings = _settingsService.Settings;
        
        if (string.IsNullOrEmpty(settings.GroqApiKey) && string.IsNullOrEmpty(settings.DeepgramApiKey))
        {
            StatusChanged?.Invoke(this, "No API key configured");
            _hotkeyService.CancelToggle();
            return;
        }

        _pasteFailed = false;
        _audioService.DeviceNumber = settings.InputDeviceNumber;

        if (settings.TranscriptionProvider == "deepgram-streaming" && !string.IsNullOrEmpty(settings.DeepgramApiKey))
        {
            StartStreamingRecording();
        }
        else
        {
            StatusChanged?.Invoke(this, "Recording...");
            _audioService.StartRecording();
        }
    }

    private async void StartStreamingRecording()
    {
        var settings = _settingsService.Settings;

        lock (_connectLock)
        {
            _isConnecting = true;
            _releasedDuringConnect = false;
            _preConnectAudio = new List<byte[]>();
        }
        try
        {
            // Start the mic now and buffer while the socket opens, so speech
            // right after the press is kept instead of waiting on the handshake.
            _streamingStartTime = DateTime.UtcNow;
            _lastSpeechTime = DateTime.UtcNow;
            StatusChanged?.Invoke(this, "Recording (connecting)...");
            _audioService.StartRecording();
            var connectStart = DateTime.UtcNow;

            var connected = await _streamingService.ConnectAsync(
                settings.DeepgramApiKey ?? "",
                settings.DeepgramModel,
                settings.Language);

            bool releasedDuringConnect;
            int buffered = 0;
            lock (_connectLock)
            {
                if (connected)
                {
                    foreach (var chunk in _preConnectAudio!)
                    {
                        _streamingService.SendAudio(chunk, chunk.Length);
                        buffered += chunk.Length;
                    }
                    _isStreaming = true;
                }
                _preConnectAudio = null;
                _isConnecting = false;
                releasedDuringConnect = _releasedDuringConnect;
            }

            if (!connected)
            {
                _audioService.StopRecording();
                StatusChanged?.Invoke(this, "Failed to connect to Deepgram");
                _hotkeyService.CancelToggle();
                return;
            }

            Log($"connected in {(DateTime.UtcNow - connectStart).TotalMilliseconds:F0}ms, flushed {buffered / 32.0:F0}ms of buffered audio");
            StatusChanged?.Invoke(this, "Recording (streaming)...");

            // Released before the socket opened: transcribe what was buffered.
            if (releasedDuringConnect)
            {
                Log("released during connect -> finishing buffered session");
                StopStreamingRecording();
            }
        }
        catch (Exception ex)
        {
            lock (_connectLock)
            {
                _preConnectAudio = null;
                _isConnecting = false;
                _isStreaming = false;
            }
            _audioService.StopRecording();
            await _streamingService.CloseAsync();
            _hotkeyService.CancelToggle();
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
        }
    }

    private void OnAudioChunkAvailable(object? sender, AudioChunkEventArgs e)
    {
        float level = CalculateAudioLevel(e.Buffer, e.BytesRecorded);
        AudioLevelChanged?.Invoke(this, level);

        bool isSpeaking = level > 0.05f;

        lock (_connectLock)
        {
            // The capture service hands each chunk over as a fresh array.
            if (_preConnectAudio != null)
            {
                _preConnectAudio.Add(e.Buffer);
                return;
            }
        }
        if (_isStreaming && _streamingService.IsConnected)
        {
            _streamingService.SendAudio(e.Buffer, e.BytesRecorded);

            var settings = _settingsService.Settings;

            if (!_isAutoStopping && settings.VadStreamingSilenceTimeoutSeconds > 0)
            {
                var silenceDuration = DateTime.UtcNow - _lastSpeechTime;
                if (silenceDuration.TotalSeconds >= settings.VadStreamingSilenceTimeoutSeconds)
                {
                    _isAutoStopping = true;
                    Task.Run(AutoStopStreamingDueToSilence);
                }
            }
        }
        else if (_audioService.IsRecording && !_isStreaming)
        {
            SpeechDetected?.Invoke(this, isSpeaking);
        }
    }

    private async Task AutoStopStreamingDueToSilence()
    {
        try
        {
            if (!_isStreaming) return;
            _isStreaming = false;
            _isProcessing = true;

            StatusChanged?.Invoke(this, "Auto-stopped (silence timeout)");

            _audioService.StopRecording();
            await _streamingService.CloseAsync();
            await _pasteService.FlushAsync();

            _hotkeyService.CancelToggle();
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Error: {ex.Message}"); }
        finally
        {
            _isAutoStopping = false;
            _isProcessing = false;
        }
    }

    // Idle mic self-noise sits around 60 on a 16-bit scale; real speech peaks
    // in the thousands. 300 separates the two with plenty of headroom.
    private const short SilenceThreshold = 300;

    /// <summary>Peak 16-bit sample in a WAV buffer (44-byte header skipped).</summary>
    private static int PeakAmplitude(byte[] wav)
    {
        int peak = 0;
        for (int i = 44; i < wav.Length - 1; i += 2)
        {
            int sample = (short)(wav[i] | (wav[i + 1] << 8));
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }

    private static float CalculateAudioLevel(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0;

        long sum = 0;
        int sampleCount = bytesRecorded / 2;

        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += Math.Abs((int)sample); // Math.Abs(short.MinValue) throws
        }

        float average = sum / (float)sampleCount;
        return Math.Min(average / 2000f, 1f);
    }

    private void OnStreamingTranscriptReceived(object? sender, string transcript)
    {
        // Enhancement requests can finish out of order and delay live dictation.
        _lastSpeechTime = DateTime.UtcNow;
        _pasteService.PasteText(transcript.Trim() + " ");
    }

    private void OnHotkeyReleased(object? sender, EventArgs e)
    {
        Log($"hotkey released (streaming={_isStreaming}, connecting={_isConnecting}, recording={_audioService.IsRecording})");

        lock (_connectLock)
        {
            if (_isConnecting)
            {
                _releasedDuringConnect = true;
                return;
            }
        }

        if (_isStreaming)
        {
            if (!_isAutoStopping)
                StopStreamingRecording();
            return;
        }

        if (!_audioService.IsRecording || _isProcessing) return;

        _isProcessing = true;

        Task.Run(async () =>
        {
            // Any escape here would be swallowed as an unobserved task
            // exception and leave _isProcessing stuck true, rejecting every
            // future hotkey press as "Busy" until restart.
            try
            {
                StatusChanged?.Invoke(this, "Processing...");
                var audioData = _audioService.StopRecording();
                await ProcessTranscriptionAsync(audioData);
            }
            catch (Exception ex)
            {
                Log($"stop/transcribe pipeline failed: {ex}");
                StatusChanged?.Invoke(this, $"Error: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        });
    }

    private async void StopStreamingRecording()
    {
        if (!_isStreaming) return;
        _isStreaming = false;
        _isProcessing = true;

        try
        {
            StatusChanged?.Invoke(this, "Finalizing...");
            _audioService.StopRecording();

            bool closed = await _streamingService.CloseAsync();

            var duration = DateTime.UtcNow - _streamingStartTime;
            await _pasteService.FlushAsync();
            if (closed && !_pasteFailed)
                StatusChanged?.Invoke(this, $"Streamed in {duration.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Error: {ex.Message}"); }
        finally { _isProcessing = false; }
    }

    private async Task ProcessTranscriptionAsync(byte[] audioData)
    {
        try
        {
            if (audioData.Length < 1000)
            {
                StatusChanged?.Invoke(this, "Recording too short");
                return;
            }

            // A silent capture (wrong/unplugged input device selected) makes
            // Deepgram return an empty transcript, which used to surface as a
            // bogus "Transcription failed". Name the real problem instead.
            var peak = PeakAmplitude(audioData);
            Log($"transcribe: {audioData.Length} bytes, peak amplitude {peak}");
            if (peak < SilenceThreshold)
            {
                StatusChanged?.Invoke(this, "No audio captured - check your microphone input device");
                return;
            }

            var settings = _settingsService.Settings;
            var processedAudio = audioData;

            TranscriptionResult result;

            if (settings.TranscriptionProvider == "deepgram" && !string.IsNullOrEmpty(settings.DeepgramApiKey))
            {
                result = await _deepgramService.TranscribeAsync(
                    processedAudio,
                    settings.DeepgramApiKey,
                    settings.DeepgramModel,
                    settings.Language);
            }
            else if (!string.IsNullOrEmpty(settings.GroqApiKey))
            {
                result = await _groqService.TranscribeAsync(
                    processedAudio,
                    settings.GroqApiKey,
                    settings.GroqModel,
                    settings.Language);
            }
            else
            {
                StatusChanged?.Invoke(this, "No valid API key for selected provider");
                return;
            }

            TranscriptionCompleted?.Invoke(this, result);

            if (result.Success && !string.IsNullOrEmpty(result.Text))
            {
                var finalText = result.Text;
                var totalDuration = result.Duration;

                if (settings.AiEnhancementEnabled && !string.IsNullOrEmpty(settings.GroqApiKey))
                {
                    StatusChanged?.Invoke(this, "Enhancing...");
                    
                    var enhanceResult = await _llmService.EnhanceTextAsync(
                        result.Text,
                        settings.GroqApiKey,
                        settings.AiEnhancementPrompt,
                        settings.AiEnhancementModel);

                    if (enhanceResult.Success && !string.IsNullOrEmpty(enhanceResult.Text))
                    {
                        finalText = enhanceResult.Text;
                        totalDuration += enhanceResult.Duration;
                    }
                }

                StatusChanged?.Invoke(this, $"Transcribed in {totalDuration.TotalMilliseconds:F0}ms");
                await _pasteService.PasteTextAsync(finalText);
            }
            else
            {
                StatusChanged?.Invoke(this, result.Error
                    ?? (result.Success ? "No speech detected" : "Transcription failed"));
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    public void UpdateHotkeySettings()
    {
        _pasteService.Shortcut = _settingsService.Settings.LinuxPasteShortcut;
        _hotkeyService.TargetVirtualKey = _settingsService.Settings.HotkeyVirtualKey;
        _hotkeyService.TargetModifiers = _settingsService.Settings.HotkeyModifiers;
        _hotkeyService.Mode = _settingsService.Settings.HotkeyMode;
    }

    public void Dispose()
    {
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.HotkeyReleased -= OnHotkeyReleased;
        _audioService.AudioChunkAvailable -= OnAudioChunkAvailable;
        _hotkeyService.Dispose();
        _audioService.Dispose();
        _streamingService.Dispose();
        _soundService.Dispose();
        _pasteService.Dispose();
    }
}
