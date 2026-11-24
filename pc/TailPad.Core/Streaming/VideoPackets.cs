using System.Buffers.Binary;

namespace TailPad.Core.Streaming;

/// <summary>
/// Remote-play packet format (PC → phone, UDP, little-endian). Kept under Tailscale's 1280-byte MTU.
///
/// Video: 20-byte header + up to 1100 bytes of one H.264 access unit (Annex B).
///   0 u32 magic "TPV1" · 4 u32 frame number · 8 u16 fragment index · 10 u16 fragment count
///   12 u8 flags (bit0 = keyframe) · 13 u8 reserved · 14 u16 reserved · 16 u32 capture time (ms)
/// Audio: 12-byte header + one Opus packet (10 ms, 48 kHz stereo).
///   0 u32 magic "TPA1" · 4 u32 sequence · 8 u32 capture time (ms)
/// </summary>
public static class StreamPackets
{
    public const uint VideoMagic = 0x31565054; // "TPV1"
    public const uint AudioMagic = 0x31415054; // "TPA1"
    public const int VideoHeader = 20, AudioHeader = 12;
    public const int MaxPayload = 1100;

    public static List<byte[]> Fragment(uint frame, ReadOnlySpan<byte> au, bool keyframe, uint timeMs)
    {
        int count = Math.Max(1, (au.Length + MaxPayload - 1) / MaxPayload);
        if (count > ushort.MaxValue) throw new ArgumentException("frame too large");
        var list = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var chunk = au.Slice(i * MaxPayload, Math.Min(MaxPayload, au.Length - i * MaxPayload));
            var p = new byte[VideoHeader + chunk.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(p, VideoMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), frame);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), (ushort)count);
            p[12] = (byte)(keyframe ? 1 : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), timeMs);
            chunk.CopyTo(p.AsSpan(VideoHeader));
            list.Add(p);
        }
        return list;
    }

    public static byte[] Audio(uint seq, uint timeMs, ReadOnlySpan<byte> opus)
    {
        var p = new byte[AudioHeader + opus.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, AudioMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), timeMs);
        opus.CopyTo(p.AsSpan(AudioHeader));
        return p;
    }

    /// <summary>NAL unit types (H.264) inside an Annex-B buffer.</summary>
    public static IEnumerable<int> NalTypes(byte[] au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) { yield return au[i + 3] & 0x1F; i += 3; }
        }
    }

    public static bool IsKeyframe(byte[] au) => NalTypes(au).Any(t => t == 5);
}

/// <summary>
/// Cuts FFmpeg's raw H.264 output into access units (one per video frame).
/// FFmpeg is told to insert an Access Unit Delimiter (NAL type 9) before every frame, so frames start at each AUD.
/// </summary>
public sealed class AccessUnitReader
{
    readonly List<byte> _buf = new(256 * 1024);
    int _scanFrom;
    int _auStart = -1;

    /// <summary>Feed bytes; returns any complete access units.</summary>
    public List<byte[]> Push(ReadOnlySpan<byte> data)
    {
        var done = new List<byte[]>();
        foreach (var b in data) _buf.Add(b);
        // find start codes followed by an AUD
        for (int i = Math.Max(0, _scanFrom); i + 3 < _buf.Count; i++)
        {
            if (_buf[i] != 0 || _buf[i + 1] != 0 || _buf[i + 2] != 1 || (_buf[i + 3] & 0x1F) != 9) continue;
            int start = i > 0 && _buf[i - 1] == 0 ? i - 1 : i; // 4-byte start code
            if (_auStart >= 0 && start > _auStart)
                done.Add(_buf.GetRange(_auStart, start - _auStart).ToArray());
            _auStart = start;
            i += 3;
        }
        _scanFrom = Math.Max(0, _buf.Count - 4);
        // drop consumed bytes
        if (_auStart > 0)
        {
            _buf.RemoveRange(0, _auStart);
            _scanFrom -= _auStart;
            _auStart = 0;
        }
        if (_buf.Count > 16 * 1024 * 1024) { _buf.Clear(); _auStart = -1; _scanFrom = 0; } // garbage guard
        return done;
    }
}

/// <summary>Rebuilds frames from video packets (used by tests; the iPhone has the same logic in Swift).</summary>
public sealed class FrameAssembler
{
    sealed class Partial { public byte[]?[] Parts = Array.Empty<byte[]>(); public int Have; public bool Key; }
    readonly Dictionary<uint, Partial> _frames = new();
    uint _last;
    bool _any;
    bool _waitKey = true;

    public int Dropped { get; private set; }

    /// <summary>Returns a complete frame when this packet finishes one.</summary>
    public (uint frame, byte[] au, bool key)? Push(ReadOnlySpan<byte> p)
    {
        if (p.Length < StreamPackets.VideoHeader || BinaryPrimitives.ReadUInt32LittleEndian(p) != StreamPackets.VideoMagic) return null;
        uint frame = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        int idx = BinaryPrimitives.ReadUInt16LittleEndian(p[8..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(p[10..]);
        bool key = (p[12] & 1) != 0;
        if (_any && (int)(frame - _last) <= 0) return null; // old
        if (!_frames.TryGetValue(frame, out var f))
        {
            f = new Partial { Parts = new byte[count][], Key = key };
            _frames[frame] = f;
        }
        if (idx >= f.Parts.Length || f.Parts[idx] != null) return null;
        f.Parts[idx] = p[StreamPackets.VideoHeader..].ToArray();
        if (++f.Have < f.Parts.Length) return null;

        _frames.Remove(frame);
        // anything older that never completed is lost → wait for the next keyframe to avoid smeared video
        foreach (var old in _frames.Keys.Where(k => (int)(k - frame) < 0).ToList()) { _frames.Remove(old); Dropped++; _waitKey = true; }
        if (_any && frame != _last + 1 && (int)(frame - _last) > 1) _waitKey = true;
        _last = frame; _any = true;
        if (_waitKey && !f.Key) { Dropped++; return null; }
        _waitKey = false;
        var au = f.Parts.SelectMany(x => x!).ToArray();
        return (frame, au, f.Key);
    }
}
