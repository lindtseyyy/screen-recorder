using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using ScreenRecorder.Capture;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder.Encoding;

/// <summary>Receives mixed audio chunks (implemented by the encoder).</summary>
public interface IAudioSink
{
    void PushAudio(byte[] pcm16, TimeSpan timestamp);
}

/// <summary>
/// How far the encoder has got through its input (PLAN §6.6). Times are the media
/// time handed to Media Foundation so far; an ended stream has delivered its
/// end-of-stream. <see cref="BytesWritten"/> counts bytes written to the file
/// (≈ its size; the sink also rewrites one 8-byte header in place at the end).
/// </summary>
public readonly record struct EncoderProgress(
    TimeSpan VideoTime, TimeSpan AudioTime, bool VideoEnded, bool AudioEnded, long BytesWritten);

/// <summary>
/// MediaStreamSource (video [+ audio]) + MediaTranscoder → H.264 MP4 (PLAN §7.1).
/// Hardware encode with automatic software fallback, AAC 48 kHz stereo 192 kbps.
/// SampleRequested is answered with deferrals, never by blocking the MF thread.
/// </summary>
public interface IMediaEncoder : IVideoSink, IAudioSink, IDisposable
{
    void CompleteVideo();
    void CompleteAudio();

    /// <summary>Snapshot for the saving screen; cheap, callable from any thread.</summary>
    EncoderProgress Progress { get; }

    /// <summary>
    /// The transcode has ended. Before <see cref="CompleteVideo"/> that means it
    /// failed (encoder or disk error) and nothing more reaches the file.
    /// </summary>
    bool HasEnded { get; }

    /// <summary>Awaits the transcode, then flushes the file to disk and closes it.</summary>
    Task FinishAsync();
}

public sealed class Mp4Encoder : IMediaEncoder
{
    private const int VideoQueueCapacity = 3;

    private readonly MediaStreamSource _source;
    private readonly VideoStreamDescriptor _videoDescriptor;
    private readonly AudioStreamDescriptor? _audioDescriptor;
    private readonly FileStream _file;
    private readonly CountingStream _counted;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private Task? _transcodeTask;

    private readonly Queue<PendingVideo> _videoQueue = new();
    private readonly Queue<PendingAudio> _audioQueue = new();
    private readonly Queue<PendingRequest> _pendingVideo = new();
    private readonly Queue<PendingRequest> _pendingAudio = new();
    private readonly HashSet<MediaStreamSample> _inFlight = new();
    private bool _videoComplete;
    private bool _audioComplete;
    private TimeSpan _videoHanded;
    private TimeSpan _audioHanded;
    private bool _videoEnded;
    private bool _audioEnded;
    private bool _disposed;

    private sealed record PendingVideo(IDirect3DSurface Surface, TimeSpan Timestamp, Action Released);
    private sealed record PendingAudio(byte[] Pcm16, TimeSpan Timestamp);
    private sealed record PendingRequest(
        MediaStreamSourceSampleRequest Request, MediaStreamSourceSampleRequestDeferral Deferral);

    private Mp4Encoder(
        MediaStreamSource source,
        VideoStreamDescriptor videoDescriptor,
        AudioStreamDescriptor? audioDescriptor,
        FileStream file)
    {
        _source = source;
        _videoDescriptor = videoDescriptor;
        _audioDescriptor = audioDescriptor;
        _file = file;
        _counted = new CountingStream(file);
        // Subscribe BEFORE the transcode starts: Media Foundation can request
        // samples synchronously from TranscodeAsync, and a request with no
        // handler is lost, stalling that stream forever.
        _source.SampleRequested += OnSampleRequested;
    }

    /// <summary>
    /// Creates the encoder and starts the transcode. Writes to <paramref name="partPath"/>
    /// (renamed to .mp4 by the recorder after <see cref="FinishAsync"/>).
    /// </summary>
    public static async Task<Mp4Encoder> CreateAsync(
        string partPath, int cropWidth, int cropHeight,
        int outputWidth, int outputHeight, int fps, long bitrate, bool audioEnabled)
    {
        var videoProps = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8, (uint)cropWidth, (uint)cropHeight);
        var videoDescriptor = new VideoStreamDescriptor(videoProps);

        AudioStreamDescriptor? audioDescriptor = null;
        MediaStreamSource source;
        if (audioEnabled)
        {
            var audioProps = AudioEncodingProperties.CreatePcm(48000, 2, 16);
            audioDescriptor = new AudioStreamDescriptor(audioProps);
            source = new MediaStreamSource(videoDescriptor, audioDescriptor);
        }
        else
        {
            source = new MediaStreamSource(videoDescriptor);
        }
        source.BufferTime = TimeSpan.Zero;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)outputWidth;
        profile.Video.Height = (uint)outputHeight;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(bitrate, 1, uint.MaxValue);
        profile.Audio = audioEnabled
            ? AudioEncodingProperties.CreateAac(48000, 2, (int)EncodingMath.AudioBitrate)
            : null;

        var file = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var encoder = new Mp4Encoder(source, videoDescriptor, audioDescriptor, file);
        try
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                source, encoder._counted.AsRandomAccessStream(), profile);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException(
                    $"The encoder refused this recording ({prepared.FailureReason}).");
            // Cancellable: an encoder disposed before it finished (a failed start)
            // would otherwise wait for samples forever, holding a hardware
            // encoder session for the rest of the app's life.
            encoder._transcodeTask = prepared.TranscodeAsync().AsTask(encoder._cancel.Token);
            Log.Info($"Encoder started: {cropWidth}×{cropHeight} → {outputWidth}×{outputHeight}, " +
                     $"{fps} fps, {bitrate / 1_000_000.0:F1} Mbps, audio={audioEnabled}.");
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    public bool TryPushVideo(IDirect3DSurface surface, TimeSpan timestamp, Action released)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_videoComplete)
            {
                released();
                return true;
            }
            var pending = new PendingVideo(surface, timestamp, released);
            if (_pendingVideo.Count > 0)
            {
                FulfillVideoLocked(_pendingVideo.Dequeue(), pending);
                return true;
            }
            if (_videoQueue.Count >= VideoQueueCapacity)
                return false; // encoder busy: caller drops the frame
            _videoQueue.Enqueue(pending);
            return true;
        }
    }

    public bool IsWaitingForVideo
    {
        get
        {
            lock (_gate)
            {
                return !_videoComplete && _pendingVideo.Count > 0;
            }
        }
    }

    public void PushAudio(byte[] pcm16, TimeSpan timestamp)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_audioComplete)
                return;
            var pending = new PendingAudio(pcm16, timestamp);
            if (_pendingAudio.Count > 0)
                FulfillAudioLocked(_pendingAudio.Dequeue(), pending);
            else
                _audioQueue.Enqueue(pending);
        }
    }

    public void CompleteVideo()
    {
        lock (_gate)
        {
            _videoComplete = true;
            if (_videoQueue.Count == 0 && DrainLocked(_pendingVideo))
                _videoEnded = true;
        }
    }

    public void CompleteAudio()
    {
        lock (_gate)
        {
            _audioComplete = true;
            if (_audioQueue.Count == 0 && DrainLocked(_pendingAudio))
                _audioEnded = true;
        }
    }

    /// <summary>Answers pending requests with end-of-stream; true if any was answered.</summary>
    private static bool DrainLocked(Queue<PendingRequest> pending)
    {
        var any = pending.Count > 0;
        while (pending.Count > 0)
        {
            var request = pending.Dequeue();
            request.Request.Sample = null; // end of stream
            request.Deferral.Complete();
        }
        return any;
    }

    public EncoderProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return new EncoderProgress(
                    _videoHanded, _audioHanded, _videoEnded, _audioEnded, _counted.BytesWritten);
            }
        }
    }

    public bool HasEnded => _transcodeTask is { IsCompleted: true };

    /// <summary>
    /// Awaits the transcode, then flushes and closes the file. The .part file is
    /// complete and on disk then, so "Saved" is only shown once it really is.
    /// </summary>
    public async Task FinishAsync()
    {
        if (_transcodeTask is null)
            throw new InvalidOperationException("The transcode was never started.");
        try
        {
            await _transcodeTask;
            // Write buffer and OS cache → disk, off the caller's (UI) thread. Most
            // of a long recording is already on disk by now, so this stays short.
            await Task.Run(() => _file.Flush(flushToDisk: true));
        }
        finally
        {
            try { _source.SampleRequested -= OnSampleRequested; } catch { /* best effort */ }
            await _file.DisposeAsync();
        }
        Log.Info("Encoder finished.");
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var deferral = args.Request.GetDeferral();
            var pending = new PendingRequest(args.Request, deferral);
            if (ReferenceEquals(args.Request.StreamDescriptor, _videoDescriptor))
            {
                if (_videoQueue.Count > 0)
                {
                    FulfillVideoLocked(pending, _videoQueue.Dequeue());
                }
                else if (_videoComplete)
                {
                    args.Request.Sample = null;
                    deferral.Complete();
                    _videoEnded = true;
                }
                else
                {
                    _pendingVideo.Enqueue(pending);
                }
            }
            else
            {
                if (_audioQueue.Count > 0)
                {
                    FulfillAudioLocked(pending, _audioQueue.Dequeue());
                }
                else if (_audioComplete || _audioDescriptor is null)
                {
                    args.Request.Sample = null;
                    deferral.Complete();
                    _audioEnded = true;
                }
                else
                {
                    _pendingAudio.Enqueue(pending);
                }
            }
        }
    }

    private void FulfillVideoLocked(PendingRequest request, PendingVideo video)
    {
        _videoHanded = video.Timestamp;
        var sample = MediaStreamSample.CreateFromDirect3D11Surface(video.Surface, video.Timestamp);
        // Keep the sample alive until Processed: if the GC collects its wrapper
        // first, Processed is never raised and the texture is never released.
        // Measured: ~1 in 20 samples lost that way; the ring ran dry after
        // ~30 s and video froze while audio kept recording.
        _inFlight.Add(sample);
        sample.Processed += (_, _) =>
        {
            lock (_gate)
            {
                _inFlight.Remove(sample);
            }
            try { video.Released(); } catch { /* never throw on the MF thread */ }
        };
        request.Request.Sample = sample;
        request.Deferral.Complete();
    }

    private void FulfillAudioLocked(PendingRequest request, PendingAudio audio)
    {
        // Wrap the PCM array directly instead of copying it through a DataWriter:
        // it is never reused once queued, and this runs 50× a second.
        var sample = MediaStreamSample.CreateFromBuffer(audio.Pcm16.AsBuffer(), audio.Timestamp);
        sample.Duration = TimeSpan.FromSeconds((double)(audio.Pcm16.Length / 2 / 2) / 48000);
        _audioHanded = audio.Timestamp + sample.Duration;
        request.Request.Sample = sample;
        request.Deferral.Complete();
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Mp4Encoder));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Release any queued video textures so the ring never leaks slots.
            while (_videoQueue.Count > 0)
            {
                try { _videoQueue.Dequeue().Released(); } catch { /* best effort */ }
            }
            _inFlight.Clear();
        }
        try { _source.SampleRequested -= OnSampleRequested; } catch { /* best effort */ }
        // No-op after FinishAsync; ends a transcode that never got its end-of-stream.
        try { _cancel.Cancel(); } catch { /* best effort */ }
        _cancel.Dispose();
        _file.Dispose();
    }
}
