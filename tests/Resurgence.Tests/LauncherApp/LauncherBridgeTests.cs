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
    public void StartsTheExeInItsFolderUnlessItAlreadyRuns()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var exe = Path.Combine(dir, "client.exe");
        File.WriteAllText(exe, "");
        try
        {
            var started = new List<ProcessStartInfo>();
            var running = false;
            var b = new LauncherBridge(() => new(dir, exe), Rest, e => running && e == exe, i => { started.Add(i); return true; });

            Assert.True(b.StartGame("srv://x").Ok);
            var i = Assert.Single(started);
            Assert.Equal(exe, i.FileName);
            Assert.Equal(dir, i.WorkingDirectory);
            Assert.StartsWith("/connect:srv://x /param:", i.Arguments);

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
        Assert.Equal("Launch.NoGame", new LauncherBridge(() => null, Rest, _ => false, _ => true).StartGame("srv://x").Error);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exe = Path.Combine(dir, "client.exe");
            File.WriteAllText(exe, "");
            Assert.Equal("Launch.StartFailed", new LauncherBridge(() => new(dir, exe), Rest, _ => false, _ => false).StartGame("srv://x").Error);
        }
        finally { Directory.Delete(dir, true); }
    }
}
