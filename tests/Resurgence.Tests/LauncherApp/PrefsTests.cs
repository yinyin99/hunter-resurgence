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
}
