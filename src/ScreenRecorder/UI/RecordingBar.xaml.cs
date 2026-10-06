using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenRecorder.Capture;

namespace ScreenRecorder.UI;

/// <summary>
/// Small floating indicator (PLAN §6.1): elapsed time, a "System sound on" label, and
/// labeled Mute/Pause/Stop buttons. Topmost, draggable, and excluded from
/// capture so it never appears in the video. No tooltips: those popups are
/// separate windows and would be captured.
/// </summary>
public partial class RecordingBar : Window
{
    // Matte palette: muted, low-saturation tones that read on the dark bar
    // without glowing. Neutral for actions that don't change much; a color
    // only marks a state the user should notice (muted, paused) or Stop.
    private static readonly Brush NeutralButton = Matte(0x3A, 0x3A, 0x3D);  // Mute Mic, Pause
    private static readonly Brush MutedButton = Matte(0x6E, 0x5C, 0x34);    // Unmute Mic (mic is off)
    private static readonly Brush PlayButton = Matte(0x2F, 0x5E, 0x48);     // Play (recording paused)
    private static readonly Brush StopButtonBrush = Matte(0x8A, 0x3E, 0x3E); // Stop
    private static readonly Brush RecordingDot = Matte(0xC8, 0x55, 0x55);
    private static readonly Brush PausedAccent = Matte(0xC9, 0xA2, 0x5A);    // dot + "Paused" label

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private MonitorInfo? _monitor;
    private bool _dragging;
    private Point _dragStart;

    public RecordingBar(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        StopButton.Background = StopButtonBrush;
        PausedLabel.Foreground = PausedAccent;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            Position();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Positions the bar top-center on the recorded monitor. Applied on load,
    /// when the auto-sized width is known.
    /// </summary>
    public void PlaceAtTopCenter(MonitorInfo monitor)
    {
        _monitor = monitor;
        if (IsLoaded)
            Position();
    }

    private void Position()
    {
        if (_monitor is null)
            return;
        var centerX = DpiConvert.ToDips(_monitor.Left + _monitor.Width / 2, _monitor.Dpi);
        Left = centerX - ActualWidth / 2;
        Top = DpiConvert.ToDips(_monitor.Top, _monitor.Dpi) + 16;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ExcludeFromCapture(this);
    }

    private void Refresh()
    {
        TimeText.Text = FormatElapsed(_vm.Recorder.Elapsed);
        SystemSoundText.Visibility = _vm.RecordingSystemOn ? Visibility.Visible : Visibility.Collapsed;
        var micOn = _vm.RecordingMicOn;
        MicButton.Visibility = micOn ? Visibility.Visible : Visibility.Collapsed;
        var muted = _vm.MicMuted;
        MicButton.Content = muted ? "Unmute Mic" : "Mute Mic";
        MicButton.Background = muted ? MutedButton : NeutralButton;
        var paused = _vm.IsPaused;
        PausedLabel.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = paused ? "Play" : "Pause";
        PauseButton.Background = paused ? PlayButton : NeutralButton;
        Dot.Fill = paused ? PausedAccent : RecordingDot;
    }

    private static Brush Matte(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

    // Custom drag (instead of DragMove): a system drag shows the snap-layout
    // overlay, which is not excluded from capture and would appear in the video.
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button)
            return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var pos = e.GetPosition(this);
        Left += pos.X - _dragStart.X;
        Top += pos.Y - _dragStart.Y;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnCaptureLost(object sender, MouseEventArgs e) => _dragging = false;

    private void OnMicClick(object sender, RoutedEventArgs e) =>
        _vm.ToggleMicMuteCommand.Execute(null);

    private void OnPauseClick(object sender, RoutedEventArgs e) =>
        _vm.PauseResumeCommand.Execute(null);

    private void OnStopClick(object sender, RoutedEventArgs e) =>
        _vm.StopCommand.Execute(null);
}
