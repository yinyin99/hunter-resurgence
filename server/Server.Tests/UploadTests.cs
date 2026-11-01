using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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

    async Task<(Reply Reply, JsonNode Body)> Upload(byte[] zip, string? session = null, bool own = true)
    {
        (string, string)[] headers = [("Content-Type", "application/zip"), .. own ? [("X-Requested-With", "XMLHttpRequest")] : Array.Empty<(string, string)>(),
                                      ("Cookie", "hr_session=" + (session ?? cookie))];
        var reply = await server.SendAsync("POST", "web.test", "/account/restore", headers, zip);
        return (reply, JsonNode.Parse(reply.Body)!);
    }

    Task<(Reply Reply, JsonNode Body)> Upload(TestZip zip, string name, string? session = null) => Upload(File.ReadAllBytes(Zip(zip, name)), session);

    static string Status(JsonNode body) => (string)body["status"]!;

    long Count(long account, string table)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a")!;
    }

    [Fact]
    public async Task TestAMatchingMyAccountZipRestoresOnceAndTheAccountAdoptsItsUserIdAndHandle()
    {
        var token = state.Store.CreateToken(player, TimeSpan.FromHours(1));
        using (var con = state.Store.Open(player))
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, '{}')");  // a starting row: it moves with the account
        var (reply, body) = await Upload(TestZip.Own(), "mine.zip");
        Assert.Equal((200, "restored"), (reply.Status, Status(body)));
        Assert.Contains("stored: items 2, wallet 2", (string)body["report"]!);
        Assert.Contains("Your backup was restored: 2 items", (string)body["message"]!);
        Assert.Contains("Your name is now the one in the backup", (string)body["message"]!);
        // the account is 1001 now, called sample; the Steam id, the token and the cookie stayed
        Assert.False(state.Store.AccountExists(player));
        Assert.Equal(1001L, state.Store.AccountBySteam(Steam));
        Assert.Equal("sample", state.Store.HandleOf(1001));
        Assert.Equal(1001L, state.Store.ResolveToken(token));
        Assert.Equal(1001L, state.Store.ResolveWebSession(cookie));
        Eq("1001", state.Store.AccountJson(1001)["user_id"]);
        Eq("""{"em": 12.5, "gm": 3000.25}""", state.Wallet(1001));
        Assert.Equal(1L, Count(1001, "pending"));
        Assert.Equal(0L, Count(player, "pending"));
        Assert.Equal(1L, Count(1001, "restore_audit"));
        Assert.Equal(1001L, state.Store.RestoredBy(1001));
        Assert.True(File.Exists(Path.Combine(tmp, "restores", "1001", Restorer.Sha256(Path.Combine(tmp, "mine.zip")) + ".zip")));
        Assert.Empty(Directory.GetFiles(tmp, "state.db.bak-*"));  // a community server keeps the zip, not a copy of the whole database
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));
        var status = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + cookie)])).Body)!;
        Eq("1001", status["restoredUserId"]);
    }

    [Fact]
    public async Task TestASecondRestoreOfTheSameUserIdIsRefused()
    {
        Assert.Equal("restored", Status((await Upload(TestZip.Own(), "first.zip")).Body));
        var again = await Upload(TestZip.Own(wallet: "\"ems\":1.0,\"gms\":2.0"), "second.zip");  // the same player's newer backup
        Assert.Equal((409, "already"), (again.Reply.Status, Status(again.Body)));
        Assert.Contains("can be restored once", (string)again.Body["message"]!);
        Eq("""{"em": 12.5, "gm": 3000.25}""", state.Wallet(1001));  // nothing changed
        // another signed-in player with that Steam id on the zip cannot take it either
        var (other, otherCookie) = SignedIn("other", "76561190000000002");
        var stolen = await Upload(File.ReadAllBytes(Zip(TestZip.Own(steam: "76561190000000002"), "third.zip")), otherCookie);
        Assert.Equal((409, "already"), (stolen.Reply.Status, Status(stolen.Body)));
        Assert.True(state.Store.AccountExists(other));
        Assert.Equal(0L, Count(other, "restore_audit"));
    }

    [Fact]
    public async Task TestAZipWithAnotherOrNoSteamIdIsKeptForReviewAndNothingIsRestored()
    {
        var before = Count(player, "items");
        Eq("""{"em": 182.0, "gm": 33.0}""", state.Wallet(player));
        var mismatch = await Upload(TestZip.Own(steam: "76561190000000002"), "mismatch.zip");
        Assert.Equal((200, "review"), (mismatch.Reply.Status, Status(mismatch.Body)));
        Assert.Contains("different Steam account", (string)mismatch.Body["message"]!);
        var missing = await Upload(TestZip.Own(wallet: "\"ems\":1.0,\"gms\":1.0", steam: null), "missing.zip");
        Assert.Equal("review", Status(missing.Body));
        Assert.Contains("does not say which Steam account", (string)missing.Body["message"]!);
        var @public = await Upload(TestZip.Public(), "public.zip");
        Assert.Equal("review", Status(@public.Body));
        // nothing restored: the account, its rows and the user id are as they were; the zips are kept and listed
        Assert.True(state.Store.AccountExists(player));
        Assert.Equal(before, Count(player, "items"));
        Eq("""{"em": 182.0, "gm": 33.0}""", state.Wallet(player));
        Assert.Null(state.Store.RestoredBy(1001));
        Assert.Equal(3L, Count(player, "uploads"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(tmp, "restores", player.ToString())).Length);
        var status = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/restore", [("Cookie", "hr_session=" + cookie)])).Body)!;
        Assert.Null(status["restoredUserId"]);
        Assert.Equal(3, status["review"]!.AsArray().Count);
        // the same zip again is not kept twice, and a fourth different one is more than may wait
        Assert.Equal("review", Status((await Upload(TestZip.Own(steam: "76561190000000002"), "mismatch2.zip")).Body));
        Assert.Equal(3L, Count(player, "uploads"));
        var fourth = await Upload(TestZip.Own(wallet: "\"ems\":9.0,\"gms\":9.0", steam: "76561190000000003"), "fourth.zip");
        Assert.Equal((429, "busy"), (fourth.Reply.Status, Status(fourth.Body)));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(tmp, "restores", player.ToString())).Length);
    }

    [Fact]
    public async Task TestTheUserIdChangesOnlyBeforeTheAccountHasPlayed()
    {
        state.ConnectReply(player);  // a hunt was opened: the account has played
        Assert.True(state.Store.Played(player));
        var (reply, body) = await Upload(TestZip.Own(), "played.zip");
        Assert.Equal((200, "review"), (reply.Status, Status(body)));
        Assert.Contains("already played", (string)body["message"]!);
        Assert.True(state.Store.AccountExists(player));
        Assert.False(state.Store.AccountExists(1001));
        Assert.Null(state.Store.RestoredBy(1001));
        // an account that has not played adopts it (the other tests); one id from the server's range is refused as not a backup's
        var (fresh, freshCookie) = SignedIn("fresh", "76561190000000009");
        Assert.False(state.Store.Played(fresh));
        var high = await Upload(File.ReadAllBytes(Zip(TestZip.Own(uid: "2000000500", steam: "76561190000000009"), "high.zip")), freshCookie);
        Assert.Equal("review", Status(high.Body));
        Assert.Contains("user id this server does not accept", (string)high.Body["message"]!);
    }

    [Fact]
    public async Task TestAUserIdAnotherAccountHoldsIsRefused()
    {
        state.CreateAccount(1001, "holder");
        var (reply, body) = await Upload(TestZip.Own(), "held.zip");
        Assert.Equal((409, "taken"), (reply.Status, Status(body)));
        Assert.True(state.Store.AccountExists(player));
        Assert.Null(state.Store.RestoredBy(1001));
        Assert.Equal("holder", state.Store.HandleOf(1001));
        Assert.False(Directory.Exists(Path.Combine(tmp, "restores", "1001")));
    }

    [Fact]
    public async Task TestAHandleAnotherPlayerHasIsNotTaken()
    {
        state.CreateAccount(5, "Sample");  // handles do not differ by case
        var (reply, body) = await Upload(TestZip.Own(), "handle.zip");
        Assert.Equal((200, "restored"), (reply.Status, Status(body)));
        Assert.Contains("Your name was not changed", (string)body["message"]!);
        Assert.Equal("player", state.Store.HandleOf(1001));
        Assert.Equal("Sample", state.Store.HandleOf(5));
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
            new byte[Uploads.MaxZipBytes + 1]);
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

    [Fact]
    public async Task TestASecondUploadWhileOneIsStillArrivingGetsBusy()
    {
        var pipe = new Pipe();
        using var first = new HttpRequestMessage(HttpMethod.Post, "http://web.test/account/restore") { Content = new StreamContent(pipe.Reader.AsStream()) };
        first.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        first.Headers.Add("X-Requested-With", "XMLHttpRequest");
        first.Headers.Add("Cookie", "hr_session=" + cookie);
        var sending = server.Http.SendAsync(first);
        await pipe.Writer.WriteAsync(new byte[1 << 17], TestContext.Current.CancellationToken);  // more than a write buffer: the headers are out and the body has begun
        await pipe.Writer.FlushAsync(TestContext.Current.CancellationToken);
        Reply second;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            second = await server.SendAsync("POST", "web.test", "/account/restore", [("X-Requested-With", "XMLHttpRequest"), ("Cookie", "hr_session=" + cookie)], []);
        } while (second.Status != 429 && DateTime.UtcNow < deadline);
        Assert.Equal(429, second.Status);
        Assert.Equal("busy", Status(JsonNode.Parse(second.Body)!));
        await pipe.Writer.CompleteAsync();
        using var done = await sending;  // the first one ends (its bytes are no zip)
        Assert.Equal(400, (int)done.StatusCode);
        // the slot is free again
        Assert.Equal("invalid", Status((await Upload("x"u8.ToArray())).Body));
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
    public async Task TestDeletingRemovesTheAccountAndEverythingOfIt()
    {
        Assert.Equal("restored", Status((await Upload(TestZip.Own(), "mine.zip")).Body));
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
    public async Task TestACommunityRestoreOfADeletedAccountsUserIdIsPossibleAgain()
    {
        Assert.Equal("restored", Status((await Upload(TestZip.Own(), "mine.zip")).Body));
        state.Store.DeleteAccount(1001);
        var (_, again) = SignedIn("again", Steam);  // the same Steam id signs in anew and uploads the same zip
        var (reply, body) = await Upload(File.ReadAllBytes(Path.Combine(tmp, "mine.zip")), again);
        Assert.Equal((200, "restored"), (reply.Status, Status(body)));
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
            con.Exec("PRAGMA user_version = 2");
        }
        var migrated = new Store(file);
        migrated.CreateAccount(5, "five", new JsonObject());
        Assert.Null(migrated.RestoredBy(1));
        using var check = migrated.Open(5);
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM uploads WHERE account_id = $a"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }
}
