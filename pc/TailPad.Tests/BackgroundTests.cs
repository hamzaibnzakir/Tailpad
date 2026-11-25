using System.Text.Json.Nodes;
using TailPad.Core;
using Xunit;
using static TailPad.Core.Placement;

namespace TailPad.Tests;

public class PlacementTests
{
    [Fact]
    public void Hidden_screen_touches_the_desktop_only_at_a_corner()
    {
        var laptop = new Rect(0, 0, 1920, 1080);
        var (x, y) = HiddenScreenPosition(new[] { laptop }, 1280, 720);
        var hidden = new Rect(x, y, 1280, 720);
        Assert.Equal((-1280, -720), (x, y));
        Assert.False(hidden.Intersects(laptop));
        Assert.Equal(laptop.X, hidden.Right);   // corners meet…
        Assert.Equal(laptop.Y, hidden.Bottom);  // …but no shared edge

        // with a second monitor on the left, it goes above-left of the whole desktop
        var left = new Rect(-2560, 100, 2560, 1440);
        var (x2, y2) = HiddenScreenPosition(new[] { laptop, left }, 1280, 720);
        Assert.Equal((-2560 - 1280, 0 - 720), (x2, y2));
        Assert.False(new Rect(x2, y2, 1280, 720).Intersects(left));
    }

    static WindowInfo W(string exe, int w, int h, DateTime started, bool visible = true, bool tool = false, bool owner = false, string title = "") =>
        new(IntPtr.Zero, 1, exe, title, new Rect(0, 0, w, h), visible, tool, owner, started);

    [Fact]
    public void Picks_the_game_window_not_the_launcher_or_old_apps()
    {
        var launch = new DateTime(2026, 11, 25, 10, 0, 0);
        var windows = new[]
        {
            W("chrome.exe", 1920, 1040, launch.AddHours(-2)),             // was already open → not the game
            W("steam.exe", 1200, 800, launch.AddSeconds(5)),              // launcher
            W("steamwebhelper.exe", 1200, 800, launch.AddSeconds(5)),
            W("GTA5.exe", 300, 200, launch.AddSeconds(20)),               // splash too small
            W("GTA5.exe", 1920, 1080, launch.AddSeconds(25)),             // the game
            W("GTA5.exe", 1920, 1080, launch.AddSeconds(25), visible: false),
            W("overlay.exe", 1920, 1080, launch.AddSeconds(26), tool: true),
        };
        var pick = PickGameWindow(windows, launch);
        Assert.NotNull(pick);
        Assert.Equal("GTA5.exe", pick!.Value.Exe);
        Assert.Equal(1920, pick.Value.Bounds.W);
    }

    [Fact]
    public void Prefers_the_launched_exe_and_returns_null_while_nothing_has_appeared()
    {
        var launch = DateTime.Now;
        Assert.Null(PickGameWindow(new[] { W("steam.exe", 1200, 800, launch) }, launch));
        var pick = PickGameWindow(new[] { W("other.exe", 2560, 1440, launch.AddSeconds(1)), W("efootball.exe", 1280, 720, launch.AddSeconds(2)) },
            launch, preferExe: "eFootball.exe");
        Assert.Equal("efootball.exe", pick!.Value.Exe);
    }

    [Fact]
    public void Admin_games_with_unknown_start_time_are_found_by_new_process_id()
    {
        var launch = DateTime.Now;
        // GTA V runs as admin: Windows hides its start time from TailPad
        var gta = W("GTA5.exe", 1920, 1080, DateTime.MinValue) with { ProcessId = 900 };
        var chrome = W("chrome.exe", 1920, 1040, DateTime.MinValue) with { ProcessId = 12 };
        // without the before-launch list an unknown start time doesn't count as new
        Assert.Null(PickGameWindow(new[] { gta, chrome }, launch));
        var before = new HashSet<int> { 12 };
        var pick = PickGameWindow(new[] { chrome, gta }, launch, runningBefore: before);
        Assert.Equal("GTA5.exe", pick!.Value.Exe);
        // already running: any window of the game's exe counts
        Assert.Equal("GTA5.exe", PickGameWindow(new[] { chrome, gta }, null, preferExe: "gta5.exe")!.Value.Exe);
    }

    [Fact]
    public void Move_check_spots_a_window_windows_refused_to_move()
    {
        var hidden = new Rect(-1280, -720, 1280, 720);
        Assert.True(LandedOn(new Rect(-1280, -720, 1280, 720), hidden));
        Assert.False(LandedOn(new Rect(0, 0, 1920, 1080), hidden));
    }

    [Fact]
    public void Sound_boost_undoes_the_pc_side_volume()
    {
        Assert.Equal(200f, CompensationGain(DuckVolume), 1);
        Assert.Equal(1f, CompensationGain(1f));
        Assert.Equal(2f, CompensationGain(0.5f));
        Assert.Equal(0f, CompensationGain(0f));        // muted means really muted
        Assert.Equal(1f, DuckVolume * CompensationGain(DuckVolume), 3);
    }
}

public class BackgroundServerTests : IDisposable
{
    readonly Config _cfg;
    readonly FakeHost _host = new();
    readonly HostServer _server;

    public BackgroundServerTests()
    {
        var p = Ports.FreeBlock();
        _cfg = new Config { ControlPort = p, InputPort = p + 1, DsuEnabled = false, PairingCode = "333444",
                            SavePath = Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}.json") };
        _server = new HostServer(_cfg, new FakeFactory(), _host);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Launch_on_hidden_screen_and_bring_back()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "bg-1", code: "333444");
        await phone.Send(new JsonObject { ["t"] = "bg", ["action"] = "launch", ["id"] = "steam:570" });
        var on = await phone.Read("bg");
        Assert.Equal("on", on["state"]!.GetValue<string>());
        Assert.Equal("Dota 2", on["game"]!.GetValue<string>());
        Assert.Contains("launch:steam:570", _host.Bg.Calls);

        await phone.Send(new JsonObject { ["t"] = "bg", ["action"] = "back" });
        Assert.Equal("off", (await phone.Read("bg"))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_the_driver_the_pc_asks_for_setup()
    {
        _host.Bg.Available = false;
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "bg-2", code: "333444");
        await phone.Send(new JsonObject { ["t"] = "bg", ["action"] = "move" });
        Assert.Equal("needs-setup", (await phone.Read("bg"))["state"]!.GetValue<string>());
        Assert.Contains("setup", _host.Bg.Calls);
        Assert.DoesNotContain("move", _host.Bg.Calls);
    }

    [Fact]
    public async Task Gyro_mouse_is_off_while_playing_in_the_background()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "bg-3", code: "333444");
        _host.Bg.Active = true;
        phone.Input(default, 50, 50);
        await Task.Delay(200);
        Assert.Equal(0f, _host.MouseX);
        _host.Bg.Active = false;
        phone.Input(default, 50, 50);
        var end = DateTime.UtcNow.AddSeconds(2);
        while (_host.MouseX == 0 && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.Equal(50f, _host.MouseX);
    }

    [Fact]
    public async Task Phone_that_connects_mid_game_is_told_it_is_in_the_background()
    {
        _host.Bg.Active = true; _host.Bg.GameName = "eFootball";
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "bg-4", code: "333444");
        var st = await phone.Read("bg");
        Assert.Equal("on", st["state"]!.GetValue<string>());
        Assert.Equal("eFootball", st["game"]!.GetValue<string>());
    }
}
