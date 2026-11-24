namespace TailPad.Core;

/// <summary>Button bit positions shared with the phone (see docs/PROTOCOL.md).</summary>
public static class Btn
{
    public const int A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, Back = 6, Start = 7, Guide = 8,
        LS = 9, RS = 10, Up = 11, Down = 12, Left = 13, Right = 14, Touchpad = 15;

    public static bool Has(uint bits, int b) => ((bits >> b) & 1) != 0;
}

/// <summary>One full controller snapshot.</summary>
public struct PadState
{
    public uint Buttons;
    public float LX, LY, RX, RY;   // -1..1, up = +
    public float LT, RT;           // 0..1

    public static PadState Neutral => default;

    /// <summary>Overlay a macro step on top of live finger input.</summary>
    public readonly PadState Merge(in PadState over)
    {
        var r = this;
        r.Buttons |= over.Buttons;
        r.LT = Math.Max(r.LT, over.LT);
        r.RT = Math.Max(r.RT, over.RT);
        if (over.LX != 0 || over.LY != 0) { r.LX = over.LX; r.LY = over.LY; }
        if (over.RX != 0 || over.RY != 0) { r.RX = over.RX; r.RY = over.RY; }
        return r;
    }
}

/// <summary>Phone motion sample (DS4/DSU conventions: g and deg/s).</summary>
public struct MotionSample
{
    public bool Valid;
    public float AccelX, AccelY, AccelZ;
    public float GyroPitch, GyroYaw, GyroRoll;
    public ulong TimestampUs;
}
