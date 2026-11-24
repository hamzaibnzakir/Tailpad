using System.Buffers.Binary;

namespace TailPad.Core;

public readonly record struct InputPacket(uint Key, uint Seq, PadState Pad, float MouseDx, float MouseDy, MotionSample Motion)
{
    public const uint Magic = 0x31445054; // "TPD1"
    public const int Size = 68;

    public static bool TryParse(ReadOnlySpan<byte> b, out InputPacket p)
    {
        p = default;
        if (b.Length < Size || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) return false;

        static float Axis(short v) => Math.Clamp(v / 32767f, -1f, 1f);
        static float F(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadSingleLittleEndian(s) is var f && float.IsFinite(f) ? f : 0f;

        var pad = new PadState
        {
            Buttons = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
            LX = Axis(BinaryPrimitives.ReadInt16LittleEndian(b[16..])),
            LY = Axis(BinaryPrimitives.ReadInt16LittleEndian(b[18..])),
            RX = Axis(BinaryPrimitives.ReadInt16LittleEndian(b[20..])),
            RY = Axis(BinaryPrimitives.ReadInt16LittleEndian(b[22..])),
            LT = b[24] / 255f,
            RT = b[25] / 255f,
        };
        var m = new MotionSample
        {
            Valid = (b[26] & 1) != 0,
            AccelX = F(b[36..]), AccelY = F(b[40..]), AccelZ = F(b[44..]),
            GyroPitch = F(b[48..]), GyroYaw = F(b[52..]), GyroRoll = F(b[56..]),
            TimestampUs = BinaryPrimitives.ReadUInt64LittleEndian(b[60..]),
        };
        p = new InputPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            pad, Math.Clamp(F(b[28..]), -2000, 2000), Math.Clamp(F(b[32..]), -2000, 2000), m);
        return true;
    }

    /// <summary>Used by tests / fake phones.</summary>
    public static byte[] Build(uint key, uint seq, in PadState s, float mdx = 0, float mdy = 0, MotionSample m = default)
    {
        var b = new byte[Size];
        static short A(float v) => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), key);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), s.Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(16), A(s.LX));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(18), A(s.LY));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), A(s.RX));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), A(s.RY));
        b[24] = (byte)Math.Round(Math.Clamp(s.LT, 0, 1) * 255);
        b[25] = (byte)Math.Round(Math.Clamp(s.RT, 0, 1) * 255);
        b[26] = (byte)(m.Valid ? 1 : 0);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28), mdx);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32), mdy);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36), m.AccelX);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(40), m.AccelY);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(44), m.AccelZ);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(48), m.GyroPitch);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(52), m.GyroYaw);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(56), m.GyroRoll);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(60), m.TimestampUs);
        return b;
    }
}
