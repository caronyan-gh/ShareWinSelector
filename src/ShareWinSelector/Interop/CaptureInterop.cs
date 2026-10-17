using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ShareWinSelector.Interop;

/// <summary>WGC と D3D11 の橋渡し（COM vtable を直接呼ぶ）。</summary>
internal static unsafe class CaptureInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    /// <summary>DXGI デバイスから WinRT の IDirect3DDevice を作る。</summary>
    public static IDirect3DDevice CreateDirect3DDevice(IntPtr dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr ptr));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>HWND から GraphicsCaptureItem を作る。</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr hstring));
        IntPtr interop = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out interop));
            // IGraphicsCaptureItemInterop::CreateForWindow (vtable slot 3)
            var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)interop)[3];
            Guid iid = IID_IGraphicsCaptureItem;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(createForWindow(interop, hwnd, &iid, &itemPtr));
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            if (interop != IntPtr.Zero) Marshal.Release(interop);
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>IDirect3DSurface の中身の DXGI/D3D11 インターフェイスを取り出す（呼び出し側が Release する）。</summary>
    public static IntPtr GetDxgiInterface(IDirect3DSurface surface, Guid iid)
    {
        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        IntPtr access = IntPtr.Zero;
        try
        {
            Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in accessIid, out access));
            // IDirect3DDxgiInterfaceAccess::GetInterface (vtable slot 3)
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)access)[3];
            IntPtr result;
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &result));
            return result;
        }
        finally
        {
            if (access != IntPtr.Zero) Marshal.Release(access);
            Marshal.Release(surfacePtr);
        }
    }
}
