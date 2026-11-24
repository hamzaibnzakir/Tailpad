using System.Text.Json;
using System.Text.RegularExpressions;

namespace TailPad.Core;

/// <summary>Finds installed Steam and Epic games plus games the user added by hand.</summary>
public static partial class GameLibrary
{
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"(appid|name)\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfField();

    static readonly string[] SteamJunk =
        { "Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "SteamVR", "Steam Controller Configs" };

    public static List<GameEntry> Scan(string? steamRoot, string? epicManifests, IEnumerable<CustomGame> custom)
    {
        var games = new List<GameEntry>();
        try { if (steamRoot != null) games.AddRange(Steam(steamRoot)); } catch { }
        try { if (epicManifests != null) games.AddRange(Epic(epicManifests)); } catch { }
        foreach (var c in custom)
        {
            if (string.IsNullOrWhiteSpace(c.Path)) continue;
            games.Add(new GameEntry("custom:" + StableId(c.Path), c.Name, "PC", null, c.Path + "\u0000" + c.Args));
        }
        return games
            .GroupBy(g => g.Id).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<GameEntry> Steam(string steamRoot)
    {
        var libs = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string? id = null, name = null;
                foreach (Match m in AcfField().Matches(File.ReadAllText(acf)))
                {
                    if (m.Groups[1].Value.Equals("appid", StringComparison.OrdinalIgnoreCase)) id ??= m.Groups[2].Value;
                    else name ??= m.Groups[2].Value;
                }
                if (id == null || name == null || id == "228980") continue;
                if (SteamJunk.Any(j => name.StartsWith(j, StringComparison.OrdinalIgnoreCase))) continue;
                yield return new GameEntry("steam:" + id, name, "Steam",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg",
                    $"steam://rungameid/{id}");
            }
        }
    }

    public static IEnumerable<GameEntry> Epic(string manifestDir)
    {
        if (!Directory.Exists(manifestDir)) yield break;
        foreach (var item in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(item)).RootElement; } catch { continue; }
            string? S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = S("DisplayName"); var app = S("AppName"); var ns = S("CatalogNamespace"); var cat = S("CatalogItemId");
            if (name == null || app == null || ns == null || cat == null) continue;
            if (root.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue;
            yield return new GameEntry("epic:" + app, name, "Epic", null,
                $"com.epicgames.launcher://apps/{ns}%3A{cat}%3A{app}?action=launch&silent=true");
        }
    }

    static string StableId(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s.ToLowerInvariant()) { h ^= ch; h *= 16777619; }
        return h.ToString("x8");
    }
}
