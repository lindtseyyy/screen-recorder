using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Recording;

/// <summary>
/// Single source of time for video and audio (PLAN §6.3).
/// activeTime(t) = t - startTime - pausedTotal - sleptTotal. While paused, active time is frozen.
/// The production clock runs on QPC, the same domain as WGC frame.SystemRelativeTime;
/// a custom time provider can be injected for tests.
/// </summary>
/// <remarks>
/// QPC keeps counting while the PC sleeps (lid closed, sleep button, idle sleep).
/// Counted, that time would reach the mixer at wake as one burst of silence
/// (~0.7 GB per hour asleep, enough to crash the app overnight and lose the
/// recording) and the video as one frozen frame for the whole sleep. So time
/// asleep is left out like a pause: the recording continues where it left off.
/// </remarks>
public sealed class RecordingClock
{
    /// <summary>Sleep shorter than this is ignored (sampling noise, never a real sleep).</summary>
    private static readonly TimeSpan SleepThreshold = TimeSpan.FromSeconds(0.5);

    private readonly Func<TimeSpan> _now;
    private readonly Func<TimeSpan> _sleptNow;
    private readonly object _gate = new();
    private TimeSpan _start;
    private TimeSpan _pausedTotal;
    private TimeSpan _pauseStartedAt;
    private TimeSpan _sleptAtStart;
    private TimeSpan _slept;
    private bool _started;

    public RecordingClock() : this(QpcNow, SystemSleptTime)
    {
    }

    /// <param name="nowProvider">Current time, in the domain of the timestamps passed to <see cref="ActiveTime"/>.</param>
    /// <param name="sleptProvider">Total time the system has slept so far (only ever grows).</param>
    public RecordingClock(Func<TimeSpan> nowProvider, Func<TimeSpan>? sleptProvider = null)
    {
        _now = nowProvider ?? throw new ArgumentNullException(nameof(nowProvider));
        _sleptNow = sleptProvider ?? (() => TimeSpan.Zero);
    }

    public bool IsPaused { get; private set; }
    public bool IsStarted => _started;
    public TimeSpan Now => _now();

    /// <summary>Time the system slept since <see cref="Start"/>, left out of active time.</summary>
    public TimeSpan SleptTime
    {
        get { lock (_gate) { return Slept(); } }
    }

    /// <summary>QPC timestamp converted to a TimeSpan, matching frame.SystemRelativeTime units.</summary>
    public static TimeSpan QpcNow() =>
        TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

    /// <summary>
    /// Total time the system has slept since boot: QPC counts sleep, unbiased
    /// interrupt time doesn't. The latter ticks every ~15 ms, well under
    /// <see cref="SleepThreshold"/>. Zero if unavailable: sleep is then counted
    /// (the old behavior) rather than failing the recording.
    /// </summary>
    public static TimeSpan SystemSleptTime()
    {
        try
        {
            var qpc = QpcNow();
            if (!QueryUnbiasedInterruptTime(out var awake100ns))
                return TimeSpan.Zero;
            var slept = qpc - TimeSpan.FromTicks((long)awake100ns);
            return slept < TimeSpan.Zero ? TimeSpan.Zero : slept;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _start = _now();
            _sleptAtStart = _sleptNow();
            _slept = TimeSpan.Zero;
            _pausedTotal = TimeSpan.Zero;
            IsPaused = false;
            _started = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            RequireStarted();
            if (IsPaused)
                throw new InvalidOperationException("Clock is already paused.");
            _pauseStartedAt = Awake(_now());
            IsPaused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            RequireStarted();
            if (!IsPaused)
                throw new InvalidOperationException("Clock is not paused.");
            _pausedTotal += Awake(_now()) - _pauseStartedAt;
            IsPaused = false;
        }
    }

    /// <summary>Active (recorded) time at instant <paramref name="now"/>. Frozen while paused.</summary>
    public TimeSpan ActiveTime(TimeSpan now)
    {
        lock (_gate)
        {
            RequireStarted();
            var awake = Awake(now);
            var paused = _pausedTotal + (IsPaused ? awake - _pauseStartedAt : TimeSpan.Zero);
            var active = awake - _start - paused;
            return active < TimeSpan.Zero ? TimeSpan.Zero : active;
        }
    }

    /// <summary>Current active time; frozen while paused. Used for the UI elapsed display.</summary>
    public TimeSpan Elapsed => _started ? ActiveTime(_now()) : TimeSpan.Zero;

    /// <summary>A time from <see cref="Now"/>'s domain with the sleep since start removed.</summary>
    private TimeSpan Awake(TimeSpan time) => time - Slept();

    /// <summary>
    /// Sleep since start. Only grows, and only in steps of at least
    /// <see cref="SleepThreshold"/>, so sampling noise never shifts timestamps.
    /// </summary>
    private TimeSpan Slept()
    {
        var measured = _sleptNow() - _sleptAtStart;
        if (measured - _slept >= SleepThreshold)
            _slept = measured;
        return _slept;
    }

    private void RequireStarted()
    {
        if (!_started)
            throw new InvalidOperationException("Clock has not been started.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
