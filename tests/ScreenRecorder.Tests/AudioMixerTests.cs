using ScreenRecorder.Audio;

namespace ScreenRecorder.Tests;

public sealed class AudioMixerTests
{
    private static AudioMixer Dual() => new() { SystemEnabled = true, MicEnabled = true };

    private static float[] Stereo(int frames, float left, float right)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            data[i * 2] = left;
            data[i * 2 + 1] = right;
        }
        return data;
    }

    private static void NearFull(short actual, int expected) =>
        Assert.InRange((int)actual, expected - 4, expected + 4);

    [Fact]
    public void Pull_SumsBothSources()
    {
        var mixer = Dual();
        mixer.FeedSystem(Stereo(960, 0.25f, 0.25f));
        mixer.FeedMic(Stereo(960, 0.25f, 0.25f));

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));

        Assert.Equal(960, chunk.Pcm.Length / 2);
        Assert.Equal(0, chunk.StartSample);
        Assert.All(chunk.Pcm, s => NearFull(s, 16384)); // 0.5 * 32767
    }

    [Fact]
    public void Pull_ClampsAboveFullScale()
    {
        var mixer = Dual();
        mixer.FeedSystem(Stereo(960, 0.8f, 0.8f));
        mixer.FeedMic(Stereo(960, 0.8f, 0.8f));

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));

        Assert.All(chunk.Pcm, s => Assert.True(s == 32767));
    }

    [Fact]
    public void Pull_ClampsNegative()
    {
        var mixer = Dual();
        mixer.FeedSystem(Stereo(960, -0.9f, -0.9f));
        mixer.FeedMic(Stereo(960, -0.9f, -0.9f));

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));

        Assert.All(chunk.Pcm, s => Assert.True(s == -32767));
    }

    [Fact]
    public void Pull_KeepsChannelsSeparate()
    {
        var mixer = Dual();
        mixer.FeedSystem(Stereo(960, 0.5f, -0.5f)); // mic contributes silence

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));

        NearFull(chunk.Pcm[0], 16384);
        NearFull(chunk.Pcm[1], -16384);
    }

    [Fact]
    public void Pull_Underflow_FillsSilence()
    {
        var mixer = Dual(); // nothing fed: loopback silence case

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));

        Assert.All(chunk.Pcm, s => Assert.True(s == 0));
        Assert.True(mixer.UnderflowSamples > 0);
    }

    [Fact]
    public void Pull_Overflow_TrimsOldest()
    {
        var mixer = new AudioMixer { SystemEnabled = true };
        mixer.FeedSystem(Stereo(24000, 0.1f, 0.1f)); // 500 ms, over the 200 ms cap
        mixer.FeedSystem(Stereo(960, 0.5f, 0.5f));   // newest data

        Assert.True(mixer.OverflowSamplesDropped > 0);

        // The buffer holds the newest 200 ms: 180 ms of old + 20 ms of new data.
        var chunks = mixer.Pull(TimeSpan.FromMilliseconds(200));
        Assert.Equal(10, chunks.Count);
        Assert.All(chunks[8].Pcm, s => NearFull(s, 3277));  // 0.1 * 32767
        Assert.All(chunks[9].Pcm, s => NearFull(s, 16384)); // 0.5 * 32767
    }

    [Fact]
    public void Pull_WhilePaused_EmitsNothingAndDiscardsFeeds()
    {
        var mixer = Dual();
        mixer.FeedSystem(Stereo(960, 0.5f, 0.5f));
        mixer.IsPaused = true;

        Assert.Empty(mixer.Pull(TimeSpan.FromSeconds(10)));

        mixer.FeedSystem(Stereo(960, 0.5f, 0.5f)); // discarded while paused
        mixer.IsPaused = false;
        mixer.Clear(); // resume clears buffers
        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));
        Assert.All(chunk.Pcm, s => Assert.True(s == 0));
    }

    [Fact]
    public void Pull_SingleSource_IgnoresDisabledSource()
    {
        var mixer = new AudioMixer { SystemEnabled = true };
        mixer.FeedSystem(Stereo(960, 0.5f, 0.5f));
        mixer.FeedMic(Stereo(960, 0.5f, 0.5f)); // must be ignored

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));

        Assert.All(chunk.Pcm, s => NearFull(s, 16384));
    }

    [Fact]
    public void Pull_OnlyCompleteChunks_UnlessFlushed()
    {
        var mixer = Dual();

        var chunks = mixer.Pull(TimeSpan.FromMilliseconds(45)); // 2160 samples
        Assert.Equal(2, chunks.Count); // 2 × 960; 240 held back
        Assert.Equal(1920, mixer.SamplesEmitted);

        var last = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(45), flush: true));
        Assert.Equal(240, last.Pcm.Length / 2);
        Assert.Equal(2160, mixer.SamplesEmitted);
    }

    [Fact]
    public void Pull_SampleCount_MatchesClock_AfterManyCycles()
    {
        var mixer = Dual();
        var active = TimeSpan.Zero;
        for (var i = 0; i < 50; i++)
        {
            mixer.FeedSystem(Stereo(960, 0.1f, 0.1f));
            active += TimeSpan.FromMilliseconds(20);
            mixer.Pull(active);
        }

        Assert.Equal(48000, mixer.SamplesEmitted); // exactly 1 s
    }

    [Fact]
    public void Chunk_Timestamps_FollowSampleIndex()
    {
        var mixer = Dual();
        mixer.FeedSystem(Stereo(1920, 0.1f, 0.1f));

        var chunks = mixer.Pull(TimeSpan.FromMilliseconds(40));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(TimeSpan.Zero, chunks[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(20), chunks[1].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(20), chunks[0].Duration);
    }

    [Fact]
    public void Underflow_FadesFromLastSample_InsteadOfJumpingToSilence()
    {
        var mixer = new AudioMixer { SystemEnabled = true };
        mixer.FeedSystem(Stereo(960, 0.5f, 0.5f));

        var chunks = mixer.Pull(TimeSpan.FromMilliseconds(40)); // 20 ms of data, then a gap

        var gap = chunks[1].Pcm;
        Assert.InRange((int)gap[0], 16000, 16384); // continues from 0.5, no click
        Assert.True(gap[0] > gap[200] && gap[200] > gap[^1]);
        Assert.InRange((int)gap[^1], 0, 300);        // faded out within the 20 ms
    }

    [Fact]
    public void ResetForStart_DropsPreStartAudio_AndItsOverflowCount()
    {
        var mixer = Dual();
        mixer.FeedMic(Stereo(48000, 0.5f, 0.5f)); // 1 s while the encoder starts
        Assert.True(mixer.OverflowSamplesDropped > 0);

        mixer.ResetForStart();

        Assert.Equal(0, mixer.OverflowSamplesDropped);
        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));
        Assert.All(chunk.Pcm, s => Assert.Equal(0, s));
    }

    [Fact]
    public void MutedMic_AudioIsDiscarded_NotPlayedAfterUnmute()
    {
        var mixer = Dual();
        mixer.MicEnabled = false;
        mixer.FeedMic(Stereo(9600, 0.5f, 0.5f)); // 200 ms said while muted

        mixer.MicEnabled = true;
        mixer.FeedMic(Stereo(960, 0.25f, 0.25f)); // first words after unmuting

        var chunk = Assert.Single(mixer.Pull(TimeSpan.FromMilliseconds(20)));
        Assert.All(chunk.Pcm, s => NearFull(s, 8192)); // 0.25, none of the muted 0.5
    }

    /// <summary>
    /// A device clock <paramref name="ppm"/> fast (negative: slow) delivering 10 ms
    /// packets, up to <paramref name="jitterMs"/> late, with the mixer pulled every
    /// 20 ms. After <paramref name="minutes"/> a marker is captured; returns how
    /// late it comes out of the mixer relative to its capture time.
    /// </summary>
    private static (double LagMs, AudioMixer Mixer) SimulateDevice(double ppm, int minutes, int jitterMs = 0)
    {
        var mixer = new AudioMixer { SystemEnabled = true };
        var rnd = new Random(3);
        var queue = new Queue<(int DueMs, float[] Data)>();
        var lastDue = 0;
        double owed = 0;
        var markerAt = minutes * 60_000;
        var lag = double.NaN;
        for (var ms = 1; ms <= markerAt + 1000 && double.IsNaN(lag); ms++)
        {
            if (ms % 10 == 0)
            {
                owed += 480 * (1 + ppm / 1e6);
                var n = (int)owed;
                owed -= n;
                var packet = Stereo(n, 0.1f, 0.1f);
                if (ms == markerAt)
                    packet[0] = packet[1] = 0.9f; // captured at markerAt - 10 ms
                lastDue = Math.Max(lastDue, ms + (jitterMs > 0 ? rnd.Next(jitterMs + 1) : 0));
                queue.Enqueue((lastDue, packet));
            }
            while (queue.Count > 0 && queue.Peek().DueMs <= ms)
                mixer.FeedSystem(queue.Dequeue().Data);
            if (ms % 20 != 0)
                continue;
            foreach (var chunk in mixer.Pull(TimeSpan.FromMilliseconds(ms)))
            {
                if (ms < markerAt)
                    continue;
                for (var i = 0; i < chunk.Pcm.Length; i += 2)
                {
                    if (chunk.Pcm[i] > 16384)
                    {
                        lag = (chunk.StartSample + i / 2) / 48.0 - (markerAt - 10);
                        break;
                    }
                }
            }
        }
        return (lag, mixer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void FastDeviceClock_LagStaysBounded_OverLongRecordings(int jitterMs)
    {
        // 500 ppm fast for 10 min = 300 ms of surplus audio: without drift control
        // the sound would end up 200 ms behind the video (the overflow cap).
        var (lag, mixer) = SimulateDevice(ppm: 500, minutes: 10, jitterMs);

        Assert.InRange(lag, 0, 70);
        Assert.True(mixer.DriftSamplesDropped > 0);
        Assert.Equal(0, mixer.OverflowSamplesDropped);
    }

    [Fact]
    public void SlowDeviceClock_StaysInSync()
    {
        var (lag, _) = SimulateDevice(ppm: -500, minutes: 10);

        Assert.InRange(lag, 0, 40);
    }

    [Fact]
    public void JitterWithoutDrift_DropsNothing()
    {
        var (lag, mixer) = SimulateDevice(ppm: 0, minutes: 10, jitterMs: 30);

        Assert.Equal(0, mixer.DriftSamplesDropped);
        Assert.InRange(lag, 0, 70);
    }
}
