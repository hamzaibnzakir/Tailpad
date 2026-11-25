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
    GameVolumeDuck? _duck;
    IntPtr _lastFocusedGame;
    volatile bool _busy;

    public event Action<JsonObject>? Changed;

    public BackgroundPlay(Config cfg, WinServices services)
    {
        _cfg = cfg;
        _services = services;
        // tidy up after a crash: no stray hidden screen, no game left turned down
        try { GameVolumeDuck.RecoverAfterCrash(cfg); } catch { }
        try { if (HiddenScreen.Installed) HiddenScreen.Off(); } catch { }
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

    void Say(JsonObject msg) => Changed?.Invoke(msg);

    public void Setup()
    {
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            try
            {
                if (HiddenScreen.SetupWithPrompt())
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
        if (_busy) return;
        _busy = true;
        Task.Run(async () =>
        {
            try
            {
                BringBackCore(silent: true);
                Say(BackgroundStatus.Waiting(game.Name));
                var userWindow = WindowTools.Foreground();
                bool userActive = WindowTools.IdleSeconds() < 120; // someone is using the PC right now
                var started = DateTime.Now;
                var preferExe = Path.GetFileName(game.Target.Split('\0')[0]);
                if (!preferExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) preferExe = null;

                // if the game is already running, just move it
                var running = Placement.PickGameWindow(WindowTools.All(), new DateTime(2000, 1, 1), preferExe);
                if (preferExe != null && running is { } r && r.Exe.Equals(preferExe, StringComparison.OrdinalIgnoreCase))
                {
                    Place(r.Handle, game.Name);
                    return;
                }

                _services.Launch(game);

                // wait for the game's own window (launchers and splash screens come and go first)
                IntPtr candidate = IntPtr.Zero; Rectangle candRect = default; int stable = 0;
                for (int i = 0; i < 240; i++) // up to 2 minutes
                {
                    await Task.Delay(500);
                    var pick = Placement.PickGameWindow(WindowTools.All(), started, preferExe);
                    if (pick is not { } w) { stable = 0; continue; }
                    var rect = WindowTools.RectOf(w.Handle);
                    if (w.Handle == candidate && rect == candRect) stable++;
                    else { candidate = w.Handle; candRect = rect; stable = 0; }
                    if (stable >= 3) // same window, same size for 1.5 s
                    {
                        Place(candidate, game.Name);
                        if (userActive) WindowTools.Focus(userWindow); // give the keyboard back to whatever you were doing
                        return;
                    }
                }
                Say(BackgroundStatus.Error($"{game.Name} started, but its window didn't appear. Open it, then tap “Move to hidden screen”."));
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
                Place(h, name);
            }
            catch (Exception e) { Say(BackgroundStatus.Error(e.Message)); }
            finally { _busy = false; }
        });
    }

    void Place(IntPtr h, string name)
    {
        var area = HiddenScreen.On();
        lock (_gate)
        {
            _was = WindowTools.RectOf(h);
            _game = h;
            GamePid = WindowTools.ProcessOf(h);
            GameName = name;
        }
        WindowTools.MoveTo(h, area);
        try { _duck?.Dispose(); _duck = new GameVolumeDuck(_cfg, GamePid, WindowTools.ExeOf(GamePid)); } catch { _duck = null; }
        Say(BackgroundStatus.On(name));
    }

    public void BringBack() => Task.Run(() => BringBackCore(silent: false));

    void BringBackCore(bool silent)
    {
        IntPtr h; Rectangle was;
        lock (_gate) { h = _game; was = _was; _game = IntPtr.Zero; GameName = null; GamePid = 0; }
        try { _duck?.Dispose(); } catch { }
        _duck = null;
        if (WindowTools.Exists(h))
        {
            // back to where it was, or centred on the main screen if that place is gone
            var primary = HiddenScreen.Primary;
            if (was.IsEmpty || !primary.IntersectsWith(was)) was = new Rectangle(primary.X + primary.Width / 8, primary.Y + primary.Height / 8, primary.Width * 3 / 4, primary.Height * 3 / 4);
            WindowTools.Restore(h, was);
        }
        try { HiddenScreen.Off(); } catch { }
        if (!silent) Say(BackgroundStatus.Off());
    }

    void Tick()
    {
        // remember the last real game/app window the person focused (for "move to hidden screen")
        var fg = WindowTools.Foreground();
        if (fg != IntPtr.Zero && CaptureTarget.FromWindow(fg) != null && fg != _game) _lastFocusedGame = fg;

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
