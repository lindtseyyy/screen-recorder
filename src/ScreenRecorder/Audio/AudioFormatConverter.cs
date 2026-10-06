namespace ScreenRecorder.Audio;

/// <summary>
/// Converts captured audio to the mixer's common format: 48 kHz stereo float.
/// Handles 16-bit PCM or 32-bit float input, any channel count (mono is upmixed
/// by duplication, 3+ channels use the first two) and any sample rate via linear
/// resampling with carried fractional state, so chunks join without clicks.
/// One instance per source; not thread-safe (each WASAPI callback is serial).
/// </summary>
public sealed class AudioFormatConverter
{
    public const int TargetRate = AudioMixer.SampleRate;

    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly bool _sourceIsFloat;
    private readonly double _step; // source frames per target frame

    // Stream-absolute resampling state.
    private long _inTotal;    // source frames consumed so far
    private long _outTotal;   // target frames emitted so far
    private float _prevL;     // last source frame of the previous call (zeros at start)
    private float _prevR;

    public AudioFormatConverter(int sourceRate, int sourceChannels, bool sourceIsFloat)
    {
        if (sourceRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (sourceChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        _sourceRate = sourceRate;
        _sourceChannels = sourceChannels;
        _sourceIsFloat = sourceIsFloat;
        _step = (double)sourceRate / TargetRate;
    }

    /// <summary>Converts raw capture bytes to interleaved stereo float at 48 kHz.</summary>
    public float[] Convert(byte[] buffer, int bytesRecorded)
    {
        var bytesPerSample = _sourceIsFloat ? 4 : 2;
        var frameSize = bytesPerSample * _sourceChannels;
        var frames = Math.Max(0, bytesRecorded / frameSize);
        if (frames == 0)
            return [];

        Span<float> inL = frames <= 4096 ? stackalloc float[frames] : new float[frames];
        Span<float> inR = frames <= 4096 ? stackalloc float[frames] : new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var off = i * frameSize;
            float l, r;
            if (_sourceIsFloat)
            {
                l = BitConverter.ToSingle(buffer, off);
                r = _sourceChannels > 1 ? BitConverter.ToSingle(buffer, off + 4) : l;
            }
            else
            {
                l = BitConverter.ToInt16(buffer, off) / 32768f;
                r = _sourceChannels > 1 ? BitConverter.ToInt16(buffer, off + 2) / 32768f : l;
            }
            inL[i] = l;
            inR[i] = r;
        }

        // Output k (absolute) sits at source position k*step; interpolate between the
        // two surrounding source frames. Both must be available, so the trailing
        // partial frame waits for the next call (total counts converge over a stream).
        var output = new List<float>(Math.Max(16, (int)(frames / _step) + 2));
        while (true)
        {
            var p = _outTotal * _step; // absolute source position of next output
            if (p >= _inTotal + frames - 1)
                break;
            var i0 = (long)Math.Floor(p);
            var frac = (float)(p - i0);
            SampleAt(i0, inL, inR, frames, out var aL, out var aR);
            SampleAt(i0 + 1, inL, inR, frames, out var bL, out var bR);
            output.Add(aL + (bL - aL) * frac);
            output.Add(aR + (bR - aR) * frac);
            _outTotal++;
        }

        _prevL = inL[frames - 1];
        _prevR = inR[frames - 1];
        _inTotal += frames;
        return [.. output];
    }

    private void SampleAt(long absolute, Span<float> inL, Span<float> inR, int frames,
        out float l, out float r)
    {
        if (absolute < _inTotal)
        {
            l = _prevL; // only ever _inTotal - 1 (previous call's last frame)
            r = _prevR;
        }
        else
        {
            var i = (int)(absolute - _inTotal);
            l = inL[i];
            r = inR[i];
        }
    }

    public void Reset()
    {
        _inTotal = 0;
        _outTotal = 0;
        _prevL = 0;
        _prevR = 0;
    }
}
