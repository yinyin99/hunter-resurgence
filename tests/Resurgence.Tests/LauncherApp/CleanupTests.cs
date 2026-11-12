using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

/// <summary>The clean-up of an older setup and the uninstall, on temporary folders, a temporary hosts file, a temporary certificate store and a temporary registry key.</summary>
public sealed class CleanupTests : IDisposable
{
    static readonly string[] Names = ["api.example.test", "web.example.test"];
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    const string UserLines = "127.0.0.1 localhost\r\n# mine\r\n10.0.0.5 nas\r\n";

    readonly TempDir tmp = new("cleanup-");
    readonly string store = "hunter-resurgence-test-" + Guid.NewGuid().ToString("N");
    readonly Pki.Place place;
    readonly string hosts;

    public CleanupTests()
    {
        place = new(Path.Combine(tmp.Path, "kit"), store, store);
        hosts = Path.Combine(tmp.Path, "hosts");
        File.WriteAllText(hosts, UserLines);
    }

    public void Dispose()
    {
        Pki.Uninstall(place);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\SystemCertificates\" + store, false);
        tmp.Dispose();
    }

    // the first Open is Untrust's, the second is the certificate prompt: the player says No there
    sealed record Declining(string Dir, string KeyName, string Store) : Pki.Place(Dir, KeyName, Store)
    {
        int opens;
        public override X509Store Open() => ++opens < 2 ? base.Open() : throw new CryptographicException("the player said No");
    }

    [Fact]
    public void OldProgramsAreAPlayExeBesideItsSettingsAndALauncherOutsideThisInstall()
    {
        var app = Path.Combine(tmp.Path, "installed", "resurgence-launcher.exe");
        Files.Touch(app);
        var play = Path.Combine(tmp.Path, "play", "play.exe").Also(Files.Touch);
        var otherPlay = Path.Combine(tmp.Path, "other", "play.exe").Also(Files.Touch);
        Files.Touch(Path.Combine(tmp.Path, "play", "settings.json"));
        var old = Path.Combine(tmp.Path, "unpacked", "resurgence-launcher.exe").Also(Files.Touch);

        Assert.True(Cleanup.IsOld(play, app));
        Assert.False(Cleanup.IsOld(otherPlay, app)); // some other program called play.exe
        Assert.True(Cleanup.IsOld(old, app));
        Assert.False(Cleanup.IsOld(app, app)); // this install
    }

    [Fact]
    public void OldFoldersAreFoundOnlyWithTheirExeInsideAndNeverThisInstall()
    {
        var downloads = Path.Combine(tmp.Path, "Downloads");
        var launcher = Path.Combine(downloads, "launcher-win-x64").Also(d => Files.Touch(Path.Combine(d, "resurgence-launcher.exe")));
        var play = Path.Combine(downloads, "play-win-x64").Also(d => Files.Touch(Path.Combine(d, "play.exe")));
        Path.Combine(downloads, "launcher-win-x64-notes").Also(d => Files.Touch(Path.Combine(d, "a.txt")));
        Files.Touch(Path.Combine(tmp.Path, "Desktop", "play-win-x64", "readme.txt")); // that name, but not the zip's content
        var self = Path.Combine(launcher, "resurgence-launcher.exe");

        Assert.Equal([play], Cleanup.Folders([downloads, Path.Combine(tmp.Path, "Desktop"), Path.Combine(tmp.Path, "missing")], self));
        Assert.Equal([launcher, play], Cleanup.Folders([downloads], Path.Combine(tmp.Path, "app", "resurgence-launcher.exe")).Order());
    }

    [Fact]
    public void StrayLogsInTheInstallRootAreDeletedAndTheGameFolderIsNotTouched()
    {
        var root = Path.Combine(tmp.Path, "install");
        var exe = Path.Combine(root, "game", "client.exe").Also(Files.Touch);
        Files.Touch(Path.Combine(root, "run.netlog"));
        Files.Touch(Path.Combine(root, "cef.log"));
        Files.Touch(Path.Combine(root, "keep.txt"));
        Files.Touch(Path.Combine(root, "game", "own.netlog"));
        Files.Touch(Path.Combine(root, "game", "cef.log"));
        Directory.CreateDirectory(Path.Combine(root, "icons"));

        var logs = Cleanup.Logs(new GameInstall(root, exe));
        Assert.Equal(3, logs.Length);
        Assert.Empty(Cleanup.Delete(logs, false));

        Assert.Equal(["game", "keep.txt"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(root, "game", "own.netlog")));
        Assert.True(File.Exists(Path.Combine(root, "game", "cef.log")));
    }

    [Fact]
    public void IconsWithFilesAreNotAStrayLogAndNoSubfolderMeansNoLogs()
    {
        var root = Path.Combine(tmp.Path, "install");
        var exe = Path.Combine(root, "game", "client.exe").Also(Files.Touch);
        Files.Touch(Path.Combine(root, "icons", "a.png"));
        Assert.Empty(Cleanup.Logs(new GameInstall(root, exe)));

        var flat = Path.Combine(tmp.Path, "flat");
        Files.Touch(Path.Combine(flat, "x.netlog"));
        Assert.Empty(Cleanup.Logs(new GameInstall(flat, Path.Combine(flat, "client.exe")))); // the exe's folder is the root: the game's own
        Assert.Empty(Cleanup.Logs(null));
    }

    [Fact]
    public void AnOlderKitSetupIsKeptWhenItFitsAndReplacedWhenItDoesNot()
    {
        Assert.Null(Cleanup.KitKept(hosts, Names, place, Now)); // nothing there

        Pki.Install(Names, Now, place);
        Hosts.Apply(hosts, Names);
        Assert.Null(Pki.Check(Names, Now, place));
        Assert.True(Cleanup.KitKept(hosts, Names, place, Now));

        Assert.False(Cleanup.KitKept(hosts, ["api.example.test", "other.example.test"], place, Now)); // other names
        Assert.NotNull(Pki.Check(Names, Now.AddDays(800), place)); // expired
        Hosts.Apply(hosts, ["api.example.test"]);
        Assert.False(Cleanup.KitKept(hosts, Names, place, Now)); // an old block for other names

        Hosts.Apply(hosts, [.. Names.AsEnumerable().Reverse()]); // another order, and a tool's lines after the block: still this setup
        File.AppendAllText(hosts, "10.0.0.9 other\r\n");
        Assert.True(Cleanup.KitKept(hosts, Names, place, Now));
    }

    [Fact]
    public void WithoutATrustedAuthorityTheSetupDoesNotFit()
    {
        Pki.Install(Names, Now, place);
        using (var s = place.Open())
            s.RemoveRange(s.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, Pki.CaSubject, false));
        Assert.NotNull(Pki.Check(Names, Now, place));
    }

    [Fact]
    public void ASetupThatStopsAtTheCertificatePromptLeavesNoLeafSoTheNextStartOffersSetupAgain()
    {
        Pki.Install(Names, Now, place); // a working setup
        Assert.Null(Pki.Check(Names, Now, place));

        var declined = new Declining(place.Dir, place.KeyName, place.Store);
        Assert.Throws<CryptographicException>(() => Pki.Install(Names, Now, declined)); // set up again, answer No
        Assert.NotNull(Pki.Check(Names, Now, place));
        Assert.Throws<InvalidOperationException>(() => Pki.LoadLeaf(Now, place));
    }

    [Fact]
    public void UninstallRestoresTheHostsFileByteForByteAndRemovesAuthorityLeafAndLink()
    {
        var before = File.ReadAllBytes(hosts);
        var key = @"Software\resurgence-test-" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(key);
        try
        {
            Pki.Install(Names, Now, place);
            Hosts.Apply(hosts, Names);
            LinkScheme.Register(root, @"C:\app\launcher.exe");
            Assert.NotEqual(before, File.ReadAllBytes(hosts));

            var data = Path.Combine(tmp.Path, "data").Also(d => Files.Touch(Path.Combine(d, "launcher.json")));
            Cleanup.Uninstall(hosts, place, root, data, () => true);
            Assert.False(Directory.Exists(data));

            Assert.Equal(before, File.ReadAllBytes(hosts));
            Assert.False(Pki.Installed(place));
            Assert.False(Directory.Exists(place.Dir));
            Assert.Null(root.OpenSubKey(@"Software\Classes\hunter-resurgence"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }

    [Fact]
    public void UninstallWithNothingInstalledChangesNothing()
    {
        var before = File.ReadAllBytes(hosts);
        var key = @"Software\resurgence-test-" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(key);
        try { Cleanup.Uninstall(hosts, place, root, Path.Combine(tmp.Path, "nodata"), () => throw new InvalidOperationException("no data, no question")); }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        Assert.Equal(before, File.ReadAllBytes(hosts));
    }

    [Fact]
    public void TheDataFolderStaysWhenThePlayerSaysNoAndWhenAnotherStepFails()
    {
        var data = Path.Combine(tmp.Path, "data").Also(d => Files.Touch(Path.Combine(d, "launcher.json")));
        var key = @"Software\resurgence-test-" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(key);
        try
        {
            var bad = Path.Combine(tmp.Path, "not-a-hosts-file"); // a step that throws must not stop the others
            Cleanup.Uninstall(bad, place, root, data, () => false);
            Assert.True(File.Exists(Path.Combine(data, "launcher.json")));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }
}
