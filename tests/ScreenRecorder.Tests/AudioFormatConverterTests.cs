using ScreenRecorder.Audio;

namespace ScreenRecorder.Tests;

public sealed class AudioFormatConverterTests
{
    private static byte[] Int16Mono(int frames, short value)
    {
        var bytes = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
            BitConverter.GetBytes(value).CopyTo(bytes, i * 2);
        return bytes;
    }

    private static byte[] FloatStereo(int frames, float left, float right)
    {
        var bytes = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            BitConverter.GetBytes(left).CopyTo(bytes, i * 8);
            BitConverter.GetBytes(right).CopyTo(bytes, i * 8 + 4);
        }
        return bytes;
    }

    [Fact]
    public void Convert_MonoInt16_DuplicatesToStereo()
    {
        var converter = new AudioFormatConverter(48000, 1, sourceIsFloat: false);

        var output = converter.Convert(Int16Mono(480, 16384), 480 * 2);

        Assert.Equal(479, output.Length / 2); // trailing frame waits for the next call
        for (var i = 0; i < output.Length; i += 2)
        {
            Assert.Equal(0.5f, output[i], precision: 5);
            Assert.Equal(0.5f, output[i + 1], precision: 5);
        }
    }

    [Fact]
    public void Convert_Int16_ScalesFullRange()
    {
        var converter = new AudioFormatConverter(48000, 1, sourceIsFloat: false);

        var output = converter.Convert(Int16Mono(100, short.MaxValue), 100 * 2);

        Assert.Equal(32767f / 32768f, output[0], precision: 5);
    }

    [Fact]
    public void Convert_StereoFloat_PassesValuesThrough()
    {
        var converter = new AudioFormatConverter(48000, 2, sourceIsFloat: true);

        var output = converter.Convert(FloatStereo(480, 0.25f, -0.75f), 480 * 8);

        Assert.Equal(479, output.Length / 2);
        Assert.Equal(0.25f, output[0], precision: 6);
        Assert.Equal(-0.75f, output[1], precision: 6);
    }

    [Fact]
    public void Convert_ConsecutiveCalls_JoinWithoutLoss()
    {
        var converter = new AudioFormatConverter(48000, 2, sourceIsFloat: true);
        var total = 0;
        for (var i = 0; i < 10; i++)
            total += converter.Convert(FloatStereo(480, 0.1f, 0.1f), 480 * 8).Length / 2;

        Assert.Equal(4799, total); // 4800 in, all but the trailing frame out
    }

    [Fact]
    public void Convert_44100Hz_Produces48000HzCounts()
    {
        var converter = new AudioFormatConverter(44100, 2, sourceIsFloat: true);

        // Exactly 1 s of input in one call.
        var output = converter.Convert(FloatStereo(44100, 0.2f, 0.2f), 44100 * 8);

        Assert.Equal(47999, output.Length / 2);
    }

    [Fact]
    public void Convert_44100Hz_Chunked_ConvergesTo48000PerSecond()
    {
        var converter = new AudioFormatConverter(44100, 2, sourceIsFloat: true);
        var total = 0;
        for (var i = 0; i < 10; i++) // 10 × 100 ms
            total += converter.Convert(FloatStereo(4410, 0.2f, 0.2f), 4410 * 8).Length / 2;

        Assert.InRange(total, 47990, 48000);
    }

    [Fact]
    public void Convert_16kHzMono_Upsamples6xToStereo()
    {
        var converter = new AudioFormatConverter(16000, 1, sourceIsFloat: false);

        var output = converter.Convert(Int16Mono(16000, 16384), 16000 * 2);

        Assert.Equal(47997, output.Length / 2);
        // Constant input survives resampling.
        Assert.Equal(0.5f, output[100], precision: 4);
        Assert.Equal(0.5f, output[101], precision: 4);
    }

    [Fact]
    public void Convert_EmptyInput_ReturnsEmpty()
    {
        var converter = new AudioFormatConverter(44100, 2, sourceIsFloat: true);

        Assert.Empty(converter.Convert([], 0));
    }

    [Fact]
    public void Convert_IgnoresPartialTrailingFrame()
    {
        var converter = new AudioFormatConverter(48000, 2, sourceIsFloat: true);

        var output = converter.Convert(FloatStereo(10, 0.1f, 0.1f), 10 * 8 - 3);

        Assert.Equal(8, output.Length / 2); // 9 whole frames in, 8 out
    }
}
