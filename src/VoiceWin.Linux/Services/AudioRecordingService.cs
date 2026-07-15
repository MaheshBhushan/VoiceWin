using System.Diagnostics;
using System.Text.Json;

namespace VoiceWin.Services;

public class AudioChunkEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int BytesRecorded { get; }

    public AudioChunkEventArgs(byte[] buffer, int bytesRecorded)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
    }
}

/// <summary>
/// Linux implementation of AudioRecordingService: captures raw 16 kHz mono
/// s16le PCM via PipeWire/PulseAudio (parecord), buffers it in memory, and
/// wraps it in a WAV container on stop. Same API as the Windows NAudio version
/// so TranscriptionOrchestrator compiles against either.
/// </summary>
public class AudioRecordingService : IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    private Process? _process;
    private MemoryStream? _pcmBuffer;
    private Task? _readTask;
    private bool _isRecording;

    public bool IsRecording => _isRecording;
    public int DeviceNumber { get; set; } = -1;

    public event EventHandler? RecordingStarted;
    public event EventHandler? RecordingStopped;
    public event EventHandler<AudioChunkEventArgs>? AudioChunkAvailable;

    /// <summary>Enumerates PulseAudio/PipeWire capture sources (monitors excluded).</summary>
    public static IReadOnlyList<(int Number, string Name)> GetInputDevices()
    {
        var devices = new List<(int, string)>();
        try
        {
            foreach (var (_, description) in ListSources())
                devices.Add((devices.Count, description));
        }
        catch { }
        return devices;
    }

    private static List<(string Name, string Description)> ListSources()
    {
        var sources = new List<(string, string)>();
        var psi = new ProcessStartInfo("pactl", "-f json list sources")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        if (p == null) return sources;
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);

        using var doc = JsonDocument.Parse(json);
        foreach (var src in doc.RootElement.EnumerateArray())
        {
            var name = src.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".monitor")) continue;
            var description = src.TryGetProperty("description", out var d) ? d.GetString() ?? name : name;
            sources.Add((name, description));
        }
        return sources;
    }

    public void StartRecording()
    {
        if (_isRecording) return;

        string? device = null;
        if (DeviceNumber >= 0)
        {
            try
            {
                var sources = ListSources();
                if (DeviceNumber < sources.Count)
                    device = sources[DeviceNumber].Name;
            }
            catch { }
        }

        var args = $"--raw --format=s16le --rate={SampleRate} --channels={Channels}"
                   + (device != null ? $" --device={device}" : "");

        _pcmBuffer = new MemoryStream();
        _process = Process.Start(new ProcessStartInfo("parecord", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start parecord (install pipewire-pulse)");

        var stdout = _process.StandardOutput.BaseStream;
        var buffer = _pcmBuffer;
        _readTask = Task.Run(() =>
        {
            var chunk = new byte[3200]; // 100 ms at 16 kHz mono s16le
            try
            {
                int read;
                while ((read = stdout.Read(chunk, 0, chunk.Length)) > 0)
                {
                    lock (buffer) buffer.Write(chunk, 0, read);
                    var copy = new byte[read];
                    Array.Copy(chunk, copy, read);
                    try
                    {
                        AudioChunkAvailable?.Invoke(this, new AudioChunkEventArgs(copy, read));
                    }
                    catch (Exception ex)
                    {
                        // A misbehaving handler must not kill audio capture.
                        TranscriptionOrchestrator.Log($"audio chunk handler failed: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                TranscriptionOrchestrator.Log($"parecord read loop died: {ex}");
            }
        });

        _isRecording = true;
        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops recording and returns the captured audio as a WAV file.</summary>
    public byte[] StopRecording()
    {
        if (!_isRecording || _process == null)
            return Array.Empty<byte>();

        _isRecording = false;
        var process = _process;
        var buffer = _pcmBuffer;
        _process = null;
        _pcmBuffer = null;

        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch { }

        try
        {
            _readTask?.Wait(2000);
        }
        catch (AggregateException ex)
        {
            // Wait() rethrows a fault from the read loop; the audio captured
            // before the fault is still in the buffer, so keep going.
            TranscriptionOrchestrator.Log($"read task faulted: {ex.InnerException}");
        }
        process.WaitForExit(2000);
        process.Dispose();

        byte[] pcm;
        lock (buffer!) pcm = buffer.ToArray();
        buffer.Dispose();
        TranscriptionOrchestrator.Log($"stop: captured {pcm.Length} bytes ({pcm.Length / 32000.0:F1}s)");

        RecordingStopped?.Invoke(this, EventArgs.Empty);
        return WrapWav(pcm);
    }

    private static byte[] WrapWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int byteRate = SampleRate * Channels * BitsPerSample / 8;
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write((short)(Channels * BitsPerSample / 8));
        w.Write((short)BitsPerSample);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_isRecording)
            StopRecording();
    }
}
