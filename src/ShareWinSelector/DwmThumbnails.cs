using System.Windows;
using System.Windows.Media;
using ShareWinSelector.Interop;

namespace ShareWinSelector;

/// <summary>
/// WPF の要素（プレースホルダー）の位置に、他ウィンドウの DWM サムネイルを重ねて表示する。
/// DWM がライブで描くので CPU/GPU 負荷はほぼ無く、最小化中のウィンドウも最後の状態が出る。
/// サムネイルは WPF の上に描かれるので、スクロール領域（viewport）からはみ出す分は自前で切り取る。
/// </summary>
internal sealed class DwmThumbnails : IDisposable
{
    private sealed class Entry
    {
        public required IntPtr Source;
        public required IntPtr Thumbnail;
        public required bool ClientAreaOnly;
        public FrameworkElement? Viewport;
    }

    private readonly Window _window;
    private readonly Dictionary<FrameworkElement, Entry> _entries = [];
    private IntPtr _hwnd;

    public DwmThumbnails(Window window)
    {
        _window = window;
    }

    private IntPtr WindowHandle
    {
        get
        {
            if (_hwnd == IntPtr.Zero) _hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            return _hwnd;
        }
    }

    /// <summary>placeholder の位置に source ウィンドウのサムネイルを出す。</summary>
    public void Register(FrameworkElement placeholder, IntPtr source, bool clientAreaOnly = false, FrameworkElement? viewport = null)
    {
        Unregister(placeholder);
        if (WindowHandle == IntPtr.Zero || source == IntPtr.Zero) return;
        if (Native.DwmRegisterThumbnail(WindowHandle, source, out var thumb) != 0) return;
        _entries[placeholder] = new Entry { Source = source, Thumbnail = thumb, ClientAreaOnly = clientAreaOnly, Viewport = viewport };
        Update(placeholder, _entries[placeholder]);
    }

    public void Unregister(FrameworkElement placeholder)
    {
        if (_entries.Remove(placeholder, out var entry))
            Native.DwmUnregisterThumbnail(entry.Thumbnail);
    }

    /// <summary>レイアウトやスクロールが変わったら呼ぶ。</summary>
    public void UpdateAll()
    {
        foreach (var (placeholder, entry) in _entries) Update(placeholder, entry);
    }

    private void Update(FrameworkElement placeholder, Entry entry)
    {
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE
                      | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            opacity = 255,
            fSourceClientAreaOnly = false,
        };

        if (!TryGetLayout(placeholder, entry, out var dest, out var source))
        {
            props.fVisible = false;
            Native.DwmUpdateThumbnailProperties(entry.Thumbnail, ref props);
            return;
        }
        props.fVisible = true;
        props.rcDestination = dest;
        props.rcSource = source;
        Native.DwmUpdateThumbnailProperties(entry.Thumbnail, ref props);
    }

    /// <summary>表示先（このウィンドウのクライアント座標、物理ピクセル）と元画像の範囲を求める。</summary>
    private bool TryGetLayout(FrameworkElement placeholder, Entry entry, out Native.RECT dest, out Native.RECT source)
    {
        dest = default;
        source = default;
        if (!placeholder.IsVisible || placeholder.ActualWidth < 1 || placeholder.ActualHeight < 1) return false;
        if (!_window.IsAncestorOf(placeholder)) return false;

        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        Rect ToPixels(FrameworkElement e) =>
            e.TransformToAncestor(_window).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)) is var r
                ? new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale)
                : Rect.Empty;

        // 元画像の範囲（クライアント領域だけ、またはウィンドウ全体）
        Rect src;
        if (entry.ClientAreaOnly)
        {
            if (!Native.GetWindowRect(entry.Source, out var wr) || !Native.GetClientRect(entry.Source, out var cr)) return false;
            var origin = new Native.POINT();
            Native.ClientToScreen(entry.Source, ref origin);
            src = new Rect(origin.X - wr.Left, origin.Y - wr.Top, cr.Width, cr.Height);
        }
        else
        {
            if (Native.DwmQueryThumbnailSourceSize(entry.Thumbnail, out var size) != 0) return false;
            src = new Rect(0, 0, size.cx, size.cy);
        }
        if (src.Width < 1 || src.Height < 1) return false;

        // 縦横比を保って枠の中央に収める
        var box = ToPixels(placeholder);
        double fit = Math.Min(box.Width / src.Width, box.Height / src.Height);
        var fitted = new Rect(box.X + (box.Width - src.Width * fit) / 2, box.Y + (box.Height - src.Height * fit) / 2,
            src.Width * fit, src.Height * fit);

        // スクロール領域からはみ出す分を、元画像側も同じ比率で削る
        var visible = fitted;
        if (entry.Viewport is { IsVisible: true } viewport) visible.Intersect(ToPixels(viewport));
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1) return false;

        double sx = src.Width / fitted.Width, sy = src.Height / fitted.Height;
        var clippedSrc = new Rect(
            src.X + (visible.X - fitted.X) * sx, src.Y + (visible.Y - fitted.Y) * sy,
            visible.Width * sx, visible.Height * sy);

        dest = ToRect(visible);
        source = ToRect(clippedSrc);
        return true;
    }

    private static Native.RECT ToRect(Rect r) => new()
    {
        Left = (int)Math.Round(r.Left),
        Top = (int)Math.Round(r.Top),
        Right = (int)Math.Round(r.Right),
        Bottom = (int)Math.Round(r.Bottom),
    };

    public void Dispose()
    {
        foreach (var entry in _entries.Values) Native.DwmUnregisterThumbnail(entry.Thumbnail);
        _entries.Clear();
    }
}
