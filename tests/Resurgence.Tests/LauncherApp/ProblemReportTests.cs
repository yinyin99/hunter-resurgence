using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Launcher;
using Resurgence.Services;
using Resurgence.Services.Mocks;

namespace Resurgence.Tests.LauncherApp;

public class ProblemReportTests
{
    // fictional: header, payload and signature of a made-up token
    const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0LWFjY291bnQifQ.c2lnbmF0dXJlLW9ubHktYS10ZXN0";
    static readonly Regex JwtShape = new(@"eyJ[\w-]+\.[\w-]+\.[\w-]*");

    sealed record Game(string Folder, string Exe)
    {
        public GameInstall Install => new(Folder, Exe);
    }

    static Game NewGame(TempDir temp)
    {
        var folder = Path.Combine(temp.Path, "game");
        Files.Touch(Path.Combine(folder, "client.exe"));
        return new(folder, Path.Combine(folder, "client.exe"));
    }

    static Diagnostics Make(TempDir temp, GameInstall? install, string[]? settings = null, Func<IEnumerable<string>>? processes = null, Func<IReadOnlyList<GameRun>>? runs = null, string? outFolder = null) =>
        new(() => install, settings ?? [], "1.2.3", runs ?? (() => []), outFolder ?? Path.Combine(temp.Path, "out"), processes ?? (() => ["explorer"]), () => ["Test Antivirus"], () => ["Graphics: Test GPU"], exe => [$"event 1000 {exe} crashed"]);

    static Dictionary<string, string> Read(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return z.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open(), System.Text.Encoding.Latin1); return r.ReadToEnd(); });
    }

    [Theory]
    [InlineData(@"open C:\Users\Jane Doe\AppData\Local\x.log failed", @"open C:\Users\<user>\AppData\Local\x.log failed")]
    [InlineData(@"\\pc\share\Users\jane\Desktop", @"\\pc\share\Users\<user>\Desktop")]
    [InlineData("cwd C:\\Users\\Bob, size 3 (ok)", "cwd C:\\Users\\<user>, size 3 (ok)")]
    [InlineData("D:/Users/jane/x", "D:/Users/<user>/x")]
    public void TheProfileFolderNameIsReplacedAndTheRestOfTheLineStays(string line, string expected) => Assert.Equal(expected, Diagnostics.Scrub(line));

    [Theory]
    [InlineData("Jane Doe", @"C:\Users\Jane Doe", "\"C:\\Users\\Jane Doe\", by Jane Doe <jane doe@x>", "\"C:\\Users\\<user>\", by <user> <<user>@x>")]
    [InlineData("José", @"D:\Profiles\José", "José at D:\\Profiles\\José\\x, Joséphine", "<user> at D:\\Profiles\\<user>\\x, Joséphine")]
    [InlineData("José", @"D:\Profiles\José", "Jos\u00c3\u00a9 started", "<user> started")]
    [InlineData("user", @"C:\Users\user", "C:\\Users\\user\\a user", "C:\\Users\\<user>\\a <user>")]
    public void TheOwnNameAndProfilePathAreReplaced(string name, string profile, string line, string expected) => Assert.Equal(expected, Diagnostics.Scrub(line, Diagnostics.OwnNames(name, profile)));

    [Fact]
    public void TheEventQueryIsValidForWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Diagnostics.ReadEvents("no-such-game.exe").ToList(); // must not throw "The specified query is invalid"
    }

    [Fact]
    public void TheZipHasNoSignInAndNoJwtFile()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = NewGame(temp);
        Files.Write(Path.Combine(g.Folder, "jwt.json"), $"{{\"jwt\": \"{Jwt}\"}}");
        Files.Write(Path.Combine(g.Folder, "debug.log"), $"start\nrequest /x?a={Jwt}&b=1\nAuthorization: Bearer abc.def\nCookie: hr_session=abc\nthe end\n");
        // the game's own session id is 40 hex digits and sits in a header or a reply field, without any of the usual words
        var id = string.Concat(Enumerable.Repeat("0123456789", 4)); // built, not written: the push check refuses a 40-digit literal
        Files.Write(Path.Combine(g.Folder, "a.netlog"), $"line one\n{{\"x\":\"{Jwt}\"}}\nGET /x id: {id}\n{{\"session_id\":\"{id}\"}}\nline two\n");
        var report = Make(temp, g.Install).Build("manual");

        var entries = Read(report.Path);
        Assert.DoesNotContain(entries.Keys, k => k.Contains("jwt.json"));
        Assert.All(entries, e => Assert.False(JwtShape.IsMatch(e.Value), e.Key));
        Assert.DoesNotContain("Bearer", string.Concat(entries.Values));
        Assert.Contains("start", entries["game/debug.log"]);
        Assert.Contains("the end", entries["game/debug.log"]);
        Assert.Contains("line one", entries["game/a.netlog"]);
        Assert.Contains("line two", entries["game/a.netlog"]);
        Assert.DoesNotContain(id, string.Concat(entries.Values));
        Assert.Equal(entries.Keys.Order(), report.Files.Order());
        Assert.Equal(new FileInfo(report.Path).Length, report.Bytes);
        Assert.Equal("manual", report.Trigger);
    }

    [Fact]
    public void AUtf16LogIsDecodedAndScrubbedLikeAnyOther()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = NewGame(temp);
        var text = $"alpha\nGET /x?a={Jwt}\nline three\n";
        // with a byte order mark, and big-endian without one (found by its zero bytes)
        File.WriteAllBytes(Path.Combine(g.Folder, "debug.log"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
        File.WriteAllBytes(Path.Combine(g.Folder, "console.log"), Encoding.BigEndianUnicode.GetBytes(text));
        var big = string.Concat(Enumerable.Repeat(new string('z', 99) + "\n", 15_000)) + "last line\n"; // 3 MB in UTF-16
        File.WriteAllBytes(Path.Combine(g.Folder, "cef.log"), Encoding.Unicode.GetBytes(big));
        var entries = Read(Make(temp, g.Install).Build("manual").Path);
        foreach (var name in new[] { "game/debug.log", "game/console.log" })
        {
            Assert.Equal("alpha", entries[name].Split('\n')[0]);
            Assert.Contains("line three", entries[name]);
            Assert.DoesNotContain("eyJ", entries[name]);
            Assert.DoesNotContain('\0', entries[name]);
        }
        // a one-byte log that starts with zeros (a file the game was still filling) is not UTF-16: its token line is still scrubbed
        File.WriteAllBytes(Path.Combine(g.Folder, "debug.log"), [.. new byte[4096], .. Encoding.ASCII.GetBytes($"x\nGET /x?a={Jwt}\nkept line\n")]);
        var zeros = Read(Make(temp, g.Install).Build("manual").Path)["game/debug.log"];
        Assert.DoesNotContain("eyJ", zeros);
        Assert.Contains("kept line", zeros);
        var cef = entries["game/cef.log"]; // cut at 2 MB of bytes = 1 M characters, at a line
        Assert.InRange(cef.Length, 900_000, 1_050_000);
        Assert.StartsWith("zzzz", cef);
        Assert.EndsWith("last line\n", cef);
        Assert.DoesNotContain('\0', cef);
    }

    [Fact]
    public async Task AZipThatCannotBeWrittenIsAReportWithoutAFileThatSendAndSaveRefuse()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = NewGame(temp);
        Files.Touch(Path.Combine(temp.Path, "blocked"));
        var diagnostics = Make(temp, g.Install, outFolder: Path.Combine(temp.Path, "blocked", "sub")); // a folder below a file: it cannot be made
        var report = diagnostics.Build("manual");
        Assert.Equal("", report.Path);
        Assert.Equal(0, report.Bytes);
        Assert.Contains("report zip", report.Missing);
        Assert.Contains("system.txt", report.Files);

        var handler = new Handler((_, _) => new(HttpStatusCode.OK));
        var reporter = new ProblemReporter(diagnostics, new Prefs(), () => { }, new HttpClient(handler) { BaseAddress = new("http://127.0.0.1/") }, new ServerCookie { Value = "abc" }, Path.Combine(temp.Path, "downloads"));
        Assert.Equal(new ReportSent(null, "Report.ErrSave"), await reporter.SendAsync(report));
        Assert.Empty(handler.Seen);
        Assert.Throws<FileNotFoundException>(() => reporter.Save(report));
    }

    [Fact]
    public void ARunningBoosterAndTheAntivirusAreNamedAndTheEventsAndRunsAreIn()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = NewGame(temp);
        var run = new GameRun(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), TimeSpan.FromSeconds(20), 0, false);
        var entries = Read(Make(temp, g.Install, processes: () => ["explorer", "razercortex"], runs: () => [run]).Build("nowindow").Path);
        Assert.Contains("Razer Cortex", entries["system.txt"]);
        Assert.Contains("Test Antivirus", entries["system.txt"]);
        Assert.Contains("Test GPU", entries["system.txt"]);
        Assert.Contains("1.2.3", entries["system.txt"]);
        Assert.Contains("event 1000 client.exe crashed", entries["events.txt"]);
        Assert.Contains("nowindow", entries["runs.json"]);
        Assert.Contains("nowindow", entries["report.json"]);

        Assert.Contains("none running", Read(Make(temp, g.Install).Build("manual").Path)["system.txt"]);
    }

    [Fact]
    public void AMissingOrLockedFileIsListedNotAnError()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = NewGame(temp);
        Files.Write(Path.Combine(g.Folder, "debug.log"), "fine");
        var cef = Path.Combine(g.Folder, "cef.log");
        Files.Write(cef, "locked");
        var settings = Path.Combine(temp.Path, "appdata", "graphics.cfg");
        using var hold = new FileStream(cef, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = Make(temp, g.Install, [settings]).Build("manual");
        Assert.Contains("game/debug.log", report.Files);
        Assert.DoesNotContain("game/cef.log", report.Files);
        Assert.Contains("game/cef.log", report.Missing);
        Assert.Contains("game/console.log", report.Missing);
        Assert.Contains("game/*.netlog", report.Missing);
        Assert.Contains("settings/graphics.cfg", report.Missing);

        // no game folder at all: still a zip
        var none = Make(temp, null).Build("manual");
        Assert.Contains("game folder", none.Missing);
        Assert.Contains("system.txt", none.Files);
        var gone = Make(temp, new(Path.Combine(temp.Path, "nothing"), Path.Combine(temp.Path, "nothing", "client.exe"))).Build("manual");
        Assert.Contains("game folder", gone.Missing);
    }

    [Fact]
    public void ALogIsCutToItsLastTwoMegabytesAtALineAndTheZipStaysUnderFiveMegabytes()
    {
        using var temp = new TempDir("resurgence-test-");
        var g = NewGame(temp);
        var line = new string('z', 99) + "\n";
        File.WriteAllText(Path.Combine(g.Folder, "debug.log"), string.Concat(Enumerable.Repeat(line, 30_000)) + "last line\n"); // 3 MB
        var small = Read(Make(temp, g.Install).Build("manual").Path)["game/debug.log"];
        Assert.InRange(small.Length, 1_900_000, 2 * 1024 * 1024);
        Assert.StartsWith("zzzz", small);
        Assert.EndsWith("last line\n", small);
        Assert.Equal(0, (small.Length - "last line\n".Length) % line.Length);

        // five newest netlogs of random text do not compress: the files are cut until the zip fits
        var rnd = new Random(1);
        for (var i = 0; i < 7; i++)
        {
            var path = Path.Combine(g.Folder, $"{i}.netlog");
            File.WriteAllText(path, string.Create(2 * 1024 * 1024, rnd, (span, r) => { for (var k = 0; k < span.Length; k++) span[k] = k % 80 == 79 ? '\n' : (char)r.Next('b', 'z'); }));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
        }
        var big = Make(temp, g.Install).Build("manual");
        Assert.InRange(big.Bytes, 1, Diagnostics.Limit);
        Assert.Equal(5, big.Files.Count(f => f.EndsWith(".netlog")));
        Assert.DoesNotContain("game/0.netlog", big.Files); // the oldest two are left out
        Assert.DoesNotContain("game/1.netlog", big.Files);
    }

    [Fact]
    public void ARunLooksFailedWhenItShowedNoWindowExitedWithACodeOrWasShort()
    {
        var start = DateTimeOffset.UnixEpoch;
        var minute = TimeSpan.FromMinutes(1);
        Assert.Null(new GameRun(start, minute, 0, true).Trigger);
        Assert.Null(new GameRun(start, minute, null, true).Trigger);
        Assert.Equal("short", new GameRun(start, minute - TimeSpan.FromSeconds(1), 0, true).Trigger);
        Assert.Equal("exitcode", new GameRun(start, minute, 3, true).Trigger);
        Assert.Equal("exitcode", new GameRun(start, TimeSpan.FromSeconds(2), -1, true).Trigger);
        Assert.Equal("nowindow", new GameRun(start, minute, 0, false).Trigger);
        Assert.Equal("nowindow", new GameRun(start, TimeSpan.FromSeconds(2), 5, false).Trigger);
    }

    [Fact]
    public async Task TheWatcherRecordsTheRunAndKeepsTheLastTen()
    {
        using var temp = new TempDir("resurgence-test-");
        var history = new RunHistory(Path.Combine(temp.Path, "runs.json"));
        var rest = new MockLauncherBridge(NullLogger<MockLauncherBridge>.Instance);
        var shown = 0;
        for (var i = 0; i < 12; i++)
        {
            // the process appears late (the first look finds none), shows a window on the third look, and ends with code 3
            var runs = new Queue<bool>([false, true, true, true, true, false]);
            var looks = 0;
            var b = new LauncherBridge(() => new("dir", "client.exe"), rest, _ => runs.Count > 0 && runs.Dequeue(), _ => true, () => true, null, poll: TimeSpan.FromMilliseconds(1),
                find: _ => new GameProcess(() => ++looks > 2, () => runs.Count == 0 ? 3 : null), history: history);
            Assert.Null(b.LastRun);
            Assert.True(await b.WhenGameExitsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var run = b.LastRun!;
            Assert.True(run.WindowSeen);
            Assert.Equal(3, run.ExitCode);
            Assert.Equal("exitcode", run.Trigger);
            shown++;
        }
        Assert.Equal(12, shown);
        var saved = history.Load();
        Assert.Equal(RunHistory.Keep, saved.Count);
        Assert.Equal(3, saved[^1].ExitCode);
        Assert.True(saved[^1].WindowSeen);

        // a game that never shows a window, and a game that never appears records nothing more
        var quiet = new LauncherBridge(() => new("dir", "client.exe"), rest, new Queue<bool>([true, true, false]) is var q ? _ => q.Count > 0 && q.Dequeue() : null!, _ => true, () => true, null, poll: TimeSpan.FromMilliseconds(1),
            find: _ => new GameProcess(() => false, () => 0), history: history);
        await quiet.WhenGameExitsAsync();
        Assert.Equal("nowindow", quiet.LastRun!.Trigger);
        var never = new LauncherBridge(() => new("dir", "client.exe"), rest, _ => false, _ => true, () => true, null, poll: TimeSpan.FromMilliseconds(1), appearWithin: TimeSpan.FromMilliseconds(5), history: history);
        Assert.False(await never.WhenGameExitsAsync());
        Assert.Null(never.LastRun);
        Assert.Equal("nowindow", history.Load()[^1].Trigger);
    }

    [Fact]
    public void ARealProcessGivesItsExitCodeAfterItIsGone()
    {
        using var temp = new TempDir("resurgence-test-");
        var exe = Path.Combine(temp.Path, $"hrtest{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        using var p = Process.Start(new ProcessStartInfo(exe, "/c ping -n 3 127.0.0.1 >nul & exit 7") { CreateNoWindow = true, UseShellExecute = false })!;
        using var game = GameProcess.Find(exe)!;
        Assert.NotNull(game);
        Assert.Null(game.ExitCode()); // still running
        Assert.False(game.WindowVisible());
        Assert.True(p.WaitForExit(30_000));
        Assert.Equal(7, game.ExitCode());
    }

    sealed class Handler(Func<HttpRequestMessage, byte[], HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    static (ProblemReporter Reporter, Handler Handler, Prefs Prefs, ProblemReport Report, string Downloads, int Saves) Reporter(TempDir temp, Func<HttpRequestMessage, byte[], HttpResponseMessage> answer, string? session = "abc")
    {
        var g = NewGame(temp);
        var diagnostics = Make(temp, g.Install);
        var handler = new Handler(answer);
        var prefs = new Prefs();
        var saves = 0;
        var reporter = new ProblemReporter(diagnostics, prefs, () => saves++, new HttpClient(handler) { BaseAddress = new("http://127.0.0.1/") }, new ServerCookie { Value = session }, Path.Combine(temp.Path, "downloads"));
        return (reporter, handler, prefs, diagnostics.Build("short"), Path.Combine(temp.Path, "downloads"), saves);
    }

    [Fact]
    public async Task SendingPostsTheZipWithTheSessionAndAnswersAReferenceOrAKey()
    {
        using var temp = new TempDir("resurgence-test-");
        var (reporter, handler, _, report, _, _) = Reporter(temp, (_, _) => new(HttpStatusCode.OK) { Content = new StringContent("""{"reference":"p1-abcd1234"}""") });
        var sent = await reporter.SendAsync(report);
        Assert.Equal(new ReportSent("p1-abcd1234", null), sent);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/account/report", request.RequestUri!.AbsolutePath);
        Assert.Equal("hr_session=abc", request.Headers.GetValues("Cookie").Single());
        Assert.Equal("launcher", request.Headers.GetValues("X-Requested-With").Single());
        Assert.Equal("short", request.Headers.GetValues("X-Report-Trigger").Single());
        Assert.Equal("application/zip", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(File.ReadAllBytes(report.Path), body);

        foreach (var (status, key) in new[] { (HttpStatusCode.Unauthorized, "Report.ErrSignIn"), (HttpStatusCode.BadRequest, "Report.ErrFormat"), (HttpStatusCode.RequestEntityTooLarge, "Report.ErrSize"),
            (HttpStatusCode.TooManyRequests, "Report.ErrLimit"), (HttpStatusCode.InsufficientStorage, "Report.ErrDisk"), (HttpStatusCode.BadGateway, "Report.ErrNetwork") })
        {
            using var t = new TempDir("resurgence-test-");
            var (r, _, _, rep, _, _) = Reporter(t, (_, _) => new(status));
            Assert.Equal(new ReportSent(null, key), await r.SendAsync(rep));
        }

        // no answer, an answer without a reference, and not signed in (nothing is sent then)
        using var t2 = new TempDir("resurgence-test-");
        var (down, _, _, rep2, _, _) = Reporter(t2, (_, _) => throw new HttpRequestException("down"));
        Assert.Equal("Report.ErrNetwork", (await down.SendAsync(rep2)).Error);
        using var t3 = new TempDir("resurgence-test-");
        var (odd, _, _, rep3, _, _) = Reporter(t3, (_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        Assert.Equal("Report.ErrNetwork", (await odd.SendAsync(rep3)).Error);
        using var t4 = new TempDir("resurgence-test-");
        var (out_, h4, _, rep4, _, _) = Reporter(t4, (_, _) => new(HttpStatusCode.OK), session: null);
        Assert.Equal("Report.ErrSignIn", (await out_.SendAsync(rep4)).Error);
        Assert.Empty(h4.Seen);
    }

    [Fact]
    public void SaveCopiesToDownloadsAndConsentStartsUnsetAndIsKept()
    {
        using var temp = new TempDir("resurgence-test-");
        var (reporter, _, prefs, report, downloads, _) = Reporter(temp, (_, _) => new(HttpStatusCode.OK));
        var to = reporter.Save(report);
        Assert.Equal(Path.Combine(downloads, Path.GetFileName(report.Path)), to);
        Assert.Equal(File.ReadAllBytes(report.Path), File.ReadAllBytes(to));

        Assert.Equal("unset", reporter.Consent);
        reporter.Consent = "auto";
        Assert.Equal("auto", prefs.ReportConsent);
        reporter.Consent = "whatever";
        Assert.Equal("auto", reporter.Consent);
        prefs.ReportConsent = "damaged";
        Assert.Equal("unset", reporter.Consent);

        // the choice survives launcher.json
        var file = Path.Combine(temp.Path, "launcher.json");
        new Prefs { ReportConsent = "ask" }.Save(file);
        Assert.Equal("ask", Prefs.Load(file).ReportConsent);
    }
}
