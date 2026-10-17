using ShareWinSelector.Interop;

namespace ShareWinSelector;

internal sealed record MonitorInfo(string DeviceName, Native.RECT Bounds, bool IsPrimary)
{
    public string Display =>
        $"{DeviceName.TrimStart('\\', '.')}  {Bounds.Width}x{Bounds.Height}{(IsPrimary ? "  " + Loc.T("Monitor_Primary") : "")}";
    public override string ToString() => Display;
}

internal static class MonitorEnumerator
{
    public static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(hMon, ref info))
                result.Add(new MonitorInfo(info.szDevice, info.rcMonitor, (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
