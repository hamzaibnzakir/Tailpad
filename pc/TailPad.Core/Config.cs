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

public sealed class ScannedGame
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
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
    /// <summary>games found by scanning the PC's drives</summary>
    public List<ScannedGame> ScannedGames { get; set; } = new();
    public DateTime LastScan { get; set; }
    /// <summary>game ids the user removed from the list</summary>
    public List<string> HiddenGames { get; set; } = new();
    /// <summary>games TailPad turned down on the PC during background play (restored on exit / after a crash)</summary>
    public List<string> DuckedApps { get; set; } = new();

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

    /// <summary>Where Save() writes; null = %AppData%\TailPad\config.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SavePath { get; set; }

    /// <summary>Saves settings. Never throws: a locked file (antivirus, sync tools) must not break a connection.</summary>
    public bool Save(string? path = null)
    {
        lock (Gate)
        {
            var p = path ?? SavePath ?? FilePath;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    var tmp = p + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                    File.Move(tmp, p, true);
                    return true;
                }
                catch (IOException) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { Thread.Sleep(50); }
            }
            return false;
        }
    }
}
