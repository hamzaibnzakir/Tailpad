namespace TailPad.Host;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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
