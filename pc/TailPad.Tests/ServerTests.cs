using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TailPad.Core;
using Xunit;

namespace TailPad.Tests;

sealed class FakePad : IVirtualPad
{
    public PadType Type { get; }
    public PadState Last;
    public MotionSample LastMotion;
    public int Submits;
    public bool Disposed;
    public FakePad(PadType t) => Type = t;
    public event Action<byte, byte>? Rumble;
    public event Action<byte, byte, byte>? Light;
    public event Action<int>? Player;
    public void Submit(in PadState s, in MotionSample m) { Last = s; LastMotion = m; Submits++; }
    public void Reset() => Last = default;
    public void Dispose() => Disposed = true;
    public void FireRumble(byte l, byte s) => Rumble?.Invoke(l, s);
    void Unused() { Light?.Invoke(0, 0, 0); Player?.Invoke(0); }
}

sealed class FakeFactory : IPadFactory
{
    public readonly List<FakePad> Pads = new();
    public IVirtualPad Create(PadType t) { var p = new FakePad(t); lock (Pads) Pads.Add(p); return p; }
}

sealed class FakeHost : IHostServices
{
    public float MouseX, MouseY;
    public readonly List<string> Launched = new();
    public void MoveMouse(float dx, float dy) { MouseX += dx; MouseY += dy; }
    public void Launch(GameEntry g) => Launched.Add(g.Id);
    public IReadOnlyList<GameEntry> Games() => new[] { new GameEntry("steam:570", "Dota 2", "Steam", null, "steam://rungameid/570") };
    public void Notify(string m) { }
    public StreamRequest? LastStream;
    public Action<byte[]>? StreamSend;
    public IDisposable StartStream(StreamRequest r, Action<byte[]> send, Action<JsonObject> status)
    {
        LastStream = r; StreamSend = send;
        status(new JsonObject { ["state"] = "started", ["w"] = 1280, ["h"] = r.Height, ["encoder"] = "fake" });
        return new Stopper(() => StreamSend = null);
    }
    sealed class Stopper(Action a) : IDisposable { public void Dispose() => a(); }
    public FakeBackground Bg { get; } = new();
    public IBackgroundPlay Background => Bg;
}

sealed class FakeBackground : IBackgroundPlay
{
    public bool Available { get; set; } = true;
    public bool Active { get; set; }
    public string? GameName { get; set; }
    public readonly List<string> Calls = new();
    public event Action<JsonObject>? Changed;
    public void Launch(GameEntry g) { Calls.Add("launch:" + g.Id); Active = true; GameName = g.Name; Changed?.Invoke(BackgroundStatus.On(g.Name)); }
    public void MoveCurrent() { Calls.Add("move"); Active = true; GameName = "Moved"; Changed?.Invoke(BackgroundStatus.On("Moved")); }
    public void BringBack() { Calls.Add("back"); Active = false; GameName = null; Changed?.Invoke(BackgroundStatus.Off()); }
    public void Setup() { Calls.Add("setup"); }
}

sealed class FakePhone : IDisposable
{
    readonly TcpClient _tcp = new();
    readonly UdpClient _udp = new();
    StreamReader _r = null!;
    StreamWriter _w = null!;
    public uint Key;
    public string Token = "";
    int _udpPort;
    uint _seq;

    public async Task<JsonObject> Hello(int port, string device, string token = "", string code = "", string pad = "x360")
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = _tcp.GetStream();
        _r = new StreamReader(s, Encoding.UTF8);
        _w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await Send(new JsonObject { ["t"] = "hello", ["v"] = 1, ["device"] = device, ["name"] = "Test Phone", ["token"] = token, ["code"] = code, ["pad"] = pad });
        var reply = await Read();
        if (reply["t"]!.GetValue<string>() == "welcome")
        {
            Key = reply["key"]!.GetValue<uint>();
            Token = reply["token"]!.GetValue<string>();
            _udpPort = reply["udp"]!.GetValue<int>();
        }
        return reply;
    }

    public Task Send(JsonObject o) => _w.WriteLineAsync(o.ToJsonString());

    public async Task<JsonObject> Read(string? type = null, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var line = await _r.ReadLineAsync(cts.Token) ?? throw new IOException("closed");
            var o = (JsonObject)JsonNode.Parse(line)!;
            if (type == null || o["t"]!.GetValue<string>() == type) return o;
        }
    }

    public void Input(PadState s, float mdx = 0, float mdy = 0, MotionSample m = default)
    {
        var b = InputPacket.Build(Key, ++_seq, s, mdx, mdy, m);
        _udp.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, _udpPort));
    }

    public void RawSeq(uint seq, PadState s)
    {
        var b = InputPacket.Build(Key, seq, s);
        _udp.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, _udpPort));
    }

    public async Task<byte[]> ReceiveUdp(int timeoutMs = 3000) =>
        (await _udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs))).Buffer;

    public void Dispose() { _tcp.Dispose(); _udp.Dispose(); }
}

public class ServerTests : IDisposable
{
    static int _port = 48800 + Random.Shared.Next(0, 500) * 2;
    readonly string _cfgPath = Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}.json");
    readonly Config _cfg;
    readonly FakeFactory _pads = new();
    readonly FakeHost _host = new();
    readonly HostServer _server;

    public ServerTests()
    {
        var p = Interlocked.Add(ref _port, 3);
        _cfg = new Config { ControlPort = p, InputPort = p + 1, DsuPort = p + 2, PairingCode = "123456", SavePath = _cfgPath };
        // keep test config out of the real AppData
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.GetTempPath());
        _server = new HostServer(_cfg, _pads, _host);
        _server.Start();
    }

    public void Dispose() { _server.Dispose(); try { File.Delete(_cfgPath); } catch { } }

    static async Task Until(Func<bool> cond, int ms = 2000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(10); }
    }

    [Fact]
    public async Task Pairs_with_code_then_reconnects_with_token()
    {
        using (var phone = new FakePhone())
        {
            var w = await phone.Hello(_cfg.ControlPort, "dev-1", code: "123456");
            Assert.Equal("welcome", w["t"]!.GetValue<string>());
            Assert.NotEmpty(phone.Token);
            var token = phone.Token;
            using var again = new FakePhone();
            var w2 = await again.Hello(_cfg.ControlPort, "dev-1", token: token);
            Assert.Equal("welcome", w2["t"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Rejects_wrong_code_and_locks_out_after_five()
    {
        for (int i = 0; i < 5; i++)
        {
            using var p = new FakePhone();
            var r = await p.Hello(_cfg.ControlPort, "evil", code: "000000");
            Assert.Equal("denied", r["t"]!.GetValue<string>());
        }
        using var p2 = new FakePhone();
        var r2 = await p2.Hello(_cfg.ControlPort, "evil", code: "123456");
        Assert.Equal("denied", r2["t"]!.GetValue<string>());
        Assert.Contains("wait", r2["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_code_does_not_count_toward_lockout()
    {
        for (int i = 0; i < 8; i++)
        {
            using var p = new FakePhone();
            Assert.Equal("denied", (await p.Hello(_cfg.ControlPort, "x"))["t"]!.GetValue<string>());
        }
        using var ok = new FakePhone();
        Assert.Equal("welcome", (await ok.Hello(_cfg.ControlPort, "x", code: "123456"))["t"]!.GetValue<string>());
    }

    [Fact]
    public async Task Udp_input_reaches_the_virtual_pad_and_idle_releases_it()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-2", code: "123456");
        var pad = _pads.Pads.Last();
        phone.Input(new PadState { Buttons = 1u << Btn.A | 1u << Btn.Up, LX = 0.5f, LY = -1f, RT = 1f });
        await Until(() => Btn.Has(pad.Last.Buttons, Btn.A));
        Assert.True(Btn.Has(pad.Last.Buttons, Btn.Up));
        Assert.InRange(pad.Last.LX, 0.49f, 0.51f);
        Assert.Equal(-1f, pad.Last.LY, 3);
        Assert.Equal(1f, pad.Last.RT, 2);
        // phone goes silent → everything released within ~0.5 s
        await Until(() => pad.Last.Buttons == 0 && pad.Last.LX == 0, 1500);
    }

    [Fact]
    public async Task Out_of_order_packets_are_dropped()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-3", code: "123456");
        var pad = _pads.Pads.Last();
        phone.RawSeq(10, new PadState { Buttons = 1u << Btn.B });
        await Until(() => Btn.Has(pad.Last.Buttons, Btn.B));
        phone.RawSeq(9, new PadState { Buttons = 1u << Btn.X });
        await Task.Delay(150);
        Assert.False(Btn.Has(pad.Last.Buttons, Btn.X));
    }

    [Fact]
    public async Task Wrong_session_key_is_ignored()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-4", code: "123456");
        var pad = _pads.Pads.Last();
        phone.Key ^= 0xFFFF;
        phone.Input(new PadState { Buttons = 1u << Btn.Y });
        await Task.Delay(150);
        Assert.False(Btn.Has(pad.Last.Buttons, Btn.Y));
    }

    [Fact]
    public async Task Macro_plays_steps_in_order_with_timing()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-5", code: "123456");
        var pad = _pads.Pads.Last();
        var seen = new List<uint>();
        var steps = new JsonArray
        {
            new JsonObject { ["b"] = 1 << Btn.Down, ["ms"] = 60 },
            new JsonObject { ["b"] = 1 << Btn.Right, ["ms"] = 60 },
            new JsonObject { ["b"] = 1 << Btn.X, ["ms"] = 60 },
        };
        await phone.Send(new JsonObject { ["t"] = "macro", ["id"] = "hadouken", ["steps"] = steps });
        var end = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < end) { var b = pad.Last.Buttons; if (seen.Count == 0 || seen[^1] != b) seen.Add(b); await Task.Delay(5); }
        var order = seen.Where(b => b != 0).ToList();
        Assert.Equal(new uint[] { 1u << Btn.Down, 1u << Btn.Right, 1u << Btn.X }, order);
        Assert.Equal(0u, pad.Last.Buttons);
    }

    [Fact]
    public async Task Looping_macro_stops_on_macroStop()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-6", code: "123456");
        var pad = _pads.Pads.Last();
        await phone.Send(new JsonObject { ["t"] = "macro", ["id"] = "turbo", ["loop"] = true, ["steps"] = new JsonArray { new JsonObject { ["b"] = 1 << Btn.A, ["ms"] = 20 } } });
        await Until(() => Btn.Has(pad.Last.Buttons, Btn.A));
        await phone.Send(new JsonObject { ["t"] = "macroStop", ["id"] = "turbo" });
        await Until(() => !Btn.Has(pad.Last.Buttons, Btn.A));
        await Task.Delay(100);
        Assert.False(Btn.Has(pad.Last.Buttons, Btn.A)); // and it stays released
    }

    [Fact]
    public async Task Rumble_from_game_is_forwarded_to_phone()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-7", code: "123456");
        _pads.Pads.Last().FireRumble(200, 50);
        var r = await phone.Read("rumble");
        Assert.Equal(200, r["l"]!.GetValue<int>());
        Assert.Equal(50, r["s"]!.GetValue<int>());
    }

    [Fact]
    public async Task Switching_to_playstation_replaces_the_pad()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-8", code: "123456");
        var x = _pads.Pads.Last();
        await phone.Send(new JsonObject { ["t"] = "pad", ["pad"] = "ds4" });
        await Until(() => _pads.Pads.Last().Type == PadType.DS4);
        await Until(() => x.Disposed); // old Xbox pad is unplugged right after the new one is plugged in
    }

    [Fact]
    public async Task Games_list_and_launch()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-9", code: "123456");
        await phone.Send(new JsonObject { ["t"] = "games" });
        var g = await phone.Read("games");
        Assert.Equal("Dota 2", g["list"]![0]!["name"]!.GetValue<string>());
        await phone.Send(new JsonObject { ["t"] = "launch", ["id"] = "steam:570" });
        await phone.Read("info");
        Assert.Contains("steam:570", _host.Launched);
    }

    [Fact]
    public async Task Gyro_mouse_deltas_move_the_cursor()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-10", code: "123456");
        phone.Input(default, 12.5f, -3f);
        await Until(() => _host.MouseX > 12);
        Assert.Equal(-3f, _host.MouseY, 2);
    }

    [Fact]
    public async Task Foreground_game_is_broadcast()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-11", code: "123456");
        _server.BroadcastForeground("eldenring.exe", "ELDEN RING");
        var fg = await phone.Read("fg");
        Assert.Equal("eldenring.exe", fg["exe"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ping_pong()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-12", code: "123456");
        await phone.Send(new JsonObject { ["t"] = "ping", ["ts"] = 42.5 });
        var p = await phone.Read("pong");
        Assert.Equal(42.5, p["ts"]!.GetValue<double>());
    }

    [Fact]
    public async Task Dsu_server_answers_emulators_with_valid_crc_and_motion()
    {
        using var phone = new FakePhone();
        await phone.Hello(_cfg.ControlPort, "dev-13", code: "123456");

        using var emu = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var dsu = new IPEndPoint(IPAddress.Loopback, _cfg.DsuPort);
        byte[] Req(uint type, byte[] payload)
        {
            var b = new byte[20 + payload.Length];
            Encoding.ASCII.GetBytes("DSUC").CopyTo(b, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 1001);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(b.Length - 16));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), type);
            payload.CopyTo(b, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), DsuServer.Crc32(b));
            return b;
        }
        static void CheckCrc(byte[] r)
        {
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(8));
            var copy = (byte[])r.Clone();
            copy[8] = copy[9] = copy[10] = copy[11] = 0;
            Assert.Equal(crc, DsuServer.Crc32(copy));
            Assert.Equal(r.Length - 16, BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(6)));
        }

        // port info for slot 0 → connected
        var pi = Req(0x100001, new byte[] { 1, 0, 0, 0, 0 });
        emu.Send(pi, pi.Length, dsu);
        var info = (await emu.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer;
        CheckCrc(info);
        Assert.Equal("DSUS", Encoding.ASCII.GetString(info, 0, 4));
        Assert.Equal(2, info[21]); // slot state connected

        // subscribe to slot 0, then send motion from the phone
        var sub = Req(0x100002, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        emu.Send(sub, sub.Length, dsu);
        await Task.Delay(50);
        phone.Input(new PadState { Buttons = 1u << Btn.A, LY = 1f }, 0, 0,
            new MotionSample { Valid = true, AccelY = 1f, GyroYaw = 90f, TimestampUs = 123456 });

        byte[] data;
        do data = (await emu.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer;
        while (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) != 0x100002 || (data[37] & 0x20) == 0);
        CheckCrc(data);
        Assert.Equal(100, data.Length);
        Assert.Equal(255, data[41]);                                                  // LY up
        Assert.Equal(123456UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(68)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(80)));   // accel Y
        Assert.Equal(90f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(92)));  // gyro yaw
    }

    [Fact]
    public void Tailnet_filter()
    {
        Assert.True(HostServer.IsTailnet(IPAddress.Parse("100.101.102.103")));
        Assert.True(HostServer.IsTailnet(IPAddress.Parse("100.64.0.1")));
        Assert.False(HostServer.IsTailnet(IPAddress.Parse("100.128.0.1")));
        Assert.False(HostServer.IsTailnet(IPAddress.Parse("192.168.1.5")));
        Assert.True(HostServer.IsTailnet(IPAddress.Parse("fd7a:115c:a1e0::1")));
        Assert.True(HostServer.IsTailnet(IPAddress.Parse("::ffff:100.80.1.1")));
        Assert.True(HostServer.IsPrivateLan(IPAddress.Parse("192.168.1.5")));
        Assert.False(HostServer.IsPrivateLan(IPAddress.Parse("8.8.8.8")));
    }
}

public class LibraryTests
{
    [Fact]
    public void Finds_steam_and_epic_games()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var lib2 = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n \"0\" {{ \"path\" \"{root}\" }}\n \"1\" {{ \"path\" \"{lib2}\" }}\n}}");
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_1245620.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"1245620\"\n\t\"name\"\t\t\"ELDEN RING\"\n}");
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_228980.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"228980\"\n\t\"name\"\t\t\"Steamworks Common Redistributables\"\n}");
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_1091500.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"1091500\"\n\t\"name\"\t\t\"Cyberpunk 2077\"\n}");
        var epic = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(epic, "a.item"),
            "{\"DisplayName\":\"Fortnite\",\"AppName\":\"Fortnite\",\"CatalogNamespace\":\"fn\",\"CatalogItemId\":\"4fe7\",\"bIsApplication\":true}");

        var games = GameLibrary.Scan(root, epic, new[] { new CustomGame { Name = "Dolphin", Path = @"C:\Emu\Dolphin.exe" } });
        Assert.Equal(new[] { "Cyberpunk 2077", "Dolphin", "ELDEN RING", "Fortnite" }, games.Select(g => g.Name));
        Assert.Equal("steam://rungameid/1245620", games.Single(g => g.Name == "ELDEN RING").Target);
        Assert.StartsWith("com.epicgames.launcher://apps/fn%3A4fe7%3AFortnite", games.Single(g => g.Name == "Fortnite").Target);
    }

    [Fact]
    public void Input_packet_round_trips()
    {
        var s = new PadState { Buttons = 0x8001, LX = -0.25f, LY = 1, RX = 0, RY = -1, LT = 0.5f, RT = 1 };
        var m = new MotionSample { Valid = true, AccelX = 0.1f, GyroRoll = -45, TimestampUs = 999 };
        Assert.True(InputPacket.TryParse(InputPacket.Build(7, 9, s, 1.5f, -2, m), out var p));
        Assert.Equal(7u, p.Key); Assert.Equal(9u, p.Seq);
        Assert.Equal(0x8001u, p.Pad.Buttons);
        Assert.Equal(-0.25f, p.Pad.LX, 3); Assert.Equal(-1f, p.Pad.RY, 3); Assert.Equal(0.5f, p.Pad.LT, 2);
        Assert.Equal(1.5f, p.MouseDx); Assert.True(p.Motion.Valid); Assert.Equal(-45f, p.Motion.GyroRoll);
        Assert.False(InputPacket.TryParse(new byte[10], out _));
    }
}
