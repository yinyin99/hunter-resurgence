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
        new Prefs { Language = "fr", GameFolder = @"C:\x y", CloseOnGameStart = true, Window = new(-1900, 20, 1296, 799, true) }.Save(path);
        var p = Prefs.Load(path);
        Assert.True(p is { Language: "fr", GameFolder: @"C:\x y", CloseOnGameStart: true, Window: (-1900, 20, 1296, 799, true) }, "launcher.json round trip");
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
        Assert.Equal(["language", "game_folder", "close_on_game_start", "game_settings_saved", "display_mode", "steam_option_set", "steam_option_before", "steam_option_file", "steam_option_asked", "steam_option_never", "report_consent", "window"], keys);
        Assert.False(File.Exists(path + ".part"));
        File.WriteAllText(path, "null");
        Assert.Equal("", Prefs.Load(path).Language);
    }

    // a window saved on a second monitor that is gone, or dragged almost off screen, must not open where nobody can reach it
    [Fact]
    public void ASavedPlaceCountsOnlyWhenItsTitleBarIsOnScreen()
    {
        var screen = new System.Windows.Rect(0, 0, 1920, 1080);
        Assert.True(new WindowPlace(100, 100, 1296, 799, false).OnScreen(screen), "on the screen");
        Assert.True(new WindowPlace(-1150, 0, 1296, 799, false).OnScreen(screen), "146 units of the title bar still on the screen");
        Assert.False(new WindowPlace(-1220, 0, 1296, 799, false).OnScreen(screen), "76 units left: too little to grab");
        Assert.False(new WindowPlace(2000, 100, 1296, 799, false).OnScreen(screen), "on a second monitor that is gone");
        Assert.False(new WindowPlace(100, 1050, 1296, 799, false).OnScreen(screen), "title bar below the screen's bottom");
        Assert.False(new WindowPlace(100, 100, 1296, -1, false).OnScreen(screen), "a damaged height: the default, never a window that cannot open");
        Assert.False(new WindowPlace(double.NaN, 100, 1296, 799, false).OnScreen(screen), "a damaged position: the default");
    }
}
