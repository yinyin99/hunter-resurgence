using System.Security.Cryptography;
using Microsoft.Win32;
using Resurgence.Launcher;
using Resurgence.Services;
using Velopack;
using Velopack.Locators;

namespace Resurgence.Tests.LauncherApp;

/// <summary>What runs without installing: the update decision against a local feed (a folder with releases.win.json and the package), and the link scheme in a temporary registry key.</summary>
public class LauncherUpdaterTests
{
    const string Id = "test-app";

    // a feed folder with one full package of the given version; the package is a few bytes (the checksum is all that is verified)
    static string Feed(TempDir dir, string version, bool badChecksum = false)
    {
        var feed = Directory.CreateDirectory(Path.Combine(dir.Path, "feed")).FullName;
        var name = $"{Id}-{version}-full.nupkg";
        var bytes = "package"u8.ToArray();
        File.WriteAllBytes(Path.Combine(feed, name), bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)) + (badChecksum ? "0" : "");
        File.WriteAllText(Path.Combine(feed, "releases.win.json"),
            $$"""{"Assets":[{"PackageId":"{{Id}}","Version":"{{version}}","Type":"Full","FileName":"{{name}}","SHA1":"{{sha1}}","SHA256":"","Size":{{bytes.Length}}}]}""");
        return feed;
    }

    static (LauncherUpdater Updater, string Packages) Updater(TempDir dir, string installed, string feedVersion, Func<bool> running, bool badChecksum = false)
    {
        var packages = Path.Combine(dir.Path, "packages");
        var manager = new UpdateManager(Feed(dir, feedVersion, badChecksum), null, new TestVelopackLocator(Id, installed, packages));
        return (new LauncherUpdater(manager, running), packages);
    }

    [Fact]
    public async Task ANewerReleaseIsDownloadedAndWaitsForTheRestart()
    {
        using var dir = new TempDir("updater-");
        var (u, packages) = Updater(dir, "1.0.0", "1.1.0", () => false);

        Assert.True(await u.CheckAsync());
        Assert.True(u.Pending);
        Assert.Equal(LauncherNotice.LauncherReady, (await u.StatusAsync()).Notice);
        Assert.True(File.Exists(Path.Combine(packages, $"{Id}-1.1.0-full.nupkg")));
    }

    [Fact]
    public async Task TheSameVersionIsNoUpdate()
    {
        using var dir = new TempDir("updater-");
        var (u, _) = Updater(dir, "1.1.0", "1.1.0", () => false);

        Assert.False(await u.CheckAsync());
        Assert.False(u.Pending);
        Assert.Equal(PatchStatus.Idle, await u.StatusAsync());
    }

    [Fact]
    public async Task NothingHappensWhileTheGameRuns()
    {
        using var dir = new TempDir("updater-");
        var running = true;
        var (u, packages) = Updater(dir, "1.0.0", "1.1.0", () => running);

        Assert.False(await u.CheckAsync());
        Assert.False(Directory.Exists(packages) && Directory.GetFiles(packages).Length > 0);

        // an update downloaded before the game started is neither offered nor applied until the game ends
        running = false;
        Assert.True(await u.CheckAsync());
        running = true;
        Assert.Equal(PatchStatus.Idle, await u.StatusAsync());
        Assert.False(u.ApplyAndRestart());
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheInstalledVersion()
    {
        using var dir = new TempDir("updater-");
        var (u, packages) = Updater(dir, "1.0.0", "1.1.0", () => false, badChecksum: true);

        Assert.False(await u.CheckAsync());
        Assert.False(u.Pending);
        Assert.Equal(PatchStatus.Idle, await u.StatusAsync());
        Assert.False(File.Exists(Path.Combine(packages, $"{Id}-1.1.0-full.nupkg")));
        Assert.False(u.ApplyAndRestart());
    }

    [Fact]
    public void TheLinkSchemeIsRegisteredAndRemovedPerUser()
    {
        var name = @"Software\resurgence-test-" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(name);
        try
        {
            LinkScheme.Register(root, @"C:\app\launcher.exe");
            using (var k = root.OpenSubKey(@"Software\Classes\hunter-resurgence"))
            {
                Assert.NotNull(k!.GetValue("URL Protocol"));
                using var cmd = k.OpenSubKey(@"shell\open\command");
                Assert.Equal("\"C:\\app\\launcher.exe\" \"%1\"", cmd!.GetValue(""));
            }

            LinkScheme.Remove(root);
            Assert.Null(root.OpenSubKey(@"Software\Classes\hunter-resurgence"));
            LinkScheme.Remove(root); // already gone: no error
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(name, false); }
    }
}
