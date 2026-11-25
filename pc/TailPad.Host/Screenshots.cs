using TailPad.Core;

namespace TailPad.Host;

/// <summary>
/// `TailPad.exe --screenshot <folder>` renders each tab of the main window to PNG (used by CI to preview the UI).
/// Uses sample data and never opens network ports or touches the driver.
/// </summary>
static class Screenshots
{
    sealed class DemoHost : IMainHost
    {
        public void RestartAsAdmin() { }
        public IReadOnlyList<string> LogLines { get; } = new[]
        {
            "13:42:07  Listening on TCP 47800 / UDP 47801",
            "13:42:31  Paired Hamza's iPhone",
            "13:42:31  Hamza's iPhone connected (100.101.7.22) as Xbox controller",
            "13:44:02  Hamza's iPhone launched ELDEN RING",
        };
        public bool EnsureDriver(bool askFirst) => false;
    }

    sealed class DemoPads : IPadFactory
    {
        public IVirtualPad Create(PadType type) => throw new NotSupportedException();
    }

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Tailnet.Override = "100.88.12.40";
        var cfg = new Config
        {
            PairingCode = "482913",
            Devices = { new PairedDevice { Id = "a", Name = "Hamza's iPhone", LastSeen = DateTime.Now } },
            CustomGames = { new CustomGame { Name = "Dolphin Emulator", Path = @"C:\Emu\Dolphin\Dolphin.exe" } },
            ScannedGames =
            {
                new ScannedGame { Name = "Grand Theft Auto V", Path = @"D:\Games\Grand.Theft.Auto.V-FitGirl\PlayGTAV.exe" },
                new ScannedGame { Name = "eFootball", Path = @"D:\Games\eFootball\eFootball.exe" },
                new ScannedGame { Name = "ELDEN RING", Path = @"D:\Games\ELDEN RING\eldenring.exe" },
                new ScannedGame { Name = "Forza Horizon 5", Path = @"D:\Games\FH5\ForzaHorizon5.exe" },
                new ScannedGame { Name = "Street Fighter 6", Path = @"D:\Games\SF6\StreetFighter6.exe" },
            },
            LastScan = DateTime.Now,
        };
        var services = new WinServices(cfg, _ => { });
        var server = new HostServer(cfg, new DemoPads(), services);
        using var pads = new ViGEmPadFactory();
        var form = new MainForm(cfg, server, services, pads, new DemoHost(), AppIcon.Make(true))
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            ShowInTaskbar = false,
        };
        form.Show();
        string[] names = { "1-phones", "2-games", "3-settings", "4-activity" };
        for (int i = 0; i < names.Length; i++)
        {
            form.SelectTab(i);
            for (int k = 0; k < 10; k++) { Application.DoEvents(); Thread.Sleep(30); }
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, $"pc-{names[i]}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Close();
    }
}
