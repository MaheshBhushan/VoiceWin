namespace VoiceWin.Services;

/// <summary>
/// Linux implementation of GlobalHotkeyService using evdev (/dev/input/event*).
/// Works on X11 and Wayland but requires membership in the "input" group.
/// Same API and hold/toggle/hybrid state machine as the Windows low-level-hook
/// version. Also supports an external toggle trigger (tray click / `voicewin
/// --toggle`) for setups without input-group access.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private bool _isKeyDown;
    private DateTime _keyDownTime;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public int TargetVirtualKey { get; set; } = 165;
    public int TargetModifiers { get; set; } = 0;
    public string Mode { get; set; } = "hold";

    /// <summary>True if at least one /dev/input device could be opened.</summary>
    public bool IsAvailable { get; private set; }

    private bool _toggleState;
    private bool _isRecording;
    private bool _recordingStartedByTap;
    private bool _externalToggleActive;
    private const int HybridHoldThresholdMs = 250;

    private readonly List<FileStream> _devices = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<int> _pressed = new();
    private readonly object _lock = new();

    public GlobalHotkeyService()
    {
        foreach (var path in Directory.GetFiles("/dev/input", "event*"))
        {
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _devices.Add(fs);
                var token = _cts.Token;
                _ = Task.Run(() => ReadLoop(fs, token), token);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        IsAvailable = _devices.Count > 0;
    }

    /// <summary>
    /// External toggle (tray click, control socket): first call raises
    /// HotkeyPressed, second raises HotkeyReleased — regardless of Mode.
    /// </summary>
    public void SimulateToggle()
    {
        lock (_lock)
        {
            _externalToggleActive = !_externalToggleActive;
            if (_externalToggleActive)
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            else
                HotkeyReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReadLoop(FileStream fs, CancellationToken token)
    {
        // struct input_event on 64-bit: timeval(16) + type(2) + code(2) + value(4)
        var buf = new byte[24];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int total = 0;
                while (total < 24)
                {
                    int n = fs.Read(buf, total, 24 - total);
                    if (n <= 0) return;
                    total += n;
                }

                var type = BitConverter.ToUInt16(buf, 16);
                var code = BitConverter.ToUInt16(buf, 18);
                var value = BitConverter.ToInt32(buf, 20);
                if (type != 1 || value == 2) continue; // EV_KEY only, ignore auto-repeat

                lock (_lock)
                {
                    if (value == 1) _pressed.Add(code);
                    else _pressed.Remove(code);

                    var targetCode = KeyMap.VkToEvdev.TryGetValue(TargetVirtualKey, out var c) ? c : 100;
                    if (code != targetCode) continue;
                    if (value == 1 && !AreModifiersPressed())
                    {
                        TranscriptionOrchestrator.Log($"evdev: target key down ignored, modifiers not held");
                        continue;
                    }

                    TranscriptionOrchestrator.Log($"evdev: target key {(value == 1 ? "down" : "up")} ({fs.Name})");
                    HandleHotkeyEvent(isKeyDownEvent: value == 1);
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    // Mirrors the Windows GlobalHotkeyService state machine exactly.
    private void HandleHotkeyEvent(bool isKeyDownEvent)
    {
        bool isKeyUpEvent = !isKeyDownEvent;

        if (Mode == "hold")
        {
            if (isKeyDownEvent && !_isKeyDown)
            {
                _isKeyDown = true;
                _keyDownTime = DateTime.UtcNow;
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            }
            else if (isKeyUpEvent && _isKeyDown)
            {
                _isKeyDown = false;
                HotkeyReleased?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (Mode == "toggle")
        {
            if (isKeyDownEvent && !_isKeyDown)
            {
                _isKeyDown = true;
                _toggleState = !_toggleState;

                if (_toggleState)
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                else
                    HotkeyReleased?.Invoke(this, EventArgs.Empty);
            }
            else if (isKeyUpEvent)
            {
                _isKeyDown = false;
            }
        }
        else if (Mode == "hybrid")
        {
            if (isKeyDownEvent && !_isKeyDown)
            {
                _isKeyDown = true;
                _keyDownTime = DateTime.UtcNow;

                if (!_isRecording)
                {
                    _isRecording = true;
                    _recordingStartedByTap = false;
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (isKeyUpEvent && _isKeyDown)
            {
                _isKeyDown = false;
                var holdDuration = (DateTime.UtcNow - _keyDownTime).TotalMilliseconds;

                if (holdDuration >= HybridHoldThresholdMs)
                {
                    // Hold gesture: stop on release.
                    if (_isRecording)
                    {
                        _isRecording = false;
                        _recordingStartedByTap = false;
                        HotkeyReleased?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (!_recordingStartedByTap)
                {
                    // First quick tap: keep recording (toggle on).
                    _recordingStartedByTap = true;
                }
                else
                {
                    // Second quick tap: toggle off.
                    _isRecording = false;
                    _recordingStartedByTap = false;
                    HotkeyReleased?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private bool AreModifiersPressed()
    {
        if (TargetModifiers == 0) return true;

        if ((TargetModifiers & 1) != 0 && !KeyMap.EvdevCtrl.Any(_pressed.Contains)) return false;
        if ((TargetModifiers & 2) != 0 && !KeyMap.EvdevAlt.Any(_pressed.Contains)) return false;
        if ((TargetModifiers & 4) != 0 && !KeyMap.EvdevShift.Any(_pressed.Contains)) return false;
        if ((TargetModifiers & 8) != 0 && !KeyMap.EvdevMeta.Any(_pressed.Contains)) return false;
        return true;
    }

    public void CancelToggle()
    {
        _toggleState = false;
        _isRecording = false;
        _recordingStartedByTap = false;
        _externalToggleActive = false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var fs in _devices) fs.Dispose();
    }
}
