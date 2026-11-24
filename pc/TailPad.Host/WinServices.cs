using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>Windows-side pieces the server needs: mouse output, game library, launching, notifications.</summary>
public sealed class WinServices : IHostServices
{
    readonly Config _cfg;
    readonly Action<string> _notify;
    List<GameEntry> _games = new();
    DateTime _scanned = DateTime.MinValue;
    float _remX, _remY;
    readonly object _gate = new();

    public WinServices(Config cfg, Action<string> notify) { _cfg = cfg; _notify = notify; }

    public void Notify(string message) => _notify(message);

    public IReadOnlyList<GameEntry> Games()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _scanned > TimeSpan.FromSeconds(30)) Rescan();
            return _games;
        }
    }

    public void Rescan()
    {
        lock (_gate)
        {
            List<CustomGame> custom;
            lock (_cfg) custom = _cfg.CustomGames.ToList();
            _games = GameLibrary.Scan(SteamRoot(), EpicManifests(), custom);
            _scanned = DateTime.UtcNow;
        }
    }

    static string? SteamRoot()
    {
        try
        {
            var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\');
        }
        catch { }
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(def) ? def : null;
    }

    static string EpicManifests() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public void Launch(GameEntry game)
    {
        var parts = game.Target.Split('\0');
        var psi = new ProcessStartInfo(parts[0]) { UseShellExecute = true };
        if (parts.Length > 1 && parts[1].Length > 0) psi.Arguments = parts[1];
        if (game.Source == "PC" && File.Exists(parts[0])) psi.WorkingDirectory = Path.GetDirectoryName(parts[0]);
        Process.Start(psi);
    }

    // ---- relative mouse (gyro aim / touchpad) ----
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    public void MoveMouse(float dx, float dy)
    {
        int ix, iy;
        lock (_gate)
        {
            _remX += dx; _remY += dy;
            ix = (int)_remX; iy = (int)_remY;
            _remX -= ix; _remY -= iy;
        }
        if (ix == 0 && iy == 0) return;
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dx = ix, dy = iy, dwFlags = 0x0001 } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}

public static class Tailnet
{
    /// <summary>This PC's Tailscale IPv4 (100.64.0.0/10), if Tailscale is up.</summary>
    public static string? Ip()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && HostServer.IsTailnet(a.Address)
                        && !System.Net.IPAddress.IsLoopback(a.Address))
                        return a.Address.ToString();
            }
        }
        catch { }
        return null;
    }
}

public static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true) ?? Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue("TailPad", $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue("TailPad", false);
    }
}

/// <summary>Reports which app/game is focused so the phone can switch to that game's layout.</summary>
public sealed class ForegroundWatcher : IDisposable
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);

    readonly System.Threading.Timer _timer;
    readonly Action<string, string> _changed;
    string _last = "";
    static readonly int Self = Environment.ProcessId;

    public ForegroundWatcher(Action<string, string> changed)
    {
        _changed = changed;
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 1000);
    }

    void Tick()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0 || pid == Self) return;
            string exe;
            using (var p = Process.GetProcessById((int)pid)) exe = p.ProcessName + ".exe";
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            var key = exe.ToLowerInvariant();
            if (key == _last) return; // only react when the focused program changes
            _last = key;
            _changed(exe.ToLowerInvariant(), sb.ToString());
        }
        catch { }
    }

    public void Dispose() => _timer.Dispose();
}
