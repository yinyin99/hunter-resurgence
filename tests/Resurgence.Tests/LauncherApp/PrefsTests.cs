using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class PrefsTests
{
    [Fact]
    public void PrefsRoundTripAndLanguagePick()
    {
        using var temp = new TempDir("resurgence-test-");
        var root = temp.Path;
        var path = Path.Combine(root, "prefs", "launcher.json");
        Assert.True(Prefs.Load(path).Language == "", "no launcher.json: defaults");
        new Prefs { Language = "fr", GameFolder = @"C:\x y", CloseOnGameStart = true }.Save(path);
        var p = Prefs.Load(path);
        Assert.True(p is { Language: "fr", GameFolder: @"C:\x y", CloseOnGameStart: true }, "launcher.json round trip");
        File.WriteAllText(path, "{ not json");
        Assert.True(Prefs.Load(path).Language == "", "damaged launcher.json: defaults");
        Assert.True(App.Pick("fr", "en") == "fr" && App.Pick("", "fr") == "fr" && App.Pick("xx", "fr") == "fr" && App.Pick("pt-BR", "en") == "pt-BR" && App.Pick("", "de-AT") == "de" && App.Pick("", "pt-PT") == "pt-PT" && App.Pick("", "pt") == "pt-BR" && App.Pick("", "nb-NO") == "nb" && App.Pick("", "no") == "nb" && App.Pick("", "ru") == "ru" && App.Pick("", "ja") == "en" && App.Pick(null, "es-MX") == "es", "language: saved choice, else Windows language (any region), else English");
    }

    // PL7: a renamed key silently resets every player's choices
    [Fact]
    public void TheFileHoldsTheDocumentedKeysAndNoHalfFile()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.json");
        new Prefs { Language = "fr", GameFolder = "x", CloseOnGameStart = true, GameSettingsSaved = true }.Save(path);
        var keys = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.EnumerateObject().Select(p => p.Name);
        Assert.Equal(["language", "game_folder", "close_on_game_start", "game_settings_saved"], keys);
        Assert.False(File.Exists(path + ".part"));
        File.WriteAllText(path, "null");
        Assert.Equal("", Prefs.Load(path).Language);
    }
}
