using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using TailPad.Core;

namespace TailPad.Host;

/// <summary>Creates real Windows controllers through the ViGEmBus driver.</summary>
public sealed class ViGEmPadFactory : IPadFactory, IDisposable
{
    ViGEmClient? _client;
    readonly object _gate = new();

    public static string? DriverError { get; private set; }

    /// <summary>True when the ViGEmBus driver is installed and reachable.</summary>
    public bool TryInit()
    {
        lock (_gate)
        {
            if (_client != null) return true;
            try { _client = new ViGEmClient(); DriverError = null; return true; }
            catch (Exception e) { DriverError = e.GetType().Name.Contains("NotFound") ? "ViGEmBus driver not installed" : e.Message; return false; }
        }
    }

    public IVirtualPad Create(PadType type)
    {
        if (!TryInit()) throw new InvalidOperationException(DriverError ?? "Controller driver unavailable");
        lock (_gate)
            return type == PadType.DS4 ? new Ds4Pad(_client!.CreateDualShock4Controller()) : new X360Pad(_client!.CreateXbox360Controller());
    }

    public void Dispose() { lock (_gate) { _client?.Dispose(); _client = null; } }
}

sealed class X360Pad : IVirtualPad
{
    readonly IXbox360Controller _c;
    public PadType Type => PadType.X360;
    public event Action<byte, byte>? Rumble;
    public event Action<byte, byte, byte>? Light;
    public event Action<int>? Player;

    static readonly (int bit, Xbox360Button btn)[] Map =
    {
        (Btn.A, Xbox360Button.A), (Btn.B, Xbox360Button.B), (Btn.X, Xbox360Button.X), (Btn.Y, Xbox360Button.Y),
        (Btn.LB, Xbox360Button.LeftShoulder), (Btn.RB, Xbox360Button.RightShoulder),
        (Btn.Back, Xbox360Button.Back), (Btn.Start, Xbox360Button.Start), (Btn.Guide, Xbox360Button.Guide),
        (Btn.LS, Xbox360Button.LeftThumb), (Btn.RS, Xbox360Button.RightThumb),
        (Btn.Up, Xbox360Button.Up), (Btn.Down, Xbox360Button.Down), (Btn.Left, Xbox360Button.Left), (Btn.Right, Xbox360Button.Right),
    };

    public X360Pad(IXbox360Controller c)
    {
        _c = c;
        _c.AutoSubmitReport = false;
        _c.FeedbackReceived += (_, e) =>
        {
            Rumble?.Invoke(e.LargeMotor, e.SmallMotor);
            Player?.Invoke(e.LedNumber + 1);
        };
        _c.Connect();
        try { Player?.Invoke(_c.UserIndex + 1); } catch { }
        _ = Light; // Xbox pads have no lightbar
    }

    static short Axis(float v) => (short)Math.Clamp(Math.Round(v * 32767f), -32767, 32767);
    static byte Trig(float v) => (byte)Math.Clamp(Math.Round(v * 255f), 0, 255);

    public void Submit(in PadState s, in MotionSample _)
    {
        foreach (var (bit, btn) in Map) _c.SetButtonState(btn, Btn.Has(s.Buttons, bit));
        _c.SetAxisValue(Xbox360Axis.LeftThumbX, Axis(s.LX));
        _c.SetAxisValue(Xbox360Axis.LeftThumbY, Axis(s.LY));
        _c.SetAxisValue(Xbox360Axis.RightThumbX, Axis(s.RX));
        _c.SetAxisValue(Xbox360Axis.RightThumbY, Axis(s.RY));
        _c.SetSliderValue(Xbox360Slider.LeftTrigger, Trig(s.LT));
        _c.SetSliderValue(Xbox360Slider.RightTrigger, Trig(s.RT));
        _c.SubmitReport();
    }

    public void Reset() { _c.ResetReport(); _c.SubmitReport(); }

    public void Dispose() { try { _c.Disconnect(); } catch { } }
}

/// <summary>
/// DualShock 4. Uses the extended raw report so the phone's gyro/accelerometer reach games
/// that read DS4 motion directly (and Steam Input's gyro aiming).
/// </summary>
sealed class Ds4Pad : IVirtualPad
{
    readonly IDualShock4Controller _c;
    readonly byte[] _report = new byte[63];
    const float GyroScale = 16.0f;     // DS4 LSB per deg/s
    const float AccelScale = 8192.0f;  // DS4 LSB per g
    byte _counter;
    bool _rawOk = true;
    public PadType Type => PadType.DS4;
    public event Action<byte, byte>? Rumble;
    public event Action<byte, byte, byte>? Light;
    public event Action<int>? Player;

    public Ds4Pad(IDualShock4Controller c)
    {
        _c = c;
        _c.AutoSubmitReport = false;
#pragma warning disable CS0618 // still delivers rumble + lightbar on current ViGEmBus builds
        _c.FeedbackReceived += (_, e) =>
        {
            Rumble?.Invoke(e.LargeMotor, e.SmallMotor);
            Light?.Invoke(e.LightbarColor.Red, e.LightbarColor.Green, e.LightbarColor.Blue);
        };
#pragma warning restore CS0618
        _c.Connect();
        _ = Player;
    }

    static byte Stick(float v) => (byte)Math.Clamp(Math.Round(128 - v * 127), 0, 255); // DS4: up = 0 on Y
    static byte StickX(float v) => (byte)Math.Clamp(Math.Round(128 + v * 127), 0, 255);
    static byte Trig(float v) => (byte)Math.Clamp(Math.Round(v * 255f), 0, 255);

    static ushort Hat(uint b)
    {
        bool u = Btn.Has(b, Btn.Up), d = Btn.Has(b, Btn.Down), l = Btn.Has(b, Btn.Left), r = Btn.Has(b, Btn.Right);
        if (u && r) return 1; if (d && r) return 3; if (d && l) return 5; if (u && l) return 7;
        if (u) return 0; if (r) return 2; if (d) return 4; if (l) return 6;
        return 8;
    }

    static ushort Buttons(in PadState s)
    {
        uint b = s.Buttons;
        ushort w = Hat(b);
        if (Btn.Has(b, Btn.X)) w |= 1 << 4;      // Square
        if (Btn.Has(b, Btn.A)) w |= 1 << 5;      // Cross
        if (Btn.Has(b, Btn.B)) w |= 1 << 6;      // Circle
        if (Btn.Has(b, Btn.Y)) w |= 1 << 7;      // Triangle
        if (Btn.Has(b, Btn.LB)) w |= 1 << 8;     // L1
        if (Btn.Has(b, Btn.RB)) w |= 1 << 9;     // R1
        if (s.LT > 0.1f) w |= 1 << 10;           // L2 digital
        if (s.RT > 0.1f) w |= 1 << 11;           // R2 digital
        if (Btn.Has(b, Btn.Back)) w |= 1 << 12;  // Share
        if (Btn.Has(b, Btn.Start)) w |= 1 << 13; // Options
        if (Btn.Has(b, Btn.LS)) w |= 1 << 14;    // L3
        if (Btn.Has(b, Btn.RS)) w |= 1 << 15;    // R3
        return w;
    }

    static void I16(byte[] a, int o, float v)
    {
        var s = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
        a[o] = (byte)s; a[o + 1] = (byte)(s >> 8);
    }

    public void Submit(in PadState s, in MotionSample m)
    {
        if (_rawOk)
        {
            try { SubmitRaw(s, m); return; }
            catch { _rawOk = false; } // older driver: fall back to the basic report (no motion)
        }
        var w = Buttons(s);
        _c.SetButtonsFull((ushort)(w & 0xFFF0));
        _c.SetDPadDirection(Hat(s.Buttons) switch
        {
            0 => DualShock4DPadDirection.North, 1 => DualShock4DPadDirection.Northeast, 2 => DualShock4DPadDirection.East,
            3 => DualShock4DPadDirection.Southeast, 4 => DualShock4DPadDirection.South, 5 => DualShock4DPadDirection.Southwest,
            6 => DualShock4DPadDirection.West, 7 => DualShock4DPadDirection.Northwest, _ => DualShock4DPadDirection.None,
        });
        _c.SetSpecialButtonsFull((byte)((Btn.Has(s.Buttons, Btn.Guide) ? 1 : 0) | (Btn.Has(s.Buttons, Btn.Touchpad) ? 2 : 0)));
        _c.SetAxisValue(DualShock4Axis.LeftThumbX, StickX(s.LX));
        _c.SetAxisValue(DualShock4Axis.LeftThumbY, Stick(s.LY));
        _c.SetAxisValue(DualShock4Axis.RightThumbX, StickX(s.RX));
        _c.SetAxisValue(DualShock4Axis.RightThumbY, Stick(s.RY));
        _c.SetSliderValue(DualShock4Slider.LeftTrigger, Trig(s.LT));
        _c.SetSliderValue(DualShock4Slider.RightTrigger, Trig(s.RT));
        _c.SubmitReport();
    }

    void SubmitRaw(in PadState s, in MotionSample m)
    {
        var r = _report;
        Array.Clear(r);
        r[0] = StickX(s.LX); r[1] = Stick(s.LY); r[2] = StickX(s.RX); r[3] = Stick(s.RY);
        var w = Buttons(s);
        r[4] = (byte)w; r[5] = (byte)(w >> 8);
        _counter = (byte)((_counter + 1) & 0x3F);
        r[6] = (byte)((Btn.Has(s.Buttons, Btn.Guide) ? 1 : 0) | (Btn.Has(s.Buttons, Btn.Touchpad) ? 2 : 0) | (_counter << 2));
        r[7] = Trig(s.LT); r[8] = Trig(s.RT);
        var ts = (ushort)((m.TimestampUs * 3 / 16) & 0xFFFF); // DS4 timestamp unit ≈ 5.33 µs
        r[9] = (byte)ts; r[10] = (byte)(ts >> 8);
        r[11] = 0xFF;                                  // battery
        if (m.Valid)
        {
            I16(r, 12, m.GyroPitch * GyroScale);
            I16(r, 14, m.GyroYaw * GyroScale);
            I16(r, 16, m.GyroRoll * GyroScale);
            I16(r, 18, m.AccelX * AccelScale);
            I16(r, 20, m.AccelY * AccelScale);
            I16(r, 22, m.AccelZ * AccelScale);
        }
        else
        {
            I16(r, 20, AccelScale); // resting: 1 g on Y
        }
        r[29] = 0x1B;  // cable connected, battery full
        r[34] = 0x80;  // touch point 1: not touching
        r[38] = 0x80;  // touch point 2: not touching
        _c.SubmitRawReport(r);
    }

    public void Reset() => Submit(default, default);

    public void Dispose() { try { _c.Disconnect(); } catch { } }
}
