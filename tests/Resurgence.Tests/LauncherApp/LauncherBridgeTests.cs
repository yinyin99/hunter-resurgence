using System.Diagnostics;
using Resurgence.Launcher;
using Resurgence.Services.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resurgence.Tests.LauncherApp;

public class LauncherBridgeTests
{
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
            var shown = new List<string>();
            var running = false;
            var b = new LauncherBridge(() => new(dir, exe), new MockLauncherBridge(NullLogger<MockLauncherBridge>.Instance), n => running && n == "client", i => { started.Add(i); return true; }, shown.Add);

            b.StartGame("srv://x");
            var i = Assert.Single(started);
            Assert.Equal(exe, i.FileName);
            Assert.Equal(dir, i.WorkingDirectory);
            Assert.StartsWith("/connect:srv://x /param:", i.Arguments);
            Assert.Empty(shown);

            running = true;
            b.StartGame("srv://x");
            Assert.Single(started);
            Assert.Single(shown);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AMissingExeOrAFailedStartShowsAMessage()
    {
        var shown = new List<string>();
        var rest = new MockLauncherBridge(NullLogger<MockLauncherBridge>.Instance);
        new LauncherBridge(() => null, rest, _ => false, _ => true, shown.Add).StartGame("srv://x");
        Assert.Single(shown);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var exe = Path.Combine(dir, "client.exe");
            File.WriteAllText(exe, "");
            new LauncherBridge(() => new(dir, exe), rest, _ => false, _ => false, shown.Add).StartGame("srv://x");
            Assert.Equal(2, shown.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
