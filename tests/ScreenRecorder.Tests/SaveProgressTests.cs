using ScreenRecorder.Encoding;
using ScreenRecorder.Recording;

namespace ScreenRecorder.Tests;

public sealed class SaveProgressTests
{
    private static readonly TimeSpan End = TimeSpan.FromSeconds(100);

    private static EncoderProgress At(double video, double audio,
        bool videoEnded = false, bool audioEnded = false, long bytes = 0) =>
        new(TimeSpan.FromSeconds(video), TimeSpan.FromSeconds(audio), videoEnded, audioEnded, bytes);

    [Fact]
    public void Backlog_IsMeasured_ByTheSlowerStream()
    {
        // At stop the encoder had consumed up to 90 s of 100 s.
        var tracker = new SaveProgressTracker(At(90, 90), End, audio: true);

        var p = tracker.Update(At(95, 99)); // video half done, audio nearly

        Assert.Equal(SaveStage.Encoding, p.Stage);
        Assert.Equal(0.5 * SaveProgressTracker.EncodedFraction, p.Fraction, 6);
    }

    [Fact]
    public void VideoOnly_IgnoresAudio()
    {
        var tracker = new SaveProgressTracker(At(80, 0), End, audio: false);

        var p = tracker.Update(At(90, 0));

        Assert.Equal(0.5 * SaveProgressTracker.EncodedFraction, p.Fraction, 6);
    }

    [Fact]
    public void AllStreamsEnded_MovesToWritingFile()
    {
        var tracker = new SaveProgressTracker(At(90, 90), End, audio: true);

        Assert.Equal(SaveStage.Encoding, tracker.Update(At(100, 100, videoEnded: true)).Stage);
        var p = tracker.Update(At(100, 100, videoEnded: true, audioEnded: true, bytes: 42));

        Assert.Equal(SaveStage.WritingFile, p.Stage);
        Assert.Equal(SaveProgressTracker.EncodedFraction, p.Fraction, 6);
        Assert.Equal(42, p.BytesWritten);
    }

    [Fact]
    public void NoBacklog_IsImmediatelyEncoded()
    {
        // Real-time encoding keeps up: everything was handed over before stop.
        var tracker = new SaveProgressTracker(At(100, 100), End, audio: true);

        var p = tracker.Update(At(100, 100));

        Assert.Equal(SaveStage.Encoding, p.Stage); // end-of-stream not delivered yet
        Assert.Equal(SaveProgressTracker.EncodedFraction, p.Fraction, 6);
    }

    [Fact]
    public void Fraction_NeverGoesBack()
    {
        var tracker = new SaveProgressTracker(At(0, 0), End, audio: true);

        var high = tracker.Update(At(80, 80));
        var later = tracker.Update(At(10, 10)); // out-of-order snapshot

        Assert.Equal(high.Fraction, later.Fraction);
    }

    [Fact]
    public void Finishing_ThenDone_EndAtOne()
    {
        var tracker = new SaveProgressTracker(At(50, 50), End, audio: true);
        tracker.Update(At(60, 60));

        var finishing = tracker.Finishing(1000);
        var done = tracker.Done(1000);

        Assert.Equal(SaveStage.Finishing, finishing.Stage);
        Assert.Equal(SaveProgressTracker.WrittenFraction, finishing.Fraction, 6);
        Assert.Equal(SaveStage.Done, done.Stage);
        Assert.Equal(1.0, done.Fraction);
    }

    [Fact]
    public void AheadOfStopTime_IsClamped()
    {
        var tracker = new SaveProgressTracker(At(90, 90), End, audio: true);

        var p = tracker.Update(At(130, 130));

        Assert.InRange(p.Fraction, 0, SaveProgressTracker.EncodedFraction);
    }

    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(812L * 1024, "812 KB")]
    [InlineData(45L * 1024 * 1024 + 300 * 1024, "45.3 MB")]
    [InlineData(2_480_000_000L, "2.31 GB")]
    public void FormatSize_IsShortAndReadable(long bytes, string expected)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, SaveProgressTracker.FormatSize(bytes));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
