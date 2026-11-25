namespace TailPad.Host;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--setup-hidden-screen") { Environment.Exit(HiddenScreen.InstallElevated()); return; }
        if (args.Length >= 1 && args[0] == "--test-window") { ApplicationConfiguration.Initialize(); SelfTest.TestWindow(); return; }
        if (args.Length >= 2 && args[0] == "--selftest-hidden") { ApplicationConfiguration.Initialize(); Environment.Exit(SelfTest.Run(args[1])); return; }
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            ApplicationConfiguration.Initialize();
            Screenshots.Run(args[1]);
            return;
        }
        using var mutex = new Mutex(true, @"Local\TailPad.Host", out var first);
        if (!first)
        {
            MessageBox.Show("TailPad is already running — look for the gamepad icon in the system tray.", "TailPad",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        var startHidden = args.Contains("--tray");
        Application.Run(new TrayApp(startHidden));
    }
}
