using System.Net.Sockets;
using System.Text;
using Avalonia;

namespace VoiceWin;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `voicewin --toggle` signals the running instance (bind this to a
        // desktop-environment global shortcut for Wayland-friendly toggling).
        if (args.Contains("--toggle"))
        {
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(new UnixDomainSocketEndPoint(App.ControlSocketPath));
                client.Send(Encoding.UTF8.GetBytes("toggle"));
                return 0;
            }
            catch
            {
                Console.Error.WriteLine("voicewin is not running.");
                return 1;
            }
        }

        // Single instance: if the control socket is live, show the existing window instead.
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(App.ControlSocketPath));
            Console.Error.WriteLine("voicewin is already running.");
            return 1;
        }
        catch (SocketException) { }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
