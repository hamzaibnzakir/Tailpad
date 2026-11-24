using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace TailPad.Core;

/// <summary>
/// cemuhook "DSU" motion server. Emulators (Cemu, Dolphin, Ryujinx, Citra, Yuzu forks…) connect to
/// 127.0.0.1:26760 and get the phone's gyro + buttons. Up to 4 phones = slots 0-3.
/// </summary>
public sealed class DsuServer : IDisposable
{
    const ushort ProtoVersion = 1001;
    const uint MsgVersion = 0x100000, MsgPorts = 0x100001, MsgData = 0x100002;

    readonly UdpClient _udp;
    readonly uint _serverId = (uint)Random.Shared.Next();
    readonly object _gate = new();
    readonly Dictionary<IPEndPoint, Subscriber> _subs = new();
    readonly Func<int, (bool connected, PadState pad, MotionSample motion)> _slot;
    readonly CancellationTokenSource _cts = new();
    readonly uint[] _packetNo = new uint[4];

    sealed class Subscriber { public DateTime Last; public bool All; public readonly bool[] Slots = new bool[4]; }

    public int Port { get; }

    public DsuServer(int port, Func<int, (bool, PadState, MotionSample)> slotProvider)
    {
        Port = port;
        _slot = slotProvider;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452; // ignore ICMP "port unreachable" resets on Windows
            _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        }
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            try { Handle(r.Buffer, r.RemoteEndPoint); } catch { /* malformed */ }
        }
    }

    void Handle(byte[] b, IPEndPoint from)
    {
        if (b.Length < 20 || b[0] != 'D' || b[1] != 'S' || b[2] != 'U' || b[3] != 'C') return;
        var type = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        switch (type)
        {
            case MsgVersion:
                {
                    var p = new byte[2];
                    BinaryPrimitives.WriteUInt16LittleEndian(p, ProtoVersion);
                    Send(MsgVersion, p, from);
                    break;
                }
            case MsgPorts:
                {
                    if (b.Length < 24) return;
                    var n = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(20)), 0, 4);
                    for (int i = 0; i < n && 24 + i < b.Length; i++)
                    {
                        int slot = b[24 + i];
                        if (slot > 3) continue;
                        var p = new byte[12];
                        WriteShared(p, slot, _slot(slot).connected);
                        Send(MsgPorts, p, from);
                    }
                    break;
                }
            case MsgData:
                {
                    if (b.Length < 28) return;
                    byte flags = b[20], slot = b[21];
                    lock (_gate)
                    {
                        if (!_subs.TryGetValue(from, out var s)) _subs[from] = s = new Subscriber();
                        s.Last = DateTime.UtcNow;
                        if (flags == 0) s.All = true;
                        else if ((flags & 1) != 0 && slot < 4) s.Slots[slot] = true;
                        else if ((flags & 2) != 0 && b[27] - 1 is var ms and >= 0 and < 4) s.Slots[ms] = true; // our MAC ends in slot+1
                    }
                    break;
                }
        }
    }

    static void WriteShared(Span<byte> p, int slot, bool connected)
    {
        p[0] = (byte)slot;
        p[1] = (byte)(connected ? 2 : 0);  // slot state
        p[2] = 2;                          // model: full gyro
        p[3] = 2;                          // connection: bluetooth
        p[4] = 0x7A; p[5] = 0x50; p[6] = 0x41; p[7] = 0x44; p[8] = 0x00; p[9] = (byte)(slot + 1); // MAC
        p[10] = connected ? (byte)0x05 : (byte)0x00; // battery full
    }

    /// <summary>Called whenever new input for a slot arrives.</summary>
    public void Publish(int slot)
    {
        if (slot is < 0 or > 3) return;
        List<IPEndPoint> targets;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var dead in _subs.Where(kv => now - kv.Value.Last > TimeSpan.FromSeconds(5)).Select(kv => kv.Key).ToList())
                _subs.Remove(dead);
            targets = _subs.Where(kv => kv.Value.All || kv.Value.Slots[slot]).Select(kv => kv.Key).ToList();
        }
        if (targets.Count == 0) return;

        var (connected, pad, m) = _slot(slot);
        var p = new byte[80];
        WriteShared(p, slot, connected);
        p[11] = (byte)(connected ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), ++_packetNo[slot]);
        uint bt = pad.Buttons;
        byte b1 = 0, b2 = 0;
        if (Btn.Has(bt, Btn.Left)) b1 |= 0x80; if (Btn.Has(bt, Btn.Down)) b1 |= 0x40;
        if (Btn.Has(bt, Btn.Right)) b1 |= 0x20; if (Btn.Has(bt, Btn.Up)) b1 |= 0x10;
        if (Btn.Has(bt, Btn.Start)) b1 |= 0x08; if (Btn.Has(bt, Btn.RS)) b1 |= 0x04;
        if (Btn.Has(bt, Btn.LS)) b1 |= 0x02; if (Btn.Has(bt, Btn.Back)) b1 |= 0x01;
        if (Btn.Has(bt, Btn.Y)) b2 |= 0x80; if (Btn.Has(bt, Btn.B)) b2 |= 0x40;
        if (Btn.Has(bt, Btn.A)) b2 |= 0x20; if (Btn.Has(bt, Btn.X)) b2 |= 0x10;
        if (Btn.Has(bt, Btn.RB)) b2 |= 0x08; if (Btn.Has(bt, Btn.LB)) b2 |= 0x04;
        if (pad.RT > 0.5f) b2 |= 0x02; if (pad.LT > 0.5f) b2 |= 0x01;
        p[16] = b1; p[17] = b2;
        p[18] = (byte)(Btn.Has(bt, Btn.Guide) ? 1 : 0);
        p[19] = (byte)(Btn.Has(bt, Btn.Touchpad) ? 1 : 0);
        static byte S(float v) => (byte)Math.Clamp(Math.Round(128 + v * 127), 0, 255); // DSU: up = 255
        p[20] = S(pad.LX); p[21] = S(pad.LY); p[22] = S(pad.RX); p[23] = S(pad.RY);
        static byte D(bool on) => on ? (byte)255 : (byte)0;
        p[24] = D(Btn.Has(bt, Btn.Left)); p[25] = D(Btn.Has(bt, Btn.Down));
        p[26] = D(Btn.Has(bt, Btn.Right)); p[27] = D(Btn.Has(bt, Btn.Up));
        p[28] = D(Btn.Has(bt, Btn.Y)); p[29] = D(Btn.Has(bt, Btn.B));
        p[30] = D(Btn.Has(bt, Btn.A)); p[31] = D(Btn.Has(bt, Btn.X));
        p[32] = D(Btn.Has(bt, Btn.RB)); p[33] = D(Btn.Has(bt, Btn.LB));
        p[34] = (byte)(pad.RT * 255); p[35] = (byte)(pad.LT * 255);
        // 36..47: two touch points, unused
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), m.TimestampUs);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(56), m.AccelX);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(60), m.AccelY);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(64), m.AccelZ);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(68), m.GyroPitch);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(72), m.GyroYaw);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(76), m.GyroRoll);
        foreach (var t in targets) Send(MsgData, p, t);
    }

    void Send(uint type, byte[] payload, IPEndPoint to)
    {
        var pkt = new byte[20 + payload.Length];
        pkt[0] = (byte)'D'; pkt[1] = (byte)'S'; pkt[2] = (byte)'U'; pkt[3] = (byte)'S';
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(4), ProtoVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(6), (ushort)(pkt.Length - 16));
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(12), _serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(16), type);
        payload.CopyTo(pkt, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(8), Crc32(pkt));
        try { _udp.Send(pkt, pkt.Length, to); } catch { }
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public void Dispose() { _cts.Cancel(); _udp.Dispose(); }
}
