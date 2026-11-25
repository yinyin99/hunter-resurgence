using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Launcher;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.LauncherApp;

public class InstallChecksTests
{
    const long Gb = 1L << 30;

    static readonly string[] Ids = ["game", "version", "files", "path", "steam", "setup", "windows", "disk", "display", "running", "dlls"];

    /// <summary>A fictional install: the exe in a subfolder and the archive set under game\archives.</summary>
    static GameInstall Game(TempDir t, bool archives = true)
    {
        var root = Path.Combine(t.Path, "install");
        var exe = Path.Combine(root, "bin", "client.exe");
        Files.Touch(exe);
        if (archives)
            for (var i = 0; i < 12; i++)
                foreach (var ext in new[] { ".arc", ".tab" }) Files.Touch(Path.Combine(root, "game", "archives", $"pc{i}{ext}"));
        return new(root, exe);
    }

    static string Manifest(long build, int flags = 4) => $"\"AppState\"\n{{\n\t\"appid\"\t\t\"1\"\n\t\"StateFlags\"\t\t\"{flags}\"\n\t\"buildid\"\t\t\"{build}\"\n}}\n";

    static InstallChecks Make(GameInstall? g, string? manifest = null, long? server = null, bool steam = true, string? setup = null, bool running = false, Func<string, bool>? network = null,
        string[]? oneDrive = null, Func<string, long?>? free = null, string[]? events = null, Func<Task<long?>>? serverBuild = null, Func<bool>? steamSignedIn = null, Func<int, string?>? ports = null, bool? installed = null, IReadOnlyDictionary<string, (long, string)>? clean = null,
        ScreenProbes? screen = null, Func<bool>? startWithSteam = null) =>
        new(() => g, "1", serverBuild ?? (() => Task.FromResult(server)), steamSignedIn ?? (() => steam), () => setup, _ => running, _ => manifest, network ?? (_ => false), () => oneDrive ?? [],
            free ?? (_ => 10 * Gb), @"C:\fictional-temp", () => events ?? [], ports, installed is { } i ? () => i : null, clean, screen, startWithSteam);

    static readonly (int, int, int)[] Offered = [(1360, 768, 60), (1920, 1080, 60)];
    static readonly DisplayAdapter RealGpu = new("Fictional GPU", true, true, true);

    /// <summary>A fake screen: its mode list, the graphics files the game may start with, the current mode and the display adapters; the saved display mode is Fullscreen (exclusive).</summary>
    static ScreenProbes Screen((int, int, int)[] modes, string[] files, (int, int, int)? current = null, params DisplayAdapter[] adapters) => new(() => modes, () => current, () => adapters, () => files, () => true);

    static string Graphics(TempDir t, string name, bool fullscreen, int w, int h, int hz)
    {
        var path = Path.Combine(t.Path, name);
        GameFiles.CreateGraphics(path, new() { Fullscreen = fullscreen, Width = w, Height = h, Hz = hz, Scene = 2, Texture = 1, Shadow = 3, Msaa = 1, Aniso = 2, Vsync = true });
        return path;
    }

    static async Task<InstallCheck> Check(InstallChecks c, string id, bool opening = false) => (await c.RunAsync(opening)).Single(r => r.Id == id);

    [Fact]
    public async Task AHealthyInstallIsOkInEveryCheckInOrder()
    {
        using var t = new TempDir("checks-");
        var all = await Make(Game(t), Manifest(100), server: 100).RunAsync(true);
        Assert.Equal(Ids, all.Select(r => r.Id));
        Assert.All(all, r => Assert.Equal(("ok", "Check.Ok", null), (r.Level, r.Text, r.Fix)));
    }

    [Fact]
    public async Task WithoutAGameTheFirstCheckBlocksWithTheFolderChooserAndTheRestHasNothingToCheck()
    {
        using var t = new TempDir("checks-");
        foreach (var none in new GameInstall?[] { null, new(Path.Combine(t.Path, "x"), Path.Combine(t.Path, "x", "gone.exe")) })
        {
            var all = await Make(none).RunAsync(true);
            Assert.Equal(("game", "block", "Check.Game.NotFound", "choose-folder"), (all[0].Id, all[0].Level, all[0].Text, all[0].Fix));
            Assert.All(all.Skip(1).Where(r => r.Id is not ("steam" or "setup")), r => Assert.Equal("ok", r.Level));
        }
    }

    [Theory]
    [InlineData(100L, 4, 100L, "ok", "Check.Ok")]
    [InlineData(100L, 4, 101L, "block", "Check.Version.Old")]
    [InlineData(100L, 4, 99L, "warn", "Check.Version.Newer")]
    [InlineData(100L, 1026, 100L, "block", "Check.Version.Updating")]
    [InlineData(100L, 6, 101L, "block", "Check.Version.Updating")]
    [InlineData(100L, 4, null, "ok", "Check.Ok")] // the server publishes no build, or does not answer: not compared
    public async Task TheBuildIsComparedWithTheServersAndAnUpdateInProgressBlocks(long build, int flags, long? server, string level, string text)
    {
        using var t = new TempDir("checks-");
        var r = await Check(Make(Game(t), Manifest(build, flags), server), "version");
        Assert.Equal((level, text), (r.Level, r.Text));
        Assert.Equal(level == "block" ? "open-steam" : null, r.Fix);
        Assert.Equal(build.ToString(), r.Args!["build"]);
    }

    [Fact]
    public async Task WithoutAManifestThereIsNoVersionToCheckAndTheServerIsNotAsked()
    {
        using var t = new TempDir("checks-");
        var asked = 0;
        var r = await Check(Make(Game(t), null, serverBuild: () => { asked++; return Task.FromResult<long?>(5); }), "version");
        Assert.Equal("ok", r.Level);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task TheServersBuildIsAskedOnceForSeveralRuns()
    {
        using var t = new TempDir("checks-");
        var asked = 0;
        var c = Make(Game(t), Manifest(100), serverBuild: () => { asked++; return Task.FromResult<long?>(100); });
        await c.RunAsync(true);
        await c.RunAsync(false, true);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task TheDefaultManifestIsTheOneNextToTheSteamLibrarysCommonFolder()
    {
        using var t = new TempDir("checks-");
        var steamapps = Path.Combine(t.Path, "lib", "steamapps");
        Files.Write(Path.Combine(steamapps, "appmanifest_1.acf"), Manifest(100));
        var root = Path.Combine(steamapps, "common", "Game");
        var exe = Path.Combine(root, "client.exe");
        Files.Touch(exe);
        var c = new InstallChecks(() => new(root, exe), "1", () => Task.FromResult<long?>(200), () => true, () => null, _ => false, blocks: () => []);
        Assert.Equal("Check.Version.Old", (await Check(c, "version")).Text);
        Assert.Equal("ok", (await Check(new InstallChecks(() => new(root, exe), "2", () => Task.FromResult<long?>(200), () => true, () => null, _ => false, blocks: () => []), "version")).Level); // another app's manifest
    }

    [Fact]
    public async Task AServerBuildIsAPositiveWholeNumberElseNothing()
    {
        static HttpClient Reply(HttpStatusCode code, string body) => new(new Stub(code, body)) { BaseAddress = new("https://server.example.test") };
        Assert.Equal<long?>(123, await InstallChecks.FetchBuild(Reply(HttpStatusCode.OK, "{\"build\": 123}")));
        Assert.Null(await InstallChecks.FetchBuild(Reply(HttpStatusCode.OK, "{\"build\": null}")));
        Assert.Null(await InstallChecks.FetchBuild(Reply(HttpStatusCode.OK, "{\"build\": -4}")));
        Assert.Null(await InstallChecks.FetchBuild(Reply(HttpStatusCode.OK, "{\"build\": \"7\"}")));
        Assert.Null(await InstallChecks.FetchBuild(Reply(HttpStatusCode.OK, "[1]")));
        Assert.Null(await InstallChecks.FetchBuild(Reply(HttpStatusCode.OK, "not json")));
        Assert.Null(await InstallChecks.FetchBuild(Reply(HttpStatusCode.InternalServerError, "{\"build\": 123}")));
    }

    [Fact]
    public async Task TheBuildRequestMeasuresThePcClockByTheDateHeader()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ServerClock(() => now, () => TimeSpan.Zero);
        Assert.Null(clock.Offset);
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, "{\"build\": 5}", now - TimeSpan.FromHours(3))) { BaseAddress = new("https://server.example.test") };
        Assert.Equal<long?>(5, await InstallChecks.FetchBuild(http, clock));
        Assert.Equal(TimeSpan.FromHours(3), clock.Offset);
        Assert.Equal(TimeSpan.FromHours(3), clock.Ahead);
        clock.Observe(new HttpResponseMessage { Headers = { Date = now - TimeSpan.FromMinutes(5) } });
        Assert.Null(clock.Ahead);      // up to five minutes is in step
        Assert.Equal(TimeSpan.FromMinutes(5), clock.Offset);
    }

    sealed class Stub(HttpStatusCode code, string body, DateTimeOffset? date = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body), Headers = { Date = date } });
    }

    /// <summary>Two DLLs next to the exe, and the table of a clean install that lists them.</summary>
    static Dictionary<string, (long, string)> CleanSet(GameInstall g)
    {
        var table = new Dictionary<string, (long, string)>();
        foreach (var (name, text) in new[] { ("steam_api.dll", "steam"), ("sound.dll", "fmod") })
        {
            Files.Write(Path.Combine(Path.GetDirectoryName(g.ClientExe)!, name), text);
            table[name] = (text.Length, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }
        return table;
    }

    [Fact]
    public async Task ADllThatIsNotPartOfTheGameWarnsAndOpensTheFolderAChangedOneSendsToSteamAndACleanFolderIsOk()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var clean = CleanSet(g);
        var folder = Path.GetDirectoryName(g.ClientExe)!;
        Task<InstallCheck> Dlls(string? manifest = null) => Check(Make(g, manifest, clean: clean), "dlls");
        // the hashes are remembered by path, size and write time, and a clock tick is longer than a rewrite: every rewrite gets a write time of its own
        var stamp = 0;
        void Rewrite(string text)
        {
            var path = Path.Combine(folder, "steam_api.dll");
            Files.Write(path, text);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(2 * ++stamp)); // two seconds apart: also a file system with a coarse clock tells them apart
        }

        var ok = await Dlls();
        Assert.Equal(("ok", "Check.Ok", null), (ok.Level, ok.Text, ok.Fix));

        Files.Write(Path.Combine(folder, "d3d9.dll"), "wrapper");
        Files.Write(Path.Combine(folder, "dxgi.dll"), "wrapper");
        var extra = await Dlls();
        Assert.Equal(("warn", "Check.Dlls.Extra", "open-folder", "d3d9.dll, dxgi.dll"), (extra.Level, extra.Text, extra.Fix, extra.Args!["files"]));

        File.Delete(Path.Combine(folder, "d3d9.dll"));
        File.Delete(Path.Combine(folder, "dxgi.dll"));
        foreach (var replaced in new[] { "stoam", "a longer old steam_api" }) // the size of the clean file, and another size
        {
            Rewrite(replaced);
            var changed = await Dlls();
            Assert.Equal(("warn", "Check.Dlls.Changed", "verify-files", "steam_api.dll"), (changed.Level, changed.Text, changed.Fix, changed.Args!["files"]));
        }

        Files.Write(Path.Combine(folder, "d3d9.dll"), "wrapper"); // both: the one the player can fix first
        Assert.Equal("Check.Dlls.Extra", (await Dlls()).Text);
        File.Delete(Path.Combine(folder, "d3d9.dll"));

        Rewrite("steam");
        Files.Write(Path.Combine(folder, "mod.asi"), "x"); // only .dll files are judged by the check (the report lists .asi too)
        Assert.Equal("ok", (await Dlls()).Level);

        Rewrite("steam"); // not remembered yet: the check has to open it
        using var held = new FileStream(Path.Combine(folder, "steam_api.dll"), FileMode.Open, FileAccess.Read, FileShare.None); // cannot be read: tells nothing, never a warning (the report names it)
        Assert.Equal("ok", (await Dlls()).Level);
    }

    [Fact]
    public async Task TheDllListIsCutAtFiveNamesAndAGameOfAnotherBuildIsNotJudged()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var clean = CleanSet(g);
        for (var i = 1; i <= 6; i++) Files.Write(Path.Combine(Path.GetDirectoryName(g.ClientExe)!, $"x{i}.dll"), "x");
        Assert.Equal("x1.dll, x2.dll, x3.dll, x4.dll, x5.dll, …", (await Check(Make(g, clean: clean), "dlls")).Args!["files"]);

        Assert.Equal("ok", (await Check(Make(g, Manifest(CleanDlls.Build + 1), clean: clean), "dlls")).Level); // the table is of one build
        Assert.Equal("warn", (await Check(Make(g, Manifest(CleanDlls.Build), clean: clean), "dlls")).Level);
        Assert.Equal("ok", (await Make(g, clean: clean).RunAsync(false, blocksOnly: true)).Single(r => r.Id == "dlls").Level); // never probed for a start
    }

    [Fact]
    public async Task TheCleanTableIsOfTheBuildTheServerIsSetUpToServeAndIsWellFormed()
    {
        // the example settings of the server name the supported build (game_build, which GET /stats/game serves): changing one without the other fails here
        using var example = JsonDocument.Parse(File.ReadAllText(Repo.Path("server", "Server", "settings.example.json")));
        Assert.Equal(CleanDlls.Build, example.RootElement.GetProperty("game_build").GetInt64());

        Assert.Equal(27, CleanDlls.Files.Count);
        Assert.All(CleanDlls.Files, f =>
        {
            Assert.EndsWith(".dll", f.Key);
            Assert.True(f.Value.Size > 0);
            Assert.Matches("^([0-9a-f]{8} ){7}[0-9a-f]{8}$", f.Value.Sha256);
        });

        // a game of the table's build on a server that serves that build: up to date, and its folder is judged by the real table
        using var t = new TempDir("checks-");
        var g = Game(t);
        Files.Write(Path.Combine(Path.GetDirectoryName(g.ClientExe)!, "d3d9.dll"), "wrapper");
        var all = await Make(g, Manifest(CleanDlls.Build), server: CleanDlls.Build).RunAsync(false);
        Assert.Equal("ok", all.Single(r => r.Id == "version").Level);
        Assert.Equal("Check.Dlls.Extra", all.Single(r => r.Id == "dlls").Text);
    }

    [Fact]
    public async Task AMissingOrEmptyArchiveBlocksAndNamesTheFirst()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        Assert.Equal("ok", (await Check(Make(g), "files")).Level);

        File.Delete(Path.Combine(g.Folder, "game", "archives", "pc3.tab"));
        var gone = await Check(Make(g), "files");
        Assert.Equal(("block", "Check.Files.Missing", "verify-files", "pc3.tab"), (gone.Level, gone.Text, gone.Fix, gone.Args!["file"]));

        File.WriteAllBytes(Path.Combine(g.Folder, "game", "archives", "pc0.arc"), []);
        Assert.Equal("pc0.arc", (await Check(Make(g), "files")).Args!["file"]);

        using var none = new TempDir("checks-");
        Assert.Equal("pc0.arc", (await Check(Make(Game(none, archives: false)), "files")).Args!["file"]);
    }

    /// <summary>The path checks look at the folder's name only: a made-up folder beside a real exe.</summary>
    static async Task<InstallCheck> PathOf(TempDir t, string folder, Func<string, bool>? network = null, string[]? oneDrive = null) =>
        await Check(Make(new(folder, Game(t).ClientExe), network: network, oneDrive: oneDrive), "path");

    [Fact]
    public async Task AnOddPathWarnsWithItsCauseAndKeepsTheStartOpen()
    {
        using var t = new TempDir("checks-");
        Assert.Equal("ok", (await PathOf(t, @"C:\Games\Hunter")).Level);

        var umlaut = await PathOf(t, @"C:\Spiele\Jäger");
        Assert.Equal(("warn", "Check.Path.NonAscii", "choose-folder", @"C:\Spiele\Jäger"), (umlaut.Level, umlaut.Text, umlaut.Fix, umlaut.Args!["path"]));

        var longPath = @"C:\Games\" + new string('a', 200);
        Assert.Equal("Check.Path.Long", (await PathOf(t, longPath)).Text);
        Assert.Equal("ok", (await PathOf(t, @"C:\Games\" + new string('a', 190))).Level);

        Assert.Equal("Check.Path.OneDrive", (await PathOf(t, @"C:\Users\x\OneDrive\Games\Hunter", oneDrive: [@"C:\Users\x\OneDrive"])).Text);
        Assert.Equal("ok", (await PathOf(t, @"C:\Users\x\OneDriveBackup\Hunter", oneDrive: [@"C:\Users\x\OneDrive"])).Level); // not below the OneDrive folder

        Assert.Equal("Check.Path.Network", (await PathOf(t, @"Z:\Hunter", network: p => p.StartsWith('Z'))).Text);
        var unc = new InstallChecks(() => new(@"\\pc\share\Hunter", Game(t).ClientExe), "1", () => Task.FromResult<long?>(null), () => true, () => null, _ => false, blocks: () => []); // the real probe
        Assert.Equal("Check.Path.Network", (await Check(unc, "path")).Text);

        Assert.Equal("ok", (await Check(Make(Game(t), oneDrive: []), "path")).Level);
    }

    [Fact]
    public async Task WithTheStartWithSteamBoxUncheckedTheGameCheckSaysNothingOfSteam()
    {
        using var t = new TempDir("checks-");
        foreach (var installed in new bool?[] { true, false })
        {
            var r = await Check(Make(Game(t), steam: false, installed: installed, startWithSteam: () => false), "steam");
            Assert.Equal(("ok", null), (r.Level, r.Fix));
        }
        Assert.Equal("ok", (await Check(Make(null, steam: false, startWithSteam: () => false), "steam")).Level); // no Steam is needed even without a game
        Assert.Equal("warn", (await Check(Make(Game(t), steam: false, startWithSteam: () => true), "steam")).Level);
    }

    [Fact]
    public async Task SteamNotSignedInWarnsThatTheGameStartsWithoutItAndBlocksOnlyWithoutAGame()
    {
        using var t = new TempDir("checks-");
        var r = await Check(Make(Game(t), steam: false), "steam");
        Assert.Equal(("warn", "Check.Steam.Fallback", "open-steam"), (r.Level, r.Text, r.Fix));
        Assert.Equal("ok", (await Check(Make(Game(t), steam: true), "steam")).Level);
        var none = await Check(Make(Game(t), steam: false, installed: false), "steam"); // not installed: its own text, and no Open Steam button that would do nothing
        Assert.Equal(("warn", "Check.Steam.NotInstalled", null), (none.Level, none.Text, none.Fix));
        Assert.Equal("block", (await Check(Make(null, steam: false), "steam")).Level); // no game: nothing to start without Steam either
    }

    [Fact]
    public async Task APlayerKitProblemBlocksWithItsOwnMessageKey()
    {
        using var t = new TempDir("checks-");
        var r = await Check(Make(Game(t), setup: "Kit.NoHosts"), "setup");
        Assert.Equal(("block", "Kit.NoHosts", "setup"), (r.Level, r.Text, r.Fix));
        Assert.Equal("ok", (await Check(Make(Game(t), setup: null), "setup")).Level);
    }

    [Fact]
    public async Task AnotherProgramOnPort80Or443BlocksTheSetupAndIsNamed()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var r80 = await Check(Make(g, ports: p => p == 80 ? "webserver" : null), "setup");
        Assert.Equal(("block", "Check.Setup.Port", null, "80", "webserver"), (r80.Level, r80.Text, r80.Fix, r80.Args!["port"], r80.Args["process"]));
        Assert.Equal("443", (await Check(Make(g, ports: p => p == 443 ? "proxy" : null), "setup")).Args!["port"]);
        Assert.Equal("ok", (await Check(Make(g, ports: _ => null), "setup")).Level); // both free, or taken by the launcher's own forwarder
        Assert.Equal("ok", (await Check(Make(g), "setup")).Level); // no kit: the ports are not looked at
        Assert.Equal("Kit.NoHosts", (await Check(Make(g, setup: "Kit.NoHosts", ports: _ => "x"), "setup")).Text); // setup first
    }

    [Fact]
    public void TheRealPortProbeSkipsFreePortsAndThisProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var own = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        own.Start();
        try { Assert.Null(InstallChecks.PortUser(((System.Net.IPEndPoint)own.LocalEndpoint).Port)); }
        finally { own.Stop(); }
        Assert.Null(InstallChecks.PortUser(1)); // nobody listens on port 1
    }

    [Fact]
    public void TheKitsReasonIsAKeyAndTheShownTextIsItsTranslation()
    {
        using var t = new TempDir("checks-");
        var place = new Pki.Place(Path.Combine(t.Path, "pki"), "no-such-key-" + Guid.NewGuid().ToString("N"), "Root");
        Assert.Equal("Kit.NoLeaf", Pki.CheckKey(["a.example.test"], DateTimeOffset.UtcNow, place));
        Assert.Equal(Kit.S("Kit.NoLeaf"), Pki.Check(["a.example.test"], DateTimeOffset.UtcNow, place));
    }

    const string BlockedRow = @"Code Integrity determined that a process (\Device\HarddiskVolume3\Windows\explorer.exe) attempted to load \Device\HarddiskVolume3\Games\Hunter\bin\crash.dll that did not meet the Smart App Control requirements.";

    [Fact]
    public async Task AWindowsBlockNamingAFileInTheGameFolderWarnsWithoutBlocking()
    {
        using var t = new TempDir("checks-");
        GameInstall In(string folder) => new(folder, Game(t).ClientExe);
        var warn = await Check(Make(In(@"C:\Games\Hunter"), events: [BlockedRow]), "windows");
        Assert.Equal(("warn", "Check.Windows.Blocked", null, @"bin\crash.dll"), (warn.Level, warn.Text, warn.Fix, warn.Args!["file"]));

        var defender = await Check(Make(In(@"D:\Games\Hunter"), events: [@"Windows Defender detected malware. Path: file:_D:\Games\Hunter\GAME\Patch.EXE; webfile:_x"]), "windows");
        Assert.Equal("Patch.EXE", Path.GetFileName(defender.Args!["file"]));

        Assert.Equal("ok", (await Check(Make(In(@"C:\Games\Other"), events: [BlockedRow]), "windows")).Level); // another folder
        Assert.Equal("ok", (await Check(Make(In(@"C:\Games\Hunter"), events: []), "windows")).Level);
        Assert.Equal("ok", (await Check(Make(In(@"C:\Games\Hunter"), events: [@"\Device\HarddiskVolume3\Games\Hunter\readme.txt"]), "windows")).Level); // not a program file
    }

    [Fact]
    public void TheWindowsEventQueriesAreValidForWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = InstallChecks.ReadBlocks().ToList(); // must not throw "The specified query is invalid"; a log this user cannot read adds nothing
    }

    [Fact]
    public async Task LowFreeSpaceOnTheGameDriveOrTheTempFolderWarns()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var drive = Path.GetPathRoot(g.Folder)!;

        var ok = await Check(Make(g, free: _ => 2 * Gb), "disk");
        Assert.Equal(("ok", "2 GB"), (ok.Level, ok.Args!["game"]));

        var low = await Check(Make(g, free: p => p == g.Folder ? 500L << 20 : 10 * Gb), "disk");
        Assert.Equal(("warn", "Check.Disk.Low", drive, "500 MB"), (low.Level, low.Text, low.Args!["drive"], low.Args["free"]));

        var temp = await Check(Make(g, free: p => p == g.Folder ? 10 * Gb : Gb - 1), "disk");
        Assert.Equal(("warn", @"C:\"), (temp.Level, temp.Args!["drive"]));

        Assert.Equal("ok", (await Check(Make(g, free: _ => null), "disk")).Level); // unknown: nothing to warn about
        Assert.Equal("ok", (await Check(Make(g, free: _ => Gb), "disk")).Level); // 1 GB is enough
    }

    [Fact]
    public async Task AGameThatRunsWhenTheLauncherOpensBlocksButOnlyThen()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var r = await Check(Make(g, running: true), "running", opening: true);
        Assert.Equal(("block", "Check.Running", "close-game"), (r.Level, r.Text, r.Fix));
        Assert.Equal("ok", (await Check(Make(g, running: true), "running", opening: false)).Level);
        Assert.Equal("ok", (await Check(Make(g, running: false), "running", opening: true)).Level);
    }

    [Fact]
    public async Task AProbeThatFailsNeverBlocksAndNeverThrows()
    {
        using var t = new TempDir("checks-");
        var all = await Make(Game(t), Manifest(100), steamSignedIn: () => throw new InvalidOperationException("no registry"), free: _ => throw new IOException("drive"),
            serverBuild: () => throw new InvalidOperationException("net")).RunAsync(true);
        Assert.Equal(Ids, all.Select(r => r.Id));
        Assert.All(all, r => Assert.Equal("ok", r.Level));
    }

    [Fact]
    public async Task OnlyWhatCanBlockAStartIsProbedForAStart()
    {
        using var t = new TempDir("checks-");
        var asked = 0;
        var c = new InstallChecks(() => Game(t), "1", () => Task.FromResult<long?>(null), () => true, () => null, _ => false, _ => null, _ => { asked++; return false; }, () => [], _ => { asked++; return null; }, null, () => { asked++; return []; });
        await c.RunAsync(false, blocksOnly: true);
        Assert.Equal(0, asked);
        await c.RunAsync(false);
        Assert.True(asked > 0);
    }

    // the bridge

    static readonly MockLauncherBridge Rest = new(NullLogger<MockLauncherBridge>.Instance);

    [Theory]
    [InlineData("old", "Check.Version.Old")]
    [InlineData("updating", "Check.Version.Updating")]
    [InlineData("files", "Check.Files.Missing")]
    [InlineData("setup", "Kit.NoHosts")]
    [InlineData("port", "Check.Setup.Port")]
    [InlineData("path", null)] // a warning only
    [InlineData("windows", null)]
    [InlineData("disk", null)]
    [InlineData("healthy", null)]
    public void AStartIsBlockedByTheVersionTheFilesAndTheSetupWithTheDashboardsText(string problem, string? blocked)
    {
        using var t = new TempDir("checks-");
        var g = Game(t, archives: problem != "files");
        var checks = problem switch
        {
            "old" => Make(g, Manifest(100), server: 101),
            "updating" => Make(g, Manifest(100, 1026), server: 100),
            "setup" => Make(g, setup: "Kit.NoHosts"),
            "port" => Make(g, ports: _ => "webserver"),
            "path" => Make(g, network: _ => true),
            "windows" => Make(g, events: [@"\Device\HarddiskVolume3" + g.Folder[2..] + @"\x.dll"]),
            "disk" => Make(g, free: _ => 1),
            _ => Make(g),
        };
        var started = 0;
        var b = new LauncherBridge(() => g, Rest, _ => false, _ => { started++; return LauncherFakes.Alive(); }, LauncherFakes.Signed, null, checks: checks);
        var result = b.StartGame("srv://x");
        Assert.Equal(blocked, result.Error);
        Assert.Equal(blocked is null ? 1 : 0, started);
    }

    [Theory]
    [InlineData("steam://nav/games/details/{steam_app_id}", "steam://nav/games/details/42")]
    [InlineData("https://example.test/a?b=1", "https://example.test/a?b=1")]
    [InlineData("http://example.test/", null)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", null)]
    [InlineData(@"C:\\Windows\\System32\\cmd.exe", null)]
    [InlineData("ms-settings:windowsdefender", null)]
    [InlineData("windowsdefender://appbrowser", "windowsdefender://appbrowser")]
    [InlineData("windowsdefender://appbrowser/x", null)]
    [InlineData("windowsdefender://other", null)]
    [InlineData("not a url", null)]
    public void OnlyHttpsSteamAndTheWindowsSecurityAddressAreOpenedAndTheAppIdIsFilledIn(string url, string? opened)
    {
        var seen = new List<string>();
        new LauncherBridge(() => null, Rest, _ => false, LauncherFakes.Up, LauncherFakes.Signed, "42", openFolder: seen.Add).OpenExternal(url);
        Assert.Equal(opened is null ? [] : [opened], seen);
    }

    [Fact]
    public void AnAddressThatNeedsTheAppIdIsIgnoredWithoutOneAndAFailureToOpenIsIgnored()
    {
        var seen = new List<string>();
        new LauncherBridge(() => null, Rest, _ => false, LauncherFakes.Up, LauncherFakes.Signed, null, openFolder: seen.Add).OpenExternal("steam://nav/games/details/{steam_app_id}");
        Assert.Empty(seen);
        new LauncherBridge(() => null, Rest, _ => false, LauncherFakes.Up, LauncherFakes.Signed, "42", openFolder: _ => throw new System.ComponentModel.Win32Exception()).OpenExternal("steam://open/main"); // must not throw
    }

    [Fact]
    public async Task TheGameRunningCheckIsOnlyForTheFirstCall()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var b = new LauncherBridge(() => g, Rest, _ => true, LauncherFakes.Up, LauncherFakes.Signed, null, checks: Make(g, running: true));
        Assert.Equal("block", (await b.CheckInstallAsync()).Single(r => r.Id == "running").Level);
        Assert.Equal("ok", (await b.CheckInstallAsync()).Single(r => r.Id == "running").Level);
    }

    [Fact]
    public async Task TheProblemReportCarriesTheResults()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var results = await Make(g, Manifest(100), server: 101, free: _ => 100L << 20, screen: Screen([], [], null, new DisplayAdapter("Fictional Remote Display Adapter", true, false, false))).RunAsync(false);
        var report = new Diagnostics(() => g, [], "1.2.3", () => [], Path.Combine(t.Path, "out"), () => [], () => [], () => [], _ => [], () => results).Build("manual");
        using var zip = ZipFile.OpenRead(report.Path);
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        var system = Read("system.txt");
        Assert.Contains("Install checks:", system);
        Assert.Contains("version: block Check.Version.Old (build=100, server=101)", system);
        Assert.Contains("files: ok Check.Ok (archives=pc0..pc11 present)", system);
        Assert.Contains("disk: warn Check.Disk.Low", system);
        Assert.Contains("display: warn Check.Display.Virtual (adapter=Fictional Remote Display Adapter)", system);
        using var json = JsonDocument.Parse(Read("report.json"));
        Assert.Equal(Ids, json.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()));

        // a probe that fails is listed as missing and the report still builds
        var failing = new Diagnostics(() => g, [], "1", () => [], Path.Combine(t.Path, "out2"), () => [], () => [], () => [], _ => [], () => throw new InvalidOperationException("x")).Build("manual");
        Assert.Contains("install checks", failing.Missing);
    }

    [Theory]
    [InlineData(true, 1366, 768, 60, "warn")]  // exclusive full screen at a mode the screen does not offer
    [InlineData(true, 1360, 768, 60, "ok")]
    [InlineData(true, 1360, 768, 75, "warn")]  // the refresh rate counts
    [InlineData(false, 1366, 768, 60, "ok")]   // windowed (and Borderless, which is written windowed): any size
    [InlineData(false, 123, 45, 1, "ok")]
    public async Task OnlyExclusiveFullScreenAtAnUnofferedModeWarnsAndTheFixIsOffered(bool fullscreen, int w, int h, int hz, string level)
    {
        using var t = new TempDir("checks-");
        var r = await Check(Make(Game(t), screen: Screen(Offered, [Graphics(t, "g.cfg", fullscreen, w, h, hz)], null, RealGpu)), "display");
        Assert.Equal(level, r.Level);
        if (level == "ok") Assert.Equal(("Check.Ok", null), (r.Text, r.Fix));
        else Assert.Equal(("Check.Display.Mode", "fix-display", $"{w}x{h} {hz} Hz"), (r.Text, r.Fix, r.Args!["mode"]));
    }

    // #810: only a saved Fullscreen (exclusive) is judged; Borderless (and no saved mode) starts windowed at the screen's size, whatever the file says now
    [Theory]
    [InlineData(true, "warn")]
    [InlineData(false, "ok")]
    [InlineData(null, "ok")]
    public async Task TheModeIsJudgedOnlyWhenExclusiveFullScreenIsTheSavedMode(bool? exclusive, string level)
    {
        using var t = new TempDir("checks-");
        var screen = Screen(Offered, [Graphics(t, "g.cfg", true, 1366, 768, 60)], null, RealGpu) with { Exclusive = exclusive is { } e ? () => e : null };
        Assert.Equal(level, (await Check(Make(Game(t), screen: screen), "display")).Level);
        // the virtual adapter warning does not depend on the mode
        var remote = Screen(Offered, [], null, new DisplayAdapter("Fictional Remote Display Adapter", true, false, false)) with { Exclusive = null };
        Assert.Equal("Check.Display.Virtual", (await Check(Make(Game(t), screen: remote), "display")).Text);
    }

    [Fact]
    public async Task AMissingGraphicsFileOrAModeListThatWindowsDoesNotGiveIsNotJudged()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        Assert.Equal("ok", (await Check(Make(g, screen: Screen(Offered, [Path.Combine(t.Path, "none.cfg")], null, RealGpu)), "display")).Level);
        Assert.Equal("ok", (await Check(Make(g, screen: Screen([], [Graphics(t, "g.cfg", true, 1366, 768, 60)], null, RealGpu)), "display")).Level);
        Assert.Equal("ok", (await Check(Make(g), "display")).Level); // no probes: nothing to judge
    }

    [Fact]
    public async Task TheFixWritesTheCurrentModeIntoTheFileThatNeedsItAndKeepsEveryOtherValue()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var bad = Graphics(t, "bad.cfg", true, 1366, 768, 60);
        var fine = Graphics(t, "fine.cfg", true, 1920, 1080, 60);
        var windowed = Graphics(t, "windowed.cfg", false, 123, 45, 7);
        var (fineBefore, windowedBefore) = (Files.Bytes(fine), Files.Bytes(windowed));
        var c = Make(g, screen: Screen(Offered, [bad, fine, windowed], (1360, 768, 60), RealGpu));
        Assert.Equal("warn", (await Check(c, "display")).Level);

        Assert.True(c.FixDisplay().Ok);

        var (fixedFile, problem) = GameFiles.ReadGraphics(bad);
        Assert.Equal(FileProblem.None, problem);
        Assert.Equal(new GraphicsSettings { Fullscreen = true, Width = 1360, Height = 768, Hz = 60, Scene = 2, Texture = 1, Shadow = 3, Msaa = 1, Aniso = 2, Vsync = true }, fixedFile);
        Assert.Equal((fineBefore, windowedBefore), (Files.Bytes(fine), Files.Bytes(windowed)));
        Assert.Equal("ok", (await Check(c, "display")).Level);
    }

    [Fact]
    public void TheFixRefusesWhileTheGameRunsAndWithoutACurrentModeAndLeavesTheFileAlone()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var bad = Graphics(t, "bad.cfg", true, 1366, 768, 60);
        var before = Files.Bytes(bad);
        Assert.Equal("GameSettings.ClientRunning", Make(g, running: true, screen: Screen(Offered, [bad], (1360, 768, 60), RealGpu)).FixDisplay().Error);
        Assert.Equal("GameSettings.SaveFailed", Make(g, screen: Screen(Offered, [bad], null, RealGpu)).FixDisplay().Error);
        Assert.Equal("GameSettings.NotConfigured", Make(g).FixDisplay().Error);
        Assert.Equal(before, Files.Bytes(bad));
    }

    [Theory]
    [InlineData("Fictional GPU", true, true, true, false)]
    [InlineData("Fictional Remote Display Adapter", true, true, true, false)] // a working screen on an adapter named like a virtual one is fine: the name never triggers
    [InlineData("Fictional Virtual Display Adapter", true, true, true, false)]
    [InlineData("Fictional GPU", true, false, true, true)]  // not part of the desktop
    [InlineData("Fictional GPU", true, true, false, true)]  // no active monitor
    [InlineData("Fictional Remote Display Adapter", true, false, false, true)]
    public async Task APrimaryDisplayDeviceThatIsNotOnTheDesktopOrHasNoActiveMonitorWarnsNamingTheAdapter(string name, bool primary, bool attached, bool monitor, bool warns)
    {
        using var t = new TempDir("checks-");
        var r = await Check(Make(Game(t), screen: Screen(Offered, [], null, new DisplayAdapter(name, primary, attached, monitor))), "display");
        Assert.Equal(warns ? "warn" : "ok", r.Level);
        if (warns) Assert.Equal(("Check.Display.Virtual", null, name), (r.Text, r.Fix, r.Args!["adapter"]));
    }

    [Fact]
    public async Task OnlyThePrimaryAdapterIsJudgedAndTheModeComesFirst()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var virtualSecond = new DisplayAdapter("Fictional Virtual Display", false, false, false);
        Assert.Equal("ok", (await Check(Make(g, screen: Screen(Offered, [], null, virtualSecond, RealGpu)), "display")).Level);
        var both = Screen(Offered, [Graphics(t, "g.cfg", true, 1366, 768, 60)], null, new DisplayAdapter("Fictional Virtual Display", true, false, false));
        Assert.Equal("Check.Display.Mode", (await Check(Make(g, screen: both), "display")).Text);
    }

    [Fact]
    public async Task TheDisplayCheckIsProbedForAStartToo()
    {
        using var t = new TempDir("checks-");
        var c = Make(Game(t), screen: Screen(Offered, [Graphics(t, "g.cfg", true, 1366, 768, 60)], null, RealGpu));
        Assert.Equal("Check.Display.Mode", (await c.RunAsync(false, blocksOnly: true)).Single(r => r.Id == "display").Text);
    }

    [Theory]
    [InlineData(0, true)]            // closed
    [InlineData(3, true)]            // crash
    [InlineData(16010, false)]       // Steam: a cause of its own
    [InlineData(3221225785, false)]  // a replaced game file: a cause of its own
    public async Task AnEarlyExitAfterADisplayWarningNamesItOnlyWhereTheGenericTextWouldShow(long code, bool named)
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        var checks = Make(g, screen: Screen(Offered, [Graphics(t, "g.cfg", true, 1366, 768, 60)], null, RealGpu));
        var b = new LauncherBridge(() => g, Rest, _ => false, _ => new GameProcess(() => false, () => unchecked((int)code)), LauncherFakes.Signed, "1", poll: TimeSpan.FromMilliseconds(1), quitWithin: TimeSpan.FromMilliseconds(20), checks: checks);
        Assert.True(b.StartGame("srv://x").Ok);
        var exit = (await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.True(exit.Early);
        Assert.Equal(named ? "Check.Display.Mode" : null, exit.Display?.Text);
        Assert.Equal(named ? "fix-display" : null, exit.Display?.Fix);
    }

    [Fact]
    public async Task AnEarlyExitWithoutADisplayWarningNamesNoDisplayWarningAndTheClockStillWins()
    {
        using var t = new TempDir("checks-");
        var g = Game(t);
        LauncherBridge Bridge(ScreenProbes screen, ServerClock? clock = null) =>
            new(() => g, Rest, _ => false, _ => new GameProcess(() => false, () => 0), LauncherFakes.Signed, "1", poll: TimeSpan.FromMilliseconds(1), quitWithin: TimeSpan.FromMilliseconds(20), checks: Make(g, screen: screen), clock: clock);
        var fine = Bridge(Screen(Offered, [Graphics(t, "ok.cfg", true, 1360, 768, 60)], null, RealGpu));
        Assert.True(fine.StartGame("srv://x").Ok);
        Assert.Null((await fine.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)))!.Display);

        // the PC's clock is 3 hours ahead of the server's: that is the cause the dialog names
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var off = new ServerClock(() => now, () => TimeSpan.Zero);
        off.Observe(new HttpResponseMessage { Headers = { Date = now - TimeSpan.FromHours(3) } });
        var bad = Bridge(Screen(Offered, [Graphics(t, "bad.cfg", true, 1366, 768, 60)], null, RealGpu), off);
        Assert.True(bad.StartGame("srv://x").Ok);
        var exit = (await bad.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Equal(("closed", TimeSpan.FromHours(3)), (exit.Cause, exit.ClockAhead));
        Assert.Null(exit.Display);
    }

    [Fact]
    public void ThisPcsScreenProbesAnswerWithoutThrowingAndAPrimaryAdapterHasAName()
    {
        var real = ScreenProbes.Real(new Settings("1", "client.exe", Path.Combine("fictional", "G_steam.cfg")), () => null);
        Assert.All(real.Adapters().Where(a => a.Primary), a => Assert.NotEmpty(a.Name));
        Assert.NotNull(real.Modes());
        Assert.All(real.GraphicsFiles(), f => Assert.EndsWith(".cfg", f));
        Assert.Equal(2, real.GraphicsFiles().Count()); // no planned mode: the files of both modes
    }
}
