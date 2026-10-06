using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using ScreenRecorder.Infrastructure;
using ScreenRecorder.Recording;

namespace ScreenRecorder.Capture;

/// <summary>Receives cropped video frames (implemented by the encoder).</summary>
public interface IVideoSink
{
    /// <summary>
    /// Pushes a frame; returns false when the sink is full (frame is dropped).
    /// <paramref name="released"/> runs once the sink is done with the surface.
    /// </summary>
    bool TryPushVideo(IDirect3DSurface surface, TimeSpan timestamp, Action released);

    /// <summary>The encoder has asked for a video frame and has none queued.</summary>
    bool IsWaitingForVideo { get; }
}

/// <summary>Video capture pipeline (implemented by <see cref="FrameSource"/>).</summary>
public interface IVideoPipeline : IDisposable
{
    event Action<string>? Lost;
    event Action<Exception>? Failed;
    void Start(CaptureTarget target, RecordingClock clock, bool showCursor, int fps);
    bool Paused { get; set; }
    void EmitHeartbeat(TimeSpan activeTime);
    void EmitLatest(TimeSpan activeTime);
    void Stop();
    long DroppedFrames { get; }
}

/// <summary>
/// WGC capture session, frame pool, GPU crop copy and texture ring (PLAN §4.4, §6.2).
/// Frames arrive on a thread-pool thread, are cropped on the GPU into a ring of
/// textures, and handed to the sink. The pool holds 2 buffers; every
/// Direct3D11CaptureFrame is disposed immediately so the pool never stalls.
/// </summary>
public sealed class FrameSource : IVideoPipeline
{
    /// <summary>Textures created up front; the ring grows from here on demand.</summary>
    public const int InitialRingSize = 4;

    /// <summary>GPU memory the ring may grow to (shared system RAM on integrated GPUs).</summary>
    public const long RingMemoryBudget = 768L * 1024 * 1024;

    public const int MinMaxRingSize = 8;
    public const int MaxMaxRingSize = 32;

    /// <summary>Smallest gap between two pushed frames' timestamps.</summary>
    private static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(1);

    private readonly IVideoSink _sink;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private readonly List<ID3D11Texture2D> _ring = new();
    private readonly List<IDirect3DSurface> _ringSurfaces = new();
    // Samples in the sink referencing each texture; 0 = free. Usually 0 or 1,
    // 2+ when the deadlock breaker resubmits a texture.
    private readonly List<int> _slotRefs = new();
    private int _lastSlot = -1;
    private Texture2DDescription _ringDesc;
    private int _maxRing = InitialRingSize;
    private ID3D11Texture2D? _latest;

    private RecordingClock? _clock;
    private Box? _cropBox;
    private int _monitorWidth;
    private int _monitorHeight;
    private TimeSpan _minInterval;
    private TimeSpan _lastPush = TimeSpan.MinValue;
    private bool _hasMinUpdateInterval;
    private bool _ringFullLogged;
    private bool _paused;
    private bool _stopping;
    private bool _disposed;

    public FrameSource(IVideoSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>Monitor unplugged (item closed) or size changed: the recording must stop &amp; save.</summary>
    public event Action<string>? Lost;
    public event Action<Exception>? Failed;

    public long DroppedFrames { get; private set; }

    /// <summary>Frames the deadlock breaker resubmitted (a repeat of the previous frame).</summary>
    public long RepeatedFrames { get; private set; }

    /// <summary>
    /// How many ring textures a crop of this size may grow to. The encoder
    /// normally holds 1–2 frames (Intel UHD, measured), but may hold more while
    /// it is busy and asks for the next one before releasing any; a ring
    /// smaller than that runs dry and video freezes while audio continues.
    /// </summary>
    public static int MaxRingSize(int width, int height)
    {
        var frameBytes = Math.Max(1L, (long)width * height * 4);
        return (int)Math.Clamp(RingMemoryBudget / frameBytes, MinMaxRingSize, MaxMaxRingSize);
    }

    public bool Paused
    {
        get { lock (_gate) { return _paused; } }
        set { lock (_gate) { _paused = value; } }
    }

    public void Start(CaptureTarget target, RecordingClock clock, bool showCursor, int fps)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FrameSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _monitorWidth = target.Monitor.Width;
        _monitorHeight = target.Monitor.Height;
        _minInterval = TimeSpan.FromSeconds(1.0 / fps);
        _lastPush = TimeSpan.MinValue;
        _lastSlot = -1;
        DroppedFrames = 0;
        RepeatedFrames = 0;

        _cropBox = target.IsFullScreen
            ? null // whole-resource copy
            : new Box
            {
                Left = target.X, Top = target.Y, Front = 0,
                Right = target.X + target.Width, Bottom = target.Y + target.Height, Back = 1,
            };

        try
        {
            CaptureHelper.CreateDevice(out var device, out var context);
            _device = device;
            _context = context;
            var winRtDevice = CaptureHelper.ToWinRtDevice(device);

            _item = CaptureHelper.CreateItemForMonitor(target.Monitor.Handle);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);

            _ringDesc = RingTextureDescription(target.Width, target.Height);
            _maxRing = MaxRingSize(target.Width, target.Height);
            for (var i = 0; i < InitialRingSize; i++)
                AddRingTexture(device, free: true);
            _latest = device.CreateTexture2D(_ringDesc);

            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = showCursor;
            if (ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                _session.IsBorderRequired = false; // no yellow capture border
            _hasMinUpdateInterval = ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval");
            if (_hasMinUpdateInterval)
                _session.MinUpdateInterval = _minInterval; // Win11 24H2+: OS-side throttle

            _pool.FrameArrived += OnFrameArrived;
            _item.Closed += OnItemClosed;
            _session.StartCapture();
            Log.Info($"Capture started: {target.Description}, {fps} fps, cursor={showCursor}.");
        }
        catch
        {
            try { TearDownCapture(); } catch { /* best effort */ }
            try { TearDownDevice(); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Video heartbeat (§6.2): WGC only delivers frames when the screen changes, so
    /// a static screen would stall the video stream. Re-send the latest frame when
    /// nothing was pushed for 0.5 s.
    /// </summary>
    public void EmitHeartbeat(TimeSpan activeTime)
    {
        lock (_gate)
        {
            if (_paused || _stopping || _clock is null)
                return;
            if (_lastPush != TimeSpan.MinValue && activeTime - _lastPush < TimeSpan.FromSeconds(0.5))
                return;
            EmitLatestLocked(activeTime);
        }
    }

    /// <summary>Enqueues the latest frame (resume, and the final frame at stop).</summary>
    public void EmitLatest(TimeSpan activeTime)
    {
        lock (_gate)
        {
            if (_stopping || _clock is null)
                return;
            EmitLatestLocked(activeTime);
        }
    }

    /// <summary>
    /// Stops capture callbacks and tears down the session/pool, but keeps the D3D
    /// device and ring textures alive: the encoder may still hold samples
    /// referencing them until the transcode finishes. Full cleanup is in Dispose.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopping)
                return; // already stopped (Dispose calls Stop again)
            _stopping = true;
        }
        TearDownCapture();
        int textures;
        lock (_gate)
        {
            textures = _ring.Count;
        }
        Log.Info($"Capture stopped. Dropped frames: {DroppedFrames}, repeated: {RepeatedFrames}, " +
                 $"ring textures: {textures}.");
    }

    private void EmitLatestLocked(TimeSpan activeTime)
    {
        if (_latest is null || _context is null)
            return;
        // A frame stamped after this time already went out (e.g. one that
        // arrived between Stop's timestamp and capture stopping): the latest
        // content is covered, and an older timestamp would go backwards.
        if (_lastPush != TimeSpan.MinValue && activeTime <= _lastPush)
            return;
        var slot = TakeFreeSlotLocked();
        if (slot < 0)
        {
            if (!TryRepeatLastLocked(activeTime))
                DroppedFrames++;
            return;
        }
        _context.CopySubresourceRegion(_ring[slot], 0, 0, 0, 0, _latest, 0, null);
        PushLocked(slot, activeTime);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            var clock = _clock;
            if (frame is null || clock is null || _context is null)
                return;
            var size = frame.ContentSize;
            if (size.Width != _monitorWidth || size.Height != _monitorHeight)
            {
                Lost?.Invoke("The display mode changed during recording.");
                return;
            }

            using var source = CaptureHelper.TextureFromSurface(frame.Surface);
            lock (_gate)
            {
                if (_stopping || _context is null || _latest is null)
                    return;
                if (_paused || !clock.IsStarted)
                {
                    // Keep "latest" fresh: resume is then instant, and a frame that
                    // arrives before the clock starts becomes the first frame
                    // (instead of a black one, or a clock-not-started failure).
                    _context.CopySubresourceRegion(_latest!, 0, 0, 0, 0, source, 0, _cropBox);
                    return;
                }
                var ts = clock.ActiveTime(frame.SystemRelativeTime);
                if (!_hasMinUpdateInterval && _lastPush != TimeSpan.MinValue
                    && ts - _lastPush < _minInterval)
                    return; // fallback throttle when the OS can't do it
                // Frames can arrive slightly out of order with a heartbeat (stamped
                // "now") or with each other (callbacks run in parallel). The new
                // content is still worth keeping; its time must not go backwards.
                if (_lastPush != TimeSpan.MinValue && ts <= _lastPush)
                    ts = _lastPush + MinStep;
                var slot = TakeFreeSlotLocked();
                if (slot < 0)
                {
                    if (!TryRepeatLastLocked(ts))
                        DroppedFrames++; // encoder busy: drop, never block
                    return;
                }
                _context.CopySubresourceRegion(_ring[slot], 0, 0, 0, 0, source, 0, _cropBox);
                _context.CopySubresourceRegion(_latest!, 0, 0, 0, 0, source, 0, _cropBox);
                PushLocked(slot, ts);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            frame?.Dispose(); // always: leaked frames stall the pool
        }
    }

    /// <summary>Pushes a slot the caller has already referenced (refs incremented).</summary>
    private void PushLocked(int slot, TimeSpan ts)
    {
        _lastPush = ts;
        _lastSlot = slot;
        var surface = _ringSurfaces[slot];
        if (!_sink.TryPushVideo(surface, ts, () => FreeSlot(slot)))
        {
            ReleaseSlotLocked(slot);
            DroppedFrames++;
        }
    }

    /// <summary>
    /// Deadlock breaker. The encoder holds every texture and is waiting for one
    /// more frame before it releases any; at the ring cap, dropping would leave it
    /// waiting forever (frozen video, audio still recording). Resubmitting the
    /// newest texture with the new timestamp looks the same as one dropped frame,
    /// costs no memory, and lets the encoder move on and release textures.
    /// </summary>
    private bool TryRepeatLastLocked(TimeSpan ts)
    {
        if (_lastSlot < 0 || _slotRefs[_lastSlot] == 0 || ts <= _lastPush || !_sink.IsWaitingForVideo)
            return false;
        _slotRefs[_lastSlot]++;
        RepeatedFrames++;
        PushLocked(_lastSlot, ts);
        return true;
    }

    private int TakeFreeSlotLocked()
    {
        for (var i = 0; i < _slotRefs.Count; i++)
        {
            if (_slotRefs[i] == 0)
            {
                _slotRefs[i] = 1;
                return i;
            }
        }
        // All textures are still held by the encoder. Grow instead of dropping:
        // the encoder won't release any until it gets another frame, so a fixed
        // ring deadlocks and video freezes for the rest of the recording.
        if (_device is null)
            return -1;
        if (_ring.Count >= _maxRing)
        {
            if (!_ringFullLogged)
            {
                _ringFullLogged = true;
                Log.Warn($"All {_ring.Count} frame textures are held by the encoder; repeating frames.");
            }
            return -1;
        }
        try
        {
            AddRingTexture(_device, free: false);
            return _ring.Count - 1;
        }
        catch (Exception ex)
        {
            _maxRing = _ring.Count; // out of GPU memory: stay at this size
            Log.Warn($"Could not grow the frame ring past {_ring.Count}: {ex.Message}");
            return -1;
        }
    }

    private void AddRingTexture(ID3D11Device device, bool free)
    {
        var texture = device.CreateTexture2D(_ringDesc);
        try
        {
            _ringSurfaces.Add(CaptureHelper.ToWinRtSurface(texture));
        }
        catch
        {
            texture.Dispose();
            throw;
        }
        _ring.Add(texture);
        _slotRefs.Add(free ? 0 : 1);
    }

    private void FreeSlot(int slot)
    {
        lock (_gate)
        {
            ReleaseSlotLocked(slot);
        }
    }

    private void ReleaseSlotLocked(int slot)
    {
        if ((uint)slot < (uint)_slotRefs.Count && _slotRefs[slot] > 0)
            _slotRefs[slot]--;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Lost?.Invoke("The monitor was disconnected.");
    }

    private static Texture2DDescription RingTextureDescription(int width, int height) => new()
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.Shared,
    };

    private void TearDownCapture()
    {
        try
        {
            if (_pool is not null)
                _pool.FrameArrived -= OnFrameArrived;
            if (_item is not null)
                _item.Closed -= OnItemClosed;
        }
        catch { /* best effort */ }

        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        // GraphicsCaptureItem is not closable; dropping the reference lets the GC
        // release the underlying COM object.
        _item = null;
    }

    private void TearDownDevice()
    {
        // D3D objects hold GPU memory: dispose deterministically under the gate so
        // no in-flight frame callback can touch them (it re-checks _stopping).
        // Managed WinRT surface wrappers are reclaimed by the GC.
        lock (_gate)
        {
            _ringSurfaces.Clear();
            foreach (var tex in _ring)
                tex.Dispose();
            _ring.Clear();
            _latest?.Dispose();
            _latest = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _slotRefs.Clear();
            _lastSlot = -1;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { Stop(); } catch { /* best effort */ }
        try { TearDownDevice(); } catch { /* best effort */ }
    }
}
