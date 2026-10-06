namespace ScreenRecorder.Audio;

/// <summary>One mixed PCM chunk: 16-bit stereo at 48 kHz with its start sample index (per channel).</summary>
public sealed class AudioChunk
{
    public required short[] Pcm { get; init; }
    public required long StartSample { get; init; }

    public TimeSpan Timestamp => TimeSpan.FromSeconds((double)StartSample / AudioMixer.SampleRate);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)(Pcm.Length / AudioMixer.Channels) / AudioMixer.SampleRate);
}

/// <summary>
/// Clock-driven mixer (PLAN §5.4). Pure logic, no WASAPI or UI dependencies.
/// Mixes system sound + microphone into one stream with a fixed sum + clamp:
/// out = clamp(system + mic, -1, 1). Driven by RecordingClock active time so the
/// audio timeline always matches the video timeline, including silence and drift.
/// All buffers hold interleaved stereo float frames (L, R, L, R, …).
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int ChunkSamples = 960; // 20 ms at 48 kHz, per channel

    /// <summary>Overflow trim threshold: buffers holding more than ~200 ms drop oldest data.</summary>
    private const int MaxBufferedPerChannel = SampleRate / 5;

    /// <summary>
    /// Drift control (PLAN §5.4). A device whose clock runs fast delivers more
    /// than the recording clock consumes, so its buffer, and with it the delay
    /// of that audio behind the video, slowly grows. Each second the smallest
    /// level the buffer reached is checked: above <see cref="DriftHighWater"/>,
    /// the excess down to <see cref="DriftTarget"/> is lag, not jitter cushion,
    /// and is dropped one frame at a time, at most one per
    /// <see cref="DriftDropSpacing"/> frames (0.1 %, inaudible).
    /// </summary>
    private const int DriftWindowChunks = SampleRate / ChunkSamples; // 1 s
    private const int DriftHighWater = SampleRate * 40 / 1000;       // 40 ms
    private const int DriftTarget = SampleRate * 20 / 1000;          // 20 ms
    private const int DriftDropSpacing = 1000;

    /// <summary>
    /// Per-frame decay while a buffer is empty: a gap fades out from the last
    /// sample over a few ms instead of jumping to silence, which would click.
    /// </summary>
    private const float UnderflowFade = 0.995f;

    private readonly Feed _system = new();
    private readonly Feed _mic = new();
    private readonly object _gate = new();
    private long _samplesEmitted;

    /// <summary>One source's jitter buffer and drift state.</summary>
    private sealed class Feed
    {
        public readonly Queue<float> Samples = new();
        public bool Enabled;
        public float LastLeft;
        public float LastRight;
        public int WindowMin = int.MaxValue; // fewest frames buffered after a pull, this window
        public int WindowChunks;
        public int DropsPending;
        public int SinceDrop;

        public int BufferedFrames => Samples.Count / Channels;

        public void Reset()
        {
            Samples.Clear();
            LastLeft = LastRight = 0f;
            WindowMin = int.MaxValue;
            WindowChunks = 0;
            DropsPending = 0;
            SinceDrop = 0;
        }
    }

    public bool SystemEnabled
    {
        get { lock (_gate) { return _system.Enabled; } }
        set { lock (_gate) { SetEnabled(_system, value); } }
    }

    /// <summary>
    /// False while the mic is muted: its input is discarded as it arrives, so no
    /// audio from the muted time is buffered and played back after unmuting.
    /// </summary>
    public bool MicEnabled
    {
        get { lock (_gate) { return _mic.Enabled; } }
        set { lock (_gate) { SetEnabled(_mic, value); } }
    }

    /// <summary>While paused, feeds are discarded and <see cref="Pull"/> emits nothing.</summary>
    public bool IsPaused { get; set; }

    public long SamplesEmitted { get { lock (_gate) { return _samplesEmitted; } } }

    /// <summary>Counts only, for the log (§5.4).</summary>
    public long UnderflowSamples { get; private set; }
    public long OverflowSamplesDropped { get; private set; }
    public long DriftSamplesDropped { get; private set; }

    public void FeedSystem(ReadOnlySpan<float> interleavedStereo) => Enqueue(_system, interleavedStereo);

    public void FeedMic(ReadOnlySpan<float> interleavedStereo) => Enqueue(_mic, interleavedStereo);

    private void Enqueue(Feed feed, ReadOnlySpan<float> interleavedStereo)
    {
        lock (_gate)
        {
            if (IsPaused || !feed.Enabled)
                return;
            foreach (var s in interleavedStereo)
                feed.Samples.Enqueue(s);
            Trim(feed.Samples);
        }
    }

    /// <summary>
    /// Emits complete 20 ms chunks up to <paramref name="activeTime"/> (or everything
    /// when <paramref name="flush"/> is true, for the final partial chunk at stop).
    /// </summary>
    public IReadOnlyList<AudioChunk> Pull(TimeSpan activeTime, bool flush = false)
    {
        lock (_gate)
        {
            var result = new List<AudioChunk>();
            if (IsPaused)
                return result;
            var target = (long)Math.Floor(activeTime.TotalSeconds * SampleRate);
            if (target < _samplesEmitted)
                target = _samplesEmitted; // clock never goes backwards
            var pending = target - _samplesEmitted;
            var toEmit = flush ? pending : pending / ChunkSamples * ChunkSamples;
            while (toEmit > 0)
            {
                var n = (int)Math.Min(toEmit, ChunkSamples);
                var pcm = new short[n * Channels];
                for (var i = 0; i < n; i++)
                {
                    Take(_system, out var sl, out var sr);
                    Take(_mic, out var ml, out var mr);
                    pcm[i * 2] = ToShort(sl + ml);
                    pcm[i * 2 + 1] = ToShort(sr + mr);
                }
                result.Add(new AudioChunk { Pcm = pcm, StartSample = _samplesEmitted });
                _samplesEmitted += n;
                toEmit -= n;
                TrackDrift(_system);
                TrackDrift(_mic);
            }
            return result;
        }
    }

    /// <summary>Clears jitter buffers (called on resume).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _system.Reset();
            _mic.Reset();
        }
    }

    /// <summary>
    /// Clears buffers and counters when the recording clock starts. The sources
    /// open before it and fill (and trim) their buffers meanwhile; none of that
    /// is recorded, so it must not show up as overflow in the log.
    /// </summary>
    public void ResetForStart()
    {
        lock (_gate)
        {
            _system.Reset();
            _mic.Reset();
            UnderflowSamples = 0;
            OverflowSamplesDropped = 0;
            DriftSamplesDropped = 0;
        }
    }

    private static void SetEnabled(Feed feed, bool enabled)
    {
        if (feed.Enabled == enabled)
            return;
        feed.Enabled = enabled;
        feed.Reset(); // disabling discards; enabling starts from an empty buffer
    }

    private void Take(Feed feed, out float left, out float right)
    {
        if (!feed.Enabled)
        {
            left = right = 0f;
            return;
        }
        var buffer = feed.Samples;
        if (buffer.Count < Channels)
        {
            // Silence fill: loopback delivers nothing while nothing plays, and a
            // late packet leaves a short gap. Fade out from the last sample.
            UnderflowSamples += Channels;
            left = feed.LastLeft *= UnderflowFade;
            right = feed.LastRight *= UnderflowFade;
            return;
        }
        if (feed.DropsPending > 0 && ++feed.SinceDrop >= DriftDropSpacing && buffer.Count >= Channels * 2)
        {
            buffer.Dequeue();
            buffer.Dequeue();
            feed.DropsPending--;
            feed.SinceDrop = 0;
            DriftSamplesDropped += Channels;
        }
        left = feed.LastLeft = buffer.Dequeue();
        right = feed.LastRight = buffer.Dequeue();
    }

    /// <summary>Called after each chunk; once per window decides how much lag to drop.</summary>
    private static void TrackDrift(Feed feed)
    {
        if (!feed.Enabled)
            return;
        feed.WindowMin = Math.Min(feed.WindowMin, feed.BufferedFrames);
        if (++feed.WindowChunks < DriftWindowChunks)
            return;
        feed.WindowChunks = 0;
        // Never schedule more than one window can drop at the spacing limit.
        feed.DropsPending = feed.WindowMin > DriftHighWater
            ? Math.Min(feed.WindowMin - DriftTarget, DriftWindowChunks * ChunkSamples / DriftDropSpacing)
            : 0;
        feed.WindowMin = int.MaxValue;
    }

    private static short ToShort(float s)
    {
        var v = (int)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f);
        return (short)Math.Clamp(v, -32768, 32767);
    }

    /// <summary>Drops oldest data beyond ~200 ms, always whole stereo frames.</summary>
    private void Trim(Queue<float> buffer)
    {
        var max = MaxBufferedPerChannel * Channels;
        while (buffer.Count > max + 1) // keep an even count: drop L+R pairs only
        {
            buffer.Dequeue();
            buffer.Dequeue();
            OverflowSamplesDropped += 2;
        }
    }
}
