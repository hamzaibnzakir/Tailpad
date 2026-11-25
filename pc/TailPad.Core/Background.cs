using System.Text.Json.Nodes;

namespace TailPad.Core;

/// <summary>
/// "Background play": the game runs on a hidden (virtual) screen that only the phone sees,
/// so the PC's real screen, mouse and keyboard stay free for other work.
/// </summary>
public interface IBackgroundPlay
{
    /// <summary>The hidden-screen driver is installed.</summary>
    bool Available { get; }
    /// <summary>A game is on the hidden screen right now.</summary>
    bool Active { get; }
    string? GameName { get; }

    /// <summary>Start a game straight onto the hidden screen (its window is moved there as soon as it appears).</summary>
    void Launch(GameEntry game);
    /// <summary>Move the game that was last focused on the PC to the hidden screen.</summary>
    void MoveCurrent();
    /// <summary>Bring the game back to the PC's main screen and remove the hidden screen.</summary>
    void BringBack();
    /// <summary>Ask the PC to set up the hidden-screen driver (one Windows prompt on the PC).</summary>
    void Setup();

    /// <summary>Raised with a "bg" status message for the phone.</summary>
    event Action<JsonObject>? Changed;
}

/// <summary>Used when the PC can't do background play (tests, other platforms).</summary>
public sealed class NoBackgroundPlay : IBackgroundPlay
{
    public bool Available => false;
    public bool Active => false;
    public string? GameName => null;
    public void Launch(GameEntry game) => Changed?.Invoke(BackgroundStatus.Error("Background play isn't available on this PC"));
    public void MoveCurrent() => Changed?.Invoke(BackgroundStatus.Error("Background play isn't available on this PC"));
    public void BringBack() { }
    public void Setup() { }
    public event Action<JsonObject>? Changed;
}

public static class BackgroundStatus
{
    /// <summary>{"t":"bg","state":…}</summary>
    public static JsonObject Make(string state, string? game = null, string? msg = null)
    {
        var o = new JsonObject { ["t"] = "bg", ["state"] = state };
        if (game != null) o["game"] = game;
        if (msg != null) o["msg"] = msg;
        return o;
    }

    public static JsonObject On(string game) => Make("on", game,
        "Playing in the background — the PC is free. If the game ignores the controller, turn on its “run in background” option.");
    public static JsonObject Off() => Make("off");
    public static JsonObject Waiting(string game) => Make("waiting", game, $"Starting {game} on the hidden screen…");
    public static JsonObject NeedsSetup() => Make("needs-setup", null,
        "One-time setup: click Yes on your PC to add TailPad's hidden screen.");
    public static JsonObject Error(string msg) => Make("error", null, msg);
    public static JsonObject NeedsAdmin(string game) => Make("error", game,
        $"{game} runs as administrator, so Windows won't let TailPad move it. On your PC, click Yes to restart TailPad as administrator, then tap the game again.");
}

/// <summary>Pure helpers for placing windows and the hidden screen (unit-tested).</summary>
public static class Placement
{
    public readonly record struct Rect(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
        public bool Intersects(Rect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
        public bool Contains(Rect o) => o.X >= X && o.Y >= Y && o.Right <= Right && o.Bottom <= Bottom;
    }

    /// <summary>
    /// Where to put the hidden screen: touching the desktop only at one corner (above-left of the
    /// whole desktop), so the mouse practically never slides onto it.
    /// </summary>
    public static (int x, int y) HiddenScreenPosition(IEnumerable<Rect> realScreens, int w, int h)
    {
        var list = realScreens.ToList();
        int left = list.Count == 0 ? 0 : list.Min(r => r.X);
        int top = list.Count == 0 ? 0 : list.Min(r => r.Y);
        return (left - w, top - h);
    }

    public readonly record struct WindowInfo(IntPtr Handle, int ProcessId, string Exe, string Title, Rect Bounds,
        bool Visible, bool ToolWindow, bool HasOwner, DateTime ProcessStarted);

    /// <summary>Programs that are never "the game" (launchers, shell, overlays, us).</summary>
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe",
        "galaxyclient.exe", "eadesktop.exe", "origin.exe", "upc.exe", "ubisoftconnect.exe", "battle.net.exe",
        "rockstarservice.exe", "launcher.exe", "playgtav.exe", "socialclubhelper.exe", "launcherpatcher.exe",
        "tailpad.exe", "textinputhost.exe", "searchhost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "applicationframehost.exe", "discord.exe", "nvidia overlay.exe", "nvcontainer.exe", "gamebar.exe",
        "msedgewebview2.exe", "crashreporter.exe", "unitycrashhandler64.exe", "easyanticheat.exe", "easyanticheat_eos.exe",
    };

    /// <summary>
    /// After a launch, pick the game's window among the top-level windows: a visible, normal-sized window
    /// that isn't a launcher/overlay and belongs to a program that wasn't running before the launch,
    /// preferring the game's own exe and then bigger windows.
    /// <para><paramref name="launchedAt"/> null = any window counts (the game may already be running).
    /// <paramref name="runningBefore"/> = process ids that existed before the launch; this works even for
    /// games running as administrator, whose start time Windows won't tell us (ProcessStarted = MinValue).</para>
    /// </summary>
    public static WindowInfo? PickGameWindow(IEnumerable<WindowInfo> windows, DateTime? launchedAt, string? preferExe = null,
        IReadOnlySet<int>? runningBefore = null)
    {
        bool Preferred(WindowInfo w) => preferExe != null && w.Exe.Equals(preferExe, StringComparison.OrdinalIgnoreCase);
        bool IsNew(WindowInfo w)
        {
            if (launchedAt is not { } at) return true;
            if (runningBefore != null && !runningBefore.Contains(w.ProcessId)) return true;
            return w.ProcessStarted != DateTime.MinValue && w.ProcessStarted >= at.AddSeconds(-2);
        }
        var candidates = windows
            .Where(w => w.Visible && !w.ToolWindow && !w.HasOwner && w.Bounds.W >= 640 && w.Bounds.H >= 360)
            .Where(w => !NotGames.Contains(w.Exe) || Preferred(w))
            .Where(IsNew)
            .ToList();
        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(Preferred)
            .ThenByDescending(w => (long)w.Bounds.W * w.Bounds.H)
            .First();
    }

    /// <summary>Did the move work? (Windows silently refuses to move windows of programs running as administrator.)</summary>
    public static bool LandedOn(Rect window, Rect hiddenScreen) =>
        window.Intersects(hiddenScreen) && window.W > 0 && window.H > 0;

    /// <summary>The game is turned almost all the way down on the PC (so the PC is quiet);
    /// this boosts the captured sound back to full for the phone.</summary>
    public const float DuckVolume = 0.005f;

    public static float CompensationGain(float sessionVolume) =>
        sessionVolume <= 0.0001f ? 0f : Math.Clamp(1f / sessionVolume, 1f, 400f);
}
