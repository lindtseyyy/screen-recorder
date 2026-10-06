using ScreenRecorder.Capture;

namespace ScreenRecorder.Tests;

/// <summary>Sizing of the growable capture ring (the "video freezes, audio continues" fix).</summary>
public sealed class FrameRingTests
{
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void CommonSizes_GetTheFullCap(int width, int height)
    {
        Assert.Equal(FrameSource.MaxMaxRingSize, FrameSource.MaxRingSize(width, height));
    }

    [Fact]
    public void LargeCrops_AreLimitedByTheMemoryBudget()
    {
        var cap = FrameSource.MaxRingSize(4096, 2304);

        Assert.InRange(cap, FrameSource.MinMaxRingSize, FrameSource.MaxMaxRingSize - 1);
        Assert.True((long)cap * 4096 * 2304 * 4 <= FrameSource.RingMemoryBudget);
    }

    [Fact]
    public void HugeCrops_StillGetTheMinimum()
    {
        Assert.Equal(FrameSource.MinMaxRingSize, FrameSource.MaxRingSize(16384, 16384));
    }

    [Fact]
    public void Cap_IsAboveTheInitialRing()
    {
        // The freeze came from a fixed ring of 4: the cap must leave room to grow.
        Assert.True(FrameSource.MinMaxRingSize > FrameSource.InitialRingSize);
    }

    [Fact]
    public void DegenerateSize_DoesNotThrow()
    {
        Assert.Equal(FrameSource.MaxMaxRingSize, FrameSource.MaxRingSize(0, 0));
    }
}
