using TailPad.Core;
using Xunit;

namespace TailPad.Tests;

public class ScannerTests
{
    static string Root() => Directory.CreateTempSubdirectory("tpscan").FullName;

    static void F(string path, int kb = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[kb * 1024]);
    }

    [Fact]
    public void Finds_repacks_unity_unreal_and_known_games_but_not_apps()
    {
        var d = Root();
        // FitGirl GTA V: launcher exe preferred, name from known exes
        var gta = Path.Combine(d, "Games", "Grand.Theft.Auto.V-FitGirl");
        F(Path.Combine(gta, "PlayGTAV.exe"), 20); F(Path.Combine(gta, "GTA5.exe"), 900);
        F(Path.Combine(gta, "bink2w64.dll")); F(Path.Combine(gta, "x64a.rpf")); F(Path.Combine(gta, "unins000.exe"), 2000);
        F(Path.Combine(gta, "update", "update.exe"), 800);
        // Unity game
        var hk = Path.Combine(d, "Games", "Hollow Knight [FitGirl Repack]");
        F(Path.Combine(hk, "hollow_knight.exe"), 650); F(Path.Combine(hk, "UnityPlayer.dll"));
        Directory.CreateDirectory(Path.Combine(hk, "hollow_knight_Data", "Managed"));
        F(Path.Combine(hk, "UnityCrashHandler64.exe"), 1500);
        // Unreal game: tiny bootstrap exe + Engine + <Project>/Binaries
        var ue = Path.Combine(d, "Games", "Stray");
        F(Path.Combine(ue, "Stray.exe"), 150);
        Directory.CreateDirectory(Path.Combine(ue, "Engine", "Binaries"));
        F(Path.Combine(ue, "Hk_project", "Binaries", "Win64", "Stray-Win64-Shipping.exe"), 5000);
        // eFootball standalone: known exe, no markers needed
        F(Path.Combine(d, "Games", "eFootball", "eFootball.exe"), 400);
        // normal app with a big exe but nothing game-like → ignored
        F(Path.Combine(d, "Tools", "PhotoTool", "PhotoTool.exe"), 3000); F(Path.Combine(d, "Tools", "PhotoTool", "libpng.dll"));
        // something in a skipped system folder → ignored
        F(Path.Combine(d, "Windows", "SysGame", "x.exe"), 900); F(Path.Combine(d, "Windows", "SysGame", "UnityPlayer.dll"));
        // game on the desktop is found, game buried in AppData is not
        var desk = Path.Combine(d, "Users", "hamza", "Desktop", "Celeste");
        F(Path.Combine(desk, "Celeste.exe"), 700); F(Path.Combine(desk, "fmod.dll"));
        F(Path.Combine(d, "Users", "hamza", "AppData", "Local", "Junk", "j.exe"), 700); F(Path.Combine(d, "Users", "hamza", "AppData", "Local", "Junk", "steam_api64.dll"));

        var games = GameScanner.Scan(new[] { d }, new GameScanner.Options
        {
            InstalledNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hk] = "Hollow Knight v1.5.78 (FitGirl Repack)" },
        });

        Assert.Equal(new[] { "Celeste", "eFootball", "Grand Theft Auto V", "Hollow Knight", "Stray" }, games.Select(g => g.Name));
        Assert.EndsWith("PlayGTAV.exe", games.Single(g => g.Name == "Grand Theft Auto V").Exe);
        Assert.EndsWith("hollow_knight.exe", games.Single(g => g.Name == "Hollow Knight").Exe);
        Assert.EndsWith(Path.Combine("Stray", "Stray.exe"), games.Single(g => g.Name == "Stray").Exe);
    }

    [Fact]
    public void Excluded_folders_are_skipped()
    {
        var d = Root();
        var g = Path.Combine(d, "Lib", "SomeGame");
        F(Path.Combine(g, "SomeGame.exe"), 800); F(Path.Combine(g, "steam_api64.dll"));
        Assert.Single(GameScanner.Scan(new[] { d }));
        Assert.Empty(GameScanner.Scan(new[] { d }, new GameScanner.Options { ExcludeFolders = new[] { Path.Combine(d, "Lib") } }));
    }

    [Theory]
    [InlineData("Grand.Theft.Auto.V-FitGirl [v1.0.3095]", "Grand Theft Auto V")]
    [InlineData("Elden Ring (FitGirl Repack)", "Elden Ring")]
    [InlineData("Hades v1.38290", "Hades")]
    [InlineData("Red_Dead_Redemption_2-ElAmigos", "Red Dead Redemption 2")]
    [InlineData("eFootball", "eFootball")]
    public void Cleans_release_names(string raw, string clean) => Assert.Equal(clean, GameScanner.CleanName(raw));

    [Fact]
    public void Library_merges_scanned_games_hides_and_prefers_launcher_copies()
    {
        var steam = Root();
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1665460.acf"), "\"appid\" \"1665460\"\n\"name\" \"eFootball\"");
        var scanned = new[]
        {
            new ScannedGame { Name = "eFootball", Path = @"D:\Other\eFootball.exe" },
            new ScannedGame { Name = "Grand Theft Auto V", Path = @"D:\Games\GTA\PlayGTAV.exe" },
            new ScannedGame { Name = "Wrong Thing", Path = @"D:\x\wrong.exe" },
        };
        var hidden = new[] { "scan:" + GameLibrary.StableId(@"D:\x\wrong.exe") };
        var games = GameLibrary.Scan(steam, null, Array.Empty<CustomGame>(), scanned, hidden);
        Assert.Equal(new[] { "eFootball", "Grand Theft Auto V" }, games.Select(g => g.Name));
        Assert.Equal("Steam", games[0].Source);
        Assert.Equal("PC", games[1].Source);
    }
}

public class ShortcutTests
{
    const string Gta = @"D:\Games\Grand Theft Auto V";

    [Fact]
    public void Prefers_the_games_own_shortcut_over_its_exe_and_skips_uninstall()
    {
        var links = new[]
        {
            new Shortcuts.Link(@"C:\Users\h\Desktop\Uninstall GTA V.lnk", Gta + @"\unins000.exe", ""),
            new Shortcuts.Link(@"C:\Users\h\Desktop\Grand Theft Auto V.lnk", Gta + @"\PlayGTAV.exe", ""),
            new Shortcuts.Link(@"C:\Users\h\Desktop\Chrome.lnk", @"C:\Program Files\Google\Chrome\chrome.exe", ""),
        };
        Assert.Equal(@"C:\Users\h\Desktop\Grand Theft Auto V.lnk", Shortcuts.Choose(Gta, "Grand Theft Auto V", links)!.Value.Path);
    }

    [Fact]
    public void Accepts_a_launcher_shortcut_only_when_the_name_matches()
    {
        var viaLauncher = new Shortcuts.Link(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Rockstar Games\Grand Theft Auto V.lnk",
            @"C:\Program Files\Rockstar Games\Launcher\LauncherPatcher.exe", "-launchTitleInFolder");
        var other = new Shortcuts.Link(@"C:\Users\h\Desktop\Discord.lnk", @"C:\Users\h\AppData\Local\Discord\Update.exe", "");
        Assert.Equal(viaLauncher, Shortcuts.Choose(Gta, "Grand Theft Auto V", new[] { viaLauncher, other }));
        Assert.Null(Shortcuts.Choose(Gta, "Grand Theft Auto V", new[] { other }));
    }

    [Fact]
    public void Scanned_games_start_through_their_shortcut_but_remember_the_game_exe()
    {
        var scanned = new[] { new ScannedGame { Name = "Grand Theft Auto V", Path = Gta + @"\GTA5.exe", Launch = @"C:\Users\h\Desktop\Grand Theft Auto V.lnk" } };
        var g = GameLibrary.Scan(null, null, Array.Empty<CustomGame>(), scanned).Single();
        Assert.Equal(@"C:\Users\h\Desktop\Grand Theft Auto V.lnk", g.StartPath);
        Assert.Equal(Gta + @"\GTA5.exe", g.GameExe);

        // "Change start file…" wins, and the game exe is still known
        var ov = new Dictionary<string, string> { [g.Id] = @"D:\Games\Grand Theft Auto V\PlayGTAV.exe" };
        var g2 = GameLibrary.Scan(null, null, Array.Empty<CustomGame>(), scanned, null, ov).Single();
        Assert.Equal(@"D:\Games\Grand Theft Auto V\PlayGTAV.exe", g2.StartPath);
        Assert.Equal(Gta + @"\GTA5.exe", g2.GameExe);
    }
}
