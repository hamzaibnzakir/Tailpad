using QRCoder;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>Main window: pairing QR + code, connected phones, game library, settings, log.</summary>
public interface IMainHost
{
    IReadOnlyList<string> LogLines { get; }
    bool EnsureDriver(bool askFirst);
}

sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(0x0E, 0x10, 0x14), Panel = Color.FromArgb(0x17, 0x1B, 0x22),
        Edge = Color.FromArgb(0x2C, 0x32, 0x40), Fg = Color.FromArgb(0xE8, 0xEB, 0xF1), Dim = Color.FromArgb(0x8A, 0x92, 0xA6);

    readonly Config _cfg;
    readonly HostServer _server;
    readonly WinServices _services;
    readonly ViGEmPadFactory _pads;
    readonly IMainHost _app;

    readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(200, 200), BackColor = Color.White };
    readonly Label _code = new() { AutoSize = true, Font = new Font("Segoe UI", 30, FontStyle.Bold), ForeColor = AppIcon.Accent };
    readonly Label _addr = new() { AutoSize = true, Font = new Font("Consolas", 11), ForeColor = Fg };
    readonly Label _driver = new() { AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Dim };
    Button _driverButton = null!;
    readonly ListView _live = MakeList(("Phone", 170), ("Controller", 110), ("Player", 60), ("Address", 140));
    readonly ListView _paired = MakeList(("Phone", 220), ("Last seen", 160));
    readonly ListView _games = MakeList(("Game", 260), ("From", 90), ("Starts with", 200));
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9) };
    bool _quitting;
    readonly BackgroundPlay? _bg;
    readonly Label _hiddenStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 4, 0, 0) };
    Button _hiddenSetup = null!;
    Button _scanButton = null!, _unhide = null!;
    readonly Label _scanStatus = new() { AutoSize = true, ForeColor = Color.FromArgb(0x8A, 0x92, 0xA6) };
    TabControl _tabs = null!;
    public void SelectTab(int i) => _tabs.SelectedIndex = i;

    public MainForm(Config cfg, HostServer server, WinServices services, ViGEmPadFactory pads, IMainHost app, Icon icon, BackgroundPlay? bg = null)
    {
        _cfg = cfg; _server = server; _services = services; _pads = pads; _app = app; _bg = bg;
        Text = "TailPad";
        Icon = icon;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(960, 640);
        MinimumSize = new Size(820, 500);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildPairPanel(), 0, 0);
        root.Controls.Add(BuildTabs(), 1, 0);

        RefreshPairing();
        RefreshSessions();
        RefreshGames();
        foreach (var l in app.LogLines) AppendLog(l);

        FormClosing += (_, e) =>
        {
            if (_quitting || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
    }

    public new void Close() { _quitting = true; base.Close(); }

    static ListView MakeList(params (string name, int width)[] cols)
    {
        var lv = new ListView
        {
            View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill,
            BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.None, MultiSelect = false,
        };
        foreach (var (n, w) in cols) lv.Columns.Add(n, w);
        return lv;
    }

    static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Fg,
            Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 6, 8, 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Edge;
        b.Click += onClick;
        return b;
    }

    static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 12), ForeColor = Fg, Margin = new Padding(0, 0, 0, 8),
    };

    Control BuildPairPanel()
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        p.Controls.Add(Header("Connect your iPhone"));
        p.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Dim, Margin = new Padding(0, 0, 0, 10),
            Text = "Turn Tailscale on, then scan this with the iPhone camera — or type the address and code into the TailPad app.",
        });
        p.Controls.Add(_qr);
        p.Controls.Add(new Label { Text = "Pairing code", AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 12, 0, 0) });
        p.Controls.Add(_code);
        p.Controls.Add(_addr);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 10) };
        row.Controls.Add(MakeButton("New code", (_, _) =>
        {
            lock (_cfg) { _cfg.PairingCode = Config.NewCode(); _cfg.Save(); }
            RefreshPairing();
        }));
        row.Controls.Add(MakeButton("Copy address", (_, _) => { var ip = Tailnet.Ip(); if (ip != null) Clipboard.SetText(ip); }));
        p.Controls.Add(row);
        p.Controls.Add(_driver);
        _driverButton = MakeButton("Set up controller driver", (_, _) => { _app.EnsureDriver(askFirst: false); RefreshDriver(); });
        p.Controls.Add(_driverButton);
        return p;
    }

    Control BuildTabs()
    {
        var tabs = _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };

        // Phones
        var phones = Page("Phones");
        var pl = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Padding = new Padding(10) };
        pl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pl.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        pl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pl.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        pl.Controls.Add(Header("Connected now"));
        pl.Controls.Add(_live);
        pl.Controls.Add(MakeButton("Disconnect", (_, _) =>
        {
            if (_live.SelectedItems.Count == 1 && _live.SelectedItems[0].Tag is uint key) _server.Kick(key);
        }));
        var pairedHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        pairedHead.Controls.Add(Header("Paired phones"));
        pairedHead.Controls.Add(MakeButton("Forget", (_, _) =>
        {
            if (_paired.SelectedItems.Count != 1 || _paired.SelectedItems[0].Tag is not string id) return;
            if (MessageBox.Show("Forget this phone? It will need the pairing code again.", "TailPad", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _server.Forget(id);
            RefreshSessions();
        }));
        pl.Controls.Add(pairedHead);
        pl.Controls.Add(_paired);
        phones.Controls.Add(pl);
        tabs.TabPages.Add(phones);

        // Games
        var games = Page("Games");
        var gl = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(10) };
        gl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gl.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Dim, MaximumSize = new Size(560, 0),
            Text = "TailPad finds your games by itself — Steam, Epic, and games installed anywhere on your drives. Missing one? Add its .exe. Wrong one? Select it and Remove.",
        });
        gl.Controls.Add(_games);
        var grow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _scanButton = MakeButton("Scan PC for games", (_, _) => StartScan());
        grow.Controls.Add(_scanButton);
        grow.Controls.Add(MakeButton("Add game…", (_, _) => AddGame()));
        grow.Controls.Add(MakeButton("Change start file…", (_, _) => ChangeStartFile()));
        grow.Controls.Add(MakeButton("Remove", (_, _) => RemoveGame()));
        _unhide = MakeButton("Show removed", (_, _) => { _services.UnhideAll(); RefreshGames(); });
        grow.Controls.Add(_unhide);
        _scanStatus.Margin = new Padding(4, 12, 0, 0);
        grow.Controls.Add(_scanStatus);
        gl.Controls.Add(grow);
        games.Controls.Add(gl);
        tabs.TabPages.Add(games);

        // Settings
        var settings = Page("Settings");
        var sl = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        sl.Controls.Add(Check("Start TailPad when Windows starts", _cfg.StartWithWindows, v =>
        {
            _cfg.StartWithWindows = v;
            try { Autostart.Set(v); } catch (Exception e) { MessageBox.Show(e.Message); }
        }));
        sl.Controls.Add(Check("Let gyro / touchpad move the mouse cursor", _cfg.GyroMouse, v => _cfg.GyroMouse = v));
        sl.Controls.Add(Check("Emulator motion server (DSU on 127.0.0.1:" + _cfg.DsuPort + ") — needs restart", _cfg.DsuEnabled, v => _cfg.DsuEnabled = v));
        sl.Controls.Add(Check("Also allow phones on the same Wi-Fi (without Tailscale)", _cfg.AllowLan, v => _cfg.AllowLan = v));
        sl.Controls.Add(new Label { Text = "Background play", AutoSize = true, Font = new Font("Segoe UI Semibold", 11), ForeColor = Fg, Margin = new Padding(0, 16, 0, 0) });
        sl.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Dim, MaximumSize = new Size(560, 0),
            Text = "Play on your phone while you use the PC: the game runs on a hidden screen only your phone sees. Start it from the phone's Games tab, or use “Move game to hidden screen” in the tray menu.",
        });
        sl.Controls.Add(_hiddenStatus);
        _hiddenSetup = MakeButton("Set up hidden screen", (_, _) => { _bg?.Setup(); });
        sl.Controls.Add(_hiddenSetup);
        RefreshHidden();
        var hiddenTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        hiddenTimer.Tick += (_, _) => RefreshHidden();
        hiddenTimer.Start();
        sl.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Dim, MaximumSize = new Size(560, 0), Margin = new Padding(0, 14, 0, 0),
            Text = $"Ports: TCP {_cfg.ControlPort} and UDP {_cfg.InputPort}. Windows Firewall may ask the first time — allow Private networks.",
        });
        settings.Controls.Add(sl);
        tabs.TabPages.Add(settings);

        // Log
        var log = Page("Activity");
        log.Padding = new Padding(10);
        log.Controls.Add(_log);
        tabs.TabPages.Add(log);
        return tabs;
    }

    static TabPage Page(string title) => new(title) { BackColor = Bg, ForeColor = Fg };

    CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 6, 0, 6) };
        c.CheckedChanged += (_, _) => { lock (_cfg) { set(c.Checked); _cfg.Save(); } };
        return c;
    }

    void RefreshPairing()
    {
        var ip = Tailnet.Override ?? Tailnet.Ip();
        _code.Text = _cfg.PairingCode;
        _addr.Text = ip != null ? $"{ip}:{_cfg.ControlPort}" : "Tailscale not running on this PC";
        _addr.ForeColor = ip != null ? Fg : Color.FromArgb(0xEF, 0x53, 0x50);
        var url = $"tailpad://pair?host={ip ?? ""}&port={_cfg.ControlPort}&code={_cfg.PairingCode}&pc={Uri.EscapeDataString(Environment.MachineName)}";
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var ms = new MemoryStream(png);
        _qr.Image?.Dispose();
        _qr.Image = Image.FromStream(ms);

        RefreshDriver();
    }

    void RefreshHidden()
    {
        bool ready = _bg?.Available == true;
        _hiddenStatus.Text = _bg == null ? "Not available in this mode." :
            _bg.Active ? $"Playing {_bg.GameName} on the hidden screen ✓" :
            ready ? "Hidden screen: ready ✓" : "Hidden screen not set up yet (one click, one Windows prompt).";
        _hiddenStatus.ForeColor = ready ? AppIcon.Accent : Color.FromArgb(0xFF, 0xCA, 0x28);
        _hiddenSetup.Visible = _bg != null && !ready;
    }

    public void RefreshDriver()
    {
        if (InvokeRequired) { BeginInvoke(RefreshDriver); return; }
        var ok = _pads.TryInit();
        _driver.Text = ok ? "Controller driver: ready ✓" : "Controller driver not set up yet — games can't see your phone until it is (one click, one time).";
        _driver.ForeColor = ok ? AppIcon.Accent : Color.FromArgb(0xFF, 0xCA, 0x28);
        _driverButton.Visible = !ok;
    }

    public void RefreshSessions()
    {
        if (InvokeRequired) { BeginInvoke(RefreshSessions); return; }
        _live.BeginUpdate();
        _live.Items.Clear();
        foreach (var s in _server.Sessions.OrderBy(s => s.Connected))
        {
            var it = new ListViewItem(new[]
            {
                s.Name, s.PadType == PadType.DS4 ? "PlayStation" : "Xbox",
                s.Player > 0 ? s.Player.ToString() : (s.DsuSlot + 1).ToString(), s.Address.ToString(),
            }) { Tag = s.Key };
            _live.Items.Add(it);
        }
        _live.EndUpdate();

        _paired.BeginUpdate();
        _paired.Items.Clear();
        List<PairedDevice> devs;
        lock (_cfg) devs = _cfg.Devices.ToList();
        foreach (var d in devs.OrderByDescending(d => d.LastSeen))
            _paired.Items.Add(new ListViewItem(new[] { d.Name, d.LastSeen.ToString("g") }) { Tag = d.Id });
        _paired.EndUpdate();
    }

    public void RefreshGames()
    {
        if (InvokeRequired) { BeginInvoke(RefreshGames); return; }
        _games.BeginUpdate();
        _games.Items.Clear();
        foreach (var g in _services.Games())
            _games.Items.Add(new ListViewItem(new[] { g.Name, g.Source == "PC" ? "On this PC" : g.Source, Path.GetFileName(g.StartPath) }) { Tag = g });
        _games.EndUpdate();
        int hidden;
        lock (_cfg) hidden = _cfg.HiddenGames.Count;
        _unhide.Visible = hidden > 0;
        _unhide.Text = $"Show removed ({hidden})";
        if (_services.Scanning) { _scanStatus.Text = "Scanning your drives…"; _scanButton.Enabled = false; }
        else
        {
            _scanButton.Enabled = true;
            DateTime last; lock (_cfg) last = _cfg.LastScan;
            _scanStatus.Text = last == default ? "" : $"{_games.Items.Count} games · last scan {last:g}";
        }
    }

    void StartScan()
    {
        _scanButton.Enabled = false;
        _scanStatus.Text = "Scanning your drives… (a minute or two)";
        Task.Run(() =>
        {
            var n = _services.ScanPc();
            if (!IsDisposed) BeginInvoke(() => { RefreshGames(); AppendLog($"{DateTime.Now:HH:mm:ss}  Scan found {n} games on your drives"); });
        });
    }

    void AddGame()
    {
        using var dlg = new OpenFileDialog { Filter = "Programs (*.exe;*.lnk;*.url;*.bat)|*.exe;*.lnk;*.url;*.bat|All files|*.*", Title = "Pick a game or launcher" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        lock (_cfg) { _cfg.CustomGames.Add(new CustomGame { Name = name, Path = dlg.FileName }); _cfg.Save(); }
        _services.Rescan();
        RefreshGames();
    }

    /// <summary>Some games must start through their launcher or a shortcut — pick the file you'd double-click.</summary>
    void ChangeStartFile()
    {
        if (_games.SelectedItems.Count != 1 || _games.SelectedItems[0].Tag is not GameEntry g)
        {
            MessageBox.Show("Select a game first.", "TailPad");
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Title = $"How do you start {g.Name}? Pick its shortcut or launcher",
            Filter = "Programs and shortcuts (*.exe;*.lnk;*.url;*.bat)|*.exe;*.lnk;*.url;*.bat|All files|*.*",
            InitialDirectory = g.GameExe is { } exe && File.Exists(exe) ? Path.GetDirectoryName(exe) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            DereferenceLinks = false, // keep the shortcut itself (it carries the launcher's arguments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _services.SetStartFile(g, dlg.FileName);
        RefreshGames();
        AppendLog($"{DateTime.Now:HH:mm:ss}  {g.Name} will start with {Path.GetFileName(dlg.FileName)}");
    }

    void RemoveGame()
    {
        if (_games.SelectedItems.Count != 1 || _games.SelectedItems[0].Tag is not GameEntry g) return;
        if (g.Id.StartsWith("custom:"))
        {
            var path = g.Target.Split('\0')[0];
            lock (_cfg) { _cfg.CustomGames.RemoveAll(c => c.Path == path); _cfg.Save(); }
            _services.Rescan();
        }
        else _services.Hide(g); // found automatically → hide it (and keep it hidden after future scans)
        RefreshGames();
    }

    public void AppendLog(string line)
    {
        if (IsDisposed) return;
        _log.AppendText(line + Environment.NewLine);
    }
}
