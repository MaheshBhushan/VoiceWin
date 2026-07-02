using System.Diagnostics;
using System.Reflection;

namespace VoiceWin.Services;

/// <summary>
/// Linux implementation of SoundService: extracts the embedded start/end MP3s
/// once and plays them with the first available command-line player.
/// </summary>
public class SoundService : IDisposable
{
    private const string StartSoundResource = "VoiceWin.Assets.sound_start.mp3";
    private const string EndSoundResource = "VoiceWin.Assets.sound_end.mp3";

    private readonly string? _player;
    private readonly string _startPath;
    private readonly string _endPath;

    public SoundService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "voicewin-sounds");
        Directory.CreateDirectory(dir);
        _startPath = ExtractResource(StartSoundResource, Path.Combine(dir, "start.mp3"));
        _endPath = ExtractResource(EndSoundResource, Path.Combine(dir, "end.mp3"));
        _player = FindPlayer();
    }

    public void PlayStartSound() => Play(_startPath);
    public void PlayEndSound() => Play(_endPath);

    private void Play(string path)
    {
        if (_player == null || !File.Exists(path)) return;
        Task.Run(() =>
        {
            try
            {
                var args = _player switch
                {
                    "ffplay" => $"-nodisp -autoexit -loglevel quiet \"{path}\"",
                    "mpv" => $"--no-video --really-quiet \"{path}\"",
                    "pw-play" => $"\"{path}\"",
                    _ => $"\"{path}\""
                };
                using var p = Process.Start(new ProcessStartInfo(_player, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                p?.WaitForExit(5000);
            }
            catch { }
        });
    }

    private static string ExtractResource(string resourceName, string targetPath)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var file = File.Create(targetPath);
                stream.CopyTo(file);
            }
        }
        catch { }
        return targetPath;
    }

    private static string? FindPlayer()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var name in new[] { "mpv", "ffplay", "pw-play" })
        {
            if (path.Split(':').Any(dir => File.Exists(Path.Combine(dir, name))))
                return name;
        }
        return null;
    }

    public void Dispose() { }
}
