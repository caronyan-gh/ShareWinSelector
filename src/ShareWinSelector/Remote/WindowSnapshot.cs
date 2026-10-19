using System.Runtime.InteropServices;

namespace ShareWinSelector.Remote;

/// <summary>
/// ウィンドウを小さく撮る（リモート連携で、子のウィンドウのサムネイルを親へ送るため）。
/// Windows の PrintWindow でウィンドウを描かせてから縮める。GPU で描くアプリ（一部のブラウザやゲーム）は真っ黒になることがある。
/// </summary>
internal static class WindowSnapshot
{
    private const uint PW_RENDERFULLCONTENT = 2;
    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>ウィンドウを縦横比を保ったまま maxWidth×maxHeight 以内に縮めて撮る（BGRA）。撮れなければ null。</summary>
    public static (byte[] Pixels, int Width, int Height)? Capture(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        // 最小化中・応答なしのアプリは撮らない（固まったアプリに描かせると待たされる）
        if (!IsWindow(hwnd) || IsIconic(hwnd) || IsHungAppWindow(hwnd)) return null;
        if (!GetWindowRect(hwnd, out var rc)) return null;
        int width = rc.Right - rc.Left, height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0) return null;
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        int tw = Math.Max(1, (int)(width * scale)), th = Math.Max(1, (int)(height * scale));

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr full = CreateCompatibleDC(screen), small = CreateCompatibleDC(screen);
        IntPtr fullBitmap = CreateCompatibleBitmap(screen, width, height);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = tw, biHeight = -th, biPlanes = 1, biBitCount = 32 };
        IntPtr smallBitmap = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr oldFull = SelectObject(full, fullBitmap), oldSmall = SelectObject(small, smallBitmap);
        try
        {
            if (!PrintWindow(hwnd, full, PW_RENDERFULLCONTENT)) return null;
            SetStretchBltMode(small, HALFTONE);
            SetBrushOrgEx(small, 0, 0, IntPtr.Zero);
            if (!StretchBlt(small, 0, 0, tw, th, full, 0, 0, width, height, SRCCOPY)) return null;
            GdiFlush();
            var pixels = new byte[tw * th * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return (pixels, tw, th);
        }
        finally
        {
            SelectObject(full, oldFull);
            SelectObject(small, oldSmall);
            DeleteObject(fullBitmap);
            DeleteObject(smallBitmap);
            DeleteDC(full);
            DeleteDC(small);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr point);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
