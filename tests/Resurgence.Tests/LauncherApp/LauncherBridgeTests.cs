using System.Diagnostics;
using Resurgence.Launcher;
using Resurgence.Services.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resurgence.Tests.LauncherApp;

public class LauncherBridgeTests
{
    static readonly MockLauncherBridge Rest = new(NullLogger<MockLauncherBridge>.Instance);

    [Fact]
    public void TheCommandLineHasTheServerARandomNumberWithBit128ClearAndTheSteamFlagOnlyWithSteam()
    {
        Assert.Equal("/connect:srv://x /param:0 /steam", LauncherBridge.Arguments("srv://x", 128, true));
        Assert.Equal("/connect:srv://x /param:1 /steam", LauncherBridge.Arguments("srv://x", 129, true));
        Assert.Equal($"/connect:u /param:{int.MaxValue - 128} /steam", LauncherBridge.Arguments("u", -1, true));
        Assert.Equal("/connect:srv://x /param:1", LauncherBridge.Arguments("srv://x", 129, false));
    }

    [Fact]
    public async Task TheDoneWindowWaitsForTheGameToAppearAndThenToClose()
    {
        // the game shows up two checks after the start (Steam starts it) and runs for three: the wait ends only after it is gone
        var checks = new Queue<bool>([false, false, true, true, true, false]);
        var b = new LauncherBridge(() => new("dir", "client.exe"), Rest, _ => checks.Count > 0 && checks.Dequeue(), LauncherFakes.Up, LauncherFakes.Signed, null, poll: TimeSpan.FromMilliseconds(1));
        Assert.True(await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(checks);

        // a game that never appears opens no window; no game folder neither
        var never = new LauncherBridge(() => new("dir", "client.exe"), Rest, _ => false, LauncherFakes.Up, LauncherFakes.Signed, null, poll: TimeSpan.FromMilliseconds(1), appearWithin: TimeSpan.FromMilliseconds(20));
        Assert.False(await never.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await new LauncherBridge(() => null, Rest, _ => false, LauncherFakes.Up, LauncherFakes.Signed, null).WhenGameExitsAsync());
    }

    [Fact]
    public void StartsTheExeInItsFolderUnlessItAlreadyRuns()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        // the exe sits in a subfolder of the game folder, as `client_exe` allows: it must start in its own folder
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName, "client.exe");
        File.WriteAllText(exe, "");
        try
        {
            var started = new List<ProcessStartInfo>();
            var running = false;
            var b = new LauncherBridge(() => new(dir, exe), Rest, e => running && e == exe, i => { started.Add(i); return LauncherFakes.Alive(); }, LauncherFakes.Signed, "123");

            Assert.True(b.StartGame("srv://x").Ok);
            var i = Assert.Single(started);
            Assert.Equal(exe, i.FileName);
            Assert.Equal(Path.GetDirectoryName(exe), i.WorkingDirectory);
            Assert.StartsWith("/connect:srv://x /param:", i.Arguments);
            Assert.EndsWith(" /steam", i.Arguments);
            Assert.Equal("123", i.Environment["SteamAppId"]);
            Assert.Equal("123", i.Environment["SteamGameId"]);

            running = true;
            Assert.Equal("Launch.AlreadyRunning", b.StartGame("srv://x").Error);
            Assert.Single(started);

            running = false;
            Assert.Equal("Launch.Failed", b.StartGame("srv://x /y").Error);
            Assert.Single(started);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AMissingExeOrAFailedStartIsAFailedResult()
    {
        Assert.Equal("Launch.NoGame", new LauncherBridge(() => null, Rest, _ => false, LauncherFakes.Up, LauncherFakes.Signed, null).StartGame("srv://x").Error);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exe = Path.Combine(dir, "client.exe");
            File.WriteAllText(exe, "");
            var failing = new LauncherBridge(() => new(dir, exe), Rest, _ => false, _ => null, LauncherFakes.Signed, null);
            Assert.Equal("Launch.StartFailed", failing.StartGame("srv://x").Error);
            Assert.Null(failing.LastStartMode); // a start that failed says nothing of Steam
        }
        finally { Directory.Delete(dir, true); }
    }

    // one game install on disk for the start tests; Started has what the client was started with
    sealed class Start : IDisposable
    {
        readonly string dir = Directory.CreateTempSubdirectory().FullName;
        public readonly List<ProcessStartInfo> Started = [];
        public readonly string Exe;
        public Start() { Exe = Path.Combine(dir, "client.exe"); File.WriteAllText(Exe, ""); }
        public void Dispose() => Directory.Delete(dir, true);

        /// <param name="exits">The exit code each start ends with (null: the client keeps running), in the order of the starts.</param>
        public LauncherBridge Bridge(SteamProbes steam, params int?[] exits) => new(() => new(dir, Exe), Rest, _ => false,
            i => { Started.Add(i); var code = exits.Length >= Started.Count ? exits[Started.Count - 1] : null; return new GameProcess(() => false, () => code); },
            steam, "123", poll: TimeSpan.FromMilliseconds(1), steamWait: TimeSpan.FromMilliseconds(50), quitWithin: TimeSpan.FromMilliseconds(20));

        public string[] Modes => [.. Started.Select(i => i.Arguments.EndsWith(" /steam") ? "steam" : "nosteam")];
    }

    [Fact]
    public void SteamNotInstalledStartsWithoutSteamAtOnce()
    {
        using var s = new Start();
        var started = 0;
        var b = s.Bridge(new(() => false, () => false, () => { started++; return true; }));
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal(["nosteam"], s.Modes);
        Assert.Equal("nosteam", b.LastStartMode);
        Assert.Equal(0, started); // Steam is not started, nothing is waited for
        Assert.Equal("123", s.Started[0].Environment["SteamAppId"]);
    }

    [Fact]
    public void TheModeOfTheLastStartIsNotToldWhileTheNextOneWaitsForSteam()
    {
        using var s = new Start();
        bool installed = false, signedIn = false;
        var b = s.Bridge(new(() => installed, () => signedIn, () => { signedIn = true; return true; }));
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal("nosteam", b.LastStartMode);

        // the next start: Steam is installed now, not signed in, and signs in during the wait
        (installed, signedIn) = (true, false);
        var told = new List<string?>();
        b.StartStateChanged += () => told.Add(b.LastStartMode);
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.DoesNotContain("nosteam", told); // the old note must not come back while this start waits and then starts with Steam
        Assert.Equal("steam", b.LastStartMode);
        Assert.Equal(["nosteam", "steam"], s.Modes);
    }

    [Fact]
    public void SteamSignedInStartsWithSteamAndIsNotStartedAgain()
    {
        using var s = new Start();
        var b = s.Bridge(new(() => true, () => true, () => throw new InvalidOperationException()));
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal(["steam"], s.Modes);
        Assert.Equal("steam", b.LastStartMode);
        Assert.False(b.WaitingForSteam);
    }

    [Fact]
    public void SteamSignedOutIsStartedAndWaitedForAndTheGameStartsWithItWhenItSignsIn()
    {
        using var s = new Start();
        var (looks, started, waiting) = (0, 0, new List<bool>());
        LauncherBridge? b = null;
        b = s.Bridge(new(() => true, () => ++looks > 4, () => { started++; return true; }));
        b.StartStateChanged += () => waiting.Add(b.WaitingForSteam);
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal(["steam"], s.Modes);
        Assert.Equal(1, started);
        Assert.Contains(true, waiting); // "Starting Steam…" was shown ...
        Assert.False(b.WaitingForSteam);     // ... and is gone
    }

    [Fact]
    public void ASteamThatIsLateAfterTheWaitStartsWithoutSteamAndTheNextStartTriesItAgain()
    {
        using var s = new Start();
        var late = true;
        var b = s.Bridge(new(() => true, () => !late, () => true));
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal("nosteam", b.LastStartMode);
        late = false;
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal(["nosteam", "steam"], s.Modes);
    }

    [Fact]
    public async Task StartWithoutSteamEndsTheWaitAtOnce()
    {
        using var s = new Start();
        var b = new LauncherBridge(() => new(Path.GetDirectoryName(s.Exe)!, s.Exe), Rest, _ => false, i => { s.Started.Add(i); return LauncherFakes.Alive(); }, new(() => true, () => false, () => true), "123",
            poll: TimeSpan.FromMilliseconds(5), steamWait: TimeSpan.FromMinutes(5));
        b.StartWithoutSteam(); // nothing waits: ignored, it must not skip the next wait
        var start = Task.Run(() => b.StartGame("srv://x"));
        while (!b.WaitingForSteam) await Task.Delay(1);
        b.StartWithoutSteam();
        Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(["nosteam"], s.Modes);
        Assert.False(b.WaitingForSteam);
        Assert.Equal("nosteam", b.LastStartMode);
    }

    [Fact]
    public void ASteamStartThatQuitsWith16010IsTriedOnceMoreWithoutSteam()
    {
        using var s = new Start();
        var b = s.Bridge(new(() => true, () => true, () => true), 16010);
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal(["steam", "nosteam"], s.Modes);
        Assert.Equal("nosteam", b.LastStartMode);
        Assert.Equal(s.Started[0].Arguments.Replace(" /steam", ""), s.Started[1].Arguments); // the same server and number

        // never in a loop: a start without Steam that quits with 16010 is not tried again, nor is the retry
        using var noSteam = new Start();
        Assert.True(noSteam.Bridge(new(() => false, () => false, () => true), 16010, 16010).StartGame("srv://x").Ok);
        Assert.Equal(["nosteam"], noSteam.Modes);
        using var twice = new Start();
        Assert.True(twice.Bridge(new(() => true, () => true, () => true), 16010, 16010, 16010).StartGame("srv://x").Ok);
        Assert.Equal(["steam", "nosteam"], twice.Modes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(0)]
    public void AnotherExitOrNoneOfASteamStartIsLeftAlone(int? code)
    {
        using var s = new Start();
        var b = s.Bridge(new(() => true, () => true, () => true), code);
        Assert.True(b.StartGame("srv://x").Ok);
        Assert.Equal(["steam"], s.Modes);
        Assert.Equal("steam", b.LastStartMode);
    }

    [Fact]
    public void AClientThatDoesNotStartIsAFailedStartInEitherMode()
    {
        using var s = new Start();
        foreach (var installed in new[] { true, false })
            Assert.Equal("Launch.StartFailed", new LauncherBridge(() => new(Path.GetDirectoryName(s.Exe)!, s.Exe), Rest, _ => false, _ => null, new(() => installed, () => installed, () => false), null).StartGame("srv://x").Error);
    }

    [Fact]
    public async Task EachRunInRunsJsonRecordsTheModeOfItsStart()
    {
        using var s = new Start();
        using var temp = new TempDir("resurgence-test-");
        var history = new RunHistory(Path.Combine(temp.Path, "runs.json"));
        foreach (var installed in new[] { false, true })
        {
            var running = new Queue<bool>(); // the game is not running at the start, then for two looks
            var b = new LauncherBridge(() => new(Path.GetDirectoryName(s.Exe)!, s.Exe), Rest, _ => running.Count > 0 && running.Dequeue(), LauncherFakes.Up, new(() => installed, () => installed, () => true), null,
                poll: TimeSpan.FromMilliseconds(1), find: _ => new GameProcess(() => true, () => running.Count == 0 ? 0 : null), history: history);
            Assert.True(b.StartGame("srv://x").Ok);
            foreach (var x in new[] { true, true, false }) running.Enqueue(x);
            Assert.True(await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(["nosteam", "steam"], history.Load().Select(r => r.Mode));
    }

    [Fact]
    public void ThePlannedModeIsNoSteamOnlyWithoutSteamElseUnknownBecauseAStartWithSteamCanFallBack()
    {
        Assert.Equal("nosteam", new SteamProbes(() => false, () => false, () => true).PlannedMode());
        Assert.Null(new SteamProbes(() => true, () => true, () => true).PlannedMode());
        Assert.Null(new SteamProbes(() => true, () => false, () => true).PlannedMode());
    }
}

/// <summary>Fakes for the bridge's seams.</summary>
static class LauncherFakes
{
    /// <summary>A client that keeps running without a window.</summary>
    public static GameProcess Alive() => new(() => false, () => null);
    public static readonly Func<ProcessStartInfo, GameProcess?> Up = _ => Alive();
    /// <summary>Steam installed and somebody signed in.</summary>
    public static readonly SteamProbes Signed = new(() => true, () => true, () => true);
}
