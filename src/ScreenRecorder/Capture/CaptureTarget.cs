namespace ScreenRecorder.Capture;

/// <summary>WPF DIPs ↔ physical pixels using a window's own DPI (never assume 100%).</summary>
public static class DpiConvert
{
    public static int ToPixels(double dips, uint dpi) =>
        (int)Math.Round(dips * dpi / 96.0);

    public static double ToDips(int pixels, uint dpi) =>
        pixels * 96.0 / dpi;
}

/// <summary>
/// The one shared capture model (PLAN §4.1): a monitor plus a crop rectangle in
/// that monitor's physical pixels, origin at its top-left. Full screen is the
/// whole-monitor rectangle; anything smaller is a region.
/// </summary>
public sealed record CaptureTarget(MonitorInfo Monitor, int X, int Y, int Width, int Height)
{
    /// <summary>Minimum size: stays within hardware encoder limits.</summary>
    public const int MinSize = 128;

    public bool IsFullScreen =>
        X == 0 && Y == 0 && Width == Monitor.Width && Height == Monitor.Height;

    public string Description => IsFullScreen
        ? $"Display {Monitor.Number} · Full screen"
        : $"Region · {Width} × {Height} on Display {Monitor.Number}";

    public static CaptureTarget FullScreen(MonitorInfo monitor) =>
        new(monitor, 0, 0, monitor.Width, monitor.Height);

    /// <summary>
    /// Normalizes a requested region: clamped to the monitor (regions cannot span
    /// monitors), width/height rounded down to even numbers (H.264 4:2:0), and a
    /// 128×128 minimum enforced.
    /// </summary>
    public static CaptureTarget Normalize(MonitorInfo monitor, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, monitor.Width);
        y = Math.Clamp(y, 0, monitor.Height);
        w = Math.Min(w, monitor.Width - x);
        h = Math.Min(h, monitor.Height - y);

        // Minimum size: grow, shifting left/up when up against the edge.
        if (w < MinSize)
        {
            x = Math.Max(0, Math.Min(x, monitor.Width - MinSize));
            w = Math.Min(MinSize, monitor.Width);
        }
        if (h < MinSize)
        {
            y = Math.Max(0, Math.Min(y, monitor.Height - MinSize));
            h = Math.Min(MinSize, monitor.Height);
        }

        // Even dimensions (H.264 4:2:0). X/Y may stay odd.
        w &= ~1;
        h &= ~1;
        if (w < MinSize) w = MinSize & ~1;
        if (h < MinSize) h = MinSize & ~1;

        return new CaptureTarget(monitor, x, y, w, h);
    }
}
