using System.Text.RegularExpressions;

namespace TailPad.Core;

/// <summary>
/// Picks the shortcut you'd double-click to start a game. Starting through it matters for games that must go
/// through their launcher (GTA V refuses to start from GTA5.exe directly: "ERR_NO_LAUNCHER").
/// </summary>
public static partial class Shortcuts
{
    public readonly record struct Link(string Path, string Target, string Args);

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    static string Simple(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");

    static readonly string[] Junk = { "uninstall", "unins", "setup", "readme", "manual", "website", "support", "redist", "config", "settings", "editor", "server", "benchmark" };

    public static Link? Choose(string gameFolder, string gameName, IEnumerable<Link> links)
    {
        var folder = gameFolder.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var name = Simple(gameName);
        Link? best = null; int bestScore = 0;
        foreach (var l in links)
        {
            var file = Simple(System.IO.Path.GetFileNameWithoutExtension(l.Path));
            if (Junk.Any(j => file.Contains(j)) || Junk.Any(j => Simple(System.IO.Path.GetFileNameWithoutExtension(l.Target)).Contains(j))) continue;
            int score = 0;
            bool inFolder = l.Target.Replace('/', '\\').StartsWith(folder.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
            if (inFolder) score += 2;
            if (name.Length >= 3 && (file == name || file.Contains(name) || name.Contains(file) && file.Length >= 4)) score += 2;
            // a shortcut that goes through a launcher (target outside the game folder) only counts with a matching name
            if (!inFolder && score < 2) continue;
            if (score > bestScore) { best = l; bestScore = score; }
        }
        return best;
    }
}
