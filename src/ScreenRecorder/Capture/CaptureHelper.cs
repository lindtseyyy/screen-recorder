using System.Runtime.InteropServices;
using WinRT;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;

namespace ScreenRecorder.Capture;

/// <summary>
/// D3D11 device management and WinRT interop for capture (PLAN §4.4):
/// device creation, IDXGIDevice → IDirect3DDevice, IDXGISurface → IDirect3DSurface,
/// surface → ID3D11Texture2D, and IGraphicsCaptureItemInterop::CreateForMonitor.
/// </summary>
public static class CaptureHelper
{
    // IGraphicsCaptureItemInterop (documented interface GUID).
    private static readonly Guid GraphicsCaptureItemInteropId =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // IGraphicsCaptureItem: the interface requested for the created item.
    private static readonly Guid GraphicsCaptureItemId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // ID3D11Texture2D (documented IID).
    private static readonly Guid Texture2DIid =
        new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern unsafe int WindowsCreateStringReference(
        char* sourceString, uint length, byte* hstringHeader, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(
        IntPtr dxgiSurface, out IntPtr graphicsSurface);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr window, [In] ref Guid iid, [Out] out IntPtr result);
        void CreateForMonitor(IntPtr monitor, [In] ref Guid iid, [Out] out IntPtr result);
    }

    // IDirect3DDxgiInterfaceAccess (documented interface GUID).
    private static readonly Guid DxgiInterfaceAccessId =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr obj);

    /// <summary>Creates a BGRA-capable D3D11 device with multithread protection enabled.</summary>
    public static void CreateDevice(out ID3D11Device device, out ID3D11DeviceContext context)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out var d, out _, out var c);
        if (result.Failure || d is null || c is null)
            throw new InvalidOperationException("Could not create a Direct3D 11 device.");
        device = d;
        context = c;

        using var multithread = device.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    /// <summary>Wraps the D3D device as a WinRT IDirect3DDevice for the frame pool.</summary>
    public static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice ToWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(
            CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var ptr));
        try
        {
            // A CsWinRT projection (not a classic RCW) so it marshals back to ABI.
            return MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>Wraps one of our textures as a WinRT IDirect3DSurface for the encoder.</summary>
    public static Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface ToWinRtSurface(ID3D11Texture2D texture)
    {
        using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(
            CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var ptr));
        try
        {
            return MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>Gets the D3D11 texture behind a captured frame's surface (caller disposes).</summary>
    public static ID3D11Texture2D TextureFromSurface(
        Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
    {
        // The surface is a CsWinRT projection: GetIUnknownForObject returns a
        // wrapper that QIs only for projected interfaces, so the raw native
        // pointer comes from IWinRTObject and QI + GetInterface go through the
        // vtable manually. IDirect3DDxgiInterfaceAccess::GetInterface is slot 3.
        // ThisPtr is borrowed (not released); QI/GetInterface results are owned.
        var native = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var accessId = DxgiInterfaceAccessId;
        var accessPtr = QueryForInterface(native, ref accessId);
        try
        {
            var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(accessPtr), 3 * IntPtr.Size);
            var getInterface = Marshal.GetDelegateForFunctionPointer<GetDxgiInterfaceDelegate>(fn);
            var textureId = Texture2DIid;
            Marshal.ThrowExceptionForHR(getInterface(accessPtr, ref textureId, out var texturePtr));
            return new ID3D11Texture2D(texturePtr); // takes ownership of the AddRef'd pointer
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }

    private static IntPtr QueryForInterface(IntPtr unknown, ref Guid iid)
    {
        var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(unknown), 0); // IUnknown::QueryInterface
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(fn);
        Marshal.ThrowExceptionForHR(query(unknown, ref iid, out var result));
        return result;
    }

    /// <summary>Creates a capture item for a monitor with no picker dialog.</summary>
    public static unsafe GraphicsCaptureItem CreateItemForMonitor(nint monitorHandle)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        byte* header = stackalloc byte[24]; // sizeof(HSTRING_HEADER)
        IntPtr factoryPtr = IntPtr.Zero;
        fixed (char* name = classId)
        {
            // The reference HSTRING is only valid while the buffer is pinned,
            // so the factory lookup happens inside the fixed block.
            Marshal.ThrowExceptionForHR(WindowsCreateStringReference(
                name, (uint)classId.Length, header, out var hstring));
            try
            {
                var iid = GraphicsCaptureItemInteropId;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out factoryPtr));
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var itemIid = GraphicsCaptureItemId;
            interop.CreateForMonitor(monitorHandle, ref itemIid, out var itemPtr);
            try
            {
                // CsWinRT projected classes can't be cast from __ComObject;
                // MarshalInterface owns its own reference, so ours is released.
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }
}
