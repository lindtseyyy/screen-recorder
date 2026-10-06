using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder.Capture;

/// <summary>A detected monitor. Bounds are physical pixels in virtual-desktop coordinates
/// and can be negative (monitor left of / above the primary).</summary>
public sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    int Left, int Top, int Right, int Bottom,
    bool IsPrimary,
    uint Dpi,
    int Number,
    string Label)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Pure monitor ordering (PLAN §3): primary first, then left to right, then top to
/// bottom. Numbers are assigned 1..N after sorting and labels built from them.
/// </summary>
public static class MonitorOrdering
{
    public static int Compare(MonitorInfo a, MonitorInfo b)
    {
        if (a.IsPrimary != b.IsPrimary)
            return a.IsPrimary ? -1 : 1;
        var c = a.Left.CompareTo(b.Left);
        return c != 0 ? c : a.Top.CompareTo(b.Top);
    }

    public static IReadOnlyList<MonitorInfo> AssignNumbers(IEnumerable<MonitorInfo> monitors)
    {
        return monitors.Order(Comparer<MonitorInfo>.Create(Compare))
            .Select((m, i) =>
            {
                var number = i + 1;
                var label = $"Display {number} · {m.Width} × {m.Height}";
                return m with { Number = number, Label = label };
            })
            .ToList();
    }
}

/// <summary>Enumerates monitors via EnumDisplayMonitors and refreshes on display changes.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly SynchronizationContext? _sync;

    public MonitorService()
    {
        _sync = SynchronizationContext.Current;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public event Action? MonitorsChanged;

    public IReadOnlyList<MonitorInfo> Enumerate()
    {
        var found = new List<MonitorInfo>();
        unsafe
        {
            BOOL Callback(HMONITOR monitor, HDC hdc, RECT* rect, LPARAM data)
            {
                try
                {
                    found.Add(ReadMonitor(monitor));
                }
                catch (Exception ex)
                {
                    Log.Warn("Skipping monitor that could not be read: " + ex.Message);
                }
                return true;
            }

            PInvoke.EnumDisplayMonitors(default, (RECT?)null, Callback, default);
        }
        return MonitorOrdering.AssignNumbers(found);
    }

    private static unsafe MonitorInfo ReadMonitor(HMONITOR monitor)
    {
        MONITORINFOEXW info = default;
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            throw new InvalidOperationException("GetMonitorInfo failed.");

        string deviceName;
        var offset = Marshal.OffsetOf<MONITORINFOEXW>("szDevice");
        MONITORINFOEXW* p = &info;
        deviceName = Marshal.PtrToStringUni((nint)p + (int)offset.ToInt64()) ?? string.Empty;

        uint dpiX = 96, dpiY = 96;
        var hr = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY);
        if (hr.Value != 0)
            dpiX = dpiY = 96;

        var bounds = info.monitorInfo.rcMonitor;
        return new MonitorInfo(
            (nint)monitor.Value,
            deviceName,
            bounds.left, bounds.top, bounds.right, bounds.bottom,
            (info.monitorInfo.dwFlags & 1) != 0, // MONITORINFOF_PRIMARY
            dpiX,
            0, string.Empty); // number/label assigned by MonitorOrdering
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Info("Display settings changed, refreshing monitor list.");
        if (_sync is not null)
            _sync.Post(_ => MonitorsChanged?.Invoke(), null);
        else
            MonitorsChanged?.Invoke();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
