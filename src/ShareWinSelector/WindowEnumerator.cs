using System.Diagnostics;
using ShareWinSelector.Interop;

namespace ShareWinSelector;

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    /// <summary>一覧に出した時点の本人確認情報。</summary>
    internal WindowIdentity Identity { get; init; }

    public string Display => $"{Title}  [{ProcessName}]";
    public override string ToString() => Display;
}

/// <summary>画面に見えているトップレベルウィンドウ 1 つ分（物理ピクセル矩形付き）。</summary>
internal readonly record struct VisibleWindow(IntPtr Handle, Native.RECT Bounds, long ExStyle)
{
    public bool IsClickThrough => (ExStyle & Native.WS_EX_TRANSPARENT) != 0;
    public bool Contains(int x, int y) => x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;
}

/// <summary>
/// ウィンドウの「本人確認」用の情報。HWND は閉じたあとすぐ別のウィンドウに使い回されるので、
/// 作成したスレッド・プロセスも合わせて覚え、同じウィンドウかどうかを照合する。
/// </summary>
internal readonly record struct WindowIdentity(IntPtr Handle, uint ProcessId, uint ThreadId)
{
    public static WindowIdentity Of(IntPtr hWnd)
    {
        uint tid = Native.GetWindowThreadProcessId(hWnd, out uint pid);
        return new WindowIdentity(hWnd, pid, tid);
    }

    /// <summary>まだ存在し、かつ同じプロセス・スレッドのウィンドウか（別アプリに使い回されていないか）。</summary>
    public bool IsAlive()
    {
        if (Handle == IntPtr.Zero || !Native.IsWindow(Handle)) return false;
        uint tid = Native.GetWindowThreadProcessId(Handle, out uint pid);
        return tid == ThreadId && pid == ProcessId;
    }
}

internal static class WindowEnumerator
{
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Alt+Tab に出るような、キャプチャ対象になり得るトップレベルウィンドウを列挙する。</summary>
    public static List<WindowInfo> GetCapturableWindows()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((hWnd, _) =>
        {
            if (!IsCapturable(hWnd)) return true;
            var identity = WindowIdentity.Of(hWnd);
            result.Add(new WindowInfo(hWnd, Native.GetTitle(hWnd), GetProcessName(identity.ProcessId)) { Identity = identity });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>一覧に出す対象（自プロセス以外の、Alt+Tab 的なメインウィンドウ）か。</summary>
    public static bool IsCapturable(IntPtr hWnd)
    {
        if (!Native.IsWindowVisible(hWnd)) return false;
        if (Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        long exStyle = Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE);
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.IsCloaked(hWnd)) return false;
        if (string.IsNullOrWhiteSpace(Native.GetTitle(hWnd))) return false;
        Native.GetWindowThreadProcessId(hWnd, out uint pid);
        return pid != OwnPid;
    }

    /// <summary>実際に画面に出ているトップレベルウィンドウを手前 → 奥の順で返す。</summary>
    public static List<VisibleWindow> GetVisibleWindowsTopDown()
    {
        var result = new List<VisibleWindow>();
        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd) || Native.IsCloaked(hWnd)) return true;
            if (!Native.TryGetFrameBounds(hWnd, out var rc) || rc.Width <= 0 || rc.Height <= 0) return true;
            result.Add(new VisibleWindow(hWnd, rc, Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// ウィンドウが指定モニター上にあるか（一部でも重なっていれば true）。
    /// 最小化中は元に戻したときの位置で判定する。
    /// </summary>
    public static bool IsOnMonitor(IntPtr hWnd, Native.RECT monitor)
    {
        Native.RECT r;
        if (Native.IsIconic(hWnd))
        {
            var placement = new Native.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (!Native.GetWindowPlacement(hWnd, ref placement)) return true; // 分からなければ出しておく
            r = placement.rcNormalPosition;
        }
        else if (!Native.TryGetFrameBounds(hWnd, out r))
        {
            return true;
        }
        return r.Left < monitor.Right && r.Right > monitor.Left && r.Top < monitor.Bottom && r.Bottom > monitor.Top;
    }

    /// <summary>所有者をたどった一番上のウィンドウ（ダイアログ → 本体）。</summary>
    public static IntPtr GetRootOwner(IntPtr hWnd)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            IntPtr owner = Native.GetWindow(hWnd, Native.GW_OWNER);
            if (owner == IntPtr.Zero) break;
            hWnd = owner;
        }
        return hWnd;
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "?";
        }
    }
}
