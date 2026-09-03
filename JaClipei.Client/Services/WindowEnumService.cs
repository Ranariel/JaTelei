using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JaClipei.Client.Services;

public class WindowInfo
{
    public IntPtr Handle    { get; init; }
    public string Title     { get; init; } = "";
    public string Process   { get; init; } = "";
    public override string ToString() => Title;
}

public class MonitorInfo
{
    public int    Index       { get; init; }
    public string DisplayName { get; init; } = "";
    public System.Windows.Rect Bounds { get; init; }
    public bool   IsPrimary  { get; init; }
    public override string ToString() => DisplayName;
}

public static class WindowEnumService
{
    // ── P/Invoke ──────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    // ── Janelas ───────────────────────────────────────────────────────────

    public static List<WindowInfo> GetVisibleWindows(bool excludeSelf = true)
    {
        var result  = new List<WindowInfo>();
        var shell   = GetShellWindow();
        var selfPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell)         return true;
            if (!IsWindowVisible(hWnd)) return true;
            if (IsIconic(hWnd))         return true;   // minimizada

            var sb = new StringBuilder(256);
            if (GetWindowText(hWnd, sb, 256) == 0) return true;
            var title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (excludeSelf && pid == selfPid) return true;

            string procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

            result.Add(new WindowInfo { Handle = hWnd, Title = title, Process = procName });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ── Monitores ─────────────────────────────────────────────────────────

    public static List<MonitorInfo> GetMonitors()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var list    = new List<MonitorInfo>();
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new MonitorInfo
            {
                Index       = i,
                DisplayName = s.Primary ? $"Monitor {i + 1} (Principal)" : $"Monitor {i + 1}",
                Bounds      = new System.Windows.Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
                IsPrimary   = s.Primary
            });
        }
        return list;
    }
}
