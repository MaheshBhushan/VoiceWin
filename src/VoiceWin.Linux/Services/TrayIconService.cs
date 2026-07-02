using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VoiceWin.Services;

public enum TrayStatus
{
    Ready,
    Recording,
    Processing
}

/// <summary>
/// Renders the tray icon: app logo with a status dot in the bottom-right
/// corner (green = ready, red = recording, orange = processing). Same visual
/// as the Windows TrayIconService.
/// </summary>
public class TrayIconService
{
    private readonly Dictionary<TrayStatus, WindowIcon> _iconCache = new();

    public TrayIconService()
    {
        using var stream = AssetLoader.Open(new Uri("avares://voicewin/Assets/vwtrans-crop.png"));
        var baseIcon = new Bitmap(stream);

        _iconCache[TrayStatus.Ready] = CreateIconWithDot(baseIcon, Colors.LimeGreen);
        _iconCache[TrayStatus.Recording] = CreateIconWithDot(baseIcon, Colors.Red);
        _iconCache[TrayStatus.Processing] = CreateIconWithDot(baseIcon, Colors.Orange);
    }

    public WindowIcon CreateIconWithStatus(TrayStatus status) => _iconCache[status];

    private static WindowIcon CreateIconWithDot(Bitmap baseIcon, Color dotColor)
    {
        const double size = 256.0;
        const double dotSize = 72.0;
        const double margin = 8.0;

        var target = new RenderTargetBitmap(new PixelSize((int)size, (int)size), new Vector(96, 96));
        using (var dc = target.CreateDrawingContext())
        {
            dc.DrawImage(baseIcon, new Rect(0, 0, size, size));

            var center = new Point(size - dotSize / 2 - margin, size - dotSize / 2 - margin);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), null,
                center, dotSize / 2 + 6, dotSize / 2 + 6);
            dc.DrawEllipse(new SolidColorBrush(dotColor), null,
                center, dotSize / 2, dotSize / 2);
        }

        return new WindowIcon(target);
    }
}
