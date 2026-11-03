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
        return (svc, prefs, saves, GameSettingsService.ToOptions(GameFiles.ReadGraphics(Path.Combine(root, "g.cfg")).Value!, GameFiles.ReadOptions(Path.Combine(root, "o.bin"), "fake_").Value));
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
        File.Delete(g);
        Assert.Equal("GameSettings.Missing", (await svc.SaveAsync(o)).Error);
    }
}
