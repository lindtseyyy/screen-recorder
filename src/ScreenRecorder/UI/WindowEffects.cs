using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder.UI;

/// <summary>Window helpers: capture exclusion, click-through, Mica backdrop.</summary>
public static class WindowEffects
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const uint DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2; // Mica

    /// <summary>Keeps our own windows out of the recording (WDA_EXCLUDEFROMCAPTURE).</summary>
    public static void ExcludeFromCapture(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != nint.Zero)
                PInvoke.SetWindowDisplayAffinity(new HWND(hwnd), WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
        }
        catch (Exception ex)
        {
            Log.Warn("ExcludeFromCapture failed: " + ex.Message);
        }
    }

    /// <summary>Makes an overlay click-through and non-activating (selection frame).</summary>
    public static void MakeClickThrough(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == nint.Zero)
                return;
            var style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }
        catch (Exception ex)
        {
            Log.Warn("MakeClickThrough failed: " + ex.Message);
        }
    }

    /// <summary>Best-effort Mica backdrop (Windows 11); silently ignored when unavailable.</summary>
    public static void TryEnableMica(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == nint.Zero)
                return;
            var value = DwmsbtMainWindow;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Info("Mica unavailable: " + ex.Message);
        }
    }
}
