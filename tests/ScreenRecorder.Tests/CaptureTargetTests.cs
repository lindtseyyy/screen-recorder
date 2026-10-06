using ScreenRecorder.Capture;

namespace ScreenRecorder.Tests;

public sealed class CaptureTargetTests
{
    private static MonitorInfo Monitor(int width = 1920, int height = 1080, int left = 0, int top = 0) =>
        new(0, "\\\\.\\DISPLAY1", left, top, left + width, top + height, true, 96, 1, "Display 1");

    [Fact]
    public void FullScreen_CoversWholeMonitor()
    {
        var target = CaptureTarget.FullScreen(Monitor());

        Assert.True(target.IsFullScreen);
        Assert.Equal((0, 0, 1920, 1080), (target.X, target.Y, target.Width, target.Height));
        Assert.Equal("Display 1 · Full screen", target.Description);
    }

    [Fact]
    public void Normalize_RoundsOddDimensionsDownToEven()
    {
        var target = CaptureTarget.Normalize(Monitor(), 11, 21, 1281, 721);

        Assert.Equal(11, target.X); // origins may stay odd
        Assert.Equal(21, target.Y);
        Assert.Equal(1280, target.Width);
        Assert.Equal(720, target.Height);
    }

    [Fact]
    public void Normalize_ClampsToMonitor()
    {
        var target = CaptureTarget.Normalize(Monitor(), 1500, 800, 500, 400);

        Assert.Equal(1500, target.X);
        Assert.Equal(800, target.Y);
        Assert.Equal(420, target.Width);  // 1920 - 1500
        Assert.Equal(280, target.Height); // 1080 - 800
    }

    [Fact]
    public void Normalize_ClampedSliver_GrowsBackToMinimum()
    {
        // Only 120×80 left at the edge: clamping wins first, then the 128 minimum
        // grows the rect back inside the monitor.
        var target = CaptureTarget.Normalize(Monitor(), 1800, 1000, 500, 400);

        Assert.Equal(1792, target.X);
        Assert.Equal(952, target.Y);
        Assert.Equal(128, target.Width);
        Assert.Equal(128, target.Height);
    }

    [Fact]
    public void Normalize_ClampsNegativeOrigins()
    {
        var target = CaptureTarget.Normalize(Monitor(), -50, -30, 500, 400);

        Assert.Equal(0, target.X);
        Assert.Equal(0, target.Y);
        Assert.Equal(500, target.Width);
        Assert.Equal(400, target.Height);
    }

    [Fact]
    public void Normalize_EnforcesMinimumSize()
    {
        var target = CaptureTarget.Normalize(Monitor(), 500, 500, 40, 60);

        Assert.Equal(128, target.Width);
        Assert.Equal(128, target.Height);
    }

    [Fact]
    public void Normalize_MinimumSize_ShiftsAwayFromEdge()
    {
        var target = CaptureTarget.Normalize(Monitor(), 1900, 1060, 40, 40);

        Assert.Equal(128, target.Width);
        Assert.Equal(128, target.Height);
        Assert.True(target.X + target.Width <= 1920);
        Assert.True(target.Y + target.Height <= 1080);
    }

    [Fact]
    public void Normalize_OnNegativeCoordinateMonitor_UsesRelativeCoords()
    {
        // Monitor placed left of the primary: virtual-desktop coords are negative,
        // but the crop rect stays monitor-relative.
        var left = Monitor(left: -1920);
        var target = CaptureTarget.Normalize(left, 0, 0, 1920, 1080);

        Assert.True(target.IsFullScreen);
        var region = CaptureTarget.Normalize(left, 100, 100, 641, 481);
        Assert.Equal((100, 100, 640, 480), (region.X, region.Y, region.Width, region.Height));
    }

    [Fact]
    public void Region_Description_ShowsSizeAndMonitor()
    {
        var target = CaptureTarget.Normalize(Monitor(), 0, 0, 1280, 720);

        Assert.False(target.IsFullScreen);
        Assert.Equal("Region · 1280 × 720 on Display 1", target.Description);
    }

    [Theory]
    [InlineData(100, 96u, 100)]
    [InlineData(100, 120u, 125)]
    [InlineData(100, 144u, 150)]
    [InlineData(100, 168u, 175)]
    [InlineData(100, 192u, 200)]
    [InlineData(1920, 144u, 2880)]
    public void DpiConvert_ToPixels(double dips, uint dpi, int expected) =>
        Assert.Equal(expected, DpiConvert.ToPixels(dips, dpi));

    [Theory]
    [InlineData(125, 120u, 100.0)]
    [InlineData(2880, 144u, 1920.0)]
    [InlineData(2160, 192u, 1080.0)]
    public void DpiConvert_ToDips(int pixels, uint dpi, double expected) =>
        Assert.Equal(expected, DpiConvert.ToDips(pixels, dpi), precision: 9);
}
