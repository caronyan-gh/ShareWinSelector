using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ShareWinSelector.Interop;

namespace ShareWinSelector.Output;

/// <summary>SwapChain の描画先にする子 HWND。</summary>
internal sealed class SwapChainHost : HwndHost
{
    public IntPtr ChildHandle { get; private set; }

    /// <summary>子 HWND のクライアントサイズ（物理ピクセル）が変わった。</summary>
    public event Action<int, int>? PixelSizeChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ChildHandle = Native.CreateWindowEx(0, "static", string.Empty,
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, ChildHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => Native.DestroyWindow(hwnd.Handle);

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        var (w, h) = GetPixelSize();
        PixelSizeChanged?.Invoke(w, h);
    }

    public (int Width, int Height) GetPixelSize()
    {
        Native.GetClientRect(ChildHandle, out var rc);
        return (rc.Width, rc.Height);
    }
}
