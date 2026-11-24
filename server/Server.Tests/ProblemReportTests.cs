using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The launcher's problem reports: POST /account/report, its limits, the 30 days, Delete my data and the admin API.</summary>
public class ProblemReportTests : StateTestBase, IAsyncLifetime
{
    static JsonObject Cfg(long minFree = 0) => new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test")))) { ["upload_min_free_bytes"] = minFree };
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Loopback server = null!;
    long player;
    string cookie = "";

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg(), state, tmp), tmp);
        (player, cookie) = SignedIn("player");
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    (long Id, string Cookie) SignedIn(string handle)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle);
        return (id, state.Store.CreateWebSession(id, TimeSpan.FromDays(1)));
    }

    /// <summary>A zip with one text file; `name` makes two zips differ.</summary>
    static byte[] Zip(string name = "system.txt")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry(name).Open()))
            w.Write("Windows, a graphics card");
        return ms.ToArray();
    }

    async Task<(Reply Reply, JsonNode Body)> Send(byte[] body, string trigger = "manual", string? session = null, bool own = true, Loopback? on = null)
    {
        List<(string, string)> headers = [("Content-Type", "application/zip"), ("X-Report-Trigger", trigger)];
        if (own) headers.Add(("X-Requested-With", "launcher"));
        if (session != "") headers.Add(("Cookie", "hr_session=" + (session ?? cookie)));
        var reply = await (on ?? server).SendAsync("POST", "web.test", "/account/report", headers, body);
        return (reply, JsonNode.Parse(reply.Body)!);
    }

    string Dir(long account) => Path.Combine(tmp, "reports", account.ToString());

    [Fact]
    public async Task TestASentReportIsStoredWithItsNoteAndAnswersItsReference()
    {
        var zip = Zip();
        var (reply, body) = await Send(zip, "short");
        var reference = (string)body["reference"]!;
        Assert.Equal(200, reply.Status);
        Assert.Matches($@"\Ap{player}-[0-9a-f]{{8}}\z", reference);
        Assert.Equal(["reference"], body.AsObject().Select(p => p.Key));
        Assert.Equal(zip, File.ReadAllBytes(Path.Combine(Dir(player), reference + ".zip")));
        var note = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(player), reference + ".json")))!;
        Assert.Equal(("short", (long)zip.Length), ((string)note["trigger"]!, (long)note["size"]!));
        Assert.InRange((long)note["ts"]!, Now - 10, Now + 10);
        Assert.Equal(reference, (await Send(zip, "short")).Body["reference"]!.ToString());  // the same zip is the same report
        Assert.Single(Directory.GetFiles(Dir(player), "*.zip"));
    }

    [Fact]
    public async Task TestOnlyAPlayerOnOurPageWithAKnownTriggerAndAZipIsTaken()
    {
        Assert.Equal(401, (await Send(Zip(), session: "")).Reply.Status);
        Assert.Equal(401, (await Send(Zip(), session: "nope")).Reply.Status);
        Assert.Equal(400, (await Send(Zip(), own: false)).Reply.Status);  // a form of another site cannot set the header
        Assert.Equal(400, (await Send(Zip(), trigger: "whenever")).Reply.Status);
        Assert.Equal(400, (await Send(Zip(), trigger: "")).Reply.Status);
        Assert.Equal(400, (await Send("not a zip at all"u8.ToArray())).Reply.Status);
        Assert.Equal(400, (await Send([])).Reply.Status);
        foreach (var trigger in ProblemReports.Triggers)
            Assert.Equal(200, (await Send(Zip(trigger), trigger)).Reply.Status);
    }

    [Fact]
    public async Task TestABodyOver6MiBIs413AndLeavesNothing()
    {
        Assert.Equal(413, (await Send(new byte[(6 << 20) + 1])).Reply.Status);
        Assert.False(Directory.Exists(Dir(player)));
    }

    [Fact]
    public async Task TestFiveReportsADayThenTheSixthIs429AndTheLimitIsPerAccount()
    {
        for (var n = 0; n < ProblemReports.PerDay; n++)
            Assert.Equal(200, (await Send(Zip($"{n}.txt"))).Reply.Status);
        Assert.Equal(429, (await Send(Zip("six.txt"))).Reply.Status);
        var (_, otherCookie) = SignedIn("other");
        Assert.Equal(200, (await Send(Zip("six.txt"), session: otherCookie)).Reply.Status);
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 0 };
        Assert.Null(reports.Begin(player, "manual", 10, Now + 86400 + 60));  // a day later the first are out of the window
    }

    [Fact]
    public void TestOneReportAtATimePerAccountUntilItEnds()
    {
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 0 };
        Assert.Null(reports.Begin(player, "manual", 10, Now));
        Assert.Equal(429, reports.Begin(player, "manual", 10, Now)!.Status);
        Assert.Null(reports.Begin(player + 1, "manual", 10, Now));  // another account is not held up
        reports.End(player);
        Assert.Null(reports.Begin(player, "manual", 10, Now));
    }

    [Fact]
    public async Task TestALowDiskIs507()
    {
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 100, FreeBytes = () => 105 };
        Assert.Equal(507, reports.Begin(player, "manual", 10, Now)!.Status);  // 105 - 10 < 100
        Assert.Null(reports.Begin(player, "manual", 5, Now));
        await using var low = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg(long.MaxValue), state, tmp), tmp);
        Assert.Equal(507, (await Send(Zip(), on: low)).Reply.Status);
        Assert.False(Directory.Exists(Dir(player)));
        // a refused start leaves the account free to send again
        Assert.Equal(507, (await Send(Zip(), on: low)).Reply.Status);
    }

    [Fact]
    public void TestAKeptReportSendsOneAlertWithAccountHandleReferenceTriggerAndSize()
    {
        var alerts = new List<(long, string, string, string, long)>();
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 0, Alert = (a, h, r, t, b) => alerts.Add((a, h, r, t, b)) };
        var zip = Zip();
        var (refused, reference) = reports.Save(player, "nowindow", zip, Now);
        Assert.Null(refused);
        Assert.Equal((player, "player", reference!, "nowindow", (long)zip.Length), Assert.Single(alerts));
        reports.Save(player, "nowindow", zip, Now);  // again: still one alert
        Assert.Single(alerts);
    }

    [Fact]
    public async Task TestReportsAreDeletedAfter30DaysAndWithTheAccount()
    {
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 0 };
        var (old, fresh) = (reports.Save(player, "manual", Zip("old.txt"), Now - 31 * 86400L).Reference!, reports.Save(player, "manual", Zip("fresh.txt"), Now - 29 * 86400L).Reference!);
        File.WriteAllBytes(Path.Combine(Dir(player), "p1-aaaaaaaa.zip"), Zip());  // no note: aged by its file time
        File.SetLastWriteTimeUtc(Path.Combine(Dir(player), "p1-aaaaaaaa.zip"), DateTime.UtcNow.AddDays(-31));
        Directory.CreateDirectory(Dir(player + 5));
        File.WriteAllBytes(Path.Combine(Dir(player + 5), "p9-bbbbbbbb.zip"), Zip());
        File.SetLastWriteTimeUtc(Path.Combine(Dir(player + 5), "p9-bbbbbbbb.zip"), DateTime.UtcNow.AddDays(-40));
        reports.Purge(Now);
        Assert.Equal([fresh + ".json", fresh + ".zip"], Directory.GetFiles(Dir(player)).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Dir(player + 5)));  // an emptied folder goes too
        Assert.Null(reports.PathOf(old));

        var reply = await server.SendAsync("POST", "web.test", "/account/delete",
            [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + cookie)], "confirm=delete"u8.ToArray());
        Assert.Equal(200, reply.Status);
        Assert.False(Directory.Exists(Dir(player)));
        Assert.Null(reports.PathOf(fresh));
    }

    [Fact]
    public void TestAReportOfAnAccountDeletedMeanwhileLeavesNoFile()
    {
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 0 };
        new Uploads(state, tmp).Delete(player);
        Assert.Equal(401, reports.Save(player, "manual", Zip(), Now).Refused!.Status);
        Assert.False(Directory.Exists(Dir(player)));
    }

    [Fact]
    public void TestOnlyAWellFormedReferenceNamesAFile()
    {
        var reports = new ProblemReports(state, tmp) { MinFreeBytes = 0 };
        var reference = reports.Save(player, "manual", Zip(), Now).Reference!;
        Assert.Equal(Path.Combine(Dir(player), reference + ".zip"), reports.PathOf(reference));
        foreach (var bad in new[] { "", "p1", $"p{player}-..\\..\\x", $"p{player}-ABCDEF12", $"../{reference}", $"{reference}.zip", $"p{player + 1}-{reference[^8..]}" })
            Assert.Null(reports.PathOf(bad));
    }

    [Fact]
    public async Task TestTheAdminApiListsAndDownloadsReportsAndSummarisesAnAccount()
    {
        var zip = Zip();
        var reference = (string)(await Send(zip, "exitcode")).Body["reference"]!;
        var ct = TestContext.Current.CancellationToken;
        var list = await server.Http.GetFromJsonAsync<JsonArray>("http://localhost/admin/reports", ct);
        var item = Assert.Single(list!)!;
        Assert.Equal((reference, player, "player", "exitcode", (long)zip.Length), ((string)item["reference"]!, (long)item["account"]!, (string)item["handle"]!, (string)item["trigger"]!, (long)item["size"]!));
        Assert.Equal(zip, await server.Http.GetByteArrayAsync($"http://localhost/admin/reports/{reference}", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await server.Http.GetAsync($"http://localhost/admin/reports/p{player}-ffffffff", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Http.GetAsync("http://localhost/admin/reports/..%2Fx", ct)).StatusCode);
        var game = await server.Http.GetAsync("http://api.test/admin/reports", ct);  // game hosts never reach the admin API
        Assert.Equal(HttpStatusCode.NotFound, game.StatusCode);

        var diagnostics = await server.Http.GetFromJsonAsync<JsonObject>($"http://localhost/admin/diagnostics?account={player}", ct);
        Assert.Equal(["crashes", "noDisconnect", "short", "event30"], diagnostics!.Select(p => p.Key));
        Assert.Equal(HttpStatusCode.NotFound, (await server.Http.GetAsync("http://localhost/admin/diagnostics?account=nobody", ct)).StatusCode);
    }

    [Fact]
    public async Task TestADeleteWaitsForAReportBeingReceived()
    {
        var live = server.Services.GetRequiredService<ProblemReports>();
        Assert.Null(live.Begin(player, "manual", 1, Now));
        Task<Reply> Delete() => server.SendAsync("POST", "web.test", "/account/delete",
            [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + cookie)], "confirm=delete"u8.ToArray());
        Assert.Equal(429, (await Delete()).Status);
        Assert.True(state.Store.AccountExists(player));
        live.End(player);
        Assert.Equal(200, (await Delete()).Status);
    }

    [Fact]
    public async Task TestARestoreThatGivesTheAccountAnotherUserIdTakesItsReportsAndCrashReportsAlong()
    {
        var reference = (string)(await Send(Zip())).Body["reference"]!;
        using (var con = state.Store.Open())
            con.Exec("INSERT INTO crash_reports (account_id, event_id, ts, kind, frames, message) VALUES (?, 'e0000001', ?, 'fatal', '[]', 'm')", player, Now);
        var up = await server.SendAsync("POST", "web.test", "/account/restore",
            [("Content-Type", "application/zip"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + cookie)], File.ReadAllBytes(TestZip.Own().Write(tmp, "mine.zip")));
        Assert.Equal(200, up.Status);
        long id;
        using (var con = state.Store.Open(player))
            id = (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
        Assert.Equal("restored", new Uploads(state, tmp).Approve(id).Kind);
        Assert.False(state.Store.AccountExists(player));  // the backup's user id is the account's now
        var reports = new ProblemReports(state, tmp);
        Assert.NotNull(reports.PathOf($"p1001-{reference[^8..]}"));
        Assert.Null(reports.PathOf(reference));
        Assert.False(Directory.Exists(Dir(player)));
        using var check = state.Store.Open();
        Assert.Equal(1L, (long)check.Scalar("SELECT COUNT(*) FROM crash_reports WHERE account_id = 1001")!);
    }
}
