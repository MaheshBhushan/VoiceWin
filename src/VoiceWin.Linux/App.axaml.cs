using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VoiceWin.Services;
using VoiceWin.Views;

namespace VoiceWin;

public partial class App : Application
{
    private TranscriptionOrchestrator? _orchestrator;
    private SettingsService? _settingsService;
    private TrayIconService? _trayIconService;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Socket? _controlSocket;

    public SettingsService SettingsService => _settingsService!;
    public TranscriptionOrchestrator Orchestrator => _orchestrator!;

    public static string ControlSocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "voicewin.sock");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => Cleanup();

            _settingsService = new SettingsService();
            _orchestrator = new TranscriptionOrchestrator(_settingsService);
            _trayIconService = new TrayIconService();

            SetupTrayIcon();
            StartControlSocket();

            // Deliberately not assigned to desktop.MainWindow: the lifetime
            // auto-shows MainWindow, which would defeat StartMinimized.
            _mainWindow = new MainWindow();
            if (!_settingsService.Settings.StartMinimized)
                _mainWindow.Show();

            _orchestrator.RecordingStarted += (_, _) => SetTrayStatus(TrayStatus.Recording, "Recording...");
            _orchestrator.RecordingStopped += (_, _) => SetTrayStatus(TrayStatus.Processing, "Processing...");
            _orchestrator.StatusChanged += (_, status) => SetTrayStatus(TrayStatus.Ready, status);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("Show Settings");
        showItem.Click += (_, _) => ShowMainWindow();
        var toggleItem = new NativeMenuItem("Start/Stop Recording");
        toggleItem.Click += (_, _) => _orchestrator?.SimulateHotkeyToggle();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => QuitApp();
        menu.Items.Add(showItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = _trayIconService!.CreateIconWithStatus(TrayStatus.Ready),
            ToolTipText = "VoiceWin - Ready",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => _orchestrator?.SimulateHotkeyToggle();

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void SetTrayStatus(TrayStatus status, string tooltip)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null) return;
            _trayIcon.Icon = _trayIconService!.CreateIconWithStatus(status);
            _trayIcon.ToolTipText = $"VoiceWin - {tooltip}";
        });
    }

    public void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow == null) return;
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    public void QuitApp()
    {
        Cleanup();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void Cleanup()
    {
        _orchestrator?.Dispose();
        _controlSocket?.Dispose();
        try { File.Delete(ControlSocketPath); } catch { }
    }

    /// <summary>
    /// Unix socket accepting "toggle" — lets desktop-environment global
    /// shortcuts drive recording via `voicewin --toggle` (useful on Wayland
    /// when evdev hotkeys are unavailable).
    /// </summary>
    private void StartControlSocket()
    {
        try
        {
            File.Delete(ControlSocketPath);
            _controlSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _controlSocket.Bind(new UnixDomainSocketEndPoint(ControlSocketPath));
            _controlSocket.Listen(4);
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var conn = await _controlSocket.AcceptAsync();
                    _ = Task.Run(async () =>
                    {
                        var buf = new byte[64];
                        var n = await conn.ReceiveAsync(buf);
                        conn.Dispose();
                        if (Encoding.UTF8.GetString(buf, 0, n).Trim() == "toggle")
                            _orchestrator?.SimulateHotkeyToggle();
                    });
                }
            });
        }
        catch { }
    }
}
