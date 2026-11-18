using System.Diagnostics;
using Resurgence.Launcher;
using Resurgence.Services.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resurgence.Tests.LauncherApp;

public class LauncherBridgeTests
{
    static readonly MockLauncherBridge Rest = new(NullLogger<MockLauncherBridge>.Instance);

    [Fact]
    public void TheCommandLineHasTheServerARandomNumberWithBit128ClearAndTheSteamFlag()
    {
        Assert.Equal("/connect:srv://x /param:0 /steam", LauncherBridge.Arguments("srv://x", 128));
        Assert.Equal("/connect:srv://x /param:1 /steam", LauncherBridge.Arguments("srv://x", 129));
        Assert.Equal($"/connect:u /param:{int.MaxValue - 128} /steam", LauncherBridge.Arguments("u", -1));
    }

    [Fact]
    public async Task TheDoneWindowWaitsForTheGameToAppearAndThenToClose()
    {
        // the game shows up two checks after the start (Steam starts it) and runs for three: the wait ends only after it is gone
        var checks = new Queue<bool>([false, false, true, true, true, false]);
        var b = new LauncherBridge(() => new("dir", "client.exe"), Rest, _ => checks.Count > 0 && checks.Dequeue(), _ => true, () => true, null, poll: TimeSpan.FromMilliseconds(1));
        Assert.True(await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(checks);

        // a game that never appears opens no window; no game folder neither
        var never = new LauncherBridge(() => new("dir", "client.exe"), Rest, _ => false, _ => true, () => true, null, poll: TimeSpan.FromMilliseconds(1), appearWithin: TimeSpan.FromMilliseconds(20));
        Assert.False(await never.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await new LauncherBridge(() => null, Rest, _ => false, _ => true, () => true, null).WhenGameExitsAsync());
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
            var b = new LauncherBridge(() => new(dir, exe), Rest, e => running && e == exe, i => { started.Add(i); return true; }, () => true, "123");

            Assert.True(b.StartGame("srv://x").Ok);
            var i = Assert.Single(started);
            Assert.Equal(exe, i.FileName);
            Assert.Equal(Path.GetDirectoryName(exe), i.WorkingDirectory);
            Assert.StartsWith("/connect:srv://x /param:", i.Arguments);
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
        Assert.Equal("Launch.NoGame", new LauncherBridge(() => null, Rest, _ => false, _ => true, () => true, null).StartGame("srv://x").Error);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exe = Path.Combine(dir, "client.exe");
            File.WriteAllText(exe, "");
            Assert.Equal("Launch.StartFailed", new LauncherBridge(() => new(dir, exe), Rest, _ => false, _ => false, () => true, null).StartGame("srv://x").Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WithoutASignedInSteamTheGameIsNotStarted()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exe = Path.Combine(dir, "client.exe");
            File.WriteAllText(exe, "");
            var started = 0;
            Assert.Equal("Launch.NoSteam", new LauncherBridge(() => new(dir, exe), Rest, _ => false, _ => { started++; return true; }, () => false, "123").StartGame("srv://x").Error);
            Assert.Equal(0, started);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SteamIsStartedAndWaitedForUnlessSomebodyIsSignedIn()
    {
        var calls = 0;
        Assert.False(Steam.SignedIn(() => 0, () => { calls++; return true; }, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(1, calls); // started, nobody signed in before the timeout

        var polls = 0;
        Assert.True(Steam.SignedIn(() => ++polls > 2 ? 7u : 0, () => { calls++; return true; }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)));
        Assert.Equal(2, calls); // waited until it signed in

        Assert.True(Steam.SignedIn(() => 1, () => throw new InvalidOperationException(), TimeSpan.Zero, TimeSpan.Zero)); // already up: not started again
        Assert.False(Steam.SignedIn(() => 0, () => false, TimeSpan.FromSeconds(5), TimeSpan.Zero)); // not installed
    }
}
