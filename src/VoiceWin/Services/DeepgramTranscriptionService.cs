using Deepgram;
using Deepgram.Models.Listen.v1.REST;
using VoiceWin.Models;

namespace VoiceWin.Services;

public class DeepgramTranscriptionService
{
    private static bool _initialized;
    private static readonly object _lock = new();

    public async Task<TranscriptionResult> TranscribeAsync(byte[] audioData, string apiKey, string model = "nova-3", string language = "en")
    {
        if (model.StartsWith("flux-", StringComparison.Ordinal))
            return new TranscriptionResult { Success = false, Error = "Flux requires Deepgram Streaming; choose Nova-3 for batch.", Provider = "deepgram" };
        var startTime = DateTime.UtcNow;

        try
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    Environment.SetEnvironmentVariable("DEEPGRAM_API_KEY", apiKey);
                    Library.Initialize();
                    _initialized = true;
                }
            }

            var listenClient = ClientFactory.CreateListenRESTClient();

            // Without an explicit CancellationTokenSource the SDK applies a
            // 30 s default timeout, which long recordings (big WAV upload +
            // processing) can exceed.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var response = await listenClient.TranscribeFile(
                audioData,
                new PreRecordedSchema
                {
                    Model = model,
                    Language = language,
                    Punctuate = true,
                    SmartFormat = true
                },
                timeout);

            var transcript = response?.Results?.Channels?
                .FirstOrDefault()?
                .Alternatives?
                .FirstOrDefault()?
                .Transcript ?? "";

            return new TranscriptionResult
            {
                Success = true,
                Text = transcript.Trim(),
                Duration = DateTime.UtcNow - startTime,
                Provider = "deepgram"
            };
        }
        catch (Exception ex)
        {
            return new TranscriptionResult
            {
                Success = false,
                Error = ex.Message,
                Duration = DateTime.UtcNow - startTime,
                Provider = "deepgram"
            };
        }
    }
}
