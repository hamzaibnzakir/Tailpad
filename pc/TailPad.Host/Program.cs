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
        // "Run TailPad as administrator" is on → come back elevated (if the Yes is skipped, carry on as normal)
        if (!Environment.IsPrivilegedProcess && !args.Contains("--no-elevate"))
        {
            bool wantAdmin = false;
            try { wantAdmin = TailPad.Core.Config.Load().RunAsAdmin; } catch { }
            if (wantAdmin && Elevation.Relaunch(args)) return;
        }
        using var mutex = new Mutex(true, @"Local\TailPad.Host", out var first);
        // after "restart as administrator" the old copy may still be closing
        if (!first && args.Contains("--restarted"))
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { first = true; }
        }
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

static class Elevation
{
    /// <summary>Start TailPad again as administrator. False if the Windows prompt was declined.</summary>
    public static bool Relaunch(IEnumerable<string> args)
    {
        var keep = args.Where(a => a == "--tray").Append("--restarted");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", keep))
            { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // "No" at the prompt
    }
}
