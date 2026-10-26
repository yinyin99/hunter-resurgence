using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.Files;

namespace Resurgence.Tests.LauncherApp;

/// <summary>Finding the game and reading settings.json, on a temp Steam folder tree.</summary>
public class GameFinderTests
{
    [Fact]
    public void FindsTheGameThroughSteamTheChosenFolderAndLaunchOptions()
    {
        using var temp = new TempDir("resurgence-test-");
        var root = temp.Path;
        var steam = Path.Combine(root, "Steam");
        var lib2 = Path.Combine(root, "Other Library");
        var s = new Settings("4711", @"client-dir\client.exe");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        // libraryfolders.vdf as Steam writes it: backslashes doubled, the default library listed too
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace(@"\", @"\\")}}" "apps" { "228980" "1" } }
                "1" { "path" "{{lib2.Replace(@"\", @"\\")}}" "apps" { "4711" "123456" } }
            }
            """);
        Assert.True(GameFinder.Libraries(steam).SequenceEqual([steam, lib2]), "libraryfolders.vdf: both library paths, backslashes unescaped");
        Write(Path.Combine(lib2, "steamapps", "appmanifest_4711.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"4711\"\n\t\"installdir\"\t\t\"Fake Game\"\n}\n");
        Assert.True(GameFinder.VdfValue("\"AppState\" { \"installdir\" \"A \\\\ B\" }", "installdir") == @"A \ B", "vdf value unescaped");

        var folder = Path.Combine(lib2, "steamapps", "common", "Fake Game");
        Assert.True(GameFinder.Find(s, null, [], steam) is null, "no client exe in the install folder: not found");
        Touch(Path.Combine(folder, s.ClientExe!));
        Assert.True(GameFinder.Find(s, null, [], steam)?.Folder == folder, "appmanifest installdir in the second library gives the install folder");
        Assert.True(GameFinder.Find(s with { SteamAppId = "999" }, null, [], steam) is null, "other app id: not found");
        Assert.True(GameFinder.Find(new Settings(null, null), null, [], steam) is null, "placeholder settings: not configured, no crash");
        Assert.True(GameFinder.Find(s, null, [], null) is null, "no Steam: not found");

        var manual = Path.Combine(root, "Manual");
        Assert.True(GameFinder.Find(s, manual, [], steam)?.Folder == folder, "chosen folder without the client is ignored");
        Touch(Path.Combine(manual, s.ClientExe!));
        Assert.True(GameFinder.Find(s, manual, [], steam)?.Folder == manual, "valid chosen folder wins over Steam");

        var portable = Path.Combine(root, "Portable");
        Touch(Path.Combine(portable, s.ClientExe!));
        Touch(Path.Combine(portable, "sub", "starter.exe"));
        Assert.True(GameFinder.Find(s, null, [Path.Combine(portable, "sub", "starter.exe"), "-flag"], null)?.Folder == portable, "launch-options exe in a subfolder gives its parent install folder");
        Assert.True(Settings.Load(Path.Combine(root, "missing.json")) == new Settings(null, null), "missing settings.json: not configured");
        var bad = Path.Combine(root, "bad.json");
        File.WriteAllText(bad, """{"steam_app_id":"<steam-app-id>","client_exe":"..\\evil.exe"}""");
        Assert.True(Settings.Load(bad) == new Settings(null, null), "placeholder and parent-relative values are not configured");
    }
}
