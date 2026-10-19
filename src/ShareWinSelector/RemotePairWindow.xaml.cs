using System.Windows;
using System.Windows.Interop;

namespace ShareWinSelector;

/// <summary>
/// リモート連携のペアリング: 初めての相手とつなぐときに、親と子の両方の画面に同じ番号を出して、許可するかを聞く。
/// 答え（許可＝true）は Result で受け取る。閉じたり、時間が過ぎたり、相手が切れたり（cancel）したら拒否。
/// </summary>
public partial class RemotePairWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly CancellationTokenRegistration _cancelRegistration;
    private bool _closed;

    /// <summary>許可されたら true、拒否・閉じたら false。</summary>
    public Task<bool> Result => _result.Task;

    /// <param name="anchor">この窓の上に重ねて出す窓（特殊設定かメイン画面）。無ければ画面の中央。</param>
    internal RemotePairWindow(string message, string code, CancellationToken cancel, Window? anchor = null)
    {
        InitializeComponent();
        if (anchor is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Loaded += (_, _) => PlaceOver(anchor);
        }
        MessageText.Text = message;
        // 6 桁は 3 桁ずつに分けて見比べやすく
        CodeText.Text = code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
        // 待つのは 60 秒まで。それを過ぎたら答えても届かないので閉じる
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(58) };
        timeout.Tick += (_, _) => { timeout.Stop(); Close(); };
        timeout.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            timeout.Stop();
            _cancelRegistration.Dispose();
            _result.TrySetResult(false);
        };
        // 相手が切れた・リモート連携を止めたときも閉じる
        _cancelRegistration = cancel.Register(() => Dispatcher.BeginInvoke(() => { if (!_closed) Close(); }));
        // 自分専用の画面なので、モニターごと共有されても映らないようにする
        SourceInitialized += (_, _) => Interop.Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// anchor の真ん中に重ねて置く（いま見ている画面に出す）。anchor のモニターの作業領域からははみ出さない。
    /// 先に anchor のモニターへ動かしてから大きさを測り直す（モニターごとに拡大率が違うと、動かしたときに大きさが変わるため）。
    /// </summary>
    private void PlaceOver(Window anchor)
    {
        var anchorHwnd = new WindowInteropHelper(anchor).Handle;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (anchorHwnd == IntPtr.Zero || hwnd == IntPtr.Zero || !Interop.Native.GetWindowRect(anchorHwnd, out var a)) return;
        const uint flags = Interop.Native.SWP_NOSIZE | Interop.Native.SWP_NOZORDER | Interop.Native.SWP_NOACTIVATE;
        Interop.Native.SetWindowPos(hwnd, IntPtr.Zero, a.Left, a.Top, 0, 0, flags);
        if (!Interop.Native.GetWindowRect(hwnd, out var me)) return;
        int width = me.Right - me.Left, height = me.Bottom - me.Top;
        int x = a.Left + (a.Right - a.Left - width) / 2, y = a.Top + (a.Bottom - a.Top - height) / 2;
        var info = new Interop.Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Interop.Native.MONITORINFOEX>() };
        if (Interop.Native.GetMonitorInfo(Interop.Native.MonitorFromWindow(anchorHwnd, Interop.Native.MONITOR_DEFAULTTONEAREST), ref info))
        {
            var work = info.rcWork;
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        }
        Interop.Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags);
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e) => Close();
}
