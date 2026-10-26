using Resurgence.Launcher;
using static Resurgence.Tests.LauncherApp.Files;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The game's graphics and options files and the settings dialog's service, on temp files.</summary>
public class GameFilesTests
{
    [Fact]
    public async Task GraphicsAndOptionsFilesAreEditedInPlaceAndTheDialogServiceUsesThem()
    {
        using var temp = new TempDir("resurgence-test-");
        var root = temp.Path;
        // settings.json with placeholder values
        File.WriteAllText(Path.Combine(root, "bad.json"), """{"steam_app_id":"<steam-app-id>","client_exe":"..\\evil.exe"}""");
        var s = Settings.Load(Path.Combine(root, "gs.json").Also(p => File.WriteAllText(p, """{"graphics_file":"g.cfg","options_file":"sub\\o.bin","options_key_prefix":"fake_"}""")));
        Assert.True(s is { GraphicsFile: "g.cfg", OptionsFile: @"sub\o.bin", OptionsKeyPrefix: "fake_" }, "settings.json: graphics file, options file and key prefix");
        Assert.True(Settings.Load(Path.Combine(root, "bad.json")) is { GraphicsFile: null, OptionsFile: null, OptionsKeyPrefix: null }, "placeholders: those parts are not configured");
        Assert.True(!GameFiles.ClientRunning(null) && !GameFiles.ClientRunning("no-such-client.exe"), "client not running: false");
        Assert.True(GameFiles.ClientRunning(Path.GetFileName(Environment.ProcessPath)), "a running process of that name: true");

        // graphics file: odd spacing on the mode line, unknown lines 10+, CRLF
        var g = Path.Combine(root, "gfx", "g.cfg");
        const string gfx = "129\r\n0\r\n1920  1080   60\r\n2\r\n0\r\n0\r\n0\r\n0\r\n0\r\n5 6 7\r\nnote  line\r\n\r\n";
        Write(g, gfx);
        var (gs, problem) = GameFiles.ReadGraphics(g);
        Assert.True(problem == FileProblem.None && gs is { Fullscreen: false, Width: 1920, Height: 1080, Hz: 60, Scene: 2, Texture: 0, Msaa: 0, Aniso: 0, Vsync: false }, "graphics file read");
        gs!.Scene = 3; gs.Width = 1280; gs.Vsync = true;
        GameFiles.WriteGraphics(g, gs);
        Assert.True(Bytes(g) == gfx.Replace("1920  1080   60", "1280  1080   60").Replace("\r\n2\r\n", "\r\n3\r\n").Replace("0\r\n5 6 7", "1\r\n5 6 7"), "graphics write changes only the edited values and keeps spacing, line ends and lines 10+");
        Assert.True(Bytes(g + GameFiles.BackupSuffix) == gfx, "first write made a copy of the original");
        gs.Scene = 0;
        GameFiles.WriteGraphics(g, gs);
        Assert.True(Bytes(g + GameFiles.BackupSuffix) == gfx && !File.Exists(g + ".resurgence-tmp"), "later writes never overwrite the copy and leave no temp file");
        var stamp = File.GetLastWriteTimeUtc(g);
        Thread.Sleep(30);
        GameFiles.WriteGraphics(g, GameFiles.ReadGraphics(g).Value!);
        Assert.True(File.GetLastWriteTimeUtc(g) == stamp, "writing unchanged values touches nothing");

        var bad = Path.Combine(root, "gfx", "bad.cfg");
        const string badText = "130\r\n0\r\n1920 1080 60\r\n2\r\n0\r\n0\r\n0\r\n0\r\n0\r\n";
        Write(bad, badText);
        Assert.True(GameFiles.ReadGraphics(bad).Problem == FileProblem.BadFormat, "wrong format marker: not read");
        var refused = false;
        try { GameFiles.WriteGraphics(bad, gs); } catch (InvalidDataException) { refused = true; }
        Assert.True(refused && Bytes(bad) == badText && !File.Exists(bad + GameFiles.BackupSuffix), "wrong format marker: write refused, file and folder untouched");
        Assert.True(GameFiles.ReadGraphics(Path.Combine(root, "gfx", "absent.cfg")).Problem == FileProblem.Missing, "missing graphics file");

        // options file: a key map object and a same-named key under another prefix must stay as they are
        var o = Path.Combine(root, "opt", "o.bin");
        const string opt = "<value name=\"fake_Version\" type=\"int\">9</value>\n<value name=\"fake_MasterVolume\" type=\"int\">100</value>\n<value name=\"other_Gamma\" type=\"int\">50</value>\n" +
            "<value name=\"fake_Gamma\" type=\"int\">50</value>\n<value name=\"fake_ShowChat\" type=\"int\">1</value>\n<object name=\"Bindings\">\n  <value name=\"Jump\" type=\"int\">32</value>\n</object>\n";
        Write(o, opt);
        var (os, oproblem) = GameFiles.ReadOptions(o, "fake_");
        Assert.True(oproblem == FileProblem.None && os is { } && os.Get("MasterVolume") == 100 && os.Get("Gamma") == 50 && os.Get("ShowChat") == 1 && !os.Has("LookSpeed") && !os.Has("Version"), "options read: only known keys that exist");
        os!.Set("MasterVolume", 99); os.Set("ShowChat", 0); os.Set("LookSpeed", 7);
        GameFiles.WriteOptions(o, "fake_", os);
        Assert.True(Bytes(o) == opt.Replace("fake_MasterVolume\" type=\"int\">100", "fake_MasterVolume\" type=\"int\">99").Replace("fake_ShowChat\" type=\"int\">1", "fake_ShowChat\" type=\"int\">0"), "options write changes only the targeted value lines, adds none, keeps the key map");
        Assert.True(Bytes(o + GameFiles.BackupSuffix) == opt, "options: first write made a copy of the original");
        os.Set("Gamma", 10);
        GameFiles.WriteOptions(o, "fake_", os);
        Assert.True(Bytes(o + GameFiles.BackupSuffix) == opt, "options: the copy is not overwritten");
        await GameSettingsDialog(root, s with { GraphicsFile = @"gfx\g.cfg", OptionsFile = null });
        Assert.True(GameFiles.ReadOptions(o, "zz_").Problem == FileProblem.BadFormat && GameFiles.ReadOptions(Path.Combine(root, "opt", "absent.bin"), "fake_").Problem == FileProblem.Missing, "options: wrong prefix is a bad format, absent file is missing");
    }

    // the dialog's service on the files written above (root is its %APPDATA%)
    static async Task GameSettingsDialog(string root, Settings s)
    {
        var prefs = new Prefs();
        var saves = 0;
        var svc = new GameSettingsService(s, prefs, root, () => saves++);
        var read = await svc.ReadAsync();
        Assert.True(read.Error is null && read.Options is { Scene: 0, Aniso: 0, ContextualHelp: null } && read.Resolutions.Contains(read.Options.Resolution), "dialog: reads the graphics file, adds the current mode to the list");
        Assert.True(await svc.FirstStartAsync(), "dialog: first start until the settings were saved");
        var g = new GraphicsSettings { Aniso = 0, Width = 1, Height = 1, Hz = 1 };
        Assert.True(GameSettingsService.ToOptions(g, null).Aniso == 0 && GameSettingsService.ToGraphics(g, GameSettingsService.ToOptions(g, null)).Aniso == 0, "dialog: Default anisotropic filtering stays Default when untouched");
        Assert.True(GameSettingsService.ToGraphics(g, GameSettingsService.ToOptions(g, null) with { Aniso = 2 }).Aniso == 3 && GameSettingsService.ToOptions(g with { Aniso = 5 }, null).Aniso == 4, "dialog: list position and file value differ by one");
        var saved = await svc.SaveAsync(read.Options with { Scene = 3, Fullscreen = true });
        var firstStart = await svc.FirstStartAsync();
        Assert.True(saved.Ok && saves == 1 && prefs.GameSettingsSaved && !firstStart, "dialog: save writes the file and remembers it");
        Assert.True((await svc.ReadAsync()).Options is { Scene: 3, Fullscreen: true }, "dialog: reads back what was saved");
        Assert.True((await new GameSettingsService(new Settings(null, null), new Prefs(), root, () => { }).ReadAsync()).Error == "GameSettings.NotConfigured", "dialog: not configured is reported");
    }
}
