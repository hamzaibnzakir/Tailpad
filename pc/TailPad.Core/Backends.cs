namespace TailPad.Core;

public enum PadType { X360, DS4 }

/// <summary>A virtual controller plugged into Windows (ViGEm on the real app, a fake in tests).</summary>
public interface IVirtualPad : IDisposable
{
    PadType Type { get; }
    void Submit(in PadState state, in MotionSample motion);
    void Reset();
    /// <summary>large, small motor 0-255</summary>
    event Action<byte, byte>? Rumble;
    /// <summary>DS4 lightbar colour</summary>
    event Action<byte, byte, byte>? Light;
    /// <summary>Xbox player LED (1-4), 0 if unknown</summary>
    event Action<int>? Player;
}

public interface IPadFactory
{
    IVirtualPad Create(PadType type);
}

/// <summary>OS integration the server needs but which lives in the Windows host.</summary>
public interface IHostServices
{
    void MoveMouse(float dx, float dy);
    void Launch(GameEntry game);
    IReadOnlyList<GameEntry> Games();
    void Notify(string message);

    /// <summary>Start remote play: send packets with <paramref name="send"/>, report state with <paramref name="status"/>.
    /// Dispose the result to stop.</summary>
    IDisposable StartStream(StreamRequest request, Action<byte[]> send, Action<System.Text.Json.Nodes.JsonObject> status);

    /// <summary>Games on a hidden screen only the phone sees.</summary>
    IBackgroundPlay Background { get; }
}

public sealed record StreamRequest(int Height = 720, int Fps = 60, int Kbps = 10_000, bool Audio = true);

public sealed record GameEntry(string Id, string Name, string Source, string? Art, string Target);
