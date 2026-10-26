using System.Diagnostics;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>Reading the game's token.</summary>
public class WatcherTests
{
    [Theory]
    [InlineData("hunt")]
    [InlineData("huntbeta")]
    [InlineData("huntdev")]
    public void ParsesEveryScheme(string scheme)
    {
        string h = new('1', 40), a = new('a', 40), r = new('b', 40);
        Assert.True(Watcher.ParseToken($"C:\\g\\client.exe /connect:{scheme}://gs.test/{h}/lang/en_US/access_token/{a}/refresh_token/{r} /param:169 /steam") == a, "parse " + scheme);
    }

    [Fact]
    public void ParserRejectsWhatIsNotAToken()
    {
        string h = new('1', 40), a = new('a', 40), r = new('b', 40);
        Assert.True(Watcher.ParseToken($"\"C:\\g\\client.exe\" \"/connect:hunt://gs.test/{h}/lang/en_US/access_token/{a}/refresh_token/{r}\"") == a, "parse quoted argument");
        Assert.True(Watcher.ParseToken($"x /connect:hunt://gs.test/{h}/lang/en_US/access_token/{a[..39]}/refresh_token/{r}") == null, "short token rejected");
        Assert.True(Watcher.ParseToken($"x /connect:other://gs.test/{h}/lang/en_US/access_token/{a}/refresh_token/{r}") == null, "other scheme rejected");
        Assert.True(Watcher.ParseToken("C:\\g\\launcher.exe") == null, "launcher command line has no token");
    }

    /// <summary>Starts a renamed copy of cmd.exe as fakeclient.exe and reads the token from its command line. Nothing but those fake processes is touched.</summary>
    [Fact]
    public void WatcherReadsTheTokenFromAFakeProcess()
    {
        using var temp = new TempDir("backup-test-game-");
        var dir = temp.Path;
        var procs = new List<Process>();
        try
        {
            // a renamed copy of cmd.exe (the 32-bit one, like the real client, when present) that sleeps with the fake argument after `rem`
            var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "cmd.exe");
            var src = File.Exists(wow) ? wow : Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process Spawn(string sub, string tok)
            {
                Directory.CreateDirectory(Path.Combine(dir, sub));
                var exe = Path.Combine(dir, sub, "fakeclient.exe");
                File.Copy(src, exe);
                var p = Process.Start(new ProcessStartInfo(exe, $"/c ping -n 40 127.0.0.1 >nul & rem /connect:hunt://fake.invalid/{new string('1', 40)}/lang/en_US/access_token/{tok}/refresh_token/{new string('d', 40)} /param:1 /steam") { UseShellExecute = false, CreateNoWindow = true })!;
                procs.Add(p);
                return p;
            }
            var w = new Watcher("fakeclient.exe");
            Assert.True(!w.Scan() && w.Token == null, "no token without the game");
            string t1 = new('c', 40), t2 = new('e', 40);
            Spawn("one", t1);
            Assert.True(Poll(w, t1), $"watcher reads the token from a fake process ({Path.GetFileName(src)} in {Path.GetFileName(Path.GetDirectoryName(src))})");
            w.Stale = true;
            Spawn("two", t2);
            Assert.True(Poll(w, t2) && !w.Stale, "a new process replaces the token and clears stale");
            foreach (var p in procs) { p.Kill(true); p.WaitForExit(); p.Dispose(); }
            procs.Clear();
            for (int i = 0; i < 40 && (w.Scan() || w.GameSeen); i++) Thread.Sleep(250); // a killed process can stay listed for a moment on a busy machine
            Assert.True(w.Token == t2 && !w.GameSeen, "token is kept after the game exits");
        }
        finally
        {
            foreach (var p in procs.Where(p => !p.HasExited)) p.Kill(true);
            foreach (var p in procs) p.WaitForExit();
        }
    }

    static bool Poll(Watcher w, string tok)
    {
        for (int i = 0; i < 40; i++)
        {
            w.Scan();
            if (w.Token == tok) return true;
            Thread.Sleep(250);
        }
        return false;
    }
}
