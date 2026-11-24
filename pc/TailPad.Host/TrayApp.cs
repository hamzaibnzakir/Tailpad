using System.Diagnostics;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>Lives in the system tray; owns the server and opens the main window on demand.</summary>
sealed class TrayApp : ApplicationContext, IMainHost
{
    readonly NotifyIcon _tray;
    readonly Config _cfg;
    readonly ViGEmPadFactory _pads = new();
    readonly WinServices _services;
    readonly HostServer _server;
    readonly ForegroundWatcher _fg;
    readonly Icon _iconIdle = AppIcon.Make(false), _iconOn = AppIcon.Make(true);
    readonly SynchronizationContext _ui;
    MainForm? _form;
    readonly List<string> _log = new();

    public TrayApp(bool startHidden)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _cfg = Config.Load();
        _services = new WinServices(_cfg, msg => Balloon(msg));
        _server = new HostServer(_cfg, _pads, _services);
        _server.Log += AddLog;
        _server.SessionsChanged += () => _ui.Post(_ => RefreshTray(), null);

        _tray = new NotifyIcon { Icon = _iconIdle, Text = "TailPad", Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        BuildMenu();

        try { _server.Start(); }
        catch (Exception e)
        {
            MessageBox.Show($"TailPad couldn't open its network ports ({_cfg.ControlPort}/{_cfg.InputPort}).\n\n{e.Message}",
                "TailPad", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        if (!_pads.TryInit()) EnsureDriver(askFirst: true);
        _fg = new ForegroundWatcher((exe, title) => _server.BroadcastForeground(exe, title));
        Task.Run(_services.Rescan);

        if (!startHidden || !_pads.TryInit()) ShowWindow();
        else Balloon($"TailPad running — pairing code {_cfg.PairingCode}");
    }

    /// <summary>One-time setup of the bundled controller driver. Returns true when controllers work.</summary>
    public bool EnsureDriver(bool askFirst)
    {
        if (_pads.TryInit()) return true;
        if (!DriverSetup.Bundled)
        {
            AddLog("Controller driver missing and not bundled in this build");
            return false;
        }
        if (askFirst && MessageBox.Show(
                "TailPad needs to set up its controller driver once, so games can see your phone as a controller.\n\n" +
                "Windows will ask for permission — click Yes. It takes a few seconds.",
                "TailPad — one-time setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
        {
            AddLog("Driver setup skipped — open TailPad and click 'Set up controller driver' when ready");
            return false;
        }
        AddLog("Setting up the controller driver…");
        var r = DriverSetup.Install();
        if (r == DriverSetup.Result.Installed && _pads.TryInit())
        {
            AddLog("Controller driver ready ✓");
            Balloon("Controller driver ready — connect your phone");
            _form?.RefreshDriver();
            return true;
        }
        var msg = r switch
        {
            DriverSetup.Result.Declined => "Setup was cancelled at the Windows permission prompt. Click 'Set up controller driver' to try again.",
            DriverSetup.Result.Installed => "The driver installed. Restart your PC once, then open TailPad again.",
            _ => "The driver setup didn't finish. Click 'Set up controller driver' to try again.",
        };
        AddLog(msg);
        MessageBox.Show(msg, "TailPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _form?.RefreshDriver();
        return false;
    }

    void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TailPad", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        var status = new ToolStripMenuItem("No phones connected") { Enabled = false, Name = "status" };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Opening += (_, _) => status.Text = StatusText();
        _tray.ContextMenuStrip = menu;
    }

    string StatusText()
    {
        var n = _server.Sessions.Count;
        return n == 0 ? "No phones connected" : n == 1 ? "1 phone connected" : $"{n} phones connected";
    }

    void RefreshTray()
    {
        var any = _server.Sessions.Count > 0;
        _tray.Icon = any ? _iconOn : _iconIdle;
        _tray.Text = "TailPad — " + StatusText();
        _form?.RefreshSessions();
    }

    void AddLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_log) { _log.Add(stamped); if (_log.Count > 200) _log.RemoveAt(0); }
        _ui.Post(_ => _form?.AppendLog(stamped), null);
    }

    public IReadOnlyList<string> LogLines { get { lock (_log) return _log.ToList(); } }

    void Balloon(string msg) => _ui.Post(_ => _tray.ShowBalloonTip(3000, "TailPad", msg, ToolTipIcon.Info), null);

    void ShowWindow()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(_cfg, _server, _services, _pads, this, _iconOn);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Quit()
    {
        _tray.Visible = false;
        _fg.Dispose();
        _server.Dispose();
        _pads.Dispose();
        _form?.Close();
        ExitThread();
    }
}
