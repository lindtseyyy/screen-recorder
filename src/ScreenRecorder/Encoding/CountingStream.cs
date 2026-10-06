using System.IO;

namespace ScreenRecorder.Encoding;

/// <summary>
/// Pass-through stream that counts the bytes written, so the saving screen can
/// show how much of the file exists. Safe to read <see cref="BytesWritten"/>
/// from any thread while Media Foundation writes on its own.
/// </summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    private long _bytesWritten;

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
    }

    // The WinRT stream adapter writes through this overload.
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    // The owner disposes the inner stream; this wrapper holds nothing of its own.
}
