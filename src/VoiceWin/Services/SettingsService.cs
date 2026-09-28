using System.IO;
using System.Text.Json;

namespace VoiceWin.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private Models.AppSettings _settings;

    public Models.AppSettings Settings => _settings;

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var voiceWinFolder = Path.Combine(appDataPath, "VoiceWin");
        Directory.CreateDirectory(voiceWinFolder);
        _settingsPath = Path.Combine(voiceWinFolder, "settings.json");
        _settings = Load();
    }

    private Models.AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new Models.AppSettings();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<Models.AppSettings>(json) ?? new Models.AppSettings();
            if (settings.DeepgramModel is not ("flux-general-multi" or "flux-general-en" or "nova-3"))
                settings.DeepgramModel = settings.TranscriptionProvider == "deepgram" ? "nova-3" : "flux-general-en";
            if (settings.TranscriptionProvider == "deepgram" && settings.DeepgramModel.StartsWith("flux-", StringComparison.Ordinal))
                settings.TranscriptionProvider = "deepgram-streaming";
            return settings;
        }
        catch
        {
            return new Models.AppSettings();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    public void UpdateSettings(Action<Models.AppSettings> updateAction)
    {
        updateAction(_settings);
        Save();
    }
}
