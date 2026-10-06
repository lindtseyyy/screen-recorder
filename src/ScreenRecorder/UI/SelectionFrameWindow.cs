using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenRecorder.Capture;

namespace ScreenRecorder.UI;

/// <summary>
/// Click-through outline around the current target (PLAN §4.3): flashes for a
/// monitor target, stays for a region, turns red while recording a region.
/// Excluded from capture so it never appears in the video.
/// </summary>
public sealed class SelectionFrameWindow : Window
{
    private readonly Border _border = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(3),
    };
    private readonly DispatcherTimer _flashTimer;

    public SelectionFrameWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Content = _border;

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            Hide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.MakeClickThrough(this);
        WindowEffects.ExcludeFromCapture(this);
    }

    /// <summary>Flashes the border around a monitor for ~1.5 s.</summary>
    public void FlashForMonitor(MonitorInfo monitor)
    {
        ShowForTarget(CaptureTarget.FullScreen(monitor), recording: false);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>Shows the border around a target until hidden (red while recording).</summary>
    public void ShowForTarget(CaptureTarget target, bool recording)
    {
        _flashTimer.Stop();
        var monitor = target.Monitor;
        var left = monitor.Left + target.X;
        var top = monitor.Top + target.Y;
        Left = DpiConvert.ToDips(left, monitor.Dpi);
        Top = DpiConvert.ToDips(top, monitor.Dpi);
        Width = DpiConvert.ToDips(target.Width, monitor.Dpi);
        Height = DpiConvert.ToDips(target.Height, monitor.Dpi);
        _border.BorderBrush = recording
            ? Brushes.Red
            : new SolidColorBrush(SystemParameters.WindowGlassColor);
        Show();
    }

    public new void Hide()
    {
        _flashTimer.Stop();
        base.Hide();
    }
}
