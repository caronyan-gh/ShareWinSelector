using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ShareWinSelector.Interop;

namespace ShareWinSelector.Output;

public enum OutputPlacement
{
    /// <summary>画面内・常に最背面（他のウィンドウをどかすと見える）。</summary>
    Back,
    /// <summary>普通のウィンドウとして表示。</summary>
    Normal,
    /// <summary>全モニターの外に置く（自分からは見えない）。</summary>
    OffScreen,
}

/// <summary>Teams 等で「ウィンドウ共有」する対象の出力ウィンドウ。</summary>
internal sealed class OutputWindow : Window
{
    private const int WM_SIZING = 0x0214;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    // 端のこの幅（物理ピクセル）だけはサイズ変更に使う
    private const int ResizeGrip = 6;
    private const int WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6;
    // 中身（共有される部分）の初期の横幅（物理ピクセル）
    private const int DefaultClientWidth = 1280;

    // 中身の縦横比（＝共有するモニターの縦横比）。ずれると左右や上下に帯が出る
    private double _aspect = 16.0 / 9.0;

    private OutputPlacement _placement = OutputPlacement.Back;
    private Point _onScreenPosition = new(100, 100);
    private IntPtr _hwnd;
    // 前回の終了時の状態から復元する中身の横幅と最大化
    private readonly int _initialClientWidth = DefaultClientWidth;
    private bool _pendingMaximize;

    public SwapChainHost Host { get; } = new();

    /// <summary>ユーザーが移動・サイズ変更・最大化/戻すをし終えた（保存の合図）。</summary>
    public event Action? LayoutChangedByUser;

    /// <summary>saved は前回の位置と大きさ（null は既定）、placement は前回の配置。</summary>
    public OutputWindow(OutputWindowState? saved = null, OutputPlacement placement = OutputPlacement.Back)
    {
        _placement = placement;
        if (saved is not null && IsOnScreen(saved.Left, saved.Top))
        {
            _onScreenPosition = new Point(saved.Left, saved.Top);
            _initialClientWidth = Math.Clamp(saved.ClientWidth, 320, 7680);
            _pendingMaximize = saved.Maximized;
        }
        // Teams の共有候補に出る名前。言語を切り替えたら追従する
        Title = Loc.T("Output_Title");
        Loc.Instance.LanguageChanged += () => Title = Loc.T("Output_Title");
        Width = 1280;
        Height = 720;
        Left = _onScreenPosition.X;
        Top = _onScreenPosition.Y;
        Background = Brushes.Black;
        ShowActivated = false;
        ShowInTaskbar = true;
        // 自分で操作する画面ではないので枠なし。移動・最大化はどこをつかんでも・ダブルクリックで
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Content = Host;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        SetClientSize(_initialClientWidth);
        ApplyPlacement();
    }

    /// <summary>保存した位置が、今のモニター構成でも画面内にあるか（タイトルバー相当の部分が見えるか）。</summary>
    private static bool IsOnScreen(double left, double top) =>
        left + 80 > SystemParameters.VirtualScreenLeft && top + 40 > SystemParameters.VirtualScreenTop
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    /// <summary>次回の起動で戻せるよう、今の位置と大きさを返す（画面外にしていたら、画面内に戻すときの位置）。</summary>
    public OutputWindowState CaptureState()
    {
        if (_hwnd == IntPtr.Zero) return new OutputWindowState(_onScreenPosition.X, _onScreenPosition.Y, _initialClientWidth, false);
        if (_placement == OutputPlacement.OffScreen)
        {
            return new OutputWindowState(_onScreenPosition.X, _onScreenPosition.Y,
                _restoreClientWidth > 0 ? _restoreClientWidth : _initialClientWidth, false);
        }
        if (WindowState == WindowState.Maximized)
        {
            // 最大化中は「元に戻したときの」位置と大きさを覚える（枠なしなので外形＝中身）
            var rb = RestoreBounds;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return new OutputWindowState(rb.Left, rb.Top, (int)Math.Round(rb.Width * scale), true);
        }
        Native.GetClientRect(_hwnd, out var cr);
        return new OutputWindowState(Left, Top, cr.Width > 0 ? cr.Width : _initialClientWidth, false);
    }

    /// <summary>
    /// 共有するモニターの解像度を教える。中身の縦横比をそれに合わせる（横幅は今のまま）。
    /// 画面外に置いているときは、解像度ぴったりの大きさにする（実質「画面外で最大化」）。
    /// </summary>
    public void SetCanvasSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _aspect = (double)width / height;
        _canvasWidth = width;
        if (_hwnd == IntPtr.Zero) return;
        if (_placement == OutputPlacement.OffScreen)
        {
            SetClientSize(_canvasWidth);
            return;
        }
        Native.GetClientRect(_hwnd, out var cr);
        SetClientSize(cr.Width > 0 ? cr.Width : DefaultClientWidth);
    }

    // 共有するモニターの横幅（物理ピクセル）
    private int _canvasWidth = 1920;
    // 画面外にする前の中身の横幅（戻すときに使う）。0 は画面外サイズにしていない
    private int _restoreClientWidth;

    /// <summary>中身（クライアント領域）が指定の横幅・今の縦横比になるようウィンドウの大きさを決める。</summary>
    private void SetClientSize(int clientWidth)
    {
        int clientHeight = (int)Math.Round(clientWidth / _aspect);
        var (dx, dy) = GetNonClientSize();
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, clientWidth + dx, clientHeight + dy,
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private int HitTest(IntPtr lParam)
    {
        if (WindowState == WindowState.Maximized) return HTCAPTION;
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        Native.GetWindowRect(_hwnd, out var r);
        bool left = x < r.Left + ResizeGrip, right = x >= r.Right - ResizeGrip;
        bool top = y < r.Top + ResizeGrip, bottom = y >= r.Bottom - ResizeGrip;
        return (top, bottom, left, right) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            _ => HTCAPTION,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public Native.POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private void FillMaximizeToMonitor(IntPtr lParam)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var m = info.rcMonitor;
        // 位置は「そのモニターの左上」からの相対
        mmi.ptMaxPosition = new Native.POINT { X = 0, Y = 0 };
        mmi.ptMaxSize = new Native.POINT { X = m.Width, Y = m.Height };
        mmi.ptMaxTrackSize = new Native.POINT { X = Math.Max(mmi.ptMaxTrackSize.X, m.Width), Y = Math.Max(mmi.ptMaxTrackSize.Y, m.Height) };
        mmi.ptMinTrackSize = new Native.POINT { X = 160, Y = 90 };
        Marshal.StructureToPtr(mmi, lParam, false);
    }

    /// <summary>タイトルバーと枠の分の大きさ（物理ピクセル）。</summary>
    private (int Width, int Height) GetNonClientSize()
    {
        Native.GetWindowRect(_hwnd, out var wr);
        Native.GetClientRect(_hwnd, out var cr);
        return (wr.Width - cr.Width, wr.Height - cr.Height);
    }

    public OutputPlacement Placement
    {
        get => _placement;
        set
        {
            if (_placement == value) return;
            // 画面内の位置を覚えておく（最大化中なら、元に戻したときの位置）
            if (_placement != OutputPlacement.OffScreen)
                _onScreenPosition = WindowState == WindowState.Maximized ? RestoreBounds.TopLeft : new Point(Left, Top);
            _placement = value;
            ApplyPlacement();
        }
    }

    private void ApplyPlacement()
    {
        if (_hwnd == IntPtr.Zero) return;
        // 最小化されているとキャプチャが止まるので戻す
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        if (_placement == OutputPlacement.OffScreen)
        {
            // 最大化は画面外に置けないので、枠なしのまま解像度ぴったりの大きさにする
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            if (_restoreClientWidth == 0)
            {
                Native.GetClientRect(_hwnd, out var cr);
                _restoreClientWidth = cr.Width > 0 ? cr.Width : DefaultClientWidth;
            }
            SetClientSize(_canvasWidth);
        }
        else if (_restoreClientWidth != 0)
        {
            SetClientSize(_restoreClientWidth);
            _restoreClientWidth = 0;
        }

        // 前回最大化していたら、画面内に置くときに最大化し直す
        if (_pendingMaximize && _placement != OutputPlacement.OffScreen)
        {
            _pendingMaximize = false;
            Left = _onScreenPosition.X;
            Top = _onScreenPosition.Y;
            WindowState = WindowState.Maximized;
            if (_placement == OutputPlacement.Back)
                Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }

        switch (_placement)
        {
            case OutputPlacement.OffScreen:
                Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 200;
                Top = SystemParameters.VirtualScreenTop;
                break;
            case OutputPlacement.Back:
                Left = _onScreenPosition.X;
                Top = _onScreenPosition.Y;
                Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                break;
            case OutputPlacement.Normal:
                Left = _onScreenPosition.X;
                Top = _onScreenPosition.Y;
                Activate();
                break;
        }
    }

    /// <summary>モニター構成が変わったときに配置を取り直す（画面外モードで画面内に戻された場合など）。</summary>
    public void ReapplyPlacement() => ApplyPlacement();

    private bool _allowClose;

    /// <summary>アプリ終了時だけ閉じる。ユーザーが閉じると描画先が消えるので拒否する。</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized) LayoutChangedByUser?.Invoke();
        // Win+D 等で最小化されたら映像が止まるので即復元
        if (WindowState == WindowState.Minimized)
            Dispatcher.BeginInvoke(() => WindowState = WindowState.Normal);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_EXITSIZEMOVE)
        {
            LayoutChangedByUser?.Invoke();
        }
        if (msg == WM_NCHITTEST)
        {
            // 端はサイズ変更、それ以外はどこでもタイトルバー扱い（つかんで移動・ダブルクリックで最大化/戻す）
            handled = true;
            return new IntPtr(HitTest(lParam));
        }
        if (msg == WM_GETMINMAXINFO)
        {
            // 最大化はタスクバーも含めたモニター全体に（縦横比がモニターと一致して帯が出ない）
            FillMaximizeToMonitor(lParam);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_SIZING)
        {
            // 端をドラッグしてサイズを変えても中身の縦横比を保つ
            var r = Marshal.PtrToStructure<Native.RECT>(lParam);
            var (dx, dy) = GetNonClientSize();
            int edge = wParam.ToInt32();
            if (edge is WMSZ_TOP or WMSZ_BOTTOM)
            {
                int cw = (int)Math.Round((r.Height - dy) * _aspect);
                r.Right = r.Left + cw + dx;
            }
            else
            {
                int ch = (int)Math.Round((r.Width - dx) / _aspect);
                if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) r.Top = r.Bottom - ch - dy;
                else r.Bottom = r.Top + ch + dy;
            }
            Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return new IntPtr(1);
        }

        if (msg == Native.WM_WINDOWPOSCHANGING && _placement == OutputPlacement.Back)
        {
            // どの操作で前に出されても最背面に固定する
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            pos.hwndInsertAfter = Native.HWND_BOTTOM;
            pos.flags &= ~Native.SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }
}
