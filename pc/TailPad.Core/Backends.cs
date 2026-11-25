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

/// <param name="Target">what to start: path \0 arguments [\0 the game's own exe, when starting through a shortcut/launcher]</param>
public sealed record GameEntry(string Id, string Name, string Source, string? Art, string Target)
{
    public string StartPath => Target.Split('\0')[0];
    public string StartArgs => Target.Split('\0') is { Length: > 1 } p ? p[1] : "";
    /// <summary>The program whose window is the game (for background play).</summary>
    public string? GameExe
    {
        get
        {
            var p = Target.Split('\0');
            var exe = p.Length > 2 && p[2].Length > 0 ? p[2] : p[0];
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
        }
    }
}
