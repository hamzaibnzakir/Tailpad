using System.Security.Cryptography;
using System.Text.Json;

namespace TailPad.Core;

public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime LastSeen { get; set; }
}

public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
}

public sealed class Config
{
    public int ControlPort { get; set; } = 47800;
    public int InputPort { get; set; } = 47801;
    public int DsuPort { get; set; } = 26760;
    public bool DsuEnabled { get; set; } = true;
    public bool AllowLan { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool GyroMouse { get; set; } = true;
    public string PairingCode { get; set; } = NewCode();
    public List<PairedDevice> Devices { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailPad");
    static string FilePath => Path.Combine(Dir, "config.json");

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static Config Load(string? path = null)
    {
        try
        {
            var p = path ?? FilePath;
            if (File.Exists(p)) return JsonSerializer.Deserialize<Config>(File.ReadAllText(p)) ?? new Config();
        }
        catch { /* corrupted config → start fresh */ }
        return new Config();
    }

    public void Save(string? path = null)
    {
        lock (Gate)
        {
            var p = path ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, p, true);
        }
    }
}
