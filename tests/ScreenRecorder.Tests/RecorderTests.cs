using NAudio.CoreAudioApi;
using Windows.Graphics.DirectX.Direct3D11;
using ScreenRecorder.Audio;
using ScreenRecorder.Capture;
using ScreenRecorder.Encoding;
using ScreenRecorder.Recording;

namespace ScreenRecorder.Tests;

public sealed class RecorderTests : IDisposable
{
    private readonly string _folder;
    private readonly List<Recorder> _recorders = new();

    public RecorderTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "sr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        foreach (var recorder in _recorders)
            recorder.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeEncoder(string partPath) : IMediaEncoder
    {
        public int VideoPushes;
        public int AudioPushes;
        public bool VideoComplete;
        public bool AudioComplete;

        public bool TryPushVideo(IDirect3DSurface surface, TimeSpan timestamp, Action released)
        {
            VideoPushes++;
            released();
            return true;
        }

        public bool IsWaitingForVideo => false;
        public readonly List<byte[]> Audio = new();

        public void PushAudio(byte[] pcm16, TimeSpan timestamp)
        {
            AudioPushes++;
            lock (Audio)
                Audio.Add(pcm16);
        }
        public void CompleteVideo() => VideoComplete = true;
        public void CompleteAudio() => AudioComplete = true;

        /// <summary>When set, FinishAsync waits on it (a slow save).</summary>
        public TaskCompletionSource? FinishGate;
        public long BytesWritten;
        public EncoderProgress Progress => new(default, default, false, false, BytesWritten);
        public bool HasEnded { get; set; }

        public async Task FinishAsync()
        {
            if (FinishGate is not null)
                await FinishGate.Task;
            File.WriteAllBytes(partPath, [1, 2, 3]);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeVideo : IVideoPipeline
    {
        public event Action<string>? Lost;
        public event Action<Exception>? Failed;

        public bool Paused { get; set; }
        public long DroppedFrames => 0;
        public int Heartbeats;
        public int Latests;

        public void Start(CaptureTarget target, RecordingClock clock, bool showCursor, int fps)
        {
        }

        public void EmitHeartbeat(TimeSpan activeTime) => Heartbeats++;
        public void EmitLatest(TimeSpan activeTime) => Latests++;
        public void Stop() { }
        public void Dispose() { }

        public void RaiseLost(string reason) => Lost?.Invoke(reason);
        public void RaiseFailed(Exception ex) => Failed?.Invoke(ex);
    }

    private sealed class FakeAudio : IAudioCapture
    {
        public AudioSourceKind Kind => AudioSourceKind.Microphone;
        public event Action? SourceLost;
        public event Action<Exception>? Failed;
        public int Starts;

        public void Start() => Starts++;
        public void Restart(MMDevice device) { }
        public void Stop() { }
        public void Dispose() { }

        public void RaiseLost() => SourceLost?.Invoke();
        public void RaiseFailed(Exception ex) => Failed?.Invoke(ex);
    }

    private sealed class TestRecorder : Recorder
    {
        public TimeSpan FakeNow;
        public FakeVideo? Video;
        public FakeEncoder? Encoder;
        public TaskCompletionSource? FinishGate;
        public long? FreeBytes;
        public long? MaxFileBytes;

        public TestRecorder() : base(audioDevices: null)
        {
        }

        /// <summary>When set, encoder creation waits on it (a slow start).</summary>
        public TaskCompletionSource? StartGate;

        /// <summary>Runs while the recorder is starting, after the mixer exists.</summary>
        public Action<AudioMixer>? WhileStarting;

        private AudioMixer? _mixer;

        public void RunDiskCheck() => CheckFreeSpace();
        public void RunEncoderCheck() => CheckEncoder();
        public void RunFileSizeCheck() => CheckFileSizeLimit();

        protected override AudioMixer CreateMixer() => _mixer = base.CreateMixer();

        protected override long? GetFreeBytes(string folder) => FreeBytes;

        protected override long? GetMaxFileBytes(string folder) => MaxFileBytes;

        protected override RecordingClock CreateClock() => new(() => FakeNow);

        protected override async Task<IMediaEncoder> CreateEncoderAsync(
            string partPath, int cropWidth, int cropHeight,
            int outputWidth, int outputHeight, int fps, long bitrate, bool audioEnabled)
        {
            WhileStarting?.Invoke(_mixer!);
            Encoder = new FakeEncoder(partPath) { FinishGate = FinishGate };
            File.WriteAllBytes(partPath, []); // the real encoder creates the file here
            if (StartGate is not null)
                await StartGate.Task;
            return Encoder;
        }

        protected override IVideoPipeline CreateVideoPipeline(IVideoSink sink)
        {
            Video = new FakeVideo();
            return Video;
        }

        protected override IAudioCapture CreateAudioCapture(
            AudioSourceKind kind, MMDevice device, AudioMixer mixer) => new FakeAudio();
    }

    private static MonitorInfo FakeMonitor() => new(
        0, "\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true, 96, 1, "Display 1");

    private TestRecorder CreateRecorder()
    {
        var recorder = new TestRecorder();
        _recorders.Add(recorder);
        return recorder;
    }

    private RecordOptions VideoOnlyOptions() => new()
    {
        Fps = 30,
        Quality = VideoQuality.Medium,
        ShowCursor = true,
        SystemAudio = false,
        Microphone = false,
        MicDeviceId = null,
        SaveFolder = _folder,
    };

    [Fact]
    public async Task StartStop_VideoOnly_SavesFile()
    {
        var recorder = CreateRecorder();
        RecordResult? stopped = null;
        recorder.RecordingStopped += r => stopped = r;

        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        Assert.Equal(RecorderState.Recording, recorder.State);

        recorder.FakeNow = TimeSpan.FromSeconds(5);
        var result = await recorder.StopAsync();

        Assert.NotNull(result);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        Assert.Empty(result.Notices);
        Assert.Same(result, stopped);
        Assert.NotNull(recorder.Encoder);
        Assert.True(recorder.Encoder.VideoComplete);
    }

    [Fact]
    public async Task PauseResume_UpdatesElapsed_AndState()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        recorder.FakeNow = TimeSpan.FromSeconds(10);
        recorder.Pause();
        Assert.Equal(RecorderState.Paused, recorder.State);

        recorder.FakeNow = TimeSpan.FromSeconds(20);
        Assert.Equal(TimeSpan.FromSeconds(10), recorder.Elapsed); // frozen while paused

        recorder.Resume();
        Assert.Equal(RecorderState.Recording, recorder.State);
        recorder.FakeNow = TimeSpan.FromSeconds(25);
        Assert.Equal(TimeSpan.FromSeconds(15), recorder.Elapsed);

        var result = await recorder.StopAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), result!.Duration);
    }

    [Fact]
    public void Pause_WhileIdle_Throws()
    {
        var recorder = CreateRecorder();
        Assert.Throws<InvalidOperationException>(() => recorder.Pause());
    }

    [Fact]
    public void Resume_WhileIdle_Throws()
    {
        var recorder = CreateRecorder();
        Assert.Throws<InvalidOperationException>(() => recorder.Resume());
    }

    [Fact]
    public async Task Stop_WhileIdle_ReturnsNull()
    {
        var recorder = CreateRecorder();
        Assert.Null(await recorder.StopAsync());
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions()));

        await recorder.StopAsync();
    }

    [Fact]
    public async Task DoublePause_Throws_DoubleResume_Throws()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        recorder.Pause();
        Assert.Throws<InvalidOperationException>(() => recorder.Pause());
        recorder.Resume();
        Assert.Throws<InvalidOperationException>(() => recorder.Resume());

        await recorder.StopAsync();
    }

    [Fact]
    public async Task Start_WithAudioButNoService_AbortsToIdle()
    {
        var recorder = CreateRecorder();
        var options = new RecordOptions
        {
            Fps = 30,
            Quality = VideoQuality.Medium,
            ShowCursor = true,
            SystemAudio = true,
            Microphone = false,
            MicDeviceId = null,
            SaveFolder = _folder,
        };

        var ex = await Assert.ThrowsAsync<RecorderException>(() =>
            recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), options));

        Assert.Contains("output device", ex.Message);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public async Task MonitorLost_MidRecording_StopsAndSaves_WithNotice()
    {
        var recorder = CreateRecorder();
        var stopped = new TaskCompletionSource<RecordResult>();
        recorder.RecordingStopped += r => stopped.TrySetResult(r);
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(3);

        recorder.Video!.RaiseLost("The monitor was disconnected.");

        var completed = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(stopped.Task, completed);
        var result = await stopped.Task;
        Assert.True(File.Exists(result.FilePath));
        Assert.Single(result.Notices);
        Assert.Contains("disconnected", result.Notices[0]);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public async Task BackToBack_Recordings_Work()
    {
        var recorder = CreateRecorder();
        var target = CaptureTarget.FullScreen(FakeMonitor());

        await recorder.StartAsync(target, VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(1);
        var first = await recorder.StopAsync();
        Assert.NotNull(first);

        await recorder.StartAsync(target, VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(3);
        var second = await recorder.StopAsync();

        Assert.NotNull(second);
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.True(File.Exists(second.FilePath));
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public async Task SetMicrophoneMuted_WithoutMicAtStart_IsNoOp()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        recorder.SetMicrophoneMuted(true);

        Assert.False(recorder.MicrophoneMuted);
        recorder.FakeNow = TimeSpan.FromSeconds(1);
        Assert.NotNull(await recorder.StopAsync());
        Assert.False(recorder.MicrophoneMuted);
    }

    [Fact]
    public async Task Stop_RaisesFinalizing_ThenProgress_ThenStopped()
    {
        // Created off xUnit's SynchronizationContext, which runs posts concurrently:
        // with none, events are raised inline in order, as the FIFO UI dispatcher
        // delivers them in the app.
        var recorder = await Task.Run(CreateRecorder);
        var events = new List<string>();
        var progress = new List<SaveProgress>();
        recorder.FinalizingStarted += d => events.Add($"finalizing {d.TotalSeconds}");
        recorder.SaveProgressChanged += p =>
        {
            events.Add("progress");
            progress.Add(p);
        };
        recorder.RecordingStopped += _ => events.Add("stopped");
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(4);

        Assert.NotNull(await recorder.StopAsync());

        Assert.Equal("finalizing 4", events[0]);
        Assert.Equal("stopped", events[^1]);
        Assert.All(events.Skip(1).SkipLast(1), e => Assert.Equal("progress", e));
        Assert.Equal(SaveStage.Done, progress[^1].Stage);
        Assert.Equal(1.0, progress[^1].Fraction);
        Assert.Equal(progress.Select(p => p.Fraction).Order(), progress.Select(p => p.Fraction));
    }

    [Fact]
    public async Task SlowSave_StaysFinalizing_WithFrozenElapsed_AndReportsProgress()
    {
        var recorder = CreateRecorder();
        recorder.FinishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<SaveProgress>();
        RecordResult? stopped = null;
        recorder.SaveProgressChanged += p => { lock (progress) progress.Add(p); };
        recorder.RecordingStopped += r => stopped = r;
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(6);

        var stop = recorder.StopAsync();
        recorder.FakeNow = TimeSpan.FromSeconds(60); // wall time moves on while saving
        await Task.Delay(500);

        Assert.False(stop.IsCompleted);
        Assert.Equal(RecorderState.Finalizing, recorder.State);
        Assert.Equal(TimeSpan.FromSeconds(6), recorder.Elapsed);
        Assert.Null(stopped); // never "saved" before the file is done
        lock (progress)
            Assert.True(progress.Count >= 2, $"expected periodic updates, got {progress.Count}");
        Assert.Same(stop, recorder.StopAsync()); // a second Stop joins the save

        recorder.Encoder!.FinishGate!.SetResult();
        var result = await stop;

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Duration);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public async Task LowDisk_StopsAndSaves_WithNotice()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(2);

        recorder.FreeBytes = Recorder.LowDiskStopBytes + 1;
        recorder.RunDiskCheck();
        Assert.Equal(RecorderState.Recording, recorder.State);

        recorder.FreeBytes = Recorder.LowDiskStopBytes - 1;
        recorder.RunDiskCheck();
        var result = await recorder.StopAsync(); // joins the stop the guard started

        Assert.NotNull(result);
        Assert.True(File.Exists(result.FilePath));
        Assert.Single(result.Notices);
        Assert.Contains("almost full", result.Notices[0]);
    }

    [Fact]
    public async Task Fat32Drive_StopsBeforeTheFileSizeLimit_WithNotice()
    {
        var recorder = CreateRecorder();
        recorder.MaxFileBytes = uint.MaxValue;
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromSeconds(2);

        recorder.Encoder!.BytesWritten = uint.MaxValue - Recorder.FileLimitMarginBytes - 1;
        recorder.RunFileSizeCheck();
        Assert.Equal(RecorderState.Recording, recorder.State);

        recorder.Encoder.BytesWritten = uint.MaxValue - Recorder.FileLimitMarginBytes;
        recorder.RunFileSizeCheck();
        var result = await recorder.StopAsync(); // joins the stop the guard started

        Assert.NotNull(result);
        Assert.True(File.Exists(result.FilePath));
        Assert.Contains("4 GB", Assert.Single(result.Notices));
    }

    [Fact]
    public async Task NoFileSizeLimit_KeepsRecording_PastFourGigabytes()
    {
        var recorder = CreateRecorder(); // MaxFileBytes null: NTFS / exFAT
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        recorder.Encoder!.BytesWritten = 5L * 1024 * 1024 * 1024;
        recorder.RunFileSizeCheck();

        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task UnknownFreeSpace_KeepsRecording()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        recorder.FreeBytes = null;
        recorder.RunDiskCheck();

        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task Stop_IsIdempotent()
    {
        var recorder = CreateRecorder();
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        var first = recorder.StopAsync();
        var second = recorder.StopAsync();

        Assert.Same(first, second);
        Assert.NotNull(await first);
    }

    [Fact]
    public async Task AudioBufferedWhileStarting_IsDropped()
    {
        // Sources open first and feed the mixer for the second or two the encoder
        // and capture take to start. Kept, that audio plays ~200 ms late.
        var recorder = CreateRecorder();
        recorder.WhileStarting = mixer =>
        {
            mixer.MicEnabled = true;
            var loud = Enumerable.Repeat(0.5f, AudioMixer.SampleRate / 5 * AudioMixer.Channels).ToArray();
            mixer.FeedMic(loud);
        };
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());
        recorder.FakeNow = TimeSpan.FromMilliseconds(100);

        Assert.NotNull(await recorder.StopAsync());

        List<byte[]> audio;
        lock (recorder.Encoder!.Audio)
            audio = recorder.Encoder.Audio.ToList();
        Assert.NotEmpty(audio);
        Assert.All(audio, chunk => Assert.All(chunk, b => Assert.Equal(0, b)));
    }

    [Fact]
    public async Task EncoderDiesMidRecording_StopsAndReportsFailure()
    {
        var recorder = CreateRecorder();
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RecordingFailed += m => failed.TrySetResult(m);
        recorder.FinishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        recorder.RunEncoderCheck();
        Assert.Equal(RecorderState.Recording, recorder.State); // healthy: keeps going

        recorder.Encoder!.HasEnded = true;
        recorder.RunEncoderCheck();
        Assert.Equal(RecorderState.Finalizing, recorder.State);

        recorder.Encoder.FinishGate!.SetException(new IOException("The device is not ready."));
        Assert.Null(await recorder.StopAsync());
        Assert.Contains("not ready", await failed.Task);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public async Task DisposeWhileStarting_AbortsTheStart_AndRemovesThePartFile()
    {
        var recorder = CreateRecorder();
        recorder.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = recorder.StartAsync(CaptureTarget.FullScreen(FakeMonitor()), VideoOnlyOptions());

        Assert.Null(await recorder.StopAsync()); // can't stop a half-built recording
        recorder.Dispose();
        recorder.StartGate.SetResult();

        await Assert.ThrowsAsync<RecorderException>(() => start);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Empty(Directory.GetFiles(_folder));
    }
}
