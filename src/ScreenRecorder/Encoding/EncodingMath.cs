namespace ScreenRecorder.Encoding;

/// <summary>User-facing recording quality. Medium matches the original fixed bitrate.</summary>
public enum VideoQuality
{
    Low,
    Medium,
    High,
}

/// <summary>Pure encoding math (PLAN §7.1), shared by the encoder and the tests.</summary>
public static class EncodingMath
{
    /// <summary>Many hardware H.264 encoders max out around 4096×2304.</summary>
    public const int MaxOutputWidth = 4096;
    public const int MaxOutputHeight = 2304;

    public const long MinBitrate = 2_000_000;
    public const long MaxBitrate = 40_000_000;

    /// <summary>≈6 Mbps at 1080p30, ≈22 Mbps at 1440p60. Screen text stays sharp.</summary>
    public static long ComputeBitrate(int width, int height, int fps) =>
        Math.Clamp((long)width * height * fps / 10, MinBitrate, MaxBitrate);

    /// <summary>Bitrate multiplier for a quality level: 0.5× / 1× / 2×.</summary>
    public static double QualityMultiplier(VideoQuality quality) => quality switch
    {
        VideoQuality.Low => 0.5,
        VideoQuality.High => 2.0,
        _ => 1.0,
    };

    /// <summary>Scales a base bitrate to the requested quality, clamped to range.</summary>
    public static long ApplyQuality(long baseBitrate, VideoQuality quality) =>
        Math.Clamp((long)(baseBitrate * QualityMultiplier(quality)), MinBitrate, MaxBitrate);

    /// <summary>AAC stereo bitrate used whenever any audio source is on.</summary>
    public const long AudioBitrate = 192_000;

    /// <summary>Free space left untouched so a long recording never fills the drive.</summary>
    public const long FreeSpaceReserveBytes = 512L * 1024 * 1024;

    /// <summary>Expected file growth per second for the given output size, fps and quality.</summary>
    public static long BytesPerSecond(int width, int height, int fps, bool audio, VideoQuality quality) =>
        (ApplyQuality(ComputeBitrate(width, height, fps), quality) + (audio ? AudioBitrate : 0)) / 8;

    /// <summary>
    /// How long can be recorded with <paramref name="freeBytes"/> available,
    /// keeping <see cref="FreeSpaceReserveBytes"/> untouched. Zero when full.
    /// </summary>
    public static TimeSpan EstimateMaxDuration(
        long freeBytes, int width, int height, int fps, bool audio, VideoQuality quality)
    {
        var usable = freeBytes - FreeSpaceReserveBytes;
        if (usable <= 0)
            return TimeSpan.Zero;
        var bps = BytesPerSecond(width, height, fps, audio, quality);
        if (bps <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)usable / bps);
    }

    /// <summary>Short human form: "≈ 5 h 12 min", "≈ 40 min", "≈ 25 s".</summary>
    public static string FormatEstimate(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "≈ 0 s";
        var totalMinutes = (long)Math.Floor(duration.TotalMinutes);
        if (totalMinutes < 1)
            return $"≈ {(long)Math.Floor(duration.TotalSeconds)} s";
        if (totalMinutes < 60)
            return $"≈ {totalMinutes} min";
        return $"≈ {totalMinutes / 60} h {totalMinutes % 60:D2} min";
    }

    /// <summary>
    /// Fits a crop inside the encoder size box, keeping the aspect ratio. Output is
    /// always even-sized (H.264 4:2:0); sizes that already fit pass through (evened).
    /// </summary>
    public static (int Width, int Height) FitInside(int width, int height,
        int maxWidth = MaxOutputWidth, int maxHeight = MaxOutputHeight)
    {
        if (width <= maxWidth && height <= maxHeight)
            return (width & ~1, height & ~1);
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        var w = Math.Max(2, (int)(width * scale) & ~1);
        var h = Math.Max(2, (int)(height * scale) & ~1);
        return (w, h);
    }
}
