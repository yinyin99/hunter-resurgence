using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Archive claims: a fake archive.db of two made-up players (alpha, hunterscore 500, with a full profile; bravo, 12,000, with one item),
/// the rules of a claim and the two /account/archive endpoints (a community server, signed in with a cookie).</summary>
public class ClaimsTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    Loopback server = null!;
    string ArchivePath => Path.Combine(tmp, "archive.db");
    int accounts;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        MakeArchive();
        var table = new HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Hit(2, "", 200, [], "{}"u8.ToArray());
        server = await Loopback.StartAsync(new Game(table, Cfg, state, tmp), tmp);  // the server looks for archive.db in the work folder
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    void MakeArchive()
    {
        using var con = new SqliteConnection($"Data Source={ArchivePath};Pooling=False");
        con.Open();
        void Run(string sql, params (string, object)[] ps)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            cmd.ExecuteNonQuery();
        }
        Run("CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES ('format', '1');"
            + "CREATE TABLE player(uid INTEGER PRIMARY KEY, handle TEXT NOT NULL, hostname TEXT NOT NULL, hunterscore INTEGER NOT NULL);"
            + "CREATE UNIQUE INDEX player_hostname ON player(hostname);"
            + "CREATE TABLE reply(uid INTEGER NOT NULL, path TEXT NOT NULL, endpoint TEXT NOT NULL, params TEXT NOT NULL, status INTEGER NOT NULL, body BLOB NOT NULL, PRIMARY KEY(uid, path));");
        void Player(long uid, string handle, long score) =>
            Run("INSERT INTO player VALUES ($u, $h, $k, $s)", ("$u", uid), ("$h", handle), ("$k", handle.ToLowerInvariant()), ("$s", score));
        void Reply(long uid, string path, string endpoint, string body, params (string K, string V)[] ps) =>
            Run("INSERT INTO reply VALUES ($u, $p, $e, $q, 200, $b)", ("$u", uid), ("$p", path), ("$e", endpoint),
                ("$q", new JsonObject([.. ps.Select(p => KeyValuePair.Create(p.K, (JsonNode?)p.V))]).ToJsonString()), ("$b", Encoding.UTF8.GetBytes(body)));

        Player(3001, "Alpha", 500);
        var a = ("user_id", "3001");
        Reply(3001, "Public_user_getByHostname__hostname=Alpha.json", "/v1/Public_user/getByHostname", """{"id":3001,"handle":"Alpha","hostname":"alpha","hunterscore":500}""", ("hostname", "Alpha"));
        Reply(3001, "Inventory_dict__user_id=3001.json", "/v1/Inventory/dict",
              """{"500":[{"id":9001,"slot":1,"units":3,"metadata":{}}],"610":[{"id":9101,"slot":1,"units":0,"metadata":{"dog_name":"Rex"}}]}""", a);
        Reply(3001, "Skill_list__user_id=3001.json", "/v1/Skill/list", $$"""{"weapon":{"200":10.2},"tracking":[{{TestZip.Zeros(59, "1.5")}}],"spotting":[{{TestZip.Zeros(59, "2")}}]}""", a);
        Reply(3001, "Statistics_total__user_id=3001.json", "/v1/Statistics/total",
              """{"duration":3600,"distance":12.5,"data":{"collectables":{},"weapon":{},"animal":{"5":{"kills":2}},"daily_missions":{}}}""", a);
        Reply(3001, "Achievement_stats__user_id=3001.json", "/v1/Achievement/stats", """[{"id":12,"category_id":1,"group_id":2,"value":3,"triggers":{"1":1600000000}}]""", a);
        Reply(3001, "Trophy_list__user_id=3001__offset=0__limit=100000.json", "/v1/Trophy/list", """{"total":1,"trophies":[{"id":81,"trophy_id":3,"ts":1600000000}]}""",
              a, ("offset", "0"), ("limit", "100000"));
        Reply(3001, "game_user_3001_lodges.json", "/game/user/3001/lodges", """[{"id":61,"name":"Lodge"}]""");
        Reply(3001, "game_user_3001_lodges_61.json", "/game/user/3001/lodges/61", """{"id":61,"rooms":[]}""");

        Player(3002, "Bravo", 12000);
        Reply(3002, "Inventory_dict__user_id=3002.json", "/v1/Inventory/dict", """{"500":[{"id":9501,"slot":1,"units":1,"metadata":{}}]}""", ("user_id", "3002"));
    }

    /// <summary>An account the way the first sign-in makes it and its cookie.</summary>
    (long Id, string Cookie) SignedIn(string handle)
    {
        var id = state.Store.NextServerId();
        state.CreateAccount(id, handle, "7656119000000100" + accounts++);
        return (id, state.Store.CreateWebSession(id, TimeSpan.FromDays(1)));
    }

    Claims Make(List<string>? alerts = null, string? archive = null) =>
        new(state, new Archive(archive ?? ArchivePath), tmp) { Alert = alerts is null ? _ => { } : alerts.Add };

    bool Owns(long account, long guid) => state.Inventory(account).Any(i => (string)i!["guid"]! == guid.ToString(CultureInfo.InvariantCulture));

    List<string> Notices(long account)
    {
        using var con = state.Store.Open(account);
        return con.Rows("SELECT content FROM messages WHERE account_id = $a AND sender = ? ORDER BY id", State.SYSTEM_SENDER).Select(r => (string)r[0]).ToList();
    }

    long Count(long account, string table)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a")!;
    }

    async Task<(Reply Reply, JsonNode Body)> Call(string method, string path, string session, string? form = null, bool own = true)
    {
        List<(string, string)> headers = [("Content-Type", "application/x-www-form-urlencoded"), ("Cookie", "hr_session=" + session)];
        if (own) headers.Add(("X-Requested-With", "x"));
        var reply = await server.SendAsync(method, "web.test", path, headers, form is null ? null : Encoding.UTF8.GetBytes(form));
        return (reply, JsonNode.Parse(reply.Body)!);
    }

    [Fact]
    public void TestAClaimBelowTenThousandRestoresAtOnceAndTheUserIdStays()
    {
        var (player, _) = SignedIn("player");
        var alerts = new List<string>();
        state.Give(player, "gm", 20000 - (double)state.Wallet(player)["gm"]!);
        var em = (double)state.Wallet(player)["em"]!;
        var done = Make(alerts).Claim(player, "ALPHA", useOldName: true, confirmOverwrite: false);
        Assert.Equal((200, "restored"), (done.Status, done.Kind));
        Assert.Contains("2 items", done.Message);
        Assert.Empty(alerts);
        Assert.True(Owns(player, 9001));
        Assert.True(Owns(player, 9101));
        Assert.True(state.Store.AccountExists(player));  // the user id never changes
        Assert.False(state.Store.AccountExists(3001));
        Assert.Equal("Alpha", state.Store.HandleOf(player));
        Assert.Null(state.Store.RestoredBy(3001));  // no restored_ids row: a zip of the same player can still be approved
        Eq($$"""{"em": {{em.ToString(CultureInfo.InvariantCulture)}}, "gm": 20000}""", state.Wallet(player));  // the wallet stays, gm$ at least 15,000
        Assert.Equal(1L, Count(player, "trophies"));
        Assert.Equal(2L, Count(player, "lodges"));
        Assert.Equal(1L, Count(player, "restore_audit"));
        var own = Make().Of(player)!;
        Assert.Equal(("restored", "Alpha"), (own.State, own.Handle));
        Assert.Contains("Your old profile was restored", Assert.Single(Notices(player)));
    }

    [Fact]
    public void TestTheGmFloorHoldsForAnAccountBelowIt()
    {
        var (player, _) = SignedIn("player");
        state.Give(player, "gm", 1 - (double)state.Wallet(player)["gm"]!);
        Assert.Equal("restored", Make().Claim(player, "alpha", false, false).Kind);
        Assert.Equal(Restorer.GmFloor, (double)state.Wallet(player)["gm"]!);
        Assert.Equal("player", state.Store.HandleOf(player));  // useOldName false: the account's own name
    }

    [Fact]
    public void TestAHandleAnotherAccountHoldsIsNotTaken()
    {
        var (player, _) = SignedIn("player");
        SignedIn("ALPHA");  // taken, in any case
        var claims = Make();
        Assert.False(claims.Lookup(player, "alpha").NameFree);
        var done = claims.Claim(player, "alpha", useOldName: true, confirmOverwrite: false);
        Assert.Equal("restored", done.Kind);
        Assert.Contains("The old name was not used", done.Message);
        Assert.Equal("player", state.Store.HandleOf(player));
        Assert.True(Owns(player, 9001));
    }

    [Fact]
    public void TestAClaimFromTenThousandWaitsForTheOperatorWhoApprovesOrRejects()
    {
        var (player, _) = SignedIn("player");
        var (other, _) = SignedIn("other");
        var alerts = new List<string>();
        var claims = Make(alerts);
        var done = claims.Claim(player, "bravo", true, false);
        Assert.Equal((200, "review"), (done.Status, done.Kind));
        Assert.False(Owns(player, 9501));  // nothing restored yet
        var waiting = Assert.Single(claims.Waiting());
        Assert.Equal((player, 3002L, "Bravo", 12000L), (waiting.Account, waiting.Uid, waiting.Handle, waiting.Hunterscore));
        Assert.Equal($"Claim waiting for review: c{waiting.Id}, account {player}, archived 3002, handle Bravo, hunterscore 12000. Run: review list", Assert.Single(alerts));
        Assert.Contains("waits for an operator", Assert.Single(Notices(player)));
        Assert.Equal("waiting", claims.Of(player)!.State);
        Assert.Equal("refused", claims.Claim(player, "alpha", true, false).Kind);  // the one claim is taken

        var second = claims.Claim(other, "bravo", true, false);  // the same archived player, another account: waits too
        Assert.Equal("review", second.Kind);
        var otherId = claims.Waiting().Single(c => c.Account == other).Id;

        var approved = claims.Approve(waiting.Id, keepHandle: true);
        Assert.Equal((200, "restored"), (approved.Status, approved.Kind));
        Assert.True(Owns(player, 9501));
        Assert.Equal("player", state.Store.HandleOf(player));  // --keep-handle
        Assert.Equal("restored", claims.Of(player)!.State);
        Assert.Equal("unknown", claims.Approve(waiting.Id).Kind);  // it no longer waits

        Assert.True(claims.Reject(otherId, "not yours"));
        Assert.False(claims.Reject(otherId));
        Assert.Equal("unknown", claims.Approve(otherId).Kind);
        Assert.False(Owns(other, 9501));
        Assert.Equal("rejected", claims.Of(other)!.State);
        Assert.Equal("Your claim of an old profile was rejected by the operator: not yours", Notices(other).Last());
        Assert.Equal("refused", claims.Claim(other, "alpha", true, false).Kind);  // a rejected claim is still the account's one
        Assert.Empty(claims.Waiting());
    }

    [Fact]
    public void TestApprovingTakesTheArchivedNameUnlessTheOperatorKeepsTheAccountsOwn()
    {
        var (player, _) = SignedIn("player");
        var claims = Make();
        claims.Claim(player, "bravo", true, false);
        Assert.Equal("restored", claims.Approve(claims.Waiting().Single().Id).Kind);
        Assert.Equal("Bravo", state.Store.HandleOf(player));
    }

    [Fact]
    public void TestASecondClaimByTheSameAccountIsRefused()
    {
        var (player, _) = SignedIn("player");
        var claims = Make();
        Assert.Equal("restored", claims.Claim(player, "alpha", true, false).Kind);
        var again = claims.Claim(player, "alpha", true, false);
        Assert.Equal((409, "refused"), (again.Status, again.Kind));
        Assert.Equal("refused", claims.Claim(player, "bravo", true, false).Kind);
        Assert.False(Owns(player, 9501));
    }

    [Fact]
    public void TestAProfileAnotherAccountClaimedGoesToReview()
    {
        var (first, _) = SignedIn("first");
        var (second, _) = SignedIn("second");
        var alerts = new List<string>();
        var claims = Make(alerts);
        Assert.Equal("restored", claims.Claim(first, "alpha", false, false).Kind);
        var done = claims.Claim(second, "alpha", false, false);  // hunterscore 500, but already claimed
        Assert.Equal("review", done.Kind);
        Assert.False(Owns(second, 9001));
        Assert.Contains("archived 3001", Assert.Single(alerts));
        Assert.Contains("already claimed", Assert.Single(claims.Waiting()).Note);
    }

    [Fact]
    public void TestAPlayedAccountNeedsTheOverwriteConfirmedAndKeepsWhatItPlayed()
    {
        var (player, _) = SignedIn("player");
        state.ConnectReply(player);  // a hunt was opened: the account has played
        Assert.True(state.Store.Played(player));
        var hunts = Count(player, "expeditions");
        var claims = Make();
        var ask = claims.Claim(player, "alpha", true, confirmOverwrite: false);
        Assert.Equal((409, "confirm"), (ask.Status, ask.Kind));
        Assert.False(Owns(player, 9001));
        Assert.Null(claims.Of(player));  // asking is not a claim
        var done = claims.Claim(player, "alpha", true, confirmOverwrite: true);
        Assert.Equal("restored", done.Kind);
        Assert.True(Owns(player, 9001));
        Assert.Equal(hunts, Count(player, "expeditions"));
        Assert.True(state.Store.AccountExists(player));
    }

    [Fact]
    public void TestTheOperatorClaimSkipsReviewLimitsAndConfirmation()
    {
        var (player, _) = SignedIn("player");
        var (other, _) = SignedIn("other");
        state.ConnectReply(player);
        var alerts = new List<string>();
        var claims = Make(alerts);
        for (var i = 0; i < Claims.LookupsPerDay; i++) claims.Lookup(player, "nobody");
        Assert.True(claims.Lookup(player, "alpha").Limited);
        var done = claims.Claim(player, "bravo", true, confirmOverwrite: false, byOperator: true);  // 12,000, played, over the limit
        Assert.Equal((200, "restored"), (done.Status, done.Kind));
        Assert.True(Owns(player, 9501));
        Assert.Empty(alerts);
        Assert.Equal("Bravo", state.Store.HandleOf(player));
        Assert.Equal("restored", claims.Claim(other, "3001", false, false, byOperator: true).Kind);  // by user id
        Assert.True(Owns(other, 9001));
        Assert.Equal("other", state.Store.HandleOf(other));  // useOldName false
        // an operator's claim replaces the account's earlier row
        Assert.Equal("restored", claims.Claim(other, "bravo", false, false, byOperator: true).Kind);
        Assert.Equal("Bravo", claims.Of(other)!.Handle);
        Assert.Equal("unknown", claims.Claim(other, "nobody", false, false, byOperator: true).Kind);
    }

    [Fact]
    public void TestWithoutAnArchiveNothingIsFound()
    {
        var (player, _) = SignedIn("player");
        var claims = Make(archive: Path.Combine(tmp, "missing.db"));
        Assert.False(claims.Enabled);
        var lookup = claims.Lookup(player, "alpha");
        Assert.Null(lookup.Player);
        Assert.False(lookup.Limited);
        Assert.Equal("unknown", claims.Claim(player, "alpha", true, false).Kind);
        Assert.Null(claims.Of(player));
    }

    [Fact]
    public async Task TestTheSixthLookupOfTheDayIs429AndTheCountIsInTheDatabase()
    {
        var (player, cookie) = SignedIn("player");
        var (reply, body) = await Call("GET", "/account/archive?name=aLpHa", cookie);
        Assert.Equal(200, reply.Status);
        Eq("""
           {"found": true, "handle": "Alpha", "hunterscore": 500, "items": 2, "trophies": 1, "lodges": 2, "skills": true, "statistics": true, "achievements": true, "nameFree": true}
           """, body);
        Eq("""{"found": true, "handle": "Bravo", "hunterscore": 12000, "items": 1, "trophies": 0, "lodges": 0, "skills": false, "statistics": false, "achievements": false, "nameFree": true}""",
           (await Call("GET", "/account/archive?name=bravo", cookie)).Body);
        for (var i = 0; i < 3; i++)
            Eq("""{"found": false}""", (await Call("GET", "/account/archive?name=nobody", cookie)).Body);
        var sixth = await Call("GET", "/account/archive?name=alpha", cookie);
        Assert.Equal(429, sixth.Reply.Status);
        Assert.Equal("limit", (string)sixth.Body["status"]!);
        using (var con = state.Store.Open())
            Assert.Equal(5L, con.Scalar("SELECT n FROM claim_lookups WHERE account_id = ?", player));
        // the limit is per account; the claim itself is not a lookup of a name that exists
        var (_, otherCookie) = SignedIn("other");
        Assert.True((bool)(await Call("GET", "/account/archive?name=alpha", otherCookie)).Body["found"]!);
        var claim = await Call("POST", "/account/archive/claim", cookie, "name=alpha&old_name=1");
        Assert.Equal((200, "restored"), (claim.Reply.Status, (string)claim.Body["status"]!));
        // the day's count is gone tomorrow
        using (var con = state.Store.Open())
            con.Exec("UPDATE claim_lookups SET day = day - 1");
        Assert.Equal(200, (await Call("GET", "/account/archive?name=alpha", cookie)).Reply.Status);
        using (var con = state.Store.Open())
            Assert.Equal(1L, con.Scalar("SELECT n FROM claim_lookups WHERE account_id = ?", player));
    }

    [Fact]
    public async Task TestTheEndpointsNeedASignInAndTheClaimNeedsOurOwnPage()
    {
        var (player, cookie) = SignedIn("player");
        Assert.Equal(401, (await Call("GET", "/account/archive?name=alpha", "nope")).Reply.Status);
        Assert.Equal(401, (await Call("GET", "/account/archive", "nope")).Reply.Status);
        Assert.Equal(401, (await Call("POST", "/account/archive/claim", "nope", "name=alpha&old_name=1")).Reply.Status);
        Assert.Equal(400, (await Call("POST", "/account/archive/claim", cookie, "name=alpha&old_name=1", own: false)).Reply.Status);
        Assert.Null(Make().Of(player));
        Eq("""{"enabled": true, "claimed": null}""", (await Call("GET", "/account/archive", cookie)).Body);
    }

    [Fact]
    public async Task TestTheClaimEndpointAnswersEachOutcomeAndTheStatusShowsTheClaim()
    {
        var (player, cookie) = SignedIn("player");
        var (other, otherCookie) = SignedIn("other");
        var (third, thirdCookie) = SignedIn("third");
        var unknown = await Call("POST", "/account/archive/claim", cookie, "name=nobody&old_name=1");
        Assert.Equal((404, "unknown"), (unknown.Reply.Status, (string)unknown.Body["status"]!));
        state.ConnectReply(other);
        var confirm = await Call("POST", "/account/archive/claim", otherCookie, "name=alpha&old_name=0");
        Assert.Equal((409, "confirm"), (confirm.Reply.Status, (string)confirm.Body["status"]!));
        var review = await Call("POST", "/account/archive/claim", thirdCookie, "name=bravo&old_name=1");
        Assert.Equal((200, "review"), (review.Reply.Status, (string)review.Body["status"]!));
        var done = await Call("POST", "/account/archive/claim", otherCookie, "name=alpha&old_name=0&confirm=overwrite");
        Assert.Equal((200, "restored"), (done.Reply.Status, (string)done.Body["status"]!));
        var again = await Call("POST", "/account/archive/claim", otherCookie, "name=alpha&old_name=0&confirm=overwrite");
        Assert.Equal((409, "refused"), (again.Reply.Status, (string)again.Body["status"]!));
        Assert.True(Owns(other, 9001));
        Assert.Equal("other", state.Store.HandleOf(other));
        var status = (await Call("GET", "/account/archive", otherCookie)).Body;
        Assert.Equal(("restored", "Alpha"), (((string)status["claimed"]!["state"]!), (string)status["claimed"]!["handle"]!));
        Assert.Equal("waiting", (string)(await Call("GET", "/account/archive", thirdCookie)).Body["claimed"]!["state"]!);
        Assert.False(Owns(third, 9501));
        Assert.Null(Make().Of(player));
    }
}
