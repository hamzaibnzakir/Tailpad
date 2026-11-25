using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>
/// TailPad's hidden screen: a virtual monitor (open-source Virtual Display Driver, MIT, signed via SignPath)
/// that exists only while a game is playing in the background. Nothing is drawn on the real screen.
/// </summary>
static class HiddenScreen
{
    public const int Width = 1280, Height = 720, Hz = 60;
    const string HardwareId = @"Root\MttVDD";
    const string AdapterName = "Virtual Display Driver";
    const string Resource = "VirtualDisplayDriver.zip";

    public static bool Bundled => typeof(HiddenScreen).Assembly.GetManifestResourceInfo(Resource) != null;

    static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TailPad", "HiddenScreen");

    // ---------------------------------------------------------------- display enumeration

    [Flags] enum DisplayFlags : int { AttachedToDesktop = 0x1, Primary = 0x4 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public DisplayFlags StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    const int DM_POSITION = 0x20, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const int CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;
    const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, int index, ref DISPLAY_DEVICE dd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsEx")]
    static extern int ApplyDisplaySettings(string? device, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);

    /// <summary>\\.\DISPLAYn of the virtual monitor, and whether it's currently part of the desktop.</summary>
    static (string name, bool attached)? Find()
    {
        for (int i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) return null;
            if (dd.DeviceString.Contains(AdapterName, StringComparison.OrdinalIgnoreCase))
                return (dd.DeviceName, dd.StateFlags.HasFlag(DisplayFlags.AttachedToDesktop));
        }
    }

    public static bool Installed => Find() != null;

    /// <summary>The hidden screen's area on the desktop, if it's switched on.</summary>
    public static Rectangle? Bounds
    {
        get
        {
            var f = Find();
            if (f is not { attached: true }) return null;
            // read straight from Windows (Screen.AllScreens is cached and can be stale right after a change)
            var dm = NewDevMode();
            if (!EnumDisplaySettings(f.Value.name, ENUM_CURRENT_SETTINGS, ref dm) || dm.dmPelsWidth == 0) return null;
            return new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
        }
    }

    /// <summary>Where background-play steps are written (the Activity log on the PC).</summary>
    public static Action<string>? Log;
    static void Note(string line) { try { Log?.Invoke(line); } catch { } }

    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);
    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_APPLY = 0x80;

    /// <summary>Switch the hidden screen on, diagonally off the corner of the real desktop.</summary>
    public static Rectangle On()
    {
        var f = Find() ?? throw new InvalidOperationException("The hidden screen isn't set up yet.");
        var real = RealScreens(f.name);
        var (x, y) = Placement.HiddenScreenPosition(real, Width, Height);
        // already on, in the right place and size → nothing to do (Windows may have switched it on next to
        // the main screen when the driver was installed, so check the position, not just "on")
        if (f.attached && Bounds is { } existing && existing.X == x && existing.Y == y && existing.Width == Width && existing.Height == Height)
            return existing;

        var modes = Modes(f.name);
        bool monitor = HasMonitor(f.name);
        Note($"hidden screen {f.name}: {(f.attached ? "on" : "off")}, monitor {(monitor ? "present" : "missing")}, " +
             $"modes {(modes.Count == 0 ? "none" : string.Join(" ", modes.Take(8).Select(m => $"{m.w}x{m.h}@{m.hz}")))}");
        if (!monitor)
            throw new InvalidOperationException("The hidden screen driver isn't running. On the PC open TailPad → Settings → “Repair hidden screen”.");

        // the size to ask for: 720p if Windows offers it, otherwise the closest 16:9 size it does offer
        var size = (w: Width, h: Height, hz: Hz);
        if (modes.Count > 0 && !modes.Any(m => m.w == Width && m.h == Height))
            size = modes.OrderBy(m => Math.Abs(m.w - Width) + Math.Abs(m.h - Height)).ThenByDescending(m => m.hz).First();

        // a few ways of asking, from most exact to "just put it there"; real PCs differ in what they accept
        var attempts = new (string name, Func<int> run)[]
        {
            ("exact", () => Change(f.name, x, y, size.w, size.h, size.hz, 32, twoStep: true)),
            ("size only", () => Change(f.name, x, y, size.w, size.h, 0, 0, twoStep: true)),
            ("single step", () => Change(f.name, x, y, size.w, size.h, 0, 0, twoStep: false)),
            ("position only", () => Change(f.name, x, y, 0, 0, 0, 0, twoStep: true)),
        };
        int last = 0;
        foreach (var (name, run) in attempts)
        {
            last = run();
            Note($"turn on hidden screen ({name}): Windows code {last}");
            if (last is 0 or 1 && WaitOn() is { } b) return Settle(f.name, x, y, b);
        }

        // last resort on a laptop with just its own screen: "Extend these displays", then move it into place
        if (real.Count <= 1)
        {
            last = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY);
            Note($"turn on hidden screen (extend displays): Windows code {last}");
            if (last == 0 && WaitOn() is { } b) return Settle(f.name, x, y, b);
        }
        throw new InvalidOperationException($"Couldn't turn on the hidden screen (Windows code {last}). The Activity tab on the PC has the details.");
    }

    static int Change(string device, int x, int y, int w, int h, int hz, int bpp, bool twoStep)
    {
        var dm = NewDevMode();
        dm.dmFields = DM_POSITION;
        dm.dmPositionX = x; dm.dmPositionY = y;
        if (w > 0) { dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT; dm.dmPelsWidth = w; dm.dmPelsHeight = h; }
        if (hz > 0) { dm.dmFields |= DM_DISPLAYFREQUENCY; dm.dmDisplayFrequency = hz; }
        if (bpp > 0) { dm.dmFields |= DM_BITSPERPEL; dm.dmBitsPerPel = bpp; }
        if (!twoStep) return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r is not (0 or 1)) return r;
        return ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>Windows needs a moment to bring a new monitor up.</summary>
    static Rectangle? WaitOn()
    {
        for (int i = 0; i < 40; i++)
        {
            if (Bounds is { Width: > 0 } b) return b;
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>Make sure it ended up off the corner (an "extend" or a "position only" may have put it next to the main screen).</summary>
    static Rectangle Settle(string device, int x, int y, Rectangle b)
    {
        var real = RealScreens(device);
        (x, y) = Placement.HiddenScreenPosition(real, b.Width, b.Height);
        if (b.X != x || b.Y != y)
        {
            Change(device, x, y, 0, 0, 0, 0, twoStep: true);
            Thread.Sleep(300);
            b = Bounds ?? b;
        }
        Note($"hidden screen on at {b.X},{b.Y} {b.Width}x{b.Height}");
        return b;
    }

    /// <summary>Picture sizes Windows offers for the hidden screen.</summary>
    static List<(int w, int h, int hz)> Modes(string device)
    {
        var list = new List<(int, int, int)>();
        var dm = NewDevMode();
        for (int i = 0; i < 500 && EnumDisplaySettings(device, i, ref dm); i++)
            if (!list.Contains((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency)))
                list.Add((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        return list;
    }

    /// <summary>Is a (virtual) monitor plugged into the hidden-screen adapter? None = the driver isn't running.</summary>
    static bool HasMonitor(string device)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(device, 0, ref dd, 0);
    }

    /// <summary>Remove the hidden screen from the desktop (windows on it move back to the main screen).</summary>
    public static void Off()
    {
        var f = Find();
        if (f is not { attached: true }) return;
        var dm = NewDevMode();
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        dm.dmPelsWidth = 0; dm.dmPelsHeight = 0;
        ChangeDisplaySettingsEx(f.Value.name, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>Every attached monitor except the hidden one, read fresh from Windows.</summary>
    public static List<Placement.Rect> RealScreens() => RealScreens(Find()?.name);

    static List<Placement.Rect> RealScreens(string? exceptName)
    {
        var list = new List<Placement.Rect>();
        for (int i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if (!dd.StateFlags.HasFlag(DisplayFlags.AttachedToDesktop)) continue;
            if (exceptName != null && dd.DeviceName.Equals(exceptName, StringComparison.OrdinalIgnoreCase)) continue;
            var dm = NewDevMode();
            if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0)
                list.Add(new Placement.Rect(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight));
        }
        return list;
    }

    /// <summary>The main (primary) screen's area — where games come back to.</summary>
    public static Rectangle Primary => new(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)); // primary is always at 0,0

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    static DEVMODE NewDevMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    static void Check(int result, string what)
    {
        // DISP_CHANGE_SUCCESSFUL = 0, DISP_CHANGE_RESTART = 1 (fine for a secondary monitor)
        if (result is 0 or 1) return;
        throw new InvalidOperationException($"Couldn't {what} (Windows code {result}).");
    }

    // ---------------------------------------------------------------- one-time setup (elevated)

    /// <summary>Runs TailPad.exe --setup-hidden-screen as administrator. Returns true when the hidden screen is ready.</summary>
    public static bool SetupWithPrompt(bool repair = false)
    {
        if (Installed && !repair) return true;
        if (!Bundled) throw new InvalidOperationException("This build doesn't include the hidden-screen driver.");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--setup-hidden-screen")
            { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; } // user said No
        for (int i = 0; i < 50 && !Installed; i++) Thread.Sleep(200);
        if (Installed) { try { Off(); } catch { } } // stays off until a game needs it
        return Installed;
    }

    /// <summary>Entry point of the elevated helper. Exit code 0 = ready.</summary>
    public static int InstallElevated()
    {
        try
        {
            var dir = Path.Combine(ConfigDir, "driver");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            using (var res = typeof(HiddenScreen).Assembly.GetManifestResourceStream(Resource)!)
            using (var zip = new ZipArchive(res))
                foreach (var e in zip.Entries.Where(e => e.Length > 0))
                    e.ExtractToFile(Path.Combine(dir, e.Name), true);
            var inf = Directory.GetFiles(dir, "*.inf").Single();

            WriteSettings();
            TrustPublisher(Directory.GetFiles(dir, "*.cat").Single());
            if (!DeviceExists()) CreateRootDevice();
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return 0;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(ConfigDir, "setup-error.txt"), e.ToString()); } catch { }
            return 1;
        }
    }

    /// <summary>One virtual monitor at 720p60 (1080p offered too); settings live in TailPad's folder.</summary>
    static void WriteSettings()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path.Combine(ConfigDir, "vdd_settings.xml"), """
            <?xml version='1.0' encoding='utf-8'?>
            <vdd_settings>
                <monitors><count>1</count></monitors>
                <gpu><friendlyname>default</friendlyname></gpu>
                <global><g_refresh_rate>60</g_refresh_rate></global>
                <resolutions>
                    <resolution><width>1280</width><height>720</height><refresh_rate>60</refresh_rate></resolution>
                    <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
                </resolutions>
                <options>
                    <CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride>
                    <HardwareCursor>true</HardwareCursor><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus>
                    <logging>false</logging><debuglogging>false</debuglogging>
                </options>
            </vdd_settings>
            """);
        using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
        k.SetValue("VDDPATH", ConfigDir);
    }

    /// <summary>
    /// Trust only the driver's signing certificate so Windows installs it without a second
    /// "Would you like to install this device software?" box.
    /// </summary>
    static void TrustPublisher(string cat)
    {
        var certs = new X509Certificate2Collection();
        certs.Import(File.ReadAllBytes(cat));
        var signer = certs.Cast<X509Certificate2>().FirstOrDefault(c =>
            c.Subject.Contains("SignPath Foundation", StringComparison.OrdinalIgnoreCase) &&
            !c.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority));
        if (signer == null) return; // Windows will ask instead
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(signer);
    }

    // ---------------------------------------------------------------- SetupAPI: create the root device

    static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    const int DICD_GENERATE_ID = 0x1, DIF_REGISTERDEVICE = 0x19, SPDRP_HARDWAREID = 0x1, DIGCF_PRESENT = 0x2;
    const int INSTALLFLAG_FORCE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid cls, string? desc, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int prop, byte[] buf, int size);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int prop, out int type, byte[]? buf, int size, out int needed);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int fn, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hwid, string inf, int flags, out bool reboot);

    static bool DeviceExists()
    {
        var cls = DisplayClass;
        var set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0); // present or not
        if (set == new IntPtr(-1)) return false;
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                var buf = new byte[2048];
                if (!SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, out _, buf, buf.Length, out _)) continue;
                var ids = System.Text.Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Any(x => x.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static void CreateRootDevice()
    {
        var cls = DisplayClass;
        var set = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfo(set, "Display", ref cls, null, IntPtr.Zero, DICD_GENERATE_ID, ref d))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var hwid = System.Text.Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, hwid, hwid.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref d))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
