using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>Finding and moving top-level windows.</summary>
static class WindowTools
{
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr p, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    const int TokenElevation = 20;

    /// <summary>When a program started. Uses the "limited" query, which also works for programs running as administrator.</summary>
    public static DateTime StartTimeOf(int pid)
    {
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return DateTime.MinValue;
        try { return GetProcessTimes(p, out var c, out _, out _, out _) ? DateTime.FromFileTime(c) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
        finally { CloseHandle(p); }
    }

    /// <summary>Every program running right now (taken just before a launch, so the game's new process stands out).</summary>
    public static HashSet<int> RunningPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcesses()) { set.Add(p.Id); p.Dispose(); }
        return set;
    }

    /// <summary>Is TailPad itself running as administrator?</summary>
    public static bool SelfElevated => Environment.IsPrivilegedProcess;

    /// <summary>Does the program run as administrator? (If Windows won't even let us look, it does.)</summary>
    public static bool IsElevated(int pid)
    {
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return !SelfElevated;
        try
        {
            if (!OpenProcessToken(p, TOKEN_QUERY, out var tok)) return !SelfElevated;
            try { return GetTokenInformation(tok, TokenElevation, out var e, 4, out _) && e != 0; }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(p); }
    }
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GW_OWNER = 4, DWMWA_CLOAKED = 14;
    const long WS_EX_TOOLWINDOW = 0x80, WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
    const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;
    const int SW_RESTORE = 9, SW_SHOWNOACTIVATE = 4;

    public static bool Exists(IntPtr h) => h != IntPtr.Zero && IsWindow(h);

    public static IntPtr Foreground() => GetForegroundWindow();

    public static Rectangle RectOf(IntPtr h) => GetWindowRect(h, out var r) ? Rectangle.FromLTRB(r.L, r.T, r.R, r.B) : Rectangle.Empty;

    public static int ProcessOf(IntPtr h) { GetWindowThreadProcessId(h, out var pid); return (int)pid; }

    public static string ExeOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; } catch { return ""; }
    }

    public static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Snapshot of every top-level window, in the shape Placement.PickGameWindow understands.</summary>
    public static List<Placement.WindowInfo> All()
    {
        var list = new List<Placement.WindowInfo>();
        var started = new Dictionary<int, (string exe, DateTime at)>();
        EnumWindows((h, _) =>
        {
            int pid = ProcessOf(h);
            if (pid == Environment.ProcessId || pid == 0) return true;
            if (!started.TryGetValue(pid, out var info))
            {
                info = (ExeOf(pid), StartTimeOf(pid));
                started[pid] = info;
            }
            var r = RectOf(h);
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            bool cloaked = DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var c, 4) == 0 && c != 0;
            list.Add(new Placement.WindowInfo(h, pid, info.exe, TitleOf(h),
                new Placement.Rect(r.X, r.Y, r.Width, r.Height),
                IsWindowVisible(h) && !IsIconic(h) && !cloaked,
                (ex & WS_EX_TOOLWINDOW) != 0,
                GetWindow(h, GW_OWNER) != IntPtr.Zero,
                info.at));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Make the window's picture fill <paramref name="area"/> exactly, without activating it: the window is made
    /// borderless (no title bar in the stream). Returns the original style so <see cref="Restore"/> can put the frame back.
    /// Games resize their picture to the new size in most cases.
    /// </summary>
    public static long MoveTo(IntPtr h, Rectangle area)
    {
        if (IsZoomed(h) || IsIconic(h)) ShowWindow(h, SW_SHOWNOACTIVATE);
        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
        long borderless = style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        if (borderless != style) SetWindowLongPtr(h, GWL_STYLE, new IntPtr(borderless));
        // top of the hidden screen's stack, without activating it (it can't cover anything on your real screen)
        SetWindowPos(h, IntPtr.Zero /* HWND_TOP */, area.X, area.Y, area.Width, area.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        return style;
    }

    /// <summary>The window's picture (client area) in screen coordinates.</summary>
    public static Rectangle ClientOnScreen(IntPtr h)
    {
        if (!GetClientRect(h, out var r)) return Rectangle.Empty;
        var p = new POINT();
        ClientToScreen(h, ref p);
        return new Rectangle(p.X, p.Y, r.R - r.L, r.B - r.T);
    }

    /// <summary>Windows puts a taskbar on every screen; hide the one on the hidden screen so it's not in the game.</summary>
    public static void HideTaskbarsOn(Rectangle area)
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            if (IsWindowVisible(h) && RectOf(h).IntersectsWith(area)) ShowWindow(h, 0 /* SW_HIDE */);
    }

    public static bool TaskbarVisibleOn(Rectangle area)
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            if (IsWindowVisible(h) && RectOf(h).IntersectsWith(area)) return true;
        return false;
    }

    /// <summary>Seconds since the person last used the PC's own keyboard or mouse.</summary>
    public static double IdleSeconds()
    {
        var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref i)) return double.MaxValue;
        return unchecked((uint)Environment.TickCount - i.dwTime) / 1000.0;
    }

    /// <summary>Hand focus back to a window (Windows only allows this right after a key event, hence the Alt tap).</summary>
    public static void Focus(IntPtr h)
    {
        if (!Exists(h)) return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);       // Alt down
        keybd_event(0x12, 0, 0x2, UIntPtr.Zero);     // Alt up
        SetForegroundWindow(h);
    }

    /// <summary>Put a window back where it was before, without stealing focus from what the user is doing.</summary>
    public static void Restore(IntPtr h, Rectangle was, long style = 0)
    {
        if (style != 0) SetWindowLongPtr(h, GWL_STYLE, new IntPtr(style));
        if (IsZoomed(h)) ShowWindow(h, SW_RESTORE);
        SetWindowPos(h, IntPtr.Zero, was.X, was.Y, was.Width, was.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }
}
