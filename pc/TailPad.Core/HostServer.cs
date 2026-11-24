using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TailPad.Core;

public sealed class HostServer : IDisposable
{
    public const int ProtocolVersion = 1;
    static readonly TimeSpan IdleLimit = TimeSpan.FromMilliseconds(500);

    readonly Config _cfg;
    readonly IPadFactory _pads;
    readonly IHostServices _host;
    readonly ConcurrentDictionary<uint, Session> _byKey = new();
    readonly ConcurrentDictionary<IPAddress, (int fails, DateTime until)> _lockout = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _slotGate = new();
    readonly Session?[] _slots = new Session?[4];
    TcpListener? _tcp;
    UdpClient? _udp;
    DsuServer? _dsu;
    (string exe, string title) _fg = ("", "");

    public event Action? SessionsChanged;
    public event Action<string>? Log;

    public HostServer(Config cfg, IPadFactory pads, IHostServices host)
    {
        _cfg = cfg; _pads = pads; _host = host;
    }

    public IReadOnlyCollection<Session> Sessions => _byKey.Values.ToList();
    public bool DsuRunning => _dsu != null;

    public void Start()
    {
        // IPv4+IPv6 when the PC has IPv6, plain IPv4 when it's disabled
        try
        {
            _tcp = new TcpListener(IPAddress.IPv6Any, _cfg.ControlPort);
            _tcp.Server.DualMode = true;
            _tcp.Start();
            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            _udp.Client.DualMode = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, _cfg.InputPort));
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            try { _tcp?.Stop(); } catch { }
            _udp?.Dispose();
            _tcp = new TcpListener(IPAddress.Any, _cfg.ControlPort);
            _tcp.Start();
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _cfg.InputPort));
        }
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        }

        if (_cfg.DsuEnabled)
        {
            try { _dsu = new DsuServer(_cfg.DsuPort, SlotState); }
            catch (Exception e) { Log?.Invoke($"Emulator motion server off: {e.Message}"); }
        }

        _ = Task.Run(AcceptLoop);
        _ = Task.Run(UdpLoop);
        _ = Task.Run(WatchdogLoop);
        Log?.Invoke($"Listening on TCP {_cfg.ControlPort} / UDP {_cfg.InputPort}");
    }

    (bool, PadState, MotionSample) SlotState(int slot)
    {
        Session? s;
        lock (_slotGate) s = _slots[slot];
        if (s == null) return (false, default, default);
        var (p, m) = s.Snapshot();
        return (true, p, m);
    }

    public static bool IsTailnet(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork) return b[0] == 100 && (b[1] & 0xC0) == 64; // 100.64.0.0/10
        return b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    public static bool IsPrivateLan(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
        return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
    }

    bool Allowed(IPAddress a) => IsTailnet(a) || (_cfg.AllowLan && IsPrivateLan(a));

    static IPAddress Norm(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _tcp!.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleClient(c));
        }
    }

    async Task HandleClient(TcpClient client)
    {
        var ip = Norm(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        if (!Allowed(ip))
        {
            Log?.Invoke($"Rejected {ip} (not on your tailnet — enable 'Allow Wi-Fi/LAN' to permit)");
            client.Dispose();
            return;
        }
        client.NoDelay = true;
        Session? session = null;
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);

            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            helloCts.CancelAfter(TimeSpan.FromSeconds(8));
            var first = await ReadLine(reader, helloCts.Token);
            if (first == null) return;
            session = Handshake(first, ip, stream);
            if (session == null) return;

            while (!_cts.IsCancellationRequested)
            {
                var line = await ReadLine(reader, _cts.Token);
                if (line == null) break;
                if (!HandleMessage(session, line)) break;
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Invoke($"Connection error: {e.Message}"); }
        finally
        {
            if (session != null) Remove(session);
        }
    }

    static async Task<string?> ReadLine(StreamReader r, CancellationToken ct)
    {
        var line = await r.ReadLineAsync(ct);
        if (line != null && line.Length > 64 * 1024) throw new IOException("message too large");
        return line;
    }

    static void Write(Stream s, JsonNode msg)
    {
        var b = Encoding.UTF8.GetBytes(msg.ToJsonString() + "\n");
        s.Write(b);
    }

    Session? Handshake(string line, IPAddress ip, Stream stream)
    {
        JsonObject? hello;
        try { hello = JsonNode.Parse(line) as JsonObject; } catch { return null; }
        if (hello?["t"]?.GetValue<string>() != "hello") return null;

        string S(string k) => hello[k] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
        var deviceId = S("device"); var name = S("name"); var token = S("token"); var code = S("code");
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 80) { Deny(stream, "Bad hello"); return null; }
        name = string.IsNullOrWhiteSpace(name) ? "Phone" : name[..Math.Min(name.Length, 40)];

        if (_lockout.TryGetValue(ip, out var lo) && lo.until > DateTime.UtcNow)
        {
            Deny(stream, "Too many wrong codes — wait a minute"); return null;
        }

        PairedDevice? dev;
        lock (_cfg)
        {
            dev = _cfg.Devices.FirstOrDefault(d => d.Id == deviceId);
            bool tokenOk = dev != null && token.Length > 0 &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(dev.Token), Encoding.UTF8.GetBytes(token));
            if (!tokenOk)
            {
                bool codeOk = code.Length > 0 &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(_cfg.PairingCode));
                if (!codeOk && code.Length == 0)
                {
                    Deny(stream, dev == null ? "Enter the pairing code shown on your PC" : "Pairing expired — enter the code again");
                    return null;
                }
                if (!codeOk)
                {
                    // 5 wrong codes from one address → locked out for a minute
                    var f = _lockout.GetValueOrDefault(ip).fails + 1;
                    _lockout[ip] = f >= 5 ? (0, DateTime.UtcNow.AddMinutes(1)) : (f, DateTime.MinValue);
                    Deny(stream, "Wrong pairing code");
                    return null;
                }
                _lockout.TryRemove(ip, out _);
                if (dev == null) { dev = new PairedDevice { Id = deviceId }; _cfg.Devices.Add(dev); }
                dev.Token = Config.NewToken();
                _host.Notify($"Paired {name}");
            }
            dev!.Name = name; // tokenOk implies dev != null
            dev.LastSeen = DateTime.Now;
            _cfg.Save();
        }

        // one session per device: a reconnect replaces the old one
        foreach (var old in _byKey.Values.Where(s => s.DeviceId == deviceId).ToList()) Remove(old);

        var type = S("pad") == "ds4" ? PadType.DS4 : PadType.X360;
        IVirtualPad pad;
        try { pad = _pads.Create(type); }
        catch (Exception e)
        {
            Deny(stream, "PC can't create a controller: " + e.Message);
            Log?.Invoke("Controller driver error: " + e.Message);
            return null;
        }

        uint key;
        do key = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue); while (_byKey.ContainsKey(key));
        var session = new Session(deviceId, name, key, ip, pad, stream, OnPublished);
        _byKey[key] = session;
        AssignSlot(session);

        session.Send(new JsonObject
        {
            ["t"] = "welcome", ["v"] = ProtocolVersion, ["token"] = dev!.Token, ["key"] = key,
            ["udp"] = _cfg.InputPort, ["pc"] = Environment.MachineName,
            ["player"] = session.DsuSlot + 1, ["pad"] = type == PadType.DS4 ? "ds4" : "x360",
        });
        if (_fg.exe.Length > 0) session.Send(new JsonObject { ["t"] = "fg", ["exe"] = _fg.exe, ["title"] = _fg.title });
        Log?.Invoke($"{name} connected ({ip}) as {(type == PadType.DS4 ? "PlayStation" : "Xbox")} controller");
        SessionsChanged?.Invoke();
        return session;
    }

    static void Deny(Stream s, string reason)
    {
        try { Write(s, new JsonObject { ["t"] = "denied", ["reason"] = reason }); } catch { }
    }

    void AssignSlot(Session s)
    {
        lock (_slotGate)
            for (int i = 0; i < 4; i++)
                if (_slots[i] == null) { _slots[i] = s; s.DsuSlot = i; return; }
    }

    bool HandleMessage(Session s, string line)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(line) as JsonObject; } catch { return true; }
        if (m == null) return true;
        string S(string k) => m[k] is JsonValue v && v.TryGetValue(out string? x) ? x ?? "" : "";

        switch (S("t"))
        {
            case "ping":
                s.Send(new JsonObject { ["t"] = "pong", ["ts"] = m["ts"]?.DeepClone() });
                break;
            case "pad":
                {
                    var type = S("pad") == "ds4" ? PadType.DS4 : PadType.X360;
                    if (type != s.PadType)
                    {
                        try { s.SwapPad(_pads.Create(type)); Log?.Invoke($"{s.Name} switched to {(type == PadType.DS4 ? "PlayStation" : "Xbox")}"); }
                        catch (Exception e) { s.Send(new JsonObject { ["t"] = "info", ["msg"] = "Couldn't switch: " + e.Message }); }
                        SessionsChanged?.Invoke();
                    }
                    break;
                }
            case "macro":
                {
                    var id = S("id");
                    if (id.Length == 0) break;
                    var loop = m["loop"] is JsonValue lv && lv.TryGetValue(out bool l) && l;
                    s.Macros.Start(id, MacroRunner.ParseSteps(m["steps"] as JsonArray), loop);
                    break;
                }
            case "macroStop":
                s.Macros.Stop(S("id"));
                break;
            case "games":
                {
                    var arr = new JsonArray();
                    foreach (var g in _host.Games())
                        arr.Add(new JsonObject { ["id"] = g.Id, ["name"] = g.Name, ["source"] = g.Source, ["art"] = g.Art });
                    s.Send(new JsonObject { ["t"] = "games", ["list"] = arr });
                    break;
                }
            case "launch":
                {
                    var g = _host.Games().FirstOrDefault(x => x.Id == S("id"));
                    if (g == null) { s.Send(new JsonObject { ["t"] = "info", ["msg"] = "Game not found" }); break; }
                    try { _host.Launch(g); s.Send(new JsonObject { ["t"] = "info", ["msg"] = $"Launching {g.Name}" }); Log?.Invoke($"{s.Name} launched {g.Name}"); }
                    catch (Exception e) { s.Send(new JsonObject { ["t"] = "info", ["msg"] = "Launch failed: " + e.Message }); }
                    break;
                }
            case "bye":
                return false;
        }
        return true;
    }

    async Task UdpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (!InputPacket.TryParse(r.Buffer, out var p)) continue;
            if (!_byKey.TryGetValue(p.Key, out var s)) continue;
            if (!Norm(r.RemoteEndPoint.Address).Equals(s.Address)) continue; // key must come from the paired phone
            if (!s.Accept(p)) continue;
            if (_cfg.GyroMouse && (p.MouseDx != 0 || p.MouseDy != 0))
                try { _host.MoveMouse(p.MouseDx, p.MouseDy); } catch { }
        }
    }

    void OnPublished(Session s) => _dsu?.Publish(s.DsuSlot);

    async Task WatchdogLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(100, _cts.Token); } catch { return; }
            foreach (var s in _byKey.Values) s.CheckIdle(IdleLimit);
        }
    }

    void Remove(Session s)
    {
        if (!_byKey.TryRemove(s.Key, out _)) return;
        lock (_slotGate)
            for (int i = 0; i < 4; i++) if (_slots[i] == s) _slots[i] = null;
        _dsu?.Publish(s.DsuSlot);
        s.Dispose();
        Log?.Invoke($"{s.Name} disconnected");
        SessionsChanged?.Invoke();
    }

    public void Kick(uint key)
    {
        if (_byKey.TryGetValue(key, out var s)) { s.Send(new JsonObject { ["t"] = "info", ["msg"] = "Disconnected from PC" }); Remove(s); }
    }

    public void Forget(string deviceId)
    {
        foreach (var s in _byKey.Values.Where(x => x.DeviceId == deviceId).ToList()) Remove(s);
        lock (_cfg) { _cfg.Devices.RemoveAll(d => d.Id == deviceId); _cfg.Save(); }
    }

    public void BroadcastForeground(string exe, string title)
    {
        _fg = (exe, title);
        foreach (var s in _byKey.Values) s.Send(new JsonObject { ["t"] = "fg", ["exe"] = exe, ["title"] = title });
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in _byKey.Values.ToList()) Remove(s);
        try { _tcp?.Stop(); } catch { }
        _udp?.Dispose();
        _dsu?.Dispose();
    }
}
