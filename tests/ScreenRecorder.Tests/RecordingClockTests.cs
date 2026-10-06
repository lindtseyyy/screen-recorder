using ScreenRecorder.Recording;

namespace ScreenRecorder.Tests;

public sealed class RecordingClockTests
{
    private sealed class ManualTime
    {
        public TimeSpan Now;
    }

    private static (RecordingClock Clock, ManualTime Time) Create()
    {
        var time = new ManualTime();
        return (new RecordingClock(() => time.Now), time);
    }

    [Fact]
    public void ActiveTime_WithoutPause_MeasuresSinceStart()
    {
        var (clock, time) = Create();
        time.Now = TimeSpan.FromSeconds(100);
        clock.Start();
        time.Now = TimeSpan.FromSeconds(110);

        Assert.Equal(TimeSpan.FromSeconds(10), clock.ActiveTime(time.Now));
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Elapsed);
    }

    [Fact]
    public void ActiveTime_WithOnePause_SubtractsPausedSpan()
    {
        var (clock, time) = Create();
        clock.Start(); // t=0
        time.Now = TimeSpan.FromSeconds(5);
        clock.Pause();
        time.Now = TimeSpan.FromSeconds(8);
        clock.Resume();
        time.Now = TimeSpan.FromSeconds(10);

        Assert.Equal(TimeSpan.FromSeconds(7), clock.ActiveTime(time.Now));
    }

    [Fact]
    public void ActiveTime_WithManyPauses_SubtractsAll()
    {
        var (clock, time) = Create();
        clock.Start();
        for (var i = 0; i < 5; i++)
        {
            time.Now += TimeSpan.FromSeconds(1); // 1 s recorded
            clock.Pause();
            time.Now += TimeSpan.FromSeconds(2); // 2 s paused
            clock.Resume();
        }
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
    }

    [Fact]
    public void Elapsed_FrozenWhilePaused()
    {
        var (clock, time) = Create();
        clock.Start();
        time.Now = TimeSpan.FromSeconds(5);
        clock.Pause();
        time.Now = TimeSpan.FromSeconds(60);

        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.ActiveTime(time.Now));
    }

    [Fact]
    public void Pause_ImmediatelyAfterStart_CountsNothing()
    {
        var (clock, time) = Create();
        clock.Start();
        clock.Pause();
        time.Now = TimeSpan.FromSeconds(30);
        clock.Resume();
        time.Now = TimeSpan.FromSeconds(35);

        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
    }

    [Fact]
    public void ActiveTime_BeforeStart_ClampsToZero()
    {
        var (clock, time) = Create();
        time.Now = TimeSpan.FromSeconds(10);
        clock.Start();

        Assert.Equal(TimeSpan.Zero, clock.ActiveTime(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Pause_BeforeStart_Throws()
    {
        var (clock, _) = Create();
        Assert.Throws<InvalidOperationException>(() => clock.Pause());
    }

    [Fact]
    public void Resume_WithoutPause_Throws()
    {
        var (clock, _) = Create();
        clock.Start();
        Assert.Throws<InvalidOperationException>(() => clock.Resume());
    }

    [Fact]
    public void DoublePause_Throws()
    {
        var (clock, _) = Create();
        clock.Start();
        clock.Pause();
        Assert.Throws<InvalidOperationException>(() => clock.Pause());
    }

    [Fact]
    public void ProductionClock_RunsOnQpc()
    {
        var clock = new RecordingClock();
        clock.Start();
        var elapsed = clock.Elapsed;
        Assert.True(elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>A machine that can sleep: QPC keeps counting while asleep, awake time doesn't.</summary>
    private sealed class SleepyTime
    {
        public TimeSpan Now;
        public TimeSpan Slept;

        public void Run(double seconds) => Now += TimeSpan.FromSeconds(seconds);

        public void Sleep(double seconds)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Slept += TimeSpan.FromSeconds(seconds);
        }
    }

    private static (RecordingClock Clock, SleepyTime Time) CreateSleepy()
    {
        var time = new SleepyTime { Slept = TimeSpan.FromHours(5) }; // slept before recording
        return (new RecordingClock(() => time.Now, () => time.Slept), time);
    }

    [Fact]
    public void SleepWhileRecording_IsLeftOut_RecordingContinuesWhereItLeftOff()
    {
        var (clock, time) = CreateSleepy();
        clock.Start();
        time.Run(60);
        time.Sleep(3600); // lid closed for an hour
        time.Run(30);

        Assert.Equal(TimeSpan.FromSeconds(90), clock.Elapsed);
        Assert.Equal(TimeSpan.FromHours(1), clock.SleptTime);
    }

    [Fact]
    public void SleepWhilePaused_IsNotSubtractedTwice()
    {
        var (clock, time) = CreateSleepy();
        clock.Start();
        time.Run(10);
        clock.Pause();
        time.Run(5);
        time.Sleep(600);
        time.Run(5);
        clock.Resume();
        time.Run(20);

        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed);
    }

    [Fact]
    public void FrameStampedJustBeforeSleep_KeepsItsTime_AfterWake()
    {
        var (clock, time) = CreateSleepy();
        clock.Start();
        time.Run(10);
        var frameTime = time.Now;
        Assert.Equal(TimeSpan.FromSeconds(10), clock.ActiveTime(frameTime));
        time.Sleep(600);
        time.Run(1);

        Assert.Equal(TimeSpan.FromSeconds(11), clock.ActiveTime(time.Now));
    }

    [Fact]
    public void SleepSamplingNoise_IsIgnored()
    {
        var (clock, time) = CreateSleepy();
        clock.Start();
        time.Run(10);
        time.Slept += TimeSpan.FromMilliseconds(3); // measurement noise, not a sleep

        Assert.Equal(TimeSpan.Zero, clock.SleptTime);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Elapsed);
    }

    [Fact]
    public void SystemSleptTime_IsStableWhileAwake()
    {
        var a = RecordingClock.SystemSleptTime();
        Thread.Sleep(200);
        var b = RecordingClock.SystemSleptTime();

        Assert.True(a >= TimeSpan.Zero);
        Assert.InRange((b - a).TotalMilliseconds, -20, 20); // awake: no sleep accrues (15.6 ms ticks)
    }
}
