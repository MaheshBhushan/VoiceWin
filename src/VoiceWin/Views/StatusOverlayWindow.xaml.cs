using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoiceWin.Views;

public partial class StatusOverlayWindow : Window
{
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly Random _random = new();
    private readonly double[] _barHeights = new double[5];
    private readonly double[] _targetHeights = new double[5];
    private bool _isAnimating;
    private double _audioLevel;
    private bool _isStreaming;
    private double _blinkOpacity = 1.0;
    private bool _blinkFadingOut = true;

    private string _position = "bottom";

    private const double MinBarHeight = 4;
    private const double MaxBarHeight = 24;
    private const double AnimationSmoothingFactor = 0.3;

    private static readonly SolidColorBrush RecColor = new(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush LiveColor = new(Color.FromRgb(34, 197, 94));

    public StatusOverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); Hide(); };
        SizeChanged += (s, e) => PositionWindow();

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += AnimationTimer_Tick;

        for (int i = 0; i < 5; i++)
        {
            _barHeights[i] = MinBarHeight;
            _targetHeights[i] = MinBarHeight;
        }
    }

    public void SetPosition(string position)
    {
        _position = position;
        PositionWindow();
    }

    public void SetMode(bool isStreaming)
    {
        _isStreaming = isStreaming;
        Dispatcher.BeginInvoke(() =>
        {
            ModeIndicator.Text = isStreaming ? "LIVE" : "REC";
            ModeIndicator.Foreground = isStreaming ? LiveColor : RecColor;
        });
    }

    private void PositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        TranscriptPanel.Width = Math.Max(160, Math.Min(440, workArea.Width - 40));
        Left = (workArea.Width - ActualWidth) / 2 + workArea.Left;
        
        if (_position == "top")
            Top = workArea.Top + 20;
        else
            Top = workArea.Bottom - ActualHeight - 20;
    }

    public void UpdateStatus(string status)
    {
        Dispatcher.BeginInvoke(() => StatusText.Text = status);
    }

    public void UpdateTranscript(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TranscriptText.Text = text;
            if (_hideTimer.IsEnabled) { _hideTimer.Stop(); _hideTimer.Start(); }
            TranscriptPanel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => TranscriptScroll.ScrollToEnd()));
        });
    }

    public void UpdateAudioLevel(float level)
    {
        _audioLevel = Math.Clamp(level, 0, 1);
    }

    public void StartAnimating()
    {
        _isAnimating = true;
        _animationTimer.Start();
        Dispatcher.BeginInvoke(() =>
        {
            _hideTimer.Stop();
            TranscriptText.Text = "";
            TranscriptPanel.Visibility = Visibility.Collapsed;
            Show();
            PositionWindow();
        });
    }

    public void StopAnimating()
    {
        _isAnimating = false;
    }

    public void HideOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _animationTimer.Stop();
            _hideTimer.Stop();
            if (string.IsNullOrWhiteSpace(TranscriptText.Text)) Hide();
            else _hideTimer.Start();
        });
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTargetHeights();
        bool allSettled = InterpolateBarHeights();
        ApplyBarHeightsToUI();
        UpdateBlinkAnimation();

        if (!_isAnimating && allSettled)
        {
            _animationTimer.Stop();
        }
    }

    private void UpdateBlinkAnimation()
    {
        if (_blinkFadingOut)
        {
            _blinkOpacity -= 0.02;
            if (_blinkOpacity <= 0.3)
            {
                _blinkOpacity = 0.3;
                _blinkFadingOut = false;
            }
        }
        else
        {
            _blinkOpacity += 0.02;
            if (_blinkOpacity >= 1.0)
            {
                _blinkOpacity = 1.0;
                _blinkFadingOut = true;
            }
        }
        ModeIndicator.Opacity = _blinkOpacity;
    }

    private void UpdateTargetHeights()
    {
        if (_isAnimating)
        {
            double baseHeight = MinBarHeight + (_audioLevel * 16);
            for (int i = 0; i < 5; i++)
            {
                double variation = _random.NextDouble() * 8 - 4;
                _targetHeights[i] = Math.Clamp(baseHeight + variation, MinBarHeight, MaxBarHeight);
            }
        }
        else
        {
            for (int i = 0; i < 5; i++)
            {
                _targetHeights[i] = MinBarHeight;
            }
        }
    }

    private bool InterpolateBarHeights()
    {
        bool allSettled = true;
        for (int i = 0; i < 5; i++)
        {
            double diff = _targetHeights[i] - _barHeights[i];
            if (Math.Abs(diff) > 0.5)
            {
                _barHeights[i] += diff * AnimationSmoothingFactor;
                allSettled = false;
            }
            else
            {
                _barHeights[i] = _targetHeights[i];
            }
        }
        return allSettled;
    }

    private void ApplyBarHeightsToUI()
    {
        Bar1.Height = _barHeights[0];
        Bar2.Height = _barHeights[1];
        Bar3.Height = _barHeights[2];
        Bar4.Height = _barHeights[3];
        Bar5.Height = _barHeights[4];
    }
}
