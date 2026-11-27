using Resurgence.Services;
using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.Files;

namespace Resurgence.Tests.LauncherApp;

/// <summary>PL6: the dialog's save on temp files (root is its %APPDATA%).</summary>
public class GameSettingsServiceTests
{
    const string Gfx = "129\n0\n1920 1080 60\n2\n0\n0\n0\n0\n0\n";
    const string Opt = "<value name=\"fake_ContextualHelp\" type=\"int\">0</value>\n";

    static Settings Config(string? client = null) => new("1", client ?? "no-such-client.exe", "g.cfg", "o.bin", "fake_");

    static (GameSettingsService Svc, Prefs Prefs, int[] Saves, GameOptions Options) Make(string root, Settings s)
    {
        var prefs = new Prefs();
        var saves = new int[1];
        var svc = new GameSettingsService(s, prefs, root, () => saves[0]++);
        return (svc, prefs, saves, GameSettingsService.ToOptions(GameFiles.ReadGraphics(Path.Combine(root, "g.cfg")).Value!, GameFiles.ReadOptions(Path.Combine(root, "o.bin"), "fake_").Value, DisplayMode.Windowed));
    }

    [Fact]
    public async Task NothingIsWrittenWhileTheClientRuns()
    {
        using var temp = new TempDir("resurgence-test-");
        Write(Path.Combine(temp.Path, "g.cfg"), Gfx);
        Write(Path.Combine(temp.Path, "o.bin"), Opt);
        var (svc, prefs, saves, o) = Make(temp.Path, Config(Path.GetFileName(Environment.ProcessPath)));
        var r = await svc.SaveAsync(o with { Scene = 3 });
        Assert.Equal("GameSettings.ClientRunning", r.Error);
        Assert.Equal(Gfx, Bytes(Path.Combine(temp.Path, "g.cfg")));
        Assert.Equal((0, false), (saves[0], prefs.GameSettingsSaved));
    }

    [Theory]
    [InlineData("nosteam", false, true)]
    [InlineData(null, true, true)]
    public async Task TheFileOfTheComingModeIsWrittenBothWhenItIsUnknown(string? mode, bool steamFile, bool noSteamFile)
    {
        using var temp = new TempDir("resurgence-test-");
        var s = new Settings("1", "no-such-client.exe", "gSettings_steam_live.cfg", "o.bin", "fake_");
        Assert.Equal("gSettings_live.cfg", s.GraphicsFileNoSteam);
        Assert.Null(Config().GraphicsFileNoSteam); // a name without "_steam": one file for both modes
        var (steam, noSteam) = (Path.Combine(temp.Path, "gSettings_steam_live.cfg"), Path.Combine(temp.Path, "gSettings_live.cfg"));
        Write(steam, Gfx);
        Write(noSteam, Gfx.Replace("1920 1080 60", "1280 720 30"));
        var svc = new GameSettingsService(s, new Prefs(), temp.Path, () => { }, () => mode);
        var read = await svc.ReadAsync();
        Assert.Equal(mode == "nosteam" ? 1280 : 1920, read.Options.Resolution.Width); // the file of the mode; the steam file when unknown
        Assert.True((await svc.SaveAsync(read.Options with { Scene = 3 })).Ok);
        Assert.Equal(steamFile, GameFiles.ReadGraphics(steam).Value!.Scene == 3);
        Assert.Equal(noSteamFile, GameFiles.ReadGraphics(noSteam).Value!.Scene == 3);
        Assert.Equal(mode == "nosteam" ? 30 : 60, GameFiles.ReadGraphics(mode == "nosteam" ? noSteam : steam).Value!.Hz); // what the dialog does not show stays
    }

    [Fact]
    public async Task TheStartWithSteamBoxSurvivesAReloadAndUncheckedWritesOnlyTheNoSteamFile()
    {
        using var temp = new TempDir("resurgence-test-");
        var s = new Settings("1", "no-such-client.exe", "gSettings_steam_live.cfg", "o.bin", "fake_");
        var (steam, noSteam, json) = (Path.Combine(temp.Path, "gSettings_steam_live.cfg"), Path.Combine(temp.Path, "gSettings_live.cfg"), Path.Combine(temp.Path, "launcher.json"));
        Write(steam, Gfx);
        Write(noSteam, Gfx);
        var prefs = new Prefs();
        Assert.True(prefs.StartWithSteam); // checked by default
        var svc = new GameSettingsService(s, prefs, temp.Path, () => prefs.Save(json));
        var read = await svc.ReadAsync();
        Assert.True(read.Options.StartWithSteam);
        Assert.True((await svc.SaveAsync(read.Options with { Scene = 3, StartWithSteam = false })).Ok);
        Assert.False(Prefs.Load(json).StartWithSteam);
        Assert.Equal((false, true), (GameFiles.ReadGraphics(steam).Value!.Scene == 3, GameFiles.ReadGraphics(noSteam).Value!.Scene == 3));
        Assert.False((await new GameSettingsService(s, Prefs.Load(json), temp.Path, () => { }).ReadAsync()).Options.StartWithSteam);
    }

    [Fact]
    public async Task AMissingFileOfTheOtherModeIsCreatedWhenBothAreWritten()
    {
        using var temp = new TempDir("resurgence-test-");
        var s = new Settings("1", "no-such-client.exe", "gSettings_steam_live.cfg", "o.bin", "fake_");
        Write(Path.Combine(temp.Path, "gSettings_steam_live.cfg"), Gfx);
        var svc = new GameSettingsService(s, new Prefs(), temp.Path, () => { });
        Assert.True((await svc.SaveAsync((await svc.ReadAsync()).Options with { Scene = 3 })).Ok);
        Assert.Equal(3, GameFiles.ReadGraphics(Path.Combine(temp.Path, "gSettings_live.cfg")).Value!.Scene);
        Write(Path.Combine(temp.Path, "gSettings_live.cfg"), "not a settings file");
        Assert.Equal("GameSettings.BadFormat", (await svc.SaveAsync((await svc.ReadAsync()).Options)).Error);
    }

    [Fact]
    public async Task ContextualHelpIsWrittenToTheOptionsFileAndReadBack()
    {
        using var temp = new TempDir("resurgence-test-");
        Write(Path.Combine(temp.Path, "g.cfg"), Gfx);
        Write(Path.Combine(temp.Path, "o.bin"), Opt);
        var (svc, _, _, o) = Make(temp.Path, Config());
        Assert.True((await svc.SaveAsync(o with { ContextualHelp = true })).Ok);
        Assert.Equal(Opt.Replace(">0<", ">1<"), Bytes(Path.Combine(temp.Path, "o.bin")));
        Assert.True((await svc.ReadAsync()).Options.ContextualHelp);
    }

    [Fact]
    public async Task AReadOnlyGraphicsFileIsSaveFailed()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = Path.Combine(temp.Path, "g.cfg");
        Write(g, Gfx);
        Write(Path.Combine(temp.Path, "o.bin"), Opt);
        var (svc, prefs, saves, o) = Make(temp.Path, Config());
        File.SetAttributes(g, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal("GameSettings.SaveFailed", (await svc.SaveAsync(o with { Scene = 3 })).Error);
            Assert.Equal(Gfx, Bytes(g));
            Assert.Equal((0, false), (saves[0], prefs.GameSettingsSaved));
        }
        finally
        {
            File.SetAttributes(g, FileAttributes.Normal);
            if (File.Exists(g + GameFiles.BackupSuffix)) File.SetAttributes(g + GameFiles.BackupSuffix, FileAttributes.Normal); // the copy inherits the attribute
        }
    }

    // fixed: this said "Missing" for a file that is there but not in the known format
    [Fact]
    public async Task AMalformedGraphicsFileIsBadFormatAndNotMissing()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = Path.Combine(temp.Path, "g.cfg");
        Write(g, Gfx);
        Write(Path.Combine(temp.Path, "o.bin"), Opt);
        var (svc, _, _, o) = Make(temp.Path, Config());
        Write(g, Gfx.Replace("129", "130"));
        Assert.Equal("GameSettings.BadFormat", (await svc.SaveAsync(o)).Error);
    }

    // #652: the game never writes the graphics file, so a missing one opens on defaults and Save creates it
    [Fact]
    public async Task AMissingGraphicsFileOpensOnDefaultsAndSaveCreatesIt()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = Path.Combine(temp.Path, "g.cfg");
        var svc = new GameSettingsService(Config(), new Prefs(), temp.Path, () => { });
        var read = await svc.ReadAsync();
        Assert.Null(read.Error);
        Assert.False(File.Exists(g));
        Assert.True((await svc.SaveAsync(read.Options with { Display = DisplayMode.Fullscreen, Resolution = new(1600, 900, 144), Scene = 3 })).Ok);
        Assert.Equal("129\r\n1\r\n1600 900 144\r\n3\r\n1\r\n1\r\n0\r\n0\r\n1\r\n0\r\n0\r\n0\r\n", Bytes(g));
        Assert.True((await svc.ReadAsync()).Options is { Display: DisplayMode.Fullscreen, Scene: 3, Resolution: { Width: 1600, Height: 900, Hz: 144 } });
    }

    static readonly Func<(int W, int H, int Hz)?> Monitor = () => (2560, 1440, 144);

    // #810: Borderless runs windowed at the monitor's size, only Fullscreen writes the fullscreen flag; the mode is kept in launcher.json
    [Theory]
    [InlineData(DisplayMode.Borderless, "0\n2560 1440 144\n")]
    [InlineData(DisplayMode.Fullscreen, "1\n1600 900 60\n")]
    [InlineData(DisplayMode.Windowed, "0\n1600 900 60\n")]
    public async Task EachDisplayModeWritesItsFileAndIsKeptInThePrefs(DisplayMode mode, string lines)
    {
        using var temp = new TempDir("resurgence-test-");
        var g = Path.Combine(temp.Path, "g.cfg");
        Write(g, Gfx);
        var prefs = new Prefs();
        var svc = new GameSettingsService(Config(), prefs, temp.Path, () => { }, monitor: Monitor);
        Assert.True((await svc.SaveAsync((await svc.ReadAsync()).Options with { Display = mode, Resolution = new(1600, 900, 60) })).Ok);
        Assert.Equal("129\n" + lines + "2\n0\n0\n0\n0\n0\n", Bytes(g));
        var json = Path.Combine(temp.Path, "launcher.json");
        prefs.Save(json);
        Assert.Contains($"\"display_mode\": \"{mode.ToString().ToLowerInvariant()}\"", File.ReadAllText(json));
        Assert.Equal(mode, Prefs.Load(json).DisplayMode);
        Assert.Equal(mode, (await new GameSettingsService(Config(), Prefs.Load(json), temp.Path, () => { }, monitor: Monitor).ReadAsync()).Options.Display);
        // a value this launcher does not know is no choice, and the rest of the file stays
        File.WriteAllText(json, """{ "display_mode": "stretched", "steam_option_set": "x" }""");
        Assert.Equal(((DisplayMode?)null, (string?)"x"), (Prefs.Load(json).DisplayMode, Prefs.Load(json).SteamOptionSet));
    }

    // #810: before the player's choice a file that says fullscreen (or none) shows Borderless at the monitor's size, a windowed one Windowed
    [Theory]
    [InlineData("1", DisplayMode.Borderless)]
    [InlineData("0", DisplayMode.Windowed)]
    [InlineData(null, DisplayMode.Borderless)]
    public async Task BeforeAChoiceAFullscreenFileShowsBorderless(string? fullscreen, DisplayMode shown)
    {
        using var temp = new TempDir("resurgence-test-");
        if (fullscreen is not null) Write(Path.Combine(temp.Path, "g.cfg"), Gfx.Replace("129\n0\n", $"129\n{fullscreen}\n"));
        var read = await new GameSettingsService(Config(), new Prefs(), temp.Path, () => { }, monitor: Monitor).ReadAsync();
        Assert.Equal(shown, read.Options.Display);
        Assert.Equal(shown == DisplayMode.Borderless ? new Resolution(2560, 1440, 144) : new Resolution(1920, 1080, 60), read.Options.Resolution);
    }

    // #810: a Borderless start writes the start's file windowed at the monitor's size (creates a missing one); the mode before a choice becomes the choice; the other modes leave the file alone
    [Fact]
    public void ABorderlessStartWritesTheFileWindowedAtTheMonitorsSize()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = Path.Combine(temp.Path, "g.cfg");
        var exclusive = Gfx.Replace("129\n0\n", "129\n1\n");
        Write(g, exclusive);
        var prefs = new Prefs();
        var saves = 0;
        var svc = new GameSettingsService(Config(), prefs, temp.Path, () => saves++, monitor: Monitor);
        Assert.Equal((true, (string?)null), svc.PrepareStart(true));
        Assert.Equal("129\n0\n2560 1440 144\n2\n0\n0\n0\n0\n0\n", Bytes(g));
        Assert.Equal((DisplayMode.Borderless, 1), (prefs.DisplayMode, saves));
        Assert.Equal((true, (string?)null), svc.PrepareStart(false));
        Assert.Equal(1, saves); // the choice is saved once

        foreach (var other in new[] { DisplayMode.Fullscreen, DisplayMode.Windowed })
        {
            prefs.DisplayMode = other;
            Write(g, exclusive);
            Assert.Equal((false, (string?)null), svc.PrepareStart(true));
            Assert.Equal(exclusive, Bytes(g));
        }

        File.Delete(g);
        Assert.Equal((true, (string?)null), new GameSettingsService(Config(), new Prefs(), temp.Path, () => { }, monitor: Monitor).PrepareStart(true));
        Assert.Equal("129\r\n0\r\n2560 1440 144\r\n1\r\n1\r\n1\r\n0\r\n0\r\n1\r\n0\r\n0\r\n0\r\n", Bytes(g));

        // a file that cannot be written: its error, and the window is left as the game makes it
        Write(g, "not a settings file");
        var (borderless, error) = new GameSettingsService(Config(), new Prefs { DisplayMode = DisplayMode.Borderless }, temp.Path, () => { }, monitor: Monitor).PrepareStart(true);
        Assert.True(!borderless && error is not null);
    }

    // #825: the game's language is the player's choice; before one, the launcher's language when the game has it, else English
    [Theory]
    [InlineData("fr", "fr_FR")]
    [InlineData("pt-BR", "pt_BR")]
    [InlineData("pt-PT", "pt_PT")]
    [InlineData("nb", "nb_NO")]
    public async Task TheGamesLanguageDefaultsToTheLaunchersAndTheChoiceIsSaved(string launcher, string expected)
    {
        var prefs = new Prefs { Language = launcher };
        var saves = 0;
        var svc = new GameSettingsService(Config(), prefs, Path.GetTempPath(), () => saves++);
        Assert.Equal(expected, await svc.LanguageAsync());
        await svc.SetLanguageAsync("ru_RU");
        Assert.Equal(("ru_RU", "ru_RU", 1), (await svc.LanguageAsync(), prefs.GameLanguage, saves));  // the choice wins over the launcher's language
        await svc.SetLanguageAsync("xx_XX");                                                            // not the game's: no choice again
        Assert.Null(prefs.GameLanguage);
        Assert.Equal(2, saves);
    }

    [Fact]
    public void TheGamesLanguageIsInLauncherJson()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.json");
        new Prefs { GameLanguage = "de_DE" }.Save(path);
        Assert.Contains("\"game_language\": \"de_DE\"", File.ReadAllText(path));
        Assert.Equal("de_DE", Prefs.Load(path).GameLanguage);
        Assert.Null(new Prefs().GameLanguage);
    }

    // #930: the host's last choice of a game language (a launcher language code) is remembered; none before the first choice, and a code the launcher does not have is no choice
    [Fact]
    public async Task TheHostLanguageIsNoneUntilChosenThenSavedAndKeptInLauncherJson()
    {
        using var temp = new TempDir("resurgence-test-");
        var path = Path.Combine(temp.Path, "launcher.json");
        var prefs = new Prefs { Language = "fr" };
        var saves = 0;
        var svc = new GameSettingsService(Config(), prefs, temp.Path, () => { saves++; prefs.Save(path); });
        Assert.Null(await svc.HostLanguageAsync());  // the form then opens with the launcher's language
        await svc.SetHostLanguageAsync("pt-br");  // stored as the launcher spells it
        Assert.Equal(("pt-BR", 1), (await svc.HostLanguageAsync(), saves));
        Assert.Contains("\"host_language\": \"pt-BR\"", File.ReadAllText(path));
        Assert.Equal("pt-BR", await new GameSettingsService(Config(), Prefs.Load(path), temp.Path, () => { }).HostLanguageAsync());  // read back at the next start
        await svc.SetHostLanguageAsync("xx");
        Assert.Null(await svc.HostLanguageAsync());
    }
}
