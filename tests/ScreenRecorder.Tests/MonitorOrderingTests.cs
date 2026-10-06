using ScreenRecorder.Capture;

namespace ScreenRecorder.Tests;

public sealed class MonitorOrderingTests
{
    private static MonitorInfo Fake(string name, int left, int top, int width, int height, bool primary) =>
        new(0, name, left, top, left + width, top + height, primary, 96, 0, string.Empty);

    [Fact]
    public void SingleMonitor_IsDisplay1()
    {
        var result = MonitorOrdering.AssignNumbers(
            [Fake("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true)]);

        var monitor = Assert.Single(result);
        Assert.Equal(1, monitor.Number);
        Assert.Equal("Display 1 · 1920 × 1080", monitor.Label);
    }

    [Fact]
    public void Primary_First_RegardlessOfPosition()
    {
        // Primary on the right: still Display 1.
        var result = MonitorOrdering.AssignNumbers(
        [
            Fake("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, false),
            Fake("\\\\.\\DISPLAY2", 1920, 0, 2560, 1440, true),
        ]);

        Assert.Equal("\\\\.\\DISPLAY2", result[0].DeviceName);
        Assert.Equal(1, result[0].Number);
        Assert.Equal("\\\\.\\DISPLAY1", result[1].DeviceName);
        Assert.Equal(2, result[1].Number);
    }

    [Fact]
    public void SecondaryMonitors_LeftToRight_ThenTopToBottom()
    {
        var result = MonitorOrdering.AssignNumbers(
        [
            Fake("right", 1920, 0, 1920, 1080, false),
            Fake("primary", 0, 0, 1920, 1080, true),
            Fake("left", -1920, 0, 1920, 1080, false),
        ]);

        Assert.Equal("primary", result[0].DeviceName);
        Assert.Equal("left", result[1].DeviceName);   // leftmost non-primary
        Assert.Equal("right", result[2].DeviceName);
        Assert.Equal([1, 2, 3], result.Select(m => m.Number));
    }

    [Fact]
    public void StackedMonitors_TopFirst()
    {
        var result = MonitorOrdering.AssignNumbers(
        [
            Fake("bottom", 0, 1080, 1920, 1080, false),
            Fake("primary", 0, 0, 1920, 1080, true),
            Fake("top", 0, -1080, 1920, 1080, false),
        ]);

        Assert.Equal("primary", result[0].DeviceName);
        Assert.Equal("top", result[1].DeviceName);
        Assert.Equal("bottom", result[2].DeviceName);
    }

    [Fact]
    public void MonitorAbovePrimary_HandledWithNegativeCoords()
    {
        var result = MonitorOrdering.AssignNumbers(
        [
            Fake("above", 0, -1440, 2560, 1440, false),
            Fake("primary", 0, 0, 1920, 1080, true),
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal("Display 2 · 2560 × 1440", result[1].Label);
        Assert.Equal(-1440, result[1].Top);
    }

    [Fact]
    public void Labels_UseSortedNumbers()
    {
        var result = MonitorOrdering.AssignNumbers(
        [
            Fake("b", 1920, 0, 1280, 720, false),
            Fake("a", 0, 0, 3840, 2160, true),
        ]);

        Assert.Equal("Display 1 · 3840 × 2160", result[0].Label);
        Assert.Equal("Display 2 · 1280 × 720", result[1].Label);
    }
}
