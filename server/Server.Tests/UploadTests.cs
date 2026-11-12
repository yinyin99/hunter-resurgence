using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The community upload: /account/restore, /account/delete and the rules a backup zip is held to (a community server, signed in with a cookie).</summary>
public class UploadTests : StateTestBase, IAsyncLifetime
{
    const string Steam = "76561190000000000";  // the one TestZip.Own claims
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    Loopback server = null!;
    long player;
    string cookie = "";

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        var table = new HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Hit(2, "", 200, [], "{}"u8.ToArray());
        server = await Loopback.StartAsync(new Game(table, Cfg, state, tmp), tmp);
        (player, cookie) = SignedIn("player", Steam);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    /// <summary>An account the way the first sign-in makes it (the server's own range of ids) and its cookie.</summary>
    (long Id, string Cookie) SignedIn(string handle, string steam)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle, steam);
        return (id, state.Store.CreateWebSession(id, TimeSpan.FromDays(1)));
    }

    string Zip(TestZip zip, string name) => zip.Write(tmp, name);

    async Task<(Reply Reply, JsonNode Body)> Upload(byte[] zip, string? session = null, bool own = true, string path = "/account/restore")
    {
        (string, string)[] headers = [("Content-Type", "application/zip"), .. own ? [("X-Requested-With", "XMLHttpRequest")] : Array.Empty<(string, string)>(),
                                      ("Cookie", "hr_session=" + (session ?? cookie))];
        var reply = await server.SendAsync("POST", "web.test", path, headers, zip);
        return (reply, JsonNode.Parse(reply.Body)!);
    }

    Task<(Reply Reply, JsonNode Body)> Upload(TestZip zip, string name, string? session = null) => Upload(File.ReadAllBytes(Zip(zip, name)), session);

    static string Status(JsonNode body) => (string)body["status"]!;

    long Count(long account, string table)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a")!;
    }

    Uploads Operator => new(state, tmp);

    /// <summary>The id the operator sees for the account's newest kept zip.</summary>
    long Waiting(long account)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
    }

    /// <summary>Upload the zip as the account's player and expect it to be kept; returns its id.</summary>
    async Task<long> Keep(TestZip zip, string name, long account, string session)
    {
        var (reply, body) = await Upload(File.ReadAllBytes(Zip(zip, name)), session);
        Assert.Equal((200, "review"), (reply.Status, Status(body)));
        return Waiting(account);
    }

    sealed class Capture : HttpMessageHandler
    {
        public readonly List<(Uri? Url, string Body)> Posts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Posts) Posts.Add((request.RequestUri, request.Content!.ReadAsStringAsync(ct).Result));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public async Task TestAKeptZipSendsOneReviewAlertOnlyWhenTheWebhookIsSet()
    {
        var texts = new List<string>();
        var uploads = new Uploads(state, tmp) { Alert = texts.Add };
        var zip = Zip(TestZip.Own(), "alert.zip");
        var id = uploads.Start(player)!;
        Assert.Equal("received", (await uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zip)))).Kind);
        Assert.Equal("review", uploads.Finish(player, id).Kind);
        var text = Assert.Single(texts);
        Assert.Equal($"Backup waiting for review: id {Waiting(player)}, account {player}, backup user id 1001, handle sample. Run: review list", text);

        var http = new Capture();
        Alerts.Review(new JsonObject { ["review_webhook"] = "https://hooks.test/w/1" }, text, new HttpClient(http));
        Alerts.Review(new JsonObject(), text, new HttpClient(http));
        Alerts.Review(new JsonObject { ["review_webhook"] = "http://hooks.test/w/1" }, text, new HttpClient(http));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && http.Posts.Count == 0) await Task.Delay(20);
        await Task.Delay(100);
        var post = Assert.Single(http.Posts);
        Assert.Equal("https://hooks.test/w/1", post.Url!.ToString());
        Assert.Equal(text, (string)JsonNode.Parse(post.Body)!["content"]!);
    }

    [Fact]
    public async Task TestAZipWhoseSteamIdMatchesIsKeptAndNeverRestored()
    {
        var before = Count(player, "items");
        var (reply, body) = await Upload(TestZip.Own(), "mine.zip");
        Assert.Equal((200, "review"), (reply.Status, Status(body)));
        Assert.Contains("An operator will check it", (string)body["message"]!);
        Assert.Null(body["report"]);
        Assert.True(state.Store.AccountExists(player));
        Assert.False(state.Store.AccountExists(1001));
        Assert.Equal(before, Count(player, "items"));
        Eq("""{"em": 182.0, "gm": 33.0}""", state.Wallet(player));
        Assert.Equal(0L, Count(player, "restore_audit"));
        Assert.Null(state.Store.RestoredBy(1001));
        Assert.Equal(1L, Count(player, "uploads"));
        Assert.True(File.Exists(Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(Path.Combine(tmp, "mine.zip")) + ".zip")));
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        var listed = Assert.Single(Operator.List());
        Assert.Equal((player, 1001L, "sample", true), (listed.Account, listed.UserId, listed.Handle, listed.SteamMatch));
        var status = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + cookie)])).Body)!;
        Assert.Null(status["restoredUserId"]);
        Assert.Single(status["review"]!.AsArray());
    }

    [Fact]
    public async Task TestApproveRestoresOnceAndTheAccountAdoptsItsUserIdAndHandle()
    {
        var token = state.Store.CreateToken(player, TimeSpan.FromHours(1));
        using (var con = state.Store.Open(player))
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, '{}')");  // a starting row: it moves with the account
        var id = await Keep(TestZip.Own(), "mine.zip", player, cookie);
        var done = Operator.Approve(id);
        Assert.Equal((200, "restored"), (done.Status, done.Kind));
        Assert.Contains("stored: items 2, wallet 2", done.Report);
        Assert.Contains("The backup was restored: 2 items", done.Message);
        Assert.Contains("The name is now the one in the backup", done.Message);
        // the account is 1001 now, called sample; the Steam id, the token and the cookie stayed
        Assert.False(state.Store.AccountExists(player));
        Assert.Equal(1001L, state.Store.AccountBySteam(Steam));
        Assert.Equal("sample", state.Store.HandleOf(1001));
        Assert.Equal(1001L, state.Store.ResolveToken(token));
        Assert.Equal(1001L, state.Store.ResolveWebSession(cookie));
        Eq("1001", state.Store.AccountJson(1001)["user_id"]);
        Eq("""{"em": 12.5, "gm": 20000.25}""", state.Wallet(1001));
        Assert.Equal(1L, Count(1001, "pending"));
        Assert.Equal(0L, Count(player, "pending"));
        Assert.Equal(1L, Count(1001, "restore_audit"));
        Assert.Equal(0L, Count(1001, "uploads"));  // it no longer waits
        Assert.Equal(1001L, state.Store.RestoredBy(1001));
        Assert.True(File.Exists(Path.Combine(tmp, "restores", "1001", Restorer.Sha256(Path.Combine(tmp, "mine.zip")) + ".zip")));
        Assert.Empty(Directory.GetFiles(tmp, "state.db.bak-*"));  // a community server keeps the zip, not a copy of the whole database
        var status = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + cookie)])).Body)!;
        Eq("1001", status["restoredUserId"]);
        Assert.Equal("unknown", Operator.Approve(id).Kind);  // the row is gone: nothing to approve twice
    }

    [Fact]
    public async Task TestAUserIdIsRestoredOnceWhateverTheZipOrAccount()
    {
        var (other, otherCookie) = SignedIn("other", "76561190000000002");
        var first = await Keep(TestZip.Own(), "first.zip", player, cookie);
        var second = await Keep(TestZip.Own(wallet: "\"ems\":1.0,\"gms\":2.0"), "second.zip", player, cookie);  // the same player's newer backup
        var stolen = await Keep(TestZip.Own(steam: "76561190000000002"), "third.zip", other, otherCookie);
        Assert.Equal("restored", Operator.Approve(first).Kind);
        foreach (var id in new[] { second, stolen })
        {
            var refused = Operator.Approve(id);
            Assert.Equal((409, "already"), (refused.Status, refused.Kind));
            Assert.Contains("can be restored once", refused.Message);
        }
        Eq("""{"em": 12.5, "gm": 20000.25}""", state.Wallet(1001));  // nothing changed
        Assert.True(state.Store.AccountExists(other));
        Assert.Equal(0L, Count(other, "restore_audit"));
        // and now an upload of that user id is refused at once
        var again = await Upload(TestZip.Own(), "fourth.zip", cookie);
        Assert.Equal((409, "already"), (again.Reply.Status, Status(again.Body)));
    }

    [Fact]
    public async Task TestRejectDeletesTheKeptZipAndTheRow()
    {
        var id = await Keep(TestZip.Own(), "mine.zip", player, cookie);
        var kept = Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(Path.Combine(tmp, "mine.zip")) + ".zip");
        Assert.True(File.Exists(kept));
        Assert.True(Operator.Reject(id));
        Assert.False(File.Exists(kept));
        Assert.Equal(0L, Count(player, "uploads"));
        Assert.False(Operator.Reject(id));
        Assert.Empty(Operator.List());
        Assert.Null(state.Store.RestoredBy(1001));
    }

    async Task<JsonNode> RestoreStatusOf(string session) =>
        JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + session)])).Body)!;

    /// <summary>The account's inbox from the server's notice sender, oldest first.</summary>
    List<string> Notices(long account)
    {
        using var con = state.Store.Open(account);
        return con.Rows("SELECT content FROM messages WHERE account_id = $a AND sender = ? ORDER BY id", State.SYSTEM_SENDER).Select(r => (string)r[0]).ToList();
    }

    [Fact]
    public async Task TestARefusalAfterPlayIsTheZipsNoteAndTheAccountIsToldOnce()
    {
        var id = await Keep(TestZip.Own(), "played.zip", player, cookie);
        Assert.Equal("waiting for the operator", Assert.Single((await RestoreStatusOf(cookie))["review"]!.AsArray())!["note"]!.ToString());
        state.ConnectReply(player);  // a hunt was opened: the account has played
        var refused = Operator.Approve(id);
        Assert.Equal((409, "refused"), (refused.Status, refused.Kind));
        var kept = Assert.Single((await RestoreStatusOf(cookie))["review"]!.AsArray())!;
        Assert.Equal(refused.Message, (string)kept["note"]!);
        Assert.True((long)kept["ts"]! > 0);
        Assert.Equal(refused.Message, Operator.List().Single().Note);
        Assert.Equal([$"Your backup was not restored: {refused.Message}"], Notices(player));
        Assert.Equal($"Your backup was not restored: {refused.Message}", (string)JsonNode.Parse(state.ConversationsReply(player))!.AsArray()[0]!["preview"]!);  // the game's inbox lists it
        Operator.Approve(id);  // the same refusal again: no second notice
        Assert.Single(Notices(player));
        Assert.Empty((await RestoreStatusOf(cookie))["decisions"]!.AsArray());
    }

    [Fact]
    public async Task TestAnApproveIsToldToThePlayerWithTheUserIdAndTheFinalName()
    {
        var id = await Keep(TestZip.Own(), "mine.zip", player, cookie);
        Assert.Equal("restored", Operator.Approve(id).Kind);
        var status = await RestoreStatusOf(cookie);
        Assert.Empty(status["review"]!.AsArray());
        var done = Assert.Single(status["decisions"]!.AsArray())!;
        Assert.Equal(("restored", 1001L, "sample", false, ""), ((string)done["kind"]!, (long)done["userId"]!, (string)done["handle"]!, (bool)done["handleKept"]!, (string)done["reason"]!));
        Assert.True((long)done["ts"]! > 0);
        // the inbox followed the account to its new user id
        var notice = Assert.Single(Notices(1001));
        Assert.Contains("The backup was restored", notice);
        Assert.Contains("Your user id is 1001 and your name is sample.", notice);
        Assert.Empty(Notices(player));
        Assert.Equal(1L, Count(1001, "upload_results"));
        Assert.Equal(0L, Count(player, "upload_results"));
    }

    [Fact]
    public async Task TestAHandleThatCannotBeAdoptedIsToldAsKept()
    {
        state.CreateAccount(2, "sample");  // the backup's name belongs to another account
        var id = await Keep(TestZip.Own(), "mine.zip", player, cookie);
        Assert.Equal("restored", Operator.Approve(id).Kind);
        var done = Assert.Single((await RestoreStatusOf(cookie))["decisions"]!.AsArray())!;
        Assert.Equal(("player", true), ((string)done["handle"]!, (bool)done["handleKept"]!));
    }

    [Fact]
    public async Task TestABackupNameTheAccountAlreadyHasIsToldAsTheBackupsNotAsKept()
    {
        const string steam = "76561190000000005";
        var (same, sameCookie) = SignedIn("sample", steam);  // the account is already called what the backup says
        var id = await Keep(TestZip.Own(steam: steam), "same.zip", same, sameCookie);
        Assert.Equal("restored", Operator.Approve(id).Kind);
        var done = Assert.Single((await RestoreStatusOf(sameCookie))["decisions"]!.AsArray())!;
        Assert.Equal(("sample", false), ((string)done["handle"]!, (bool)done["handleKept"]!));
    }

    [Fact]
    public async Task TestAnUnreadableKeptZipGivesThePlayerPlainWordsNotAPath()
    {
        var id = await Keep(TestZip.Own(), "mine.zip", player, cookie);
        File.WriteAllBytes(Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(Path.Combine(tmp, "mine.zip")) + ".zip"), [1, 2, 3]);
        var refused = Operator.Approve(id);
        Assert.Equal((400, "invalid"), (refused.Status, refused.Kind));
        var note = (string)Assert.Single((await RestoreStatusOf(cookie))["review"]!.AsArray())!["note"]!;
        Assert.Equal("The kept zip cannot be read, so it was not restored.", note);
        Assert.Equal(["Your backup was not restored: " + note], Notices(player));
    }

    [Fact]
    public async Task TestARejectKeepsItsReasonForThePlayerAndSaysSoInTheGame()
    {
        var first = await Keep(TestZip.Own(), "first.zip", player, cookie);
        var second = await Keep(TestZip.Own(wallet: "\"ems\":1.0,\"gms\":2.0"), "second.zip", player, cookie);
        Assert.True(Operator.Reject(first, "  This is not your backup.  "));
        Assert.True(Operator.Reject(second));
        var status = await RestoreStatusOf(cookie);
        Assert.Empty(status["review"]!.AsArray());
        var decisions = status["decisions"]!.AsArray();
        Assert.Equal(["rejected", "rejected"], decisions.Select(d => (string)d!["kind"]!));
        Assert.Equal(["This is not your backup.", ""], decisions.Select(d => (string)d!["reason"]!));
        Assert.Equal([1001L, 1001L], decisions.Select(d => (long)d!["userId"]!));
        Assert.Equal(["Your backup was rejected by the operator: This is not your backup.", "Your backup was rejected by the operator."], Notices(player));
        Assert.True(state.Store.AccountExists(player));
        Assert.Null(state.Store.RestoredBy(1001));
    }

    [Fact]
    public async Task TestTheAlreadyRefusalPromisesNoReset()
    {
        var first = await Keep(TestZip.Own(), "first.zip", player, cookie);
        var second = await Keep(TestZip.Own(wallet: "\"ems\":1.0,\"gms\":2.0"), "second.zip", player, cookie);
        Assert.Equal("restored", Operator.Approve(first).Kind);
        var refused = Operator.Approve(second);
        Assert.Equal("already", refused.Kind);
        Assert.DoesNotContain("reset", refused.Message);
        Assert.DoesNotContain("ask the operator", refused.Message, StringComparison.OrdinalIgnoreCase);
        var again = await Upload(TestZip.Own(wallet: "\"ems\":3.0,\"gms\":4.0"), "third.zip");
        Assert.DoesNotContain("reset", (string)again.Body["message"]!);
    }

    [Fact]
    public async Task TestAZipWithAnotherOrNoSteamIdIsKeptForReviewAndNothingIsRestored()
    {
        var before = Count(player, "items");
        Eq("""{"em": 182.0, "gm": 33.0}""", state.Wallet(player));
        await Keep(TestZip.Own(steam: "76561190000000002"), "mismatch.zip", player, cookie);
        await Keep(TestZip.Own(wallet: "\"ems\":1.0,\"gms\":1.0", steam: null), "missing.zip", player, cookie);
        Assert.Equal("review", Status((await Upload(TestZip.Public(), "public.zip")).Body));
        // nothing restored: the account, its rows and the user id are as they were; the zips are kept and listed, the match is information
        Assert.True(state.Store.AccountExists(player));
        Assert.Equal(before, Count(player, "items"));
        Eq("""{"em": 182.0, "gm": 33.0}""", state.Wallet(player));
        Assert.Null(state.Store.RestoredBy(1001));
        Assert.Equal(3L, Count(player, "uploads"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(tmp, "restores", player.ToString())).Length);
        Assert.Equal([false, false, false], Operator.List().Select(p => p.SteamMatch));
        var status = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + cookie)])).Body)!;
        Assert.Null(status["restoredUserId"]);
        Assert.Equal(3, status["review"]!.AsArray().Count);
        // the same bytes again are not kept twice (a rebuilt zip would carry a new timestamp, so another hash), and a fourth different one is more than may wait
        Assert.Equal("review", Status((await Upload(File.ReadAllBytes(Path.Combine(tmp, "mismatch.zip")))).Body));
        Assert.Equal(3L, Count(player, "uploads"));
        var fourth = await Upload(TestZip.Own(wallet: "\"ems\":9.0,\"gms\":9.0", steam: "76561190000000003"), "fourth.zip");
        Assert.Equal((429, "busy"), (fourth.Reply.Status, Status(fourth.Body)));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(tmp, "restores", player.ToString())).Length);
    }

    [Fact]
    public async Task TestTheUserIdChangesOnlyBeforeTheAccountHasPlayed()
    {
        var id = await Keep(TestZip.Own(), "played.zip", player, cookie);
        state.ConnectReply(player);  // a hunt was opened: the account has played
        Assert.True(state.Store.Played(player));
        var refused = Operator.Approve(id);
        Assert.Equal((409, "refused"), (refused.Status, refused.Kind));
        Assert.Contains("already played", refused.Message);
        Assert.True(state.Store.AccountExists(player));
        Assert.False(state.Store.AccountExists(1001));
        Assert.Null(state.Store.RestoredBy(1001));
        Assert.Equal(1L, Count(player, "uploads"));  // still kept: the operator rejects it
        // one id from the server's range is not a backup's
        var (fresh, freshCookie) = SignedIn("fresh", "76561190000000009");
        Assert.False(state.Store.Played(fresh));
        var high = await Keep(TestZip.Own(uid: "2000000500", steam: "76561190000000009"), "high.zip", fresh, freshCookie);
        var notAccepted = Operator.Approve(high);
        Assert.Equal("refused", notAccepted.Kind);
        Assert.Contains("user id this server does not accept", notAccepted.Message);
    }

    [Fact]
    public async Task TestAUserIdAnotherAccountHoldsIsRefused()
    {
        state.CreateAccount(1001, "holder");
        var (reply, body) = await Upload(TestZip.Own(), "held.zip");
        Assert.Equal((409, "taken"), (reply.Status, Status(body)));  // refused at once
        Assert.Equal(0L, Count(player, "uploads"));
        // an id taken after the upload is refused on approve
        state.Store.DeleteAccount(1001);
        var id = await Keep(TestZip.Own(), "held2.zip", player, cookie);
        state.CreateAccount(1001, "holder");
        var refused = Operator.Approve(id);
        Assert.Equal((409, "taken"), (refused.Status, refused.Kind));
        Assert.True(state.Store.AccountExists(player));
        Assert.Null(state.Store.RestoredBy(1001));
        Assert.Equal("holder", state.Store.HandleOf(1001));
    }

    [Fact]
    public async Task TestAHandleAnotherPlayerHasIsNotTaken()
    {
        state.CreateAccount(5, "Sample");  // handles do not differ by case
        var done = Operator.Approve(await Keep(TestZip.Own(), "handle.zip", player, cookie));
        Assert.Equal((200, "restored"), (done.Status, done.Kind));
        Assert.Contains("The name was not changed", done.Message);
        Assert.Equal("player", state.Store.HandleOf(1001));
        Assert.Equal("Sample", state.Store.HandleOf(5));
    }

    [Fact]
    public async Task TestTheOperatorCanKeepTheAccountsOwnHandle()
    {
        var done = Operator.Approve(await Keep(TestZip.Own(), "keep.zip", player, cookie), keepHandle: true);
        Assert.Equal((200, "restored"), (done.Status, done.Kind));
        Assert.Contains("you keep the one you chose here", done.Message);
        Assert.Equal("player", state.Store.HandleOf(1001));  // the user id is the backup's, the name the account's
        var decision = Assert.Single((await RestoreStatusOf(cookie))["decisions"]!.AsArray())!;
        Assert.Equal(("player", true), ((string)decision["handle"]!, (bool)decision["handleKept"]!));
    }

    [Fact]
    public async Task TestUploadLimitsAndAFileThatIsNotABackup()
    {
        var notZip = await Upload("not a zip"u8.ToArray());
        Assert.Equal((400, "invalid"), (notZip.Reply.Status, Status(notZip.Body)));
        // a zip bomb: a manifest that inflates over 100:1 is refused by the reader before it is parsed
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var s = z.CreateEntry("manifest.json", CompressionLevel.SmallestSize).Open();
            s.Write(new byte[4 << 20]);
        }
        var bomb = await Upload(ms.ToArray());
        Assert.Equal((400, "invalid"), (bomb.Reply.Status, Status(bomb.Body)));
        Assert.Contains("compressed more than", (string)bomb.Body["message"]!);
        // Expect: 100-continue, so the client stops when the server refuses (a reset mid-send fails on Linux)
        var big = await server.SendAsync("POST", "web.test", "/account/restore",
            [("Content-Type", "application/zip"), ("X-Requested-With", "XMLHttpRequest"), ("Expect", "100-continue"), ("Cookie", "hr_session=" + cookie)],
            new byte[Uploads.MaxPartBytes + 1]);
        Assert.Equal((413, "too_large"), (big.Status, Status(JsonNode.Parse(big.Body)!)));
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        Assert.True(state.Store.AccountExists(player));
        Assert.Equal(0L, Count(player, "uploads"));
    }

    [Fact]
    public void TestOneUploadAtATimePerAccountAndFourOnTheServer()
    {
        var uploads = new Uploads(state, tmp);
        var first = uploads.Begin(1);
        Assert.NotNull(first);
        Assert.Null(uploads.Begin(1));  // the same account has one running
        var more = new[] { uploads.Begin(2), uploads.Begin(3), uploads.Begin(4) };
        Assert.All(more, Assert.NotNull);
        Assert.Null(uploads.Begin(5));  // the server holds four at once
        Assert.True(uploads.Busy(1));
        first.Dispose();
        Assert.False(uploads.Busy(1));
        using var again = uploads.Begin(1);
        Assert.NotNull(again);
        foreach (var m in more) m!.Dispose();
    }

    Task<(Reply Reply, JsonNode Body)> Post(string path, byte[]? body = null, string? session = null) => Upload(body ?? [], session, path: path);

    /// <summary>Start an upload in parts; its id.</summary>
    async Task<string> StartParts()
    {
        var (reply, body) = await Post("/account/restore/start");
        Assert.Equal((200, "started", Uploads.MaxPartBytes), (reply.Status, Status(body), (int)body["partBytes"]!));
        return (string)body["id"]!;
    }

    [Fact]
    public async Task TestAZipSentInPartsIsJoinedOnDiskAndKept()
    {
        var zip = File.ReadAllBytes(Zip(TestZip.Own(), "parts.zip"));
        var id = await StartParts();
        var cuts = new[] { 0, zip.Length / 3, 2 * zip.Length / 3, zip.Length };
        for (var i = 0; i < 3; i++)
        {
            var (part, body) = await Post($"/account/restore/{id}/{i}", zip[cuts[i]..cuts[i + 1]]);
            Assert.Equal((200, "received"), (part.Status, Status(body)));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        var (reply, done) = await Post($"/account/restore/{id}/finish");
        Assert.Equal((200, "review"), (reply.Status, Status(done)));
        Assert.True(File.Exists(Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(Path.Combine(tmp, "parts.zip")) + ".zip")));
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        var (gone, _) = await Post($"/account/restore/{id}/finish");  // the finish ended the upload
        Assert.Equal(404, gone.Status);
        Assert.NotNull(await StartParts());  // and freed the account's slot
    }

    [Fact]
    public async Task TestAPartOutOfOrderIsRefusedAndTheLastPartMayBeSentAgain()
    {
        var zip = File.ReadAllBytes(Zip(TestZip.Own(), "order.zip"));
        var half = zip.Length / 2;
        var id = await StartParts();
        var (early, body) = await Post($"/account/restore/{id}/1", zip[half..]);
        Assert.Equal((409, "invalid"), (early.Status, Status(body)));
        Assert.Contains("expects part 0", (string)body["message"]!);
        Assert.Equal(200, (await Post($"/account/restore/{id}/0", zip[..10])).Reply.Status);
        Assert.Equal(200, (await Post($"/account/restore/{id}/0", zip[..half])).Reply.Status);  // sent again: it replaces itself
        Assert.Equal(409, (await Post($"/account/restore/{id}/2", zip[half..])).Reply.Status);
        Assert.Equal(200, (await Post($"/account/restore/{id}/1", zip[half..])).Reply.Status);
        var (other, _) = await Post($"/account/restore/{id}/finish", session: SignedIn("other", "76561190000000001").Cookie);
        Assert.Equal(404, other.Status);  // another account's upload is not open for it
        var (reply, done) = await Post($"/account/restore/{id}/finish");
        Assert.Equal((200, "review"), (reply.Status, Status(done)));
        Assert.True(File.Exists(Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(Path.Combine(tmp, "order.zip")) + ".zip")));
    }

    [Fact]
    public async Task TestAZipOverTheCapInPartsEndsTheUpload()
    {
        var uploads = new Uploads(state, tmp) { MaxZip = 10 };
        var id = uploads.Start(player)!;
        Assert.Equal("received", (await uploads.Part(player, id, 0, new MemoryStream(new byte[6]))).Kind);
        var over = await uploads.Part(player, id, 1, new MemoryStream(new byte[6]));
        Assert.Equal((413, "too_large"), (over.Status, over.Kind));
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        Assert.Equal(404, (await uploads.Part(player, id, 1, new MemoryStream(new byte[1]))).Status);
        Assert.NotNull(uploads.Start(player));  // the slot is free again
    }

    [Fact]
    public async Task TestAnAbandonedUploadIsDeletedAndFreesItsSlot()
    {
        var now = Py.Now();
        state.Clock = () => now;
        var uploads = new Uploads(state, tmp);
        var held = new[] { uploads.Begin(2), uploads.Begin(3), uploads.Begin(4) };  // three of the server's four slots
        var id = uploads.Start(player)!;
        Assert.Equal("received", (await uploads.Part(player, id, 0, new MemoryStream(new byte[6]))).Kind);
        now += Uploads.AbandonedSeconds - 1;
        Assert.Null(uploads.Start(5));  // the server's slots are all taken
        Assert.Single(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        now += 2;
        var other = uploads.Start(5);  // the abandoned upload was deleted and freed its slot
        Assert.NotNull(other);
        Assert.Equal(404, (await uploads.Part(player, id, 1, new MemoryStream(new byte[1]))).Status);
        Assert.Equal(other + ".zip", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")))));
        foreach (var h in held) h!.Dispose();
    }

    [Fact]
    public async Task TestAStartOverDropsTheAccountsUnfinishedUpload()
    {
        var uploads = new Uploads(state, tmp);
        var first = uploads.Start(player)!;
        Assert.Equal("received", (await uploads.Part(player, first, 0, new MemoryStream(new byte[6]))).Kind);
        var second = uploads.Start(player);
        Assert.NotNull(second);
        Assert.Equal(404, (await uploads.Part(player, first, 1, new MemoryStream(new byte[1]))).Status);
        Assert.Equal(second + ".zip", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")))));
        Assert.False(uploads.Busy(player));  // waiting for a part is not running: a delete drops it
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
    }

    /// <summary>An upload whose body stays open, so it holds the account's slot. The write is enough body for the request to reach the server; it is not
    /// awaited here, since an upload refused as busy never reads it and the write would wait for good.</summary>
    (Pipe Body, Task Write, Task<HttpResponseMessage> Sending) OpenUpload()
    {
        var pipe = new Pipe();
        var first = new HttpRequestMessage(HttpMethod.Post, "http://web.test/account/restore") { Content = new StreamContent(pipe.Reader.AsStream()) };
        first.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        first.Headers.Add("X-Requested-With", "XMLHttpRequest");
        first.Headers.Add("Cookie", "hr_session=" + cookie);
        var sending = server.Http.SendAsync(first);
        return (pipe, pipe.Writer.WriteAsync(new byte[1 << 17], TestContext.Current.CancellationToken).AsTask(), sending);
    }

    /// <summary>An open upload once the server's own <see cref="Uploads"/> says it holds the account's slot. Nothing else asks for the slot meanwhile:
    /// a probe upload would take it for a moment, and an open upload arriving then is refused while its client keeps waiting to send the body.</summary>
    async Task<(Pipe Body, Task Write, Task<HttpResponseMessage> Sending)> HoldTheSlot()
    {
        var uploads = server.Services.GetRequiredService<Uploads>();
        var (body, write, sending) = OpenUpload();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!uploads.Busy(player))
        {
            Assert.False(sending.IsCompleted, "the open upload ended before it held the slot");
            Assert.True(DateTime.UtcNow < deadline, "the open upload never held the slot");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return (body, write, sending);
    }

    [Fact]
    public async Task TestASecondUploadWhileOneIsStillArrivingGetsBusy()
    {
        var (body, write, sending) = await HoldTheSlot();
        var second = await Upload("x"u8.ToArray());
        Assert.Equal((429, "busy"), (second.Reply.Status, Status(second.Body)));
        await write;
        await body.Writer.CompleteAsync();
        using var done = await sending;  // the first one ends (its bytes are no zip)
        Assert.Equal(400, (int)done.StatusCode);
        // the slot is free again
        Assert.Equal("invalid", Status((await Upload("x"u8.ToArray())).Body));
    }

    [Fact]
    public async Task TestDeletingIsRefusedWhileAnUploadIsBusy()
    {
        (string, string)[] headers = [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + cookie)];
        var (body, write, sending) = await HoldTheSlot();  // a delete before the upload holds the slot would succeed
        var refused = await server.SendAsync("POST", "web.test", "/account/delete", headers, "confirm=delete"u8.ToArray());
        Assert.Equal(429, refused.Status);
        Assert.Equal("busy", Status(JsonNode.Parse(refused.Body)!));
        Assert.True(state.Store.AccountExists(player));
        await write;
        await body.Writer.CompleteAsync();
        using var done = await sending;
        Assert.Equal(200, (await server.SendAsync("POST", "web.test", "/account/delete", headers, "confirm=delete"u8.ToArray())).Status);
        Assert.False(state.Store.AccountExists(player));
    }

    [Fact]
    public async Task TestRequestsNeedASessionAndAPageOfOurs()
    {
        Assert.Equal(401, (await Upload([1], session: "0000")).Reply.Status);
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/restore", [])).Status);
        Assert.Equal(400, (await Upload(File.ReadAllBytes(Zip(TestZip.Own(), "csrf.zip")), own: false)).Reply.Status);  // no X-Requested-With or Origin
        var form = new[] { ("Content-Type", "application/x-www-form-urlencoded"), ("Cookie", "hr_session=" + cookie) };
        Assert.Equal(400, (await server.SendAsync("POST", "web.test", "/account/delete", [.. form, ("X-Requested-With", "x")], "confirm=no"u8.ToArray())).Status);
        Assert.Equal(400, (await server.SendAsync("POST", "web.test", "/account/delete", form, "confirm=delete"u8.ToArray())).Status);
        Assert.Equal(401, (await server.SendAsync("POST", "web.test", "/account/delete", [("X-Requested-With", "x")], "confirm=delete"u8.ToArray())).Status);
        Assert.True(state.Store.AccountExists(player));
        // a same-origin Origin is as good as the header
        var origin = await server.SendAsync("POST", "web.test", "/account/restore", [("Origin", "https://web.test"), ("Cookie", "hr_session=" + cookie)], [1]);
        Assert.Equal(400, origin.Status);
        Assert.Equal("invalid", Status(JsonNode.Parse(origin.Body)!));
        // another site's origin is not
        var foreign = await server.SendAsync("POST", "web.test", "/account/restore", [("Origin", "https://elsewhere.test"), ("Cookie", "hr_session=" + cookie)], [1]);
        Assert.Equal("This request is not from a page of this server.", (string)JsonNode.Parse(foreign.Body)!["message"]!);
    }

    [Fact]
    public async Task TestASecondApproveThatAdoptsAnotherUserIdMovesThePhotosAndDeleteLeavesNoFolder()
    {
        Assert.Equal("restored", Operator.Approve(await Keep(TestZip.Own().Pictures(), "a.zip", player, cookie)).Kind);  // the account is 1001, its pictures in photos/1001
        var first = Path.Combine(tmp, "photos", "1001");
        Assert.Equal(5, Directory.GetFiles(first).Length);
        File.WriteAllText(Path.Combine(first, "9-2.jpg"), "mine");  // a photo taken here
        using (var con = state.Store.Open(1001))
            con.Exec("INSERT INTO photos (account_id, photo_type, file) VALUES ($a, 2, '9-2.jpg')");
        Assert.Equal("restored", Operator.Approve(await Keep(TestZip.Own(uid: "1002").Pictures(), "b.zip", 1001, cookie)).Kind);
        var moved = Path.Combine(tmp, "photos", "1002");
        Assert.False(Directory.Exists(first));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(moved, "9-2.jpg")));
        Assert.Equal(6, Directory.GetFiles(moved).Length);  // the second backup's five pictures (the first's were replaced) and the photo taken here
        Assert.Equal(1L, (long)state.Store.Open(1002).Scalar("SELECT COUNT(*) FROM photos WHERE account_id = $a AND file = '9-2.jpg'")!);
        Assert.NotNull(state.PhotoFile("/photos/1002/9-2.jpg"));
        Assert.Null(state.PhotoFile("/photos/1001/9-2.jpg"));
        Operator.Delete(1002);
        Assert.False(Directory.Exists(moved));
        Assert.False(Directory.Exists(first));
    }

    [Fact]
    public void TestMovingPhotosMergesAndTheAccountsOwnPhotoWinsAClash()
    {
        var from = Path.Combine(tmp, "photos", "5");
        var to = Path.Combine(tmp, "photos", "6");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "1-2.jpg"), "own");
        File.WriteAllText(Path.Combine(to, "1-2.jpg"), "other");
        File.WriteAllText(Path.Combine(to, "2-2.jpg"), "kept");
        state.MovePhotos(5, 6);
        Assert.False(Directory.Exists(from));
        Assert.Equal("own", File.ReadAllText(Path.Combine(to, "1-2.jpg")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(to, "2-2.jpg")));
    }

    [Fact]
    public async Task TestDeletingRemovesTheAccountAndEverythingOfIt()
    {
        Assert.Equal("restored", Operator.Approve(await Keep(TestZip.Own().Pictures(), "mine.zip", player, cookie)).Kind);
        Assert.Equal(5, Directory.GetFiles(Path.Combine(tmp, "photos", "1001")).Length);  // the pictures went to the adopted user id's folder
        var (other, otherCookie) = SignedIn("other", "76561190000000002");
        var token = state.Store.CreateToken(1001, TimeSpan.FromHours(1));
        Directory.CreateDirectory(Path.Combine(tmp, "photos", "1001"));
        File.WriteAllText(Path.Combine(tmp, "photos", "1001", "1-2.jpg"), "x");
        state.ConnectReply(1001);  // a hunt, so history rows exist too
        var otherBefore = (Count(other, "items"), Count(other, "wallet"));
        var reply = await server.SendAsync("POST", "web.test", "/account/delete",
            [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + cookie)], "confirm=delete"u8.ToArray());
        Assert.Equal(200, reply.Status);
        Assert.Contains(("Set-Cookie", "hr_session=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax"), reply.Headers);
        Assert.False(state.Store.AccountExists(1001));
        Assert.Null(state.Store.AccountBySteam(Steam));
        Assert.Null(state.Store.AccountByHandle("sample"));
        Assert.Null(state.Store.ResolveToken(token));
        Assert.Null(state.Store.ResolveWebSession(cookie));
        Assert.Null(state.Store.RestoredBy(1001));
        foreach (var table in Store.PlayerTables)
            Assert.Equal(0L, Count(1001, table));
        using (var con = state.Store.Open())
            foreach (var table in new[] { "tokens", "web_sessions", "restored_ids" })
                Assert.Equal(0L, (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = 1001")!);
        Assert.False(Directory.Exists(Path.Combine(tmp, "restores", "1001")));
        Assert.False(Directory.Exists(Path.Combine(tmp, "photos", "1001")));
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + cookie)])).Status);
        // another player is untouched
        Assert.True(state.Store.AccountExists(other));
        Assert.Equal(otherBefore, (Count(other, "items"), Count(other, "wallet")));
        Assert.Equal(other, state.Store.ResolveWebSession(otherCookie));
    }

    [Fact]
    public async Task TestARestoredPageAndFileAreServedWithNoScriptAndNoSniffing()
    {
        Assert.Equal("restored", Operator.Approve(await Keep(TestZip.Own().Pictures(), "mine.zip", player, cookie)).Kind);
        var page = await server.SendAsync("GET", "web.test", "/user/sample/score/?id=777", []);
        Assert.Equal(200, page.Status);
        Assert.Equal("<div>saved sheet</div>", Encoding.UTF8.GetString(page.Body));
        Assert.Contains(("Content-Security-Policy", "sandbox"), page.Headers);
        Assert.Equal("miss", (await server.SendAsync("GET", "web.test", "/user/sample/score/?id=778", [])).Source);  // a kill with no saved page
        foreach (var host in new[] { "web.test", "gameserver.test" })
        {
            var file = await server.SendAsync("GET", host, "/photos/1001/kills_score_777.html", []);  // any stored file is served as a picture, never as a page
            Assert.Equal(200, file.Status);
            Assert.Contains(("Content-Type", "image/jpeg"), file.Headers);
            Assert.Contains(("X-Content-Type-Options", "nosniff"), file.Headers);
            Assert.Contains(("Content-Security-Policy", "sandbox"), file.Headers);
        }
    }

    [Fact]
    public async Task TestACommunityRestoreOfADeletedAccountsUserIdIsPossibleAgain()
    {
        Assert.Equal("restored", Operator.Approve(await Keep(TestZip.Own(), "mine.zip", player, cookie)).Kind);
        state.Store.DeleteAccount(1001);
        var (again, againCookie) = SignedIn("again", Steam);  // the same Steam id signs in anew and uploads the same zip
        var id = await Keep(TestZip.Own(), "mine2.zip", again, againCookie);
        Assert.Equal("restored", Operator.Approve(id).Kind);
    }

    [Fact]
    public async Task TestOfflineHasNoAccountPages()
    {
        await using var local = await Loopback.StartAsync(new Game(new HarTable(), Cfg, new State(Path.Combine(tmp, "local.db"), Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES), tmp), tmp);
        var reply = await local.SendAsync("POST", "web.test", "/account/restore", [("X-Requested-With", "x")], [1]);
        Assert.Equal("miss", reply.Source);
    }

    [Fact]
    public void TestTheSchemaOfVersion2IsMigratedToTheUploadTables()
    {
        var file = Path.Combine(tmp, "v2.db");
        var old = new Store(file);
        using (var con = old.Open())
        {
            con.Exec("DROP TABLE uploads");
            con.Exec("DROP TABLE restored_ids");
            con.Exec("DROP TABLE upload_results");
            con.Exec("ALTER TABLE expeditions DROP COLUMN stats");
            con.Exec("DROP TABLE friends");
            con.Exec("ALTER TABLE harvests DROP COLUMN hidden");
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 2");
        }
        var migrated = new Store(file);
        migrated.CreateAccount(5, "five", new JsonObject());
        Assert.Null(migrated.RestoredBy(1));
        using var check = migrated.Open(5);
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM uploads WHERE account_id = $a"));
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM upload_results WHERE account_id = $a"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }
}
