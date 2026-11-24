using System.Text.RegularExpressions;

namespace TailPad.Core;

public sealed record FoundGame(string Name, string Exe, string Folder);

/// <summary>
/// Finds games anywhere on the PC — including ones not from a launcher (repacks, standalone installs).
/// A folder counts as a game when it holds an .exe plus a tell-tale game-engine/runtime file
/// (Unity, Unreal, Bink video, Steam/Epic/GOG runtimes, DLSS/FSR…). Once a folder is a game we don't go deeper.
/// </summary>
public static partial class GameScanner
{
    /// <summary>Files whose presence next to an .exe means "this is a game".</summary>
    static readonly string[] MarkerFiles =
    {
        "unityplayer.dll", "gameassembly.dll", "unitycrashhandler64.exe",
        "bink2w64.dll", "binkw64.dll", "bink2w32.dll", "binkw32.dll",
        "steam_api64.dll", "steam_api.dll", "eossdk-win64-shipping.dll", "galaxy64.dll", "galaxy.dll",
        "fmod.dll", "fmodstudio.dll", "fmod64.dll", "wwiseengine.dll",
        "nvngx_dlss.dll", "amd_fidelityfx_dx12.dll", "amd_ags_x64.dll", "libxess.dll", "sl.interposer.dll",
        "physx3_x64.dll", "physxdevice64.dll", "gfsdk_aftermath_lib.x64.dll",
        "xinput1_3.dll", "d3dx9_43.dll", "openal32.dll", "sdl2.dll",
        "steam_emu.ini", "steam_settings", "codex.ini", "cpy.ini", "onlinefix.ini", "rld.dll", "3dmgame.dll",
        "x64a.rpf", "common.rpf", "data.win", "nw.pak", "renpy",
    };

    static readonly string[] MarkerDirExact = { "engine", "binaries", "content", "steam_settings" };

    /// <summary>Main exes for games that need a specific name or file to launch.</summary>
    static readonly Dictionary<string, string> KnownExes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playgtav.exe"] = "Grand Theft Auto V",
        ["gta5.exe"] = "Grand Theft Auto V",
        ["gta5_enhanced.exe"] = "Grand Theft Auto V Enhanced",
        ["gtaiv.exe"] = "Grand Theft Auto IV",
        ["gta_sa.exe"] = "GTA San Andreas",
        ["efootball.exe"] = "eFootball",
        ["pes2021.exe"] = "eFootball PES 2021",
        ["fc25.exe"] = "EA SPORTS FC 25",
        ["fc24.exe"] = "EA SPORTS FC 24",
        ["rdr2.exe"] = "Red Dead Redemption 2",
        ["eldenring.exe"] = "ELDEN RING",
        ["cyberpunk2077.exe"] = "Cyberpunk 2077",
        ["witcher3.exe"] = "The Witcher 3",
        ["forzahorizon5.exe"] = "Forza Horizon 5",
        ["forzahorizon4.exe"] = "Forza Horizon 4",
        ["nfsheat.exe"] = "Need for Speed Heat",
        ["mk11.exe"] = "Mortal Kombat 11",
        ["tekken8.exe"] = "TEKKEN 8",
        ["streetfighter6.exe"] = "Street Fighter 6",
        ["godofwar.exe"] = "God of War",
        ["spider-man.exe"] = "Marvel's Spider-Man",
        ["re4.exe"] = "Resident Evil 4",
        ["hogwartslegacy.exe"] = "Hogwarts Legacy",
    };

    /// <summary>When several of these sit in one folder, prefer the first (e.g. PlayGTAV.exe over GTA5.exe).</summary>
    static readonly string[] PreferredExes = { "playgtav.exe" };

    static readonly string[] JunkExeWords =
    {
        "unins", "uninst", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "directx",
        "crash", "report", "bugsplat", "easyanticheat", "eac_launcher", "battleye", "beservice", "dotnet", "ndp4",
        "unitycrashhandler", "ue4prereq", "ueprereq", "prereq", "cefprocess", "cef", "helper", "updater", "update",
        "touchup", "quicksfv", "7z", "unrar", "winrar", "config", "settings", "editor", "server", "benchmark",
        "socialclubhelper", "subprocess", "webhelper", "overlay", "notification", "dxdiag",
    };

    /// <summary>Folders never worth walking into.</summary>
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "$recycle.bin", "system volume information", "programdata", "recovery", "perflogs",
        "msocache", "intel", "amd", "nvidia", "drivers", "windowsapps", "windows defender", "windows nt",
        "microsoft", "microsoft office", "microsoft visual studio", "microsoft sdks", "windows kits", "reference assemblies",
        "common files", "dotnet", "node_modules", ".git", ".vs", "packages", "nuget", "python", "anaconda3", "miniconda3",
        "jetbrains", "android", "sdk", "go", "msys64", "cygwin64", "texlive",
        "google", "mozilla firefox", "mozilla", "brave software", "opera", "chrome",
        "adobe", "autodesk", "blender foundation", "obs-studio", "vlc", "videolan", "7-zip", "winrar", "notepad++",
        "tailscale", "zoom", "slack", "discord", "spotify", "whatsapp", "telegram desktop",
        "appdata", "temp", "tmp", "cache", "logs", "_commonredist", "commonredist", "redist", "directx", "support",
        "steamapps", "epic games", "steam", "origin", "ea desktop", "gog galaxy", "ubisoft game launcher", // launchers handled elsewhere
        "uninstall", "installer", "__installer", "_installer", "prerequisites", "dlc",
    };

    // folders inside a user profile worth checking; everything else in a profile is skipped
    static readonly HashSet<string> ProfileKeep = new(StringComparer.OrdinalIgnoreCase) { "desktop", "downloads", "documents", "games" };

    public sealed class Options
    {
        public int MaxDepth { get; init; } = 6;
        public TimeSpan Budget { get; init; } = TimeSpan.FromMinutes(3);
        /// <summary>install folder → proper name, from installer records (uninstall registry)</summary>
        public IReadOnlyDictionary<string, string> InstalledNames { get; init; } = new Dictionary<string, string>();
        /// <summary>folders already covered by Steam/Epic, skipped to avoid duplicates</summary>
        public IReadOnlyCollection<string> ExcludeFolders { get; init; } = Array.Empty<string>();
    }

    public static List<FoundGame> Scan(IEnumerable<string> roots, Options? opt = null, CancellationToken ct = default)
    {
        opt ??= new Options();
        var found = new List<FoundGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exclude = opt.ExcludeFolders.Select(Norm).ToList();
        var deadline = DateTime.UtcNow + opt.Budget;

        void Walk(DirectoryInfo dir, int depth, bool inProfiles)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline || depth > opt.MaxDepth) return;
            var full = Norm(dir.FullName);
            if (exclude.Any(e => full.StartsWith(e, StringComparison.OrdinalIgnoreCase))) return;

            var g = Inspect(dir, opt.InstalledNames);
            if (g != null)
            {
                if (seen.Add(g.Exe)) found.Add(g);
                return; // don't look inside a game folder for more games
            }

            IEnumerable<DirectoryInfo> subs;
            try
            {
                subs = dir.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint }).ToList();
            }
            catch { return; }

            foreach (var s in subs)
            {
                var n = s.Name;
                if (n.StartsWith('.') || n.StartsWith('$')) continue;
                if (SkipDirs.Contains(n)) continue;
                bool profiles = n.Equals("users", StringComparison.OrdinalIgnoreCase) && depth == 0;
                if (inProfiles && depth == 2 && !ProfileKeep.Contains(n)) continue; // C:\Users\<you>\<only these>
                if (inProfiles && depth == 1 && (n.Equals("public", StringComparison.OrdinalIgnoreCase) || n.Equals("default", StringComparison.OrdinalIgnoreCase) || n.Equals("all users", StringComparison.OrdinalIgnoreCase))) continue;
                Walk(s, depth + 1, inProfiles || profiles);
            }
        }

        foreach (var r in roots)
        {
            try { if (Directory.Exists(r)) Walk(new DirectoryInfo(r), 0, false); } catch { }
        }

        // installer records point straight at install folders — check those too (catches games installed very deep)
        foreach (var (folder, _) in opt.InstalledNames)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var d = new DirectoryInfo(folder);
                if (!d.Exists || exclude.Any(e => Norm(d.FullName).StartsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                var g = Inspect(d, opt.InstalledNames);
                if (g != null && seen.Add(g.Exe)) found.Add(g);
            }
            catch { }
        }

        return found.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Norm(string p) => p.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    /// <summary>Is this folder a game? Returns the game with its main exe, or null.</summary>
    public static FoundGame? Inspect(DirectoryInfo dir, IReadOnlyDictionary<string, string>? installedNames = null)
    {
        FileInfo[] files;
        DirectoryInfo[] dirs;
        try
        {
            var eo = new EnumerationOptions { IgnoreInaccessible = true };
            files = dir.GetFiles("*", eo);
            dirs = dir.GetDirectories("*", eo);
        }
        catch { return null; }

        var exes = files.Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !IsJunk(f.Name)).ToList();
        if (exes.Count == 0 || exes.Count > 25) return null; // tool folders tend to have lots of exes

        var names = new HashSet<string>(files.Select(f => f.Name.ToLowerInvariant()));
        var dirNames = new HashSet<string>(dirs.Select(d => d.Name.ToLowerInvariant()));

        var known = exes.FirstOrDefault(e => KnownExes.ContainsKey(e.Name));
        bool unityData = dirs.Any(d => d.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                                       && (Directory.Exists(Path.Combine(d.FullName, "Managed")) || File.Exists(Path.Combine(d.FullName, "globalgamemanagers")) || File.Exists(Path.Combine(d.FullName, "data.unity3d"))));
        bool unreal = dirNames.Contains("engine") && dirs.Any(d => !d.Name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                                                             && Directory.Exists(Path.Combine(d.FullName, "Binaries")));
        bool marker = MarkerFiles.Any(m => names.Contains(m) || dirNames.Contains(m));
        bool paks = files.Count(f => f.Extension is ".pak" or ".rpf" or ".vpk" or ".bdt" or ".arc" or ".cpk" or ".forge" or ".bundle") >= 2;

        if (known == null && !unityData && !unreal && !marker && !paks) return null;

        // pick the main exe
        FileInfo main = exes.FirstOrDefault(e => PreferredExes.Contains(e.Name.ToLowerInvariant()))
                        ?? known
                        ?? exes.FirstOrDefault(e => unityData && dirs.Any(d => d.Name.Equals(Path.GetFileNameWithoutExtension(e.Name) + "_Data", StringComparison.OrdinalIgnoreCase)))
                        ?? exes.FirstOrDefault(e => Simplify(Path.GetFileNameWithoutExtension(e.Name)) == Simplify(dir.Name))
                        ?? exes.Where(e => !e.Name.Contains("launcher", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Length).FirstOrDefault()
                        ?? exes.OrderByDescending(e => e.Length).First();

        // tiny exe with no engine files and nothing else is more likely a tool than a game
        if (known == null && !unityData && !unreal && !paks && main.Length < 300 * 1024 && !names.Contains("steam_api64.dll") && !names.Contains("steam_api.dll"))
            return null;

        string name;
        if (installedNames != null && installedNames.TryGetValue(dir.FullName.TrimEnd('\\', '/'), out var installed) && !string.IsNullOrWhiteSpace(installed))
            name = CleanName(installed);
        else if (KnownExes.TryGetValue(main.Name, out var kn) || (known != null && KnownExes.TryGetValue(known.Name, out kn)))
            name = kn;
        else
            name = CleanName(dir.Name);

        return new FoundGame(name, main.FullName, dir.FullName);
    }

    static bool IsJunk(string exe)
    {
        var n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        if (KnownExes.ContainsKey(exe)) return false;
        return JunkExeWords.Any(w => n.Contains(w));
    }

    static string Simplify(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s*[\[\(][^\]\)]*[\]\)]\s*")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[-_ ]+(fitgirl|dodi|elamigos|codex|plaza|skidrow|empress|rune|tenoke|flt|gog|repack|razor1911|cpy|rld|p2p)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTags();

    [GeneratedRegex(@"[-_ ]+v?\d+(\.\d+){1,}.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Version();

    /// <summary>"Grand.Theft.Auto.V-FitGirl [v1.0.3095]" → "Grand Theft Auto V"</summary>
    public static string CleanName(string raw)
    {
        var s = Brackets().Replace(raw, " ");
        s = ReleaseTags().Replace(s, "");
        s = Version().Replace(s, "");
        if (!s.Contains(' ')) s = s.Replace('.', ' ').Replace('_', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', '.');
        return s.Length == 0 ? raw : s;
    }
}
