using System.Text.Json.Nodes;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>
/// Background play: the game lives on TailPad's hidden screen and streams to the phone,
/// while the PC's real screen, mouse and keyboard stay free.
/// </summary>
sealed class BackgroundPlay : IBackgroundPlay, IDisposable
{
    readonly Config _cfg;
    readonly WinServices _services;
    readonly object _gate = new();
    readonly System.Threading.Timer _tick;
    IntPtr _game;
    Rectangle _was;
    long _style;
    GameVolumeDuck? _duck;
    IntPtr _lastFocusedGame;
    volatile bool _busy;

    public event Action<JsonObject>? Changed;
    /// <summary>A line for the Activity log on the PC.</summary>
    public event Action<string>? Log;
    /// <summary>The game runs as administrator and TailPad doesn't, so Windows blocked the move (arg: game name).</summary>
    public event Action<string>? NeedsAdmin;

    public BackgroundPlay(Config cfg, WinServices services)
    {
        _cfg = cfg;
        _services = services;
        // tidy up after a crash: no stray hidden screen, no game left turned down
        try { GameVolumeDuck.RecoverAfterCrash(cfg); } catch { }
        try { if (HiddenScreen.Installed) HiddenScreen.Off(); } catch { }
        HiddenScreen.Log = Note;
        _tick = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    public bool Available => HiddenScreen.Installed;
    public bool Active { get { lock (_gate) return WindowTools.Exists(_game); } }
    public string? GameName { get; private set; }
    public int GamePid { get; private set; }

    /// <summary>The hidden screen's area while a game is on it.</summary>
    public Rectangle? Area => Active ? HiddenScreen.Bounds : null;

    /// <summary>The game's volume on the PC right now (the phone gets it boosted back to full).</summary>
    public float PcVolume => _duck?.Level ?? 1f;

    void Say(JsonObject msg)
    {
        if (msg["msg"]?.GetValue<string>() is { } m) Note(m);
        Changed?.Invoke(msg);
    }

    void Note(string line) { try { Log?.Invoke("Background play: " + line); } catch { } }

    public void Setup() => Setup(repair: false);

    /// <summary>Install the hidden screen, or reinstall it when it's there but not working.</summary>
    public void Setup(bool repair)
    {
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            try
            {
                if (HiddenScreen.SetupWithPrompt(repair))
                    Say(BackgroundStatus.Make("ready", null, "Hidden screen ready — pick a game to play in the background"));
                else
                    Say(BackgroundStatus.Error("Hidden screen setup was cancelled on the PC"));
            }
            catch (Exception e) { Say(BackgroundStatus.Error("Hidden screen setup failed: " + e.Message)); }
            finally { _busy = false; }
        });
    }

    public void Launch(GameEntry game)
    {
        if (_busy) { Note("still busy with the last request"); return; }
        _busy = true;
        Task.Run(async () =>
        {
            try
            {
                BringBackCore(silent: true);
                Say(BackgroundStatus.Waiting(game.Name));
                var userWindow = WindowTools.Foreground();
                bool userActive = WindowTools.IdleSeconds() < 120; // someone is using the PC right now
                // the game's own exe, even when it's started through a shortcut or launcher
                var preferExe = game.GameExe is { } ge ? Path.GetFileName(ge) : null;
                Note($"TailPad {(WindowTools.SelfElevated ? "runs" : "doesn't run")} as administrator; looking for {preferExe ?? "a new game window"}");

                // if the game is already running, just move it
                var running = Placement.PickGameWindow(WindowTools.All(), null, preferExe);
                if (preferExe != null && running is { } r && r.Exe.Equals(preferExe, StringComparison.OrdinalIgnoreCase))
                {
                    Note($"{r.Exe} is already running — moving it");
                    Place(r.Handle, game.Name, userActive ? userWindow : IntPtr.Zero);
                    return;
                }

                // everything running now is "not the game", whatever Windows lets us know about it
                var before = WindowTools.RunningPids();
                var started = DateTime.Now;
                _services.Launch(game);
                Note($"started {game.Name} ({game.StartPath})");

                // wait for the game's own window (launchers and splash screens come and go first)
                IntPtr candidate = IntPtr.Zero; Rectangle candRect = default; int stable = 0;
                for (int i = 0; i < 240; i++) // up to 2 minutes
                {
                    await Task.Delay(500);
                    var pick = Placement.PickGameWindow(WindowTools.All(), started, preferExe, before);
                    if (pick is not { } w) { stable = 0; continue; }
                    var rect = WindowTools.RectOf(w.Handle);
                    if (w.Handle == candidate && rect == candRect) stable++;
                    else
                    {
                        candidate = w.Handle; candRect = rect; stable = 0;
                        Note($"found window “{w.Title}” ({w.Exe}, {rect.Width}×{rect.Height})");
                    }
                    if (stable >= 3) // same window, same size for 1.5 s
                    {
                        Place(candidate, game.Name, userActive ? userWindow : IntPtr.Zero);
                        return;
                    }
                }
                Say(BackgroundStatus.Error($"{game.Name} started, but TailPad couldn't find its window. When it's open, tap “Move to hidden screen”."));
            }
            catch (Exception e) { Say(BackgroundStatus.Error(e.Message)); }
            finally { _busy = false; }
        });
    }

    public void MoveCurrent()
    {
        if (_busy) return;
        var h = _lastFocusedGame;
        if (!WindowTools.Exists(h)) { Say(BackgroundStatus.Error("Open the game on your PC first, then try again.")); return; }
        _busy = true;
        Task.Run(() =>
        {
            try
            {
                var pid = WindowTools.ProcessOf(h);
                var name = WindowTools.TitleOf(h);
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(WindowTools.ExeOf(pid));
                Place(h, name, IntPtr.Zero);
            }
            catch (Exception e) { Say(BackgroundStatus.Error(e.Message)); }
            finally { _busy = false; }
        });
    }

    /// <summary>Put the window on the hidden screen and check that Windows actually let us.</summary>
    void Place(IntPtr h, string name, IntPtr giveFocusBackTo)
    {
        var pid = WindowTools.ProcessOf(h);
        var exe = WindowTools.ExeOf(pid);
        // Windows never lets a normal program move an administrator program's window
        if (!WindowTools.SelfElevated && WindowTools.IsElevated(pid))
        {
            Blocked(name, exe);
            return;
        }

        var area = HiddenScreen.On();
        var was = WindowTools.RectOf(h);
        var style = WindowTools.MoveTo(h, area);
        Thread.Sleep(400);
        var now = WindowTools.RectOf(h);
        if (!Placement.LandedOn(new Placement.Rect(now.X, now.Y, now.Width, now.Height),
                                new Placement.Rect(area.X, area.Y, area.Width, area.Height)))
        {
            Note($"{exe} stayed at {now} instead of moving to the hidden screen {area}");
            WindowTools.Restore(h, was, style);
            try { HiddenScreen.Off(); } catch { }
            if (!WindowTools.SelfElevated) { Blocked(name, exe); return; }
            Say(BackgroundStatus.Error($"{name} wouldn't move to the hidden screen. In the game's settings pick “Windowed” or “Borderless”, then tap “Move to hidden screen”."));
            return;
        }

        lock (_gate)
        {
            _was = was;
            _game = h;
            _style = style;
            GamePid = pid;
            GameName = name;
        }
        WindowTools.HideTaskbarsOn(area);
        try { _duck?.Dispose(); _duck = new GameVolumeDuck(_cfg, pid, exe); } catch { _duck = null; }
        if (giveFocusBackTo != IntPtr.Zero) WindowTools.Focus(giveFocusBackTo); // keyboard back to whatever you were doing
        Note($"{exe} is on the hidden screen {area.Width}×{area.Height}");
        Say(BackgroundStatus.On(name));
    }

    void Blocked(string name, string exe)
    {
        Note($"{exe} runs as administrator and TailPad doesn't — Windows blocks the move");
        Say(BackgroundStatus.NeedsAdmin(name));
        NeedsAdmin?.Invoke(name);
    }

    public void BringBack() => Task.Run(() => BringBackCore(silent: false));

    void BringBackCore(bool silent)
    {
        IntPtr h; Rectangle was; long style;
        lock (_gate) { h = _game; was = _was; style = _style; _game = IntPtr.Zero; GameName = null; GamePid = 0; }
        try { _duck?.Dispose(); } catch { }
        _duck = null;
        if (WindowTools.Exists(h))
        {
            // back to where it was, or centred on the main screen if that place is gone
            var primary = HiddenScreen.Primary;
            if (was.IsEmpty || !primary.IntersectsWith(was)) was = new Rectangle(primary.X + primary.Width / 8, primary.Y + primary.Height / 8, primary.Width * 3 / 4, primary.Height * 3 / 4);
            WindowTools.Restore(h, was, style); // frame and title bar come back
        }
        try { HiddenScreen.Off(); } catch { }
        if (!silent) Say(BackgroundStatus.Off());
    }

    void Tick()
    {
        // remember the last real game/app window the person focused (for "move to hidden screen")
        var fg = WindowTools.Foreground();
        if (fg != IntPtr.Zero && CaptureTarget.FromWindow(fg) != null && fg != _game) _lastFocusedGame = fg;

        // keep the hidden screen clean: Windows re-shows its taskbar now and then
        if (Active && HiddenScreen.Bounds is { } hs) WindowTools.HideTaskbarsOn(hs);

        // the game was closed → tidy up
        bool ended;
        lock (_gate) ended = _game != IntPtr.Zero && !WindowTools.Exists(_game);
        if (ended)
        {
            var name = GameName ?? "The game";
            BringBackCore(silent: true);
            Say(BackgroundStatus.Make("off", null, $"{name} closed"));
        }
    }

    public void Dispose()
    {
        _tick.Dispose();
        BringBackCore(silent: true);
    }
}
