using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JaClipei.Client.Services;

public class WindowInfo
{
    public IntPtr Handle  { get; init; }
    public string Title   { get; init; } = "";
    public string Process { get; init; } = "";
    public override string ToString() => Title;
}

public class MonitorInfo
{
    public int    Index       { get; init; }
    public string DisplayName { get; init; } = "";
    public System.Windows.Rect Bounds { get; init; }
    public bool   IsPrimary   { get; init; }
    public override string ToString() => DisplayName;
}

public static class WindowEnumService
{
    // ── P/Invoke janelas ──────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();

    // ── P/Invoke monitores ────────────────────────────────────────────────

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int    cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
    private const uint MONITORINFOF_PRIMARY = 1;

    // ── Janelas ───────────────────────────────────────────────────────────

    public static List<WindowInfo> GetVisibleWindows(bool excludeSelf = true)
    {
        var result  = new List<WindowInfo>();
        var shell   = GetShellWindow();
        var selfPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell)          return true;
            if (!IsWindowVisible(hWnd)) return true;
            if (IsIconic(hWnd))         return true;

            var sb = new StringBuilder(256);
            if (GetWindowText(hWnd, sb, 256) == 0) return true;
            var title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (excludeSelf && pid == selfPid) return true;

            string procName = "";
            try { procName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }

            result.Add(new WindowInfo { Handle = hWnd, Title = title, Process = procName });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ── Monitores (sem WinForms) ──────────────────────────────────────────

    public static List<MonitorInfo> GetMonitors()
    {
        var list  = new List<MonitorInfo>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, ref rc, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                list.Add(new MonitorInfo
                {
                    Index       = index++,
                    DisplayName = primary ? $"Monitor {index} (Principal)" : $"Monitor {index}",
                    Bounds      = new System.Windows.Rect(
                        info.rcMonitor.Left, info.rcMonitor.Top,
                        info.rcMonitor.Right  - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top),
                    IsPrimary   = primary
                });
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }
}
