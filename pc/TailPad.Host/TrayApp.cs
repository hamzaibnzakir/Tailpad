using System.Diagnostics;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>Lives in the system tray; owns the server and opens the main window on demand.</summary>
sealed class TrayApp : ApplicationContext
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

        if (!_pads.TryInit()) AddLog("ViGEmBus driver missing — install it from the TailPad window");
        _fg = new ForegroundWatcher((exe, title) => _server.BroadcastForeground(exe, title));
        Task.Run(_services.Rescan);

        if (!startHidden || !_pads.TryInit()) ShowWindow();
        else Balloon($"TailPad running — pairing code {_cfg.PairingCode}");
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
