using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Infrastructure;

namespace ScreenRecorder.Audio;

/// <summary>Which WASAPI source an <see cref="IAudioCapture"/> reads.</summary>
public enum AudioSourceKind
{
    Loopback,
    Microphone,
}

/// <summary>One running WASAPI capture feeding the mixer. Implemented by <see cref="AudioSource"/>.</summary>
public interface IAudioCapture : IDisposable
{
    AudioSourceKind Kind { get; }
    event Action? SourceLost;
    event Action<Exception>? Failed;
    void Start();
    void Restart(MMDevice device);
    void Stop();
}

/// <summary>
/// One WASAPI client (loopback or mic) producing 48 kHz float stereo for the mixer
/// (PLAN §5.3). Opens in shared mode, asks for 48 kHz stereo float, and falls back
/// to the device mix format with in-code conversion (<see cref="AudioFormatConverter"/>).
/// </summary>
public sealed class AudioSource : IAudioCapture
{
    private readonly AudioMixer _mixer;
    private readonly Action<float[]> _feed;
    private MMDevice _device;
    private WasapiCapture? _capture;
    private AudioFormatConverter? _converter;
    private bool _running;
    private bool _stopping;

    public AudioSource(AudioSourceKind kind, MMDevice device, AudioMixer mixer)
    {
        Kind = kind;
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        _feed = kind == AudioSourceKind.Loopback
            ? (Action<float[]>)(f => _mixer.FeedSystem(f))
            : (f => _mixer.FeedMic(f));
    }

    public AudioSourceKind Kind { get; }

    public event Action? SourceLost;
    public event Action<Exception>? Failed;

    public void Start()
    {
        Stop();
        _stopping = false;

        // Preferred: the common format (Windows converts when the device allows it).
        // If the device refuses, capture its native mix format and convert in code.
        var wanted = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, AudioMixer.Channels);
        Exception? error = null;
        if (StartWithFormat(wanted, ref error))
        {
            _converter = null; // already 48 kHz float stereo
            Log.Info($"{Kind} capture: {Describe(wanted)} (native)");
        }
        else
        {
            var native = _device.AudioClient.MixFormat; // RCW reclaimed by the GC
            if (IsCommonFormat(native))
            {
                // Same format failed twice: a real error (privacy block, exclusive
                // mode, …), not a format problem. Preserve the original HResult.
                throw error!;
            }
            _converter = CreateConverter(native);
            if (!StartWithFormat(native, ref error))
                throw error!;
            Log.Info($"{Kind} capture: {Describe(native)} (converting in code)");
        }

        _running = true;
    }

    private bool StartWithFormat(WaveFormat format, ref Exception? error)
    {
        var capture = Kind == AudioSourceKind.Loopback
            ? (WasapiCapture)new WasapiLoopbackCapture()
            : new WasapiCapture(_device);
        capture.ShareMode = AudioClientShareMode.Shared;
        capture.WaveFormat = format;
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        try
        {
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            error = ex;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            return false;
        }
        _capture = capture;
        return true;
    }

    private static bool IsCommonFormat(WaveFormat f) =>
        f.SampleRate == AudioMixer.SampleRate
        && f.Channels == AudioMixer.Channels
        && f.Encoding == WaveFormatEncoding.IeeeFloat;

    /// <summary>Reopens the capture on a new device (default output changed mid-recording).</summary>
    public void Restart(MMDevice device)
    {
        _device.Dispose();
        _device = device;
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    public void Stop()
    {
        _stopping = true;
        _running = false;
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                Log.Warn($"{Kind} stop failed: " + ex.Message);
            }
            finally
            {
                capture.Dispose();
            }
        }
    }

    private static AudioFormatConverter CreateConverter(WaveFormat native)
    {
        var isFloat = native.Encoding == WaveFormatEncoding.IeeeFloat;
        if (!isFloat && native.Encoding != WaveFormatEncoding.Pcm)
            throw new InvalidOperationException($"Unsupported capture encoding: {native.Encoding}.");
        if (!isFloat && native.BitsPerSample != 16)
            throw new InvalidOperationException($"Unsupported capture depth: {native.BitsPerSample}-bit.");
        return new AudioFormatConverter(native.SampleRate, native.Channels, isFloat);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (!_running || e.BytesRecorded <= 0)
                return;
            float[] floats;
            if (_converter is null)
            {
                // Fast path: already 48 kHz float stereo; reinterpret the bytes.
                var count = e.BytesRecorded / 4;
                floats = new float[count];
                Buffer.BlockCopy(e.Buffer, 0, floats, 0, count * 4);
            }
            else
            {
                floats = _converter.Convert(e.Buffer, e.BytesRecorded);
            }
            _feed(floats);
        }
        catch (Exception ex)
        {
            Log.Warn($"{Kind} data callback failed: " + ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping)
            return; // intentional stop
        _running = false;
        if (e.Exception is not null)
        {
            Log.Warn($"{Kind} stopped with error: " + e.Exception.Message);
            Failed?.Invoke(e.Exception);
        }
        else
        {
            Log.Info($"{Kind} source lost (device removed?).");
            SourceLost?.Invoke();
        }
    }

    private static string Describe(WaveFormat f) =>
        $"{f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample}-bit {f.Encoding}";

    public void Dispose()
    {
        Stop();
        _device.Dispose();
    }
}
