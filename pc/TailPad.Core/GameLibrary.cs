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

    public static List<GameEntry> Scan(string? steamRoot, string? epicManifests, IEnumerable<CustomGame> custom,
        IEnumerable<ScannedGame>? scanned = null, ICollection<string>? hidden = null, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var games = new List<GameEntry>();
        try { if (steamRoot != null) games.AddRange(Steam(steamRoot)); } catch { }
        try { if (epicManifests != null) games.AddRange(Epic(epicManifests)); } catch { }
        foreach (var c in custom)
        {
            if (string.IsNullOrWhiteSpace(c.Path)) continue;
            games.Add(new GameEntry("custom:" + StableId(c.Path), c.Name, "PC", null, c.Path + "\u0000" + c.Args));
        }
        // games found on the drives; skip any already added by hand (same exe)
        var manual = new HashSet<string>(custom.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var g in scanned ?? Enumerable.Empty<ScannedGame>())
        {
            if (string.IsNullOrWhiteSpace(g.Path) || manual.Contains(g.Path)) continue;
            // start it the way you do (its shortcut) when we know it, but remember the real exe for its window
            var start = string.IsNullOrWhiteSpace(g.Launch) ? g.Path : g.Launch;
            games.Add(new GameEntry("scan:" + StableId(g.Path), g.Name, "PC", null, start + "\u0000\u0000" + g.Path));
        }
        // "Change start file…" in the PC app wins over everything
        if (overrides is { Count: > 0 })
            for (int i = 0; i < games.Count; i++)
                if (overrides.TryGetValue(games[i].Id, out var ov) && !string.IsNullOrWhiteSpace(ov))
                    games[i] = games[i] with { Target = ov + "\u0000\u0000" + (games[i].GameExe ?? "") };
        // same name from two places (e.g. Steam + a scan) → keep the launcher version
        return games
            .Where(g => hidden == null || !hidden.Contains(g.Id))
            .GroupBy(g => g.Id).Select(g => g.First())
            .GroupBy(g => g.Name.ToLowerInvariant()).Select(g => g.OrderBy(x => x.Source == "PC" ? 1 : 0).First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Install folders of Epic games, so the drive scan doesn't list them twice.</summary>
    public static IEnumerable<string> EpicInstallFolders(string manifestDir)
    {
        if (!Directory.Exists(manifestDir)) yield break;
        foreach (var item in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            string? loc = null;
            try
            {
                var root = JsonDocument.Parse(File.ReadAllText(item)).RootElement;
                if (root.TryGetProperty("InstallLocation", out var v) && v.ValueKind == JsonValueKind.String) loc = v.GetString();
            }
            catch { }
            if (!string.IsNullOrEmpty(loc)) yield return loc;
        }
    }

    /// <summary>All Steam library folders (…\steamapps\common lives under each).</summary>
    public static IEnumerable<string> SteamLibraries(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
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

    public static string StableId(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s.ToLowerInvariant()) { h ^= ch; h *= 16777619; }
        return h.ToString("x8");
    }
}
