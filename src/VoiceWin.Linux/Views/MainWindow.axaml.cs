using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using VoiceWin.Services;

namespace VoiceWin.Views;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly StatusOverlayWindow _statusOverlay;
    private bool _isRecordingHotkey;
    private int _pendingHotkeyVirtualKey;
    private int _pendingHotkeyModifiers;

    public MainWindow()
    {
        InitializeComponent();
        _app = (App)Avalonia.Application.Current!;
        _statusOverlay = new StatusOverlayWindow();

        LoadSettings();
        SubscribeToEvents();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void LoadSettings()
    {
        var settings = _app.SettingsService.Settings;

        GroqApiKeyBox.Text = settings.GroqApiKey ?? "";
        DeepgramApiKeyBox.Text = settings.DeepgramApiKey ?? "";

        SelectComboItemByTag(ProviderCombo, settings.TranscriptionProvider);
        SelectComboItemByTag(PasteShortcutCombo, settings.LinuxPasteShortcut);
        PopulateInputDevices(settings.InputDeviceNumber);
        SelectComboItemByTag(GroqModelCombo, settings.GroqModel);
        SelectComboItemByTag(DeepgramModelCombo, settings.DeepgramModel);
        SelectComboItemByTag(HotkeyModeCombo, settings.HotkeyMode);
        SelectComboItemByTag(OverlayPositionCombo, settings.OverlayPosition);
        SelectComboItemByTag(LanguageCombo, settings.Language);

        AiEnhancementCheckBox.IsChecked = settings.AiEnhancementEnabled;
        SelectComboItemByTag(AiEnhancementModelCombo, settings.AiEnhancementModel);
        AiEnhancementPromptBox.Text = settings.AiEnhancementPrompt;

        VadSilenceTimeoutBox.Text = settings.VadStreamingSilenceTimeoutSeconds.ToString();

        ShowOverlayCheckBox.IsChecked = settings.ShowRecordingOverlay;
        StartWithSystemCheckBox.IsChecked = settings.StartWithWindows;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimized;

        _pendingHotkeyVirtualKey = settings.HotkeyVirtualKey;
        _pendingHotkeyModifiers = settings.HotkeyModifiers;
        HotkeyDisplayBox.Text = GetHotkeyDisplayString(_pendingHotkeyModifiers, _pendingHotkeyVirtualKey);

        _statusOverlay.SetPosition(settings.OverlayPosition);

        if (!_app.Orchestrator.IsHotkeyAvailable)
        {
            StatusText.Text = "Hotkey unavailable: add yourself to the 'input' group " +
                              "(sudo usermod -aG input $USER, then re-login), or bind a " +
                              "global shortcut to 'voicewin --toggle'. Tray click also toggles.";
        }
    }

    private void PopulateInputDevices(int selectedDeviceNumber)
    {
        InputDeviceCombo.Items.Clear();
        InputDeviceCombo.Items.Add(new ComboBoxItem { Content = "System Default", Tag = "-1" });

        foreach (var device in AudioRecordingService.GetInputDevices())
        {
            InputDeviceCombo.Items.Add(new ComboBoxItem
            {
                Content = device.Name,
                Tag = device.Number.ToString()
            });
        }

        SelectComboItemByTag(InputDeviceCombo, selectedDeviceNumber.ToString());
    }

    private void SelectComboItemByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private string GetSelectedTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    }

    private void SubscribeToEvents()
    {
        _app.Orchestrator.TranscriptPreviewChanged += (s, text) => _statusOverlay.UpdateTranscript(text);
        _app.Orchestrator.RecordingStarted += (s, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = "Recording...";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));

                if (_app.SettingsService.Settings.ShowRecordingOverlay)
                {
                    bool isStreaming = _app.SettingsService.Settings.TranscriptionProvider == "deepgram-streaming";
                    _statusOverlay.SetMode(isStreaming);
                    _statusOverlay.UpdateStatus("Recording");
                    _statusOverlay.StartAnimating();
                }
            });
        };

        _app.Orchestrator.RecordingStopped += (s, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = "Processing...";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(234, 179, 8));

                _statusOverlay.StopAnimating();
                _statusOverlay.UpdateStatus("Processing");
            });
        };

        _app.Orchestrator.StatusChanged += (s, status) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = status;
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));

                _statusOverlay.UpdateStatus(status);
                if (IsTerminalStatus(status))
                {
                    _statusOverlay.HideOverlay();
                }
            });
        };

        _app.Orchestrator.TranscriptionCompleted += (s, result) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (result.Success)
                {
                    _statusOverlay.UpdateTranscript(result.Text ?? "");
                    StatusText.Text = $"Done ({result.Duration.TotalMilliseconds:F0}ms)";
                }
            });
        };

        _app.Orchestrator.AudioLevelChanged += (s, level) =>
        {
            _statusOverlay.UpdateAudioLevel(level);
        };

        _app.Orchestrator.SpeechDetected += (s, isSpeaking) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _statusOverlay.UpdateStatus(isSpeaking ? "Recording" : "Listening");
            });
        };
    }

    private static bool IsTerminalStatus(string status)
    {
        return status.Contains("Transcribed") ||
               status.Contains("Streamed") ||
               status.Contains("Error") ||
               status.Contains("too short") ||
               status.Contains("No API") ||
               status.Contains("No valid") ||
               status.Contains("No speech") ||
               status.Contains("Auto-stopped");
    }

    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(VadSilenceTimeoutBox.Text, out int timeout) || timeout < 0)
        {
            StatusText.Text = "Invalid silence timeout (must be a non-negative number)";
            return;
        }

        if (GetSelectedTag(ProviderCombo) == "deepgram" &&
            GetSelectedTag(DeepgramModelCombo).StartsWith("flux-", StringComparison.Ordinal))
        {
            StatusText.Text = "Flux requires Deepgram Streaming. Select Streaming or choose Nova-3 for batch.";
            return;
        }

        _app.SettingsService.UpdateSettings(settings =>
        {
            settings.GroqApiKey = GroqApiKeyBox.Text?.Trim() ?? "";
            settings.DeepgramApiKey = DeepgramApiKeyBox.Text?.Trim() ?? "";
            settings.TranscriptionProvider = GetSelectedTag(ProviderCombo);
            settings.LinuxPasteShortcut = GetSelectedTag(PasteShortcutCombo);
            if (int.TryParse(GetSelectedTag(InputDeviceCombo), out int deviceNumber))
            {
                settings.InputDeviceNumber = deviceNumber;
            }
            settings.GroqModel = GetSelectedTag(GroqModelCombo);
            settings.DeepgramModel = GetSelectedTag(DeepgramModelCombo);
            settings.HotkeyMode = GetSelectedTag(HotkeyModeCombo);
            settings.OverlayPosition = GetSelectedTag(OverlayPositionCombo);
            settings.Language = GetSelectedTag(LanguageCombo);
            settings.AiEnhancementEnabled = AiEnhancementCheckBox.IsChecked ?? false;
            settings.AiEnhancementModel = GetSelectedTag(AiEnhancementModelCombo);
            settings.AiEnhancementPrompt = AiEnhancementPromptBox.Text ?? "";
            settings.VadStreamingSilenceTimeoutSeconds = timeout;
            settings.ShowRecordingOverlay = ShowOverlayCheckBox.IsChecked ?? true;
            settings.StartWithWindows = StartWithSystemCheckBox.IsChecked ?? false;
            settings.StartMinimized = StartMinimizedCheckBox.IsChecked ?? true;
            settings.HotkeyVirtualKey = _pendingHotkeyVirtualKey;
            settings.HotkeyModifiers = _pendingHotkeyModifiers;
        });

        _app.Orchestrator.UpdateHotkeySettings();
        _statusOverlay.SetPosition(_app.SettingsService.Settings.OverlayPosition);
        ApplyAutostart(_app.SettingsService.Settings.StartWithWindows);

        StatusText.Text = "Settings saved!";
        ResetStatusTextAfterDelay();
    }

    private void ResetStatusTextAfterDelay()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            StatusText.Text = "Ready - Press your hotkey to record";
        };
        timer.Start();
    }

    /// <summary>Freedesktop autostart entry (~/.config/autostart), Linux
    /// equivalent of the Windows registry Run key.</summary>
    private static void ApplyAutostart(bool enabled)
    {
        try
        {
            var autostartDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
            var desktopFile = Path.Combine(autostartDir, "voicewin.desktop");

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? "voicewin";
                Directory.CreateDirectory(autostartDir);
                File.WriteAllText(desktopFile, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=VoiceWin
                    Comment=Speech-to-Text Transcription
                    Exec="{exePath}"
                    X-GNOME-Autostart-enabled=true
                    """);
            }
            else
            {
                File.Delete(desktopFile);
            }
        }
        catch { }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void RecordHotkey_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            RecordHotkeyButton.Content = "Record";
            HotkeyDisplayBox.Text = GetHotkeyDisplayString(_pendingHotkeyModifiers, _pendingHotkeyVirtualKey);
            return;
        }

        _isRecordingHotkey = true;
        RecordHotkeyButton.Content = "Cancel";
        HotkeyDisplayBox.Text = "Press key combo...";
        HotkeyDisplayBox.Focus();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;

        e.Handled = true;

        int vkCode = AvaloniaKeyToVk(e.Key);
        if (vkCode == 0) return;

        bool isModifier = IsModifierKey(vkCode);

        int modifiers = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= 1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= 2;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= 4;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= 8;

        if (isModifier)
        {
            HotkeyDisplayBox.Text = GetModifierString(ModifiersFromModifierKey(vkCode, modifiers)) + "...";
            return;
        }

        _pendingHotkeyModifiers = modifiers;
        _pendingHotkeyVirtualKey = vkCode;
        HotkeyDisplayBox.Text = GetHotkeyDisplayString(modifiers, vkCode);
        _isRecordingHotkey = false;
        RecordHotkeyButton.Content = "Record";
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;

        int vkCode = AvaloniaKeyToVk(e.Key);
        if (!IsModifierKey(vkCode)) return;

        // Releasing a bare modifier binds the modifier itself as the hotkey
        // (e.g. Right Alt), matching the Windows behavior. The release event
        // still reports the key's own modifier as held, so mask it out before
        // deciding whether it was pressed alone.
        int modifiers = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= 1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= 2;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= 4;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= 8;
        modifiers &= ~ModifierBitOfKey(vkCode);

        if (modifiers == 0)
        {
            _pendingHotkeyVirtualKey = vkCode;
            _pendingHotkeyModifiers = 0;
            HotkeyDisplayBox.Text = GetKeyName(vkCode);
            _isRecordingHotkey = false;
            RecordHotkeyButton.Content = "Record";
            e.Handled = true;
        }
    }

    private static int ModifierBitOfKey(int vkCode)
    {
        return vkCode switch
        {
            162 or 163 or 17 => 1,
            164 or 165 or 18 => 2,
            160 or 161 or 16 => 4,
            91 or 92 => 8,
            _ => 0
        };
    }

    private static int ModifiersFromModifierKey(int vkCode, int modifiers)
    {
        return vkCode switch
        {
            162 or 163 => modifiers | 1,
            164 or 165 => modifiers | 2,
            160 or 161 => modifiers | 4,
            91 or 92 => modifiers | 8,
            _ => modifiers
        };
    }

    private static bool IsModifierKey(int vkCode)
    {
        return vkCode is 160 or 161 or 162 or 163 or 164 or 165 or 91 or 92 or 16 or 17 or 18;
    }

    private static string GetModifierString(int modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & 1) != 0) parts.Add("Ctrl");
        if ((modifiers & 2) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Super");
        return parts.Count > 0 ? string.Join(" + ", parts) : "";
    }

    private static string GetHotkeyDisplayString(int modifiers, int virtualKey)
    {
        var modStr = GetModifierString(modifiers);
        var keyStr = GetKeyName(virtualKey);
        return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr} + {keyStr}";
    }

    private static int AvaloniaKeyToVk(Key key)
    {
        return key switch
        {
            >= Key.A and <= Key.Z => 65 + (key - Key.A),
            >= Key.D0 and <= Key.D9 => 48 + (key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => 96 + (key - Key.NumPad0),
            >= Key.F1 and <= Key.F12 => 112 + (key - Key.F1),
            Key.LeftShift => 160,
            Key.RightShift => 161,
            Key.LeftCtrl => 162,
            Key.RightCtrl => 163,
            Key.LeftAlt => 164,
            Key.RightAlt => 165,
            Key.LWin => 91,
            Key.RWin => 92,
            Key.Back => 8,
            Key.Tab => 9,
            Key.Enter => 13,
            Key.Pause => 19,
            Key.CapsLock => 20,
            Key.Escape => 27,
            Key.Space => 32,
            Key.PageUp => 33,
            Key.PageDown => 34,
            Key.End => 35,
            Key.Home => 36,
            Key.Left => 37,
            Key.Up => 38,
            Key.Right => 39,
            Key.Down => 40,
            Key.Insert => 45,
            Key.Delete => 46,
            Key.Apps => 93,
            Key.NumLock => 144,
            Key.Scroll => 145,
            Key.OemSemicolon => 186,
            Key.OemPlus => 187,
            Key.OemComma => 188,
            Key.OemMinus => 189,
            Key.OemPeriod => 190,
            Key.OemQuestion => 191,
            Key.OemTilde => 192,
            Key.OemOpenBrackets => 219,
            Key.OemPipe => 220,
            Key.OemCloseBrackets => 221,
            Key.OemQuotes => 222,
            _ => 0
        };
    }

    private static string GetKeyName(int virtualKey)
    {
        return virtualKey switch
        {
            8 => "Backspace",
            9 => "Tab",
            13 => "Enter",
            16 => "Shift",
            17 => "Ctrl",
            18 => "Alt",
            19 => "Pause",
            20 => "Caps Lock",
            27 => "Escape",
            32 => "Space",
            33 => "Page Up",
            34 => "Page Down",
            35 => "End",
            36 => "Home",
            37 => "Left Arrow",
            38 => "Up Arrow",
            39 => "Right Arrow",
            40 => "Down Arrow",
            45 => "Insert",
            46 => "Delete",
            91 => "Left Super",
            92 => "Right Super",
            93 => "Menu",
            112 => "F1",
            113 => "F2",
            114 => "F3",
            115 => "F4",
            116 => "F5",
            117 => "F6",
            118 => "F7",
            119 => "F8",
            120 => "F9",
            121 => "F10",
            122 => "F11",
            123 => "F12",
            144 => "Num Lock",
            145 => "Scroll Lock",
            160 => "Left Shift",
            161 => "Right Shift",
            162 => "Left Ctrl",
            163 => "Right Ctrl",
            164 => "Left Alt",
            165 => "Right Alt",
            186 => ";",
            187 => "=",
            188 => ",",
            189 => "-",
            190 => ".",
            191 => "/",
            192 => "`",
            219 => "[",
            220 => "\\",
            221 => "]",
            222 => "'",
            _ when virtualKey >= 48 && virtualKey <= 57 => ((char)virtualKey).ToString(),
            _ when virtualKey >= 65 && virtualKey <= 90 => ((char)virtualKey).ToString(),
            _ when virtualKey >= 96 && virtualKey <= 105 => $"Numpad {virtualKey - 96}",
            _ => $"Key {virtualKey}"
        };
    }
}
