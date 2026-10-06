using System.Globalization;
using System.IO;
using NAudio.CoreAudioApi;
using Windows.Win32;
using ScreenRecorder.Audio;
using ScreenRecorder.Capture;
using ScreenRecorder.Encoding;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder.Recording;

public enum RecorderState
{
    Idle,
    Recording,
    Paused,
    Finalizing,
}

/// <summary>Pure state machine (PLAN §6.1). Invalid transitions throw.</summary>
public sealed class RecorderStateMachine
{
    public RecorderState State { get; private set; } = RecorderState.Idle;

    public void OnStart() => Move(RecorderState.Idle, RecorderState.Recording, "start");
    public void OnAbort() => Move(RecorderState.Recording, RecorderState.Idle, "abort");
    public void OnPause() => Move(RecorderState.Recording, RecorderState.Paused, "pause");
    public void OnResume() => Move(RecorderState.Paused, RecorderState.Recording, "resume");

    public void OnBeginStop()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
            throw new InvalidOperationException($"Cannot stop while {State}.");
        State = RecorderState.Finalizing;
    }

    public void OnStopped() => Move(RecorderState.Finalizing, RecorderState.Idle, "finish");

    private void Move(RecorderState expected, RecorderState next, string action)
    {
        if (State != expected)
            throw new InvalidOperationException($"Cannot {action} while {State}.");
        State = next;
    }
}

/// <summary>Options for one recording (locked while recording).</summary>
public sealed class RecordOptions
{
    public required int Fps { get; init; }
    public required VideoQuality Quality { get; init; }
    public required bool ShowCursor { get; init; }
    public required bool SystemAudio { get; init; }
    public required bool Microphone { get; init; }
    public required string? MicDeviceId { get; init; }
    public required string SaveFolder { get; init; }
}

/// <summary>Outcome of a finished recording.</summary>
public sealed record RecordResult(string FilePath, TimeSpan Duration, IReadOnlyList<string> Notices);

/// <summary>User-facing recording error. <see cref="IsPrivacyError"/> shows an "Open settings" button.</summary>
public sealed class RecorderException : Exception
{
    public RecorderException(
        string message, bool isPrivacyError = false, bool isCaptureUnsupported = false,
        Exception? inner = null)
        : base(message, inner)
    {
        IsPrivacyError = isPrivacyError;
        IsCaptureUnsupported = isCaptureUnsupported;
    }

    public bool IsPrivacyError { get; }
    public bool IsCaptureUnsupported { get; }
}

/// <summary>
/// Recording orchestrator (PLAN §6): owns the state machine, clock, mixer timer,
/// video/audio pipelines and encoder. All events are raised on the thread that
/// created the recorder (the UI thread).
/// </summary>
public class Recorder : IDisposable
{
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int AudclntEDeviceInUse = unchecked((int)0x8889000A);

    /// <summary>
    /// Recording stops (and saves) when the save drive has less than this free.
    /// Leaves room for the MP4 index (~1.5 MB per hour) and the final flush.
    /// </summary>
    public const long LowDiskStopBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Room left below a file-system's file-size limit when recording stops:
    /// covers up to one check interval at the top bitrate (~25 MB), the
    /// encoder's buffered output and the MP4 index.
    /// </summary>
    public const long FileLimitMarginBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly RecorderStateMachine _sm = new();
    private readonly AudioDeviceService? _audioDevices;
    private readonly SynchronizationContext? _sync;
    private readonly object _gate = new();
    // Held by each mixer tick; stopping takes it so no tick is mid-push when the
    // final audio is flushed (a late tick would push out of order or be lost).
    private readonly object _tickGate = new();
    private readonly List<string> _notices = new();

    private RecordingClock? _clock;
    private AudioMixer? _mixer;
    private IMediaEncoder? _encoder;
    private IVideoPipeline? _video;
    private IAudioCapture? _loopback;
    private IAudioCapture? _mic;
    private bool _micOnAtStart;
    private bool _micMuted;
    private bool _audioOn;
    private Timer? _mixerTimer;
    private Timer? _diskTimer;
    private Task<RecordResult?>? _stopTask;
    private TimeSpan? _stopTime;
    private string? _saveFolder;
    private long? _maxFileBytes;
    private string? _partPath;
    private string? _finalPath;
    private bool _starting;
    private bool _disposed;

    public Recorder(AudioDeviceService? audioDevices = null)
    {
        _audioDevices = audioDevices;
        _sync = SynchronizationContext.Current;
        if (_audioDevices is not null)
            _audioDevices.DefaultRenderDeviceChanged += OnDefaultRenderChanged;
    }

    public RecorderState State => _sm.State;
    public bool IsRecording => State is RecorderState.Recording or RecorderState.Paused;
    /// <summary>Recorded time; frozen at the stop time while finalizing.</summary>
    public TimeSpan Elapsed => _stopTime ?? _clock?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Whether the microphone is currently muted (it was on at start, then muted mid-recording).</summary>
    public bool MicrophoneMuted => _micMuted;

    public event Action? RecordingStarted;

    /// <summary>
    /// Stopping began (user, error or low disk): capture has ended and the file is
    /// being finished. Carries the recorded duration. Followed by
    /// <see cref="SaveProgressChanged"/> updates, then RecordingStopped or RecordingFailed.
    /// </summary>
    public event Action<TimeSpan>? FinalizingStarted;

    public event Action<SaveProgress>? SaveProgressChanged;
    public event Action<RecordResult>? RecordingStopped;
    public event Action<string>? RecordingFailed;

    /// <summary>
    /// Starts recording. Audio sources are opened first so a failure aborts before
    /// anything visible changes. Throws <see cref="RecorderException"/> with a
    /// user-facing message; the state rolls back to Idle.
    /// </summary>
    public async Task StartAsync(CaptureTarget target, RecordOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_stopTask is { IsCompleted: false })
                throw new InvalidOperationException("The previous recording is still finalizing.");
            _stopTask = null; // clear any settled stop; only an in-flight one blocks
        }
        _sm.OnStart();
        _starting = true;
        try
        {
            Directory.CreateDirectory(options.SaveFolder);
            EnsureWritable(options.SaveFolder);
            _finalPath = NextFileName(options.SaveFolder);
            _partPath = _finalPath + ".part";

            _mixer = CreateMixer();
            _mixer.SystemEnabled = options.SystemAudio;
            _mixer.MicEnabled = options.Microphone;
            _micOnAtStart = options.Microphone;
            _micMuted = false;
            if (options.SystemAudio)
                _loopback = StartOne(AudioSourceKind.Loopback, ResolveLoopbackDevice());
            if (options.Microphone)
                _mic = StartOne(AudioSourceKind.Microphone, ResolveMicDevice(options.MicDeviceId));

            var audioOn = options.SystemAudio || options.Microphone;
            _audioOn = audioOn;
            _saveFolder = options.SaveFolder;
            _maxFileBytes = GetMaxFileBytes(options.SaveFolder);
            var (outW, outH) = EncodingMath.FitInside(target.Width, target.Height);
            var bitrate = EncodingMath.ApplyQuality(
                EncodingMath.ComputeBitrate(outW, outH, options.Fps), options.Quality);
            try
            {
                _encoder = await CreateEncoderAsync(_partPath, target.Width, target.Height,
                    outW, outH, options.Fps, bitrate, audioOn);
            }
            catch (Exception ex)
            {
                throw new RecorderException("The encoder failed to start: " + ex.Message, inner: ex);
            }
            // The only await: the app may have closed meanwhile (Dispose skips a
            // start in progress and leaves the cleanup to it).
            if (_disposed)
                throw new RecorderException("The app is closing.");

            _clock = CreateClock();
            _video = CreateVideoPipeline(_encoder);
            _video.Lost += OnVideoLost;
            _video.Failed += OnVideoFailed;
            try
            {
                _video.Start(target, _clock, options.ShowCursor, options.Fps);
            }
            catch (Exception ex)
            {
                // Only a missing API disables recording for good; anything else
                // (a stale monitor handle, a lost GPU) may work on the next try.
                if (!IsCaptureSupported())
                    throw new RecorderException(
                        "Screen capture isn't available on this system. " +
                        "Some VMs and remote sessions don't support it.",
                        isCaptureUnsupported: true, inner: ex);
                throw new RecorderException("Could not start screen capture: " + ex.Message, inner: ex);
            }

            _clock.Start();
            // The sources have been feeding the mixer since they opened (up to a
            // couple of seconds ago). That audio predates the video: kept, it
            // would make the whole recording's sound ~200 ms late.
            _mixer.ResetForStart();
            _mixerTimer = new Timer(MixerTick, null, 0, 20);
            _diskTimer = new Timer(_ => CheckHealth(), null, DiskCheckInterval, DiskCheckInterval);
            SetSleepBlocked(true);
            Log.Info($"Recording started: {target.Description}, {options.Fps} fps, {options.Quality}, " +
                     $"cursor={options.ShowCursor}, system={options.SystemAudio}, mic={options.Microphone}.");
            Raise(RecordingStarted);
        }
        catch
        {
            CleanupAfterFailedStart();
            _sm.OnAbort();
            throw;
        }
        finally
        {
            _starting = false;
        }
    }

    public void Pause()
    {
        _sm.OnPause();
        _clock!.Pause();
        _mixer!.IsPaused = true;
        if (_video is not null)
            _video.Paused = true;
        Log.Info("Paused.");
    }

    public void Resume()
    {
        _sm.OnResume();
        _clock!.Resume();
        _mixer!.Clear();
        _mixer.IsPaused = false;
        if (_video is not null)
        {
            _video.Paused = false;
            // The screen may have changed while paused: enqueue a fresh frame now.
            _video.EmitLatest(_clock.ActiveTime(_clock.Now));
        }
        Log.Info("Resumed.");
    }

    /// <summary>
    /// Mutes or unmutes the microphone mid-recording. Applies only when the mic
    /// was enabled at start and a recording is in progress; otherwise a no-op.
    /// Capture keeps running while muted, so unmuting resumes seamlessly.
    /// </summary>
    public void SetMicrophoneMuted(bool muted)
    {
        if (_mixer is null || !_micOnAtStart || !IsRecording)
            return;
        _micMuted = muted;
        _mixer.MicEnabled = !muted;
        Log.Info(muted ? "Microphone muted." : "Microphone unmuted.");
    }

    /// <summary>
    /// Stops and finalizes. Safe to call twice: concurrent calls coalesce onto the
    /// in-flight stop, and later calls replay its result. Returns null when idle
    /// (or when finalizing failed).
    /// </summary>
    public Task<RecordResult?> StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null)
                return _stopTask;
            if (_sm.State == RecorderState.Idle)
                return Task.FromResult<RecordResult?>(null); // never stored
            if (_starting)
            {
                // Half-built (no clock or encoder yet): stopping now would tear it
                // down under StartAsync. Callers wait for the start to finish first.
                Log.Warn("Stop ignored: the recording is still starting.");
                return Task.FromResult<RecordResult?>(null);
            }
            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task<RecordResult?> StopCoreAsync()
    {
        try
        {
            _sm.OnBeginStop();
        }
        catch
        {
            return null;
        }

        try
        {
            var clock = _clock!;
            var stopTime = clock.ActiveTime(clock.Now);
            _stopTime = stopTime;
            var slept = clock.SleptTime;
            if (slept > TimeSpan.Zero)
            {
                Log.Info($"The PC slept for {slept.TotalMinutes:F1} min during recording; left out of the video.");
                lock (_gate)
                {
                    _notices.Add("The PC went to sleep during recording; the recording continued after it woke.");
                }
            }

            lock (_tickGate)
            {
                // Waits out a tick in flight; later ticks see Finalizing and return.
                _mixerTimer?.Dispose();
                _mixerTimer = null;
            }
            _diskTimer?.Dispose();
            _diskTimer = null;

            // Tell the UI first: the saving screen replaces the recording bar now,
            // not after the file is done.
            Raise(() => FinalizingStarted?.Invoke(stopTime));

            // Final frame at the stop time, so a static ending doesn't shorten the video.
            try { _video?.EmitLatest(stopTime); }
            catch (Exception ex) { Log.Warn("Final frame failed: " + ex.Message); }

            // Run the mixer up to the stop time, so audio ends at the same point.
            if (_mixer is not null && _encoder is not null)
            {
                foreach (var chunk in _mixer.Pull(stopTime, flush: true))
                    PushChunk(_encoder, chunk);
                Log.Info($"Audio: {_mixer.SamplesEmitted} samples, " +
                         $"underflows={_mixer.UnderflowSamples}, overflows={_mixer.OverflowSamplesDropped}, " +
                         $"drift drops={_mixer.DriftSamplesDropped}.");
            }

            // Stop capture callbacks, but keep D3D textures alive: the encoder may
            // still hold samples referencing them until FinishAsync completes.
            try { _video?.Stop(); }
            catch (Exception ex) { Log.Warn("Video stop failed: " + ex.Message); }
            StopAudioSources();

            var encoder = _encoder!;
            encoder.CompleteVideo();
            encoder.CompleteAudio();
            var tracker = new SaveProgressTracker(encoder.Progress, stopTime, _audioOn);
            var saveWatch = System.Diagnostics.Stopwatch.StartNew();
            var finish = encoder.FinishAsync();
            // Poll instead of blocking: the UI gets a steady heartbeat even when
            // a long backlog or a slow drive makes the encoder take a while.
            while (!finish.IsCompleted)
            {
                var progress = tracker.Update(encoder.Progress);
                Raise(() => SaveProgressChanged?.Invoke(progress));
                await Task.WhenAny(finish, Task.Delay(SaveProgressInterval));
            }
            await finish;

            var bytes = encoder.Progress.BytesWritten;
            var finishing = tracker.Finishing(bytes);
            Raise(() => SaveProgressChanged?.Invoke(finishing));
            File.Move(_partPath!, _finalPath!, overwrite: false);
            var done = tracker.Done(bytes);
            Raise(() => SaveProgressChanged?.Invoke(done));
            Log.Info($"Recording saved: {_finalPath} ({stopTime.TotalSeconds:F1} s, " +
                     $"{bytes / 1_000_000.0:F1} MB, finalized in {saveWatch.ElapsedMilliseconds} ms).");
            var result = new RecordResult(_finalPath!, stopTime, _notices.ToList());
            _sm.OnStopped();
            Raise(() => RecordingStopped?.Invoke(result));
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("Stop/finalize failed: " + ex);
            KeepNonEmptyPart();
            _sm.OnStopped();
            var message = "Could not save the recording: " + ex.Message;
            Raise(() => RecordingFailed?.Invoke(message));
            return null;
        }
        finally
        {
            SetSleepBlocked(false);
            // Now safe: the encoder is done with all video textures.
            // (_stopTask is intentionally left for replay; StartAsync clears it.
            // Clearing here would race a synchronously-completing stop, whose
            // assignment in StopAsync runs after this finally.)
            CleanupAfterStop();
        }
    }

    // ---- private helpers ----

    private MMDevice ResolveLoopbackDevice() =>
        _audioDevices?.GetDefaultRenderDevice()
        ?? throw new RecorderException("No output device found for system sound.");

    private MMDevice ResolveMicDevice(string? id) =>
        _audioDevices?.GetMicDevice(id)
        ?? throw new RecorderException("No microphone found.");

    private IAudioCapture StartOne(AudioSourceKind kind, MMDevice device)
    {
        var capture = CreateAudioCapture(kind, device, _mixer!);
        if (kind == AudioSourceKind.Microphone)
        {
            capture.SourceLost += OnMicLost;
            capture.Failed += OnMicFailed;
        }
        else
        {
            capture.SourceLost += OnLoopbackLost;
            capture.Failed += OnLoopbackFailed;
        }
        try
        {
            capture.Start();
        }
        catch (Exception ex)
        {
            capture.Dispose();
            throw MapAudioStartError(kind, ex);
        }
        return capture;
    }

    private static RecorderException MapAudioStartError(AudioSourceKind kind, Exception ex)
    {
        var what = kind == AudioSourceKind.Microphone ? "The microphone" : "System sound";
        if (kind == AudioSourceKind.Microphone && ex.HResult == EAccessDenied)
            return new RecorderException(
                "Windows is blocking microphone access. Allow desktop apps to use the " +
                "microphone, then try again.", isPrivacyError: true, inner: ex);
        if (ex.HResult == AudclntEDeviceInUse)
            return new RecorderException(
                $"{what} is in use by another app and can't be opened right now.", inner: ex);
        return new RecorderException($"Could not start {what.ToLowerInvariant()}: {ex.Message}", inner: ex);
    }

    private void MixerTick(object? state)
    {
        lock (_tickGate)
        {
            try
            {
                var clock = _clock;
                var mixer = _mixer;
                var encoder = _encoder;
                if (clock is null || mixer is null || encoder is null)
                    return;
                if (_sm.State != RecorderState.Recording)
                    return;
                var active = clock.ActiveTime(clock.Now);
                foreach (var chunk in mixer.Pull(active))
                    PushChunk(encoder, chunk);
                _video?.EmitHeartbeat(active);
            }
            catch (Exception ex)
            {
                Log.Warn("Mixer tick failed: " + ex.Message);
            }
        }
    }

    private static void PushChunk(IMediaEncoder encoder, AudioChunk chunk)
    {
        var bytes = new byte[chunk.Pcm.Length * 2];
        Buffer.BlockCopy(chunk.Pcm, 0, bytes, 0, bytes.Length);
        encoder.PushAudio(bytes, chunk.Timestamp);
    }

    private void OnVideoLost(string reason)
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
            return;
        Log.Warn("Video lost: " + reason);
        lock (_gate)
        {
            _notices.Add(reason + " Saved what was recorded.");
        }
        StopFromCallback();
    }

    private void OnVideoFailed(Exception ex)
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
            return;
        Log.Error("Video failed: " + ex);
        lock (_gate)
        {
            _notices.Add("Video capture failed. Saved what was recorded.");
        }
        StopFromCallback();
    }

    /// <summary>
    /// Stops from a capture callback without running the stop on its thread: the
    /// stop tears down the capture session, which must not happen inside that
    /// session's own frame callback.
    /// </summary>
    private void StopFromCallback() => _ = Task.Run(StopAsync);

    private void OnMicLost()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
            return;
        Log.Warn("Microphone disconnected mid-recording; continuing with silence.");
        lock (_gate)
        {
            _notices.Add("The microphone was disconnected — continuing without it.");
        }
    }

    private void OnMicFailed(Exception ex)
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
            return;
        Log.Warn("Microphone failed mid-recording; continuing with silence: " + ex.Message);
        lock (_gate)
        {
            _notices.Add("Microphone error — continuing without it.");
        }
    }

    private void OnLoopbackLost() => ReopenLoopback();
    private void OnLoopbackFailed(Exception ex)
    {
        Log.Warn("System sound failed: " + ex.Message);
        ReopenLoopback();
    }

    private void OnDefaultRenderChanged()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused) || _loopback is null)
            return;
        Log.Info("Default output device changed; reopening system-sound capture.");
        ReopenLoopback();
    }

    /// <summary>Reopens loopback on the current default output; gaps become silence.</summary>
    private void ReopenLoopback()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused) || _loopback is null)
            return;
        var device = _audioDevices?.GetDefaultRenderDevice();
        if (device is null)
        {
            lock (_gate)
            {
                _notices.Add("System sound was lost — continuing without it.");
            }
            return;
        }
        _loopback.Restart(device); // failures raise Failed → notice, recording continues
    }

    /// <summary>Periodic checks while recording (every <see cref="DiskCheckInterval"/>).</summary>
    private void CheckHealth()
    {
        CheckEncoder();
        CheckFreeSpace();
        CheckFileSizeLimit();
    }

    /// <summary>
    /// Encoder guard: if the transcode dies mid-recording (GPU reset, the save
    /// drive unplugged, …) nothing more reaches the file. Stop now and report it,
    /// rather than let the user record on and only learn at Stop.
    /// </summary>
    protected void CheckEncoder()
    {
        try
        {
            if (_encoder is not { HasEnded: true }
                || State is not (RecorderState.Recording or RecorderState.Paused))
                return;
            Log.Error("The encoder stopped mid-recording; stopping.");
            _ = StopAsync(); // FinishAsync surfaces the encoder's error
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Low-disk guard (PLAN §6.6). If the drive fills mid-write the transcode
    /// fails before the MP4 index is written, and the whole recording is
    /// unplayable. Stopping while there is still room saves all of it.
    /// </summary>
    protected void CheckFreeSpace()
    {
        try
        {
            var folder = _saveFolder;
            if (folder is null || State is not (RecorderState.Recording or RecorderState.Paused))
                return;
            var free = GetFreeBytes(folder);
            if (free is null || free >= LowDiskStopBytes)
                return;
            Log.Warn($"Save drive almost full ({free / (1024 * 1024)} MB free); stopping to save the recording.");
            lock (_gate)
            {
                _notices.Add("The save drive is almost full, so recording stopped early.");
            }
            _ = StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Free-space check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// File-size guard (PLAN §6.6). FAT32 can't hold a file of 4 GB or more
    /// (~80 min at 1080p Medium). Writing past that fails the transcode before
    /// the MP4 index is written, losing the whole recording, so stop and save
    /// <see cref="FileLimitMarginBytes"/> before the limit.
    /// </summary>
    protected void CheckFileSizeLimit()
    {
        try
        {
            var limit = _maxFileBytes;
            var encoder = _encoder;
            if (limit is null || encoder is null
                || State is not (RecorderState.Recording or RecorderState.Paused))
                return;
            if (encoder.Progress.BytesWritten < limit - FileLimitMarginBytes)
                return;
            Log.Warn($"Recording reached the save drive's {limit / (1024 * 1024)} MB file-size limit; stopping.");
            lock (_gate)
            {
                _notices.Add("The save drive (FAT32) can't hold files over 4 GB, so recording stopped " +
                             "early. Save to an NTFS or exFAT drive for longer recordings.");
            }
            _ = StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("File-size check failed: " + ex.Message);
        }
    }

    private void StopAudioSources()
    {
        try { _loopback?.Stop(); } catch { /* best effort */ }
        try { _mic?.Stop(); } catch { /* best effort */ }
    }

    private void CleanupAfterFailedStart()
    {
        _mixerTimer?.Dispose();
        _mixerTimer = null;
        _diskTimer?.Dispose();
        _diskTimer = null;
        _video?.Dispose();
        _video = null;
        _loopback?.Dispose();
        _loopback = null;
        _mic?.Dispose();
        _mic = null;
        _encoder?.Dispose();
        _encoder = null;
        _mixer = null;
        _clock = null;
        _micOnAtStart = false;
        _micMuted = false;
        _audioOn = false;
        _saveFolder = null;
        _maxFileBytes = null;
        SetSleepBlocked(false);
        // A failed start leaves at most an empty/unplayable .part file: remove it.
        try
        {
            if (_partPath is not null && File.Exists(_partPath))
                File.Delete(_partPath);
        }
        catch { /* best effort */ }
        _partPath = null;
        _finalPath = null;
    }

    private void CleanupAfterStop()
    {
        _mixerTimer?.Dispose();
        _mixerTimer = null;
        _diskTimer?.Dispose();
        _diskTimer = null;
        _video?.Dispose();
        _video = null;
        _loopback?.Dispose();
        _loopback = null;
        _mic?.Dispose();
        _mic = null;
        _encoder?.Dispose();
        _encoder = null;
        _mixer = null;
        _clock = null;
        _micOnAtStart = false;
        _micMuted = false;
        _audioOn = false;
        _saveFolder = null;
        _maxFileBytes = null;
        _stopTime = null;
        _partPath = null;
        _finalPath = null;
        lock (_gate)
        {
            _notices.Clear();
        }
    }

    private void KeepNonEmptyPart()
    {
        try
        {
            if (_partPath is not null && File.Exists(_partPath)
                && new FileInfo(_partPath).Length == 0)
                File.Delete(_partPath);
        }
        catch { /* best effort */ }
    }

    private static void EnsureWritable(string folder)
    {
        var probe = Path.Combine(folder, ".write-test.tmp");
        try
        {
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new RecorderException(
                "The save folder isn't writable. Choose another folder.", inner: ex);
        }
    }

    private static string NextFileName(string folder)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(folder, $"Recording {stamp}.mp4");
        var n = 2;
        while (File.Exists(candidate) || File.Exists(candidate + ".part"))
            candidate = Path.Combine(folder, $"Recording {stamp} ({n++}).mp4");
        return candidate;
    }

    private static bool IsCaptureSupported()
    {
        try
        {
            return Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// SetThreadExecutionState is per thread, so the request is always made and
    /// cleared on the UI thread: a stop that ends on a pool thread (low disk,
    /// monitor lost) would otherwise leave the PC unable to sleep until exit.
    /// </summary>
    private void SetSleepBlocked(bool blocked)
    {
        if (_sync is not null && SynchronizationContext.Current != _sync)
            _sync.Post(_ => PreventSleep(blocked), null);
        else
            PreventSleep(blocked);
    }

    private static void PreventSleep(bool prevent)
    {
        PInvoke.SetThreadExecutionState(prevent
            ? Windows.Win32.System.Power.EXECUTION_STATE.ES_CONTINUOUS
              | Windows.Win32.System.Power.EXECUTION_STATE.ES_SYSTEM_REQUIRED
              | Windows.Win32.System.Power.EXECUTION_STATE.ES_DISPLAY_REQUIRED
            : Windows.Win32.System.Power.EXECUTION_STATE.ES_CONTINUOUS);
    }

    private void Raise(Action? handler)
    {
        if (handler is null)
            return;
        if (_sync is not null)
            _sync.Post(_ => handler(), null);
        else
            handler();
    }

    // Factories: production defaults; tests override with fakes.

    protected virtual RecordingClock CreateClock() => new();
    protected virtual AudioMixer CreateMixer() => new();

    protected virtual Task<IMediaEncoder> CreateEncoderAsync(
        string partPath, int cropWidth, int cropHeight,
        int outputWidth, int outputHeight, int fps, long bitrate, bool audioEnabled) =>
        Mp4Encoder.CreateAsync(partPath, cropWidth, cropHeight,
            outputWidth, outputHeight, fps, bitrate, audioEnabled).ContinueWith<IMediaEncoder>(
                t => t.Result, TaskScheduler.Default);

    protected virtual IVideoPipeline CreateVideoPipeline(IVideoSink sink) => new FrameSource(sink);

    protected virtual IAudioCapture CreateAudioCapture(
        AudioSourceKind kind, MMDevice device, AudioMixer mixer) =>
        new AudioSource(kind, device, mixer);

    /// <summary>
    /// Largest file the save folder's file system can hold; null when there is
    /// no practical limit or it is unknown (no guard then).
    /// </summary>
    protected virtual long? GetMaxFileBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return null;
            return new DriveInfo(root).DriveFormat switch
            {
                "FAT32" => uint.MaxValue,  // 4 GB − 1 byte
                "FAT" => int.MaxValue,     // FAT16: 2 GB − 1 byte
                _ => null,                 // NTFS, exFAT, ReFS: no practical limit
            };
        }
        catch
        {
            return null; // e.g. a network share DriveInfo can't open
        }
    }

    /// <summary>Free bytes on the save folder's drive; null when unknown (no guard then).</summary>
    protected virtual long? GetFreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null; // e.g. a network share DriveInfo can't open
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_audioDevices is not null)
            _audioDevices.DefaultRenderDeviceChanged -= OnDefaultRenderChanged;
        if (_starting)
            return; // StartAsync sees _disposed after its await and cleans up
        if (State == RecorderState.Finalizing && _sync is not null && SynchronizationContext.Current == _sync)
        {
            // A save started from this thread continues on it: blocking here would
            // deadlock it. It cleans up itself when done. (The window waits for
            // the save before disposing, so this is only a safety net.)
            Log.Warn("Disposed while saving; the save finishes on its own.");
            return;
        }
        try
        {
            // Off the calling thread: a stop started here runs without the UI
            // context, so blocking on it can't deadlock. A save already in
            // progress (from another thread) is awaited too.
            if (IsRecording || State == RecorderState.Finalizing)
                Task.Run(StopAsync).GetAwaiter().GetResult();
        }
        catch { /* best effort */ }
        CleanupAfterStop();
    }
}
