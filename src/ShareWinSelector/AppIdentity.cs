using System.IO;
using System.Runtime.InteropServices;

namespace ShareWinSelector;

/// <summary>
/// クロップ量などを「アプリごと」に覚えるためのキー。
/// ウィンドウのアプリ ID（AppUserModelID）があればそれを使う。PWA やストアアプリは実行ファイルが
/// ブラウザ本体（chrome.exe 等）や ApplicationFrameHost.exe と同じなので、ID で区別しないと混ざる。
/// 無ければ実行ファイル名。
/// </summary>
internal static class AppIdentity
{
    public static string GetKey(IntPtr hWnd, string processName)
    {
        string? aumid = TryGetAppUserModelId(hWnd);
        if (!string.IsNullOrWhiteSpace(aumid)) return "aumid:" + aumid;
        if (processName != "?") return "exe:" + processName.ToLowerInvariant();

        // プロセス名が取れなかった（権限など）: 実行ファイルのパスから取り直す。
        // それも無理なら、そのプロセスだけのキーにする（不明なアプリ同士で設定が混ざらないように）
        Interop.Native.GetWindowThreadProcessId(hWnd, out uint pid);
        string? image = TryGetProcessImageName(pid);
        return image is not null
            ? "exe:" + Path.GetFileNameWithoutExtension(image).ToLowerInvariant()
            : $"pid:{pid}";
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? TryGetProcessImageName(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid FormatId = fmtid;
        public readonly uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private static string? TryGetAppUserModelId(IntPtr hWnd)
    {
        try
        {
            if (SHGetPropertyStoreForWindow(hWnd, IID_IPropertyStore, out var store) != 0 || store is null) return null;
            try
            {
                store.GetValue(AppUserModelIdKey, out var value);
                try
                {
                    return value.vt == VT_LPWSTR && value.p != IntPtr.Zero ? Marshal.PtrToStringUni(value.p) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }
}
