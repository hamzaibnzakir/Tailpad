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

    public IDisposable StartStream(StreamRequest request, Action<byte[]> send, Action<System.Text.Json.Nodes.JsonObject> status)
        => new RemotePlaySession(request, send, status, _bg);

    BackgroundPlay? _bg;
    public IBackgroundPlay Background => (IBackgroundPlay?)_bg ?? _none;
    readonly NoBackgroundPlay _none = new();

    /// <summary>Turn on background play (Windows only; done by the tray app at startup).</summary>
    internal BackgroundPlay EnableBackground() => _bg ??= new BackgroundPlay(_cfg, this);

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
            FindShortcuts();
            List<CustomGame> custom; List<ScannedGame> scanned; HashSet<string> hidden; Dictionary<string, string> overrides;
            lock (_cfg)
            {
                custom = _cfg.CustomGames.ToList();
                scanned = _cfg.ScannedGames.ToList();
                hidden = new HashSet<string>(_cfg.HiddenGames);
                overrides = new Dictionary<string, string>(_cfg.LaunchOverrides);
            }
            _games = GameLibrary.Scan(SteamRoot(), EpicManifests(), custom, scanned, hidden, overrides)
                .Select(g => g.Art == null && g.Source == "PC" ? g with { Art = IconFor(g.GameExe ?? g.StartPath) } : g)
                .ToList();
            _scanned = DateTime.UtcNow;
        }
    }

    /// <summary>Start found games the way you do: through their desktop / Start Menu shortcut when there is one.</summary>
    void FindShortcuts()
    {
        List<ScannedGame> todo;
        lock (_cfg) todo = _cfg.ScannedGames.Where(g => g.Launch == null).ToList();
        if (todo.Count == 0) return;
        List<Shortcuts.Link> links;
        try { links = ShortcutReader.All(); } catch { return; }
        lock (_cfg)
        {
            foreach (var g in todo)
            {
                var folder = Path.GetDirectoryName(g.Path) ?? "";
                g.Launch = Shortcuts.Choose(folder, g.Name, links)?.Path ?? ""; // "" = looked, none found
            }
            _cfg.Save();
        }
    }

    static List<Shortcuts.Link> SafeShortcuts() { try { return ShortcutReader.All(); } catch { return new(); } }

    /// <summary>"Change start file…": start this game with a file you pick (its launcher, a shortcut, …).</summary>
    public void SetStartFile(GameEntry g, string? path)
    {
        lock (_cfg)
        {
            if (string.IsNullOrWhiteSpace(path)) _cfg.LaunchOverrides.Remove(g.Id);
            else _cfg.LaunchOverrides[g.Id] = path;
            _cfg.Save();
        }
        Rescan();
    }

    // ---- whole-PC game scan ----

    public bool Scanning { get; private set; }

    /// <summary>Looks through every local drive for games. Safe to call from a background thread.</summary>
    public int ScanPc(CancellationToken ct = default)
    {
        if (Scanning) return -1;
        Scanning = true;
        try
        {
            var roots = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName).ToList();

            var exclude = new List<string>();
            var steam = SteamRoot();
            if (steam != null) exclude.AddRange(GameLibrary.SteamLibraries(steam).Select(l => Path.Combine(l, "steamapps")));
            exclude.AddRange(GameLibrary.EpicInstallFolders(EpicManifests()));

            var found = GameScanner.Scan(roots, new GameScanner.Options
            {
                InstalledNames = InstalledPrograms(),
                ExcludeFolders = exclude,
            }, ct);

            lock (_cfg)
            {
                var links = SafeShortcuts();
                _cfg.ScannedGames = found.Select(f => new ScannedGame
                {
                    Name = f.Name, Path = f.Exe,
                    Launch = Shortcuts.Choose(f.Folder, f.Name, links)?.Path ?? "",
                }).ToList();
                _cfg.LastScan = DateTime.Now;
                _cfg.Save();
            }
            Rescan();
            return found.Count;
        }
        finally { Scanning = false; }
    }

    /// <summary>Install folder → name, from Windows' "installed apps" list (gives repacks their proper names).</summary>
    static Dictionary<string, string> InstalledPrograms()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey hive, string path)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k == null) continue;
                    var name = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    var loc = k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(loc) && k.GetValue("DisplayIcon") is string icon)
                    {
                        icon = icon.Split(',')[0].Trim('"', ' ');
                        if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) loc = Path.GetDirectoryName(icon);
                    }
                    if (string.IsNullOrWhiteSpace(loc)) continue;
                    loc = loc.Trim('"', ' ').TrimEnd('\\', '/');
                    map.TryAdd(loc, name);
                }
            }
            catch { }
        }
        const string U = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(Registry.LocalMachine, U);
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        Read(Registry.CurrentUser, U);
        return map;
    }

    public void Hide(GameEntry g)
    {
        lock (_cfg)
        {
            if (!_cfg.HiddenGames.Contains(g.Id)) _cfg.HiddenGames.Add(g.Id);
            _cfg.Save();
        }
        Rescan();
    }

    public void UnhideAll()
    {
        lock (_cfg) { _cfg.HiddenGames.Clear(); _cfg.Save(); }
        Rescan();
    }

    // ---- game icons for the phone (small PNG as a data: URL) ----

    readonly Dictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);

    string? IconFor(string exe)
    {
        if (_icons.TryGetValue(exe, out var cached)) return cached;
        string? url = null;
        try
        {
            if (File.Exists(exe))
            {
                using var icon = Icon.ExtractIcon(exe, 0, 96) ?? Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                {
                    using var bmp = icon.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    url = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch { }
        _icons[exe] = url;
        return url;
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
        // shortcuts (.lnk/.url) carry their own target, arguments and start folder; Windows opens them like a double-click
        var start = game.StartPath;
        var psi = new ProcessStartInfo(start) { UseShellExecute = true };
        if (game.StartArgs.Length > 0) psi.Arguments = game.StartArgs;
        if (start.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(start)) psi.WorkingDirectory = Path.GetDirectoryName(start);
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
    /// <summary>Only used by --screenshot mode.</summary>
    public static string? Override;

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
