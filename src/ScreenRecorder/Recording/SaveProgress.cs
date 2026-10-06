using System.Globalization;
using ScreenRecorder.Encoding;

namespace ScreenRecorder.Recording;

/// <summary>What the recorder is doing while saving (PLAN §6.6).</summary>
public enum SaveStage
{
    /// <summary>Handing the last queued frames and audio to the encoder.</summary>
    Encoding,
    /// <summary>Encoder drained: MP4 index written, file flushed to disk.</summary>
    WritingFile,
    /// <summary>Renaming the finished .part file to .mp4.</summary>
    Finishing,
    Done,
}

/// <summary>One saving-screen update. <see cref="Fraction"/> is 0–1 and never goes back.</summary>
public readonly record struct SaveProgress(SaveStage Stage, double Fraction, long BytesWritten);

/// <summary>
/// Turns encoder snapshots into one monotonic 0–1 fraction. Draining the encoder
/// is measured (media time handed over vs. the stop time); writing the file and
/// renaming it can't be measured, so each holds a fixed point rather than faking
/// movement. Pure: unit-tested without Media Foundation.
/// </summary>
public sealed class SaveProgressTracker
{
    /// <summary>Fraction reached once every stream has been fully handed to the encoder.</summary>
    public const double EncodedFraction = 0.90;

    /// <summary>Fraction once the file is written and flushed; only the rename remains.</summary>
    public const double WrittenFraction = 0.97;

    private readonly EncoderProgress _start;
    private readonly TimeSpan _end;
    private readonly bool _audio;
    private double _fraction;

    /// <param name="start">Encoder snapshot when stopping began.</param>
    /// <param name="end">The stop time: every stream is complete up to here.</param>
    /// <param name="audio">Whether the recording has an audio stream.</param>
    public SaveProgressTracker(EncoderProgress start, TimeSpan end, bool audio)
    {
        _start = start;
        _end = end;
        _audio = audio;
    }

    /// <summary>Progress while the encoder is still running.</summary>
    public SaveProgress Update(EncoderProgress now)
    {
        var videoDone = StreamFraction(_start.VideoTime, now.VideoTime, now.VideoEnded);
        var audioDone = _audio ? StreamFraction(_start.AudioTime, now.AudioTime, now.AudioEnded) : 1.0;
        var ended = now.VideoEnded && (!_audio || now.AudioEnded);
        // The slower stream decides: the file isn't done until both are.
        var target = ended ? EncodedFraction : Math.Min(videoDone, audioDone) * EncodedFraction;
        return Report(ended ? SaveStage.WritingFile : SaveStage.Encoding, target, now.BytesWritten);
    }

    /// <summary>The file is complete on disk; renaming it.</summary>
    public SaveProgress Finishing(long bytesWritten) =>
        Report(SaveStage.Finishing, WrittenFraction, bytesWritten);

    public SaveProgress Done(long bytesWritten) => Report(SaveStage.Done, 1.0, bytesWritten);

    private SaveProgress Report(SaveStage stage, double target, long bytes)
    {
        _fraction = Math.Max(_fraction, Math.Clamp(target, 0.0, 1.0));
        return new SaveProgress(stage, _fraction, bytes);
    }

    private double StreamFraction(TimeSpan from, TimeSpan now, bool ended)
    {
        if (ended)
            return 1.0;
        var total = _end - from;
        if (total <= TimeSpan.Zero)
            return 1.0; // nothing was backlogged; only the end-of-stream is pending
        return Math.Clamp((now - from) / total, 0.0, 1.0);
    }

    /// <summary>Short file size for the saving screen: "812 KB", "45.3 MB", "2.31 GB".</summary>
    public static string FormatSize(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        var c = CultureInfo.CurrentCulture;
        if (bytes >= Gb)
            return (bytes / Gb).ToString("F2", c) + " GB";
        if (bytes >= Mb)
            return (bytes / Mb).ToString("F1", c) + " MB";
        return Math.Ceiling(bytes / Kb).ToString("F0", c) + " KB";
    }
}
