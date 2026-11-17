using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Launcher;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.LauncherApp;

/// <summary>Settings: the folder buttons and the cache buttons (docs/launcher.md#settings-folders-and-caches), with temporary folders.</summary>
public class FolderActionTests : IDisposable
{
    readonly TempDir temp = new("resurgence-folders-");
    readonly string game, exe, appData, pictures;
    readonly List<string> opened = [];

    public FolderActionTests()
    {
        game = Path.Combine(temp.Path, "common", "Client");
        exe = Path.Combine(game, "bin", "hunter.exe");
        appData = Path.Combine(temp.Path, "appdata");
        pictures = Path.Combine(temp.Path, "pictures");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(pictures);
    }

    public void Dispose() => temp.Dispose();

    LauncherBridge Bridge(bool installed = true, bool running = false, Func<Task>? clearBrowser = null) =>
        new(() => installed ? new(game, exe) : null, new MockLauncherBridge(NullLogger<MockLauncherBridge>.Instance), _ => running, _ => true, () => true, null,
            openFolder: opened.Add, appData: appData, pictures: pictures, clearBrowser: clearBrowser);

    [Fact]
    public void GameAndLogsOpenTheInstallFolderAndTheExesFolder()
    {
        var b = Bridge();
        Assert.True(b.OpenFolder("game").Ok);
        Assert.True(b.OpenFolder("logs").Ok);
        Assert.Equal([game, Path.GetDirectoryName(exe)], opened);
    }

    [Fact]
    public void ScreenshotsOpenTheNearestFolderThatExists()
    {
        var b = Bridge();
        Assert.True(b.OpenFolder("screenshots").Ok);
        Directory.CreateDirectory(Path.Combine(pictures, "hunter"));
        Assert.True(b.OpenFolder("screenshots").Ok);
        Directory.CreateDirectory(Path.Combine(pictures, "hunter", "Screenshots"));
        Assert.True(b.OpenFolder("screenshots").Ok);
        Assert.Equal([pictures, Path.Combine(pictures, "hunter"), Path.Combine(pictures, "hunter", "Screenshots")], opened);
    }

    [Fact]
    public void AMissingFolderOrNoGameOpensNothingAndSaysSo()
    {
        Directory.Delete(game, true);
        Assert.Equal("Settings.NoFolder", Bridge().OpenFolder("game").Error);
        Assert.Equal("Settings.NoFolder", Bridge().OpenFolder("logs").Error);
        Assert.Equal("Settings.NoFolder", Bridge(installed: false).OpenFolder("screenshots").Error);
        Assert.Equal("Settings.NoFolder", Bridge().OpenFolder("elsewhere").Error);
        Assert.Empty(opened);
    }

    [Fact]
    public void ClearingTheGameCacheKeepsCookiesLocalStorageAndSettings()
    {
        var profile = Path.Combine(appData, "hunter", "cache", "Default");
        string[] gone = ["Cache", "Code Cache", "GPUCache"], kept = ["Local Storage", "Network"];
        foreach (var d in gone.Concat(kept)) File.WriteAllText(Directory.CreateDirectory(Path.Combine(profile, d)).FullName + "/f", "x");
        File.WriteAllText(Path.Combine(appData, "hunter", "cache", "Local State"), "x");
        var beta = Path.Combine(appData, "hunter", "cache_beta", "Cache");
        File.WriteAllText(Directory.CreateDirectory(beta).FullName + "/f", "x");
        var settings = Path.Combine(appData, "hunter", "settings.bin");
        File.WriteAllText(settings, "x");

        Assert.True(Bridge().ClearCache("game").Ok);

        Assert.All(gone, d => Assert.False(Directory.Exists(Path.Combine(profile, d))));
        Assert.All(kept, d => Assert.True(File.Exists(Path.Combine(profile, d, "f"))));
        Assert.False(Directory.Exists(beta));
        Assert.True(File.Exists(Path.Combine(appData, "hunter", "cache", "Local State")));
        Assert.True(File.Exists(settings));
        Assert.True(Bridge().ClearCache("game").Ok); // nothing left to clear is not a failure
    }

    [Fact]
    public void TheGameCacheIsNotClearedWhileTheGameRunsOrWithoutAGame()
    {
        var cache = Path.Combine(appData, "hunter", "cache", "Cache");
        Directory.CreateDirectory(cache);
        Assert.Equal("Settings.CacheGameRunning", Bridge(running: true).ClearCache("game").Error);
        Assert.True(Directory.Exists(cache));
        Assert.Equal("Settings.NoFolder", Bridge(installed: false).ClearCache("game").Error);
        Assert.Equal("Settings.CacheFailed", Bridge().ClearCache("elsewhere").Error);
    }

    [Fact]
    public void ALockedGameCacheFolderIsReportedAndTheRestIsStillCleared()
    {
        var root = Path.Combine(appData, "hunter", "cache");
        Directory.CreateDirectory(Path.Combine(root, "Cache"));
        Directory.CreateDirectory(Path.Combine(root, "GPUCache"));
        // a file open without sharing keeps its folder from being deleted
        using var held = new FileStream(Path.Combine(root, "Cache", "f"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("Settings.CacheFailed", Bridge().ClearCache("game").Error);
        Assert.False(Directory.Exists(Path.Combine(root, "GPUCache")));
    }

    [Fact]
    public async Task TheLauncherCacheIsTheBrowsersToClear()
    {
        var cleared = new TaskCompletionSource();
        Assert.True(Bridge(clearBrowser: () => { cleared.SetResult(); return Task.CompletedTask; }).ClearCache("launcher").Ok);
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Bridge(clearBrowser: () => throw new InvalidOperationException()).ClearCache("launcher").Ok); // a browser that is not up yet is not shown as a failure
        Assert.Equal("Settings.CacheFailed", Bridge().ClearCache("launcher").Error);
    }
}
