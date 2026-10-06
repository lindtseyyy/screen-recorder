using ScreenRecorder.Encoding;

namespace ScreenRecorder.Tests;

public sealed class EncodingMathTests
{
    [Fact]
    public void Bitrate_1080p30_IsAbout6Mbps()
    {
        Assert.Equal(6_220_800, EncodingMath.ComputeBitrate(1920, 1080, 30));
    }

    [Fact]
    public void Bitrate_1440p60_IsAbout22Mbps()
    {
        Assert.Equal(22_118_400, EncodingMath.ComputeBitrate(2560, 1440, 60));
    }

    [Fact]
    public void Bitrate_ClampsToMinimum()
    {
        Assert.Equal(2_000_000, EncodingMath.ComputeBitrate(128, 128, 30));
    }

    [Fact]
    public void Bitrate_ClampsToMaximum()
    {
        Assert.Equal(40_000_000, EncodingMath.ComputeBitrate(7680, 4320, 60));
    }

    [Fact]
    public void FitInside_PassesThrough_WhenSmallEnough()
    {
        Assert.Equal((1920, 1080), EncodingMath.FitInside(1920, 1080));
        Assert.Equal((3840, 2160), EncodingMath.FitInside(3840, 2160));
    }

    [Fact]
    public void FitInside_EvensOddSizes()
    {
        Assert.Equal((1918, 1078), EncodingMath.FitInside(1919, 1079));
    }

    [Fact]
    public void FitInside_Ultrawide_KeepsAspectRatio()
    {
        // 5120×1440 scaled by 0.8 → exactly 4096×1152.
        Assert.Equal((4096, 1152), EncodingMath.FitInside(5120, 1440));
    }

    [Fact]
    public void FitInside_TallContent_FitsHeight()
    {
        var (w, h) = EncodingMath.FitInside(2000, 4000);

        Assert.True(w <= 4096 && h <= 2304);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        // Aspect ratio preserved within rounding.
        Assert.InRange((double)w / h, 0.49, 0.51);
    }

    [Fact]
    public void FitInside_8K_FitsBoth()
    {
        var (w, h) = EncodingMath.FitInside(7680, 4320);

        Assert.True(w <= 4096 && h <= 2304);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void BytesPerSecond_1080p30_AddsAudio()
    {
        Assert.Equal(6_220_800 / 8,
            EncodingMath.BytesPerSecond(1920, 1080, 30, audio: false, VideoQuality.Medium));
        Assert.Equal((6_220_800 + 192_000) / 8,
            EncodingMath.BytesPerSecond(1920, 1080, 30, audio: true, VideoQuality.Medium));
    }

    [Fact]
    public void BytesPerSecond_High_DoublesVideoBitrate()
    {
        Assert.Equal((6_220_800 * 2 + 192_000) / 8,
            EncodingMath.BytesPerSecond(1920, 1080, 30, audio: true, VideoQuality.High));
    }

    [Fact]
    public void ApplyQuality_ScalesAndClamps()
    {
        Assert.Equal(6_220_800, EncodingMath.ApplyQuality(6_220_800, VideoQuality.Medium));
        Assert.Equal(3_110_400, EncodingMath.ApplyQuality(6_220_800, VideoQuality.Low));
        Assert.Equal(12_441_600, EncodingMath.ApplyQuality(6_220_800, VideoQuality.High));
        Assert.Equal(EncodingMath.MinBitrate, EncodingMath.ApplyQuality(2_000_000, VideoQuality.Low));
        Assert.Equal(EncodingMath.MaxBitrate, EncodingMath.ApplyQuality(40_000_000, VideoQuality.High));
    }

    [Fact]
    public void EstimateMaxDuration_TwoHours_Of1080p30()
    {
        var bps = EncodingMath.BytesPerSecond(1920, 1080, 30, audio: true, VideoQuality.Medium);
        var free = EncodingMath.FreeSpaceReserveBytes + bps * 7200;

        var duration = EncodingMath.EstimateMaxDuration(free, 1920, 1080, 30, audio: true, VideoQuality.Medium);

        Assert.Equal(7200, duration.TotalSeconds);
    }

    [Fact]
    public void EstimateMaxDuration_FullDrive_IsZero()
    {
        var duration = EncodingMath.EstimateMaxDuration(
            EncodingMath.FreeSpaceReserveBytes - 1, 1920, 1080, 30, audio: true, VideoQuality.Medium);

        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Theory]
    [InlineData("5:12:00", "≈ 5 h 12 min")]
    [InlineData("0:40:00", "≈ 40 min")]
    [InlineData("0:00:25", "≈ 25 s")]
    [InlineData("0:00:00", "≈ 0 s")]
    public void FormatEstimate_FormatsDurations(string span, string expected)
    {
        Assert.Equal(expected, EncodingMath.FormatEstimate(TimeSpan.Parse(span)));
    }
}
