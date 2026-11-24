using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TailPad.Core;

/// <summary>One connected phone = one virtual controller.</summary>
public sealed class Session : IDisposable
{
    readonly object _gate = new();
    readonly StreamWriter _out;
    readonly Action<Session> _published;
    IVirtualPad _pad;
    PadState _live;
    MotionSample _motion;
    uint _lastSeq;
    bool _haveSeq;

    public string DeviceId { get; }
    public string Name { get; }
    public uint Key { get; }
    public IPAddress Address { get; }
    public int DsuSlot { get; set; } = -1;
    public int Player { get; private set; }
    public PadType PadType => _pad.Type;
    public DateTime LastInput { get; private set; } = DateTime.UtcNow;
    public bool Idle { get; private set; } = true;
    public MacroRunner Macros { get; }
    public DateTime Connected { get; } = DateTime.Now;
    /// <summary>Where the phone's input packets come from — remote-play video/audio is sent back here.</summary>
    public IPEndPoint? UdpEndpoint { get; set; }
    public IDisposable? RemotePlay { get; set; }

    public Session(string deviceId, string name, uint key, IPAddress address, IVirtualPad pad, Stream stream, Action<Session> published)
    {
        DeviceId = deviceId; Name = name; Key = key; Address = address;
        _out = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _published = published;
        Macros = new MacroRunner(Push);
        _pad = pad;
        Hook(_pad);
    }

    void Hook(IVirtualPad pad)
    {
        pad.Rumble += (l, s) => Send(new JsonObject { ["t"] = "rumble", ["l"] = l, ["s"] = s });
        pad.Light += (r, g, b) => Send(new JsonObject { ["t"] = "light", ["r"] = r, ["g"] = g, ["b"] = b });
        pad.Player += n => { Player = n; Send(new JsonObject { ["t"] = "player", ["n"] = n }); };
    }

    public void SwapPad(IVirtualPad newPad)
    {
        lock (_gate)
        {
            _pad.Reset();
            _pad.Dispose();
            _pad = newPad;
            Hook(_pad);
        }
        Push();
    }

    /// <summary>Returns false for stale/out-of-order packets.</summary>
    public bool Accept(in InputPacket p)
    {
        lock (_gate)
        {
            if (_haveSeq && (int)(p.Seq - _lastSeq) <= 0 && (int)(_lastSeq - p.Seq) < 1000) return false;
            _haveSeq = true;
            _lastSeq = p.Seq;
            _live = p.Pad;
            _motion = p.Motion;
            LastInput = DateTime.UtcNow;
            Idle = false;
        }
        Push();
        return true;
    }

    public void CheckIdle(TimeSpan limit)
    {
        lock (_gate)
        {
            if (Idle || DateTime.UtcNow - LastInput < limit) return;
            Idle = true;
            _live = default;
            _motion = default;
        }
        Push();
    }

    public (PadState pad, MotionSample motion) Snapshot()
    {
        lock (_gate) return (Macros.Overlay(_live), _motion);
    }

    void Push()
    {
        PadState s; MotionSample m;
        lock (_gate)
        {
            s = Macros.Overlay(_live);
            m = _motion;
            try { _pad.Submit(s, m); } catch { /* driver hiccup; next frame retries */ }
        }
        _published(this);
    }

    public void Send(JsonNode msg)
    {
        var line = msg.ToJsonString();
        lock (_out)
        {
            try { _out.WriteLine(line); } catch { /* socket closing */ }
        }
    }

    public void Dispose()
    {
        try { RemotePlay?.Dispose(); } catch { }
        RemotePlay = null;
        Macros.Dispose();
        lock (_gate)
        {
            try { _pad.Reset(); } catch { }
            _pad.Dispose();
        }
    }
}
