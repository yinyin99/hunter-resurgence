using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Discord's API as the server calls it (the token endpoint, the user, the role connection): what it was asked, and the answers a test sets.</summary>
sealed class FakeDiscord : HttpMessageHandler
{
    public string UserId = "111111111111111111";
    public string Scope = "identify role_connections.write";
    public int PutStatus = 200;
    public int RefreshStatus = 200;
    public string RefreshError = "invalid_grant";
    public bool Down;
    public readonly List<Call> Asked = [];
    int issued;

    public sealed record Call(string Method, string Path, string? Bearer, Dictionary<string, string> Form, JsonNode? Json)
    {
        public string Line => Method + " " + Path;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("discord.com", request.RequestUri!.Host);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri.AbsolutePath;
        var isForm = request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded";
        var form = isForm ? HttpUtility.ParseQueryString(body).AllKeys.ToDictionary(k => k!, k => HttpUtility.ParseQueryString(body)[k]!) : [];
        lock (Asked)
            Asked.Add(new(request.Method.Method, path, request.Headers.Authorization?.Parameter, form, body.StartsWith('{') ? JsonNode.Parse(body) : null));
        if (Down) throw new HttpRequestException("down");
        static HttpResponseMessage Json(int status, string json) => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (path == "/api/v10/oauth2/token")
        {
            if (form.GetValueOrDefault("grant_type") == "refresh_token" && RefreshStatus != 200)
                return Json(RefreshStatus, $$"""{"error": "{{RefreshError}}"}""");
            var n = Interlocked.Increment(ref issued);
            return Json(200, $$"""{"token_type": "Bearer", "access_token": "access-{{n}}", "refresh_token": "refresh-{{n}}", "expires_in": 604800, "scope": "{{Scope}}"}""");
        }
        if (path == "/api/v10/users/@me") return Json(200, $$"""{"id": "{{UserId}}", "username": "someone"}""");
        if (path.EndsWith("/role-connection", StringComparison.Ordinal) && request.Method == HttpMethod.Put) return Json(PutStatus, "{}");
        return Json(404, "{}");
    }

    public IEnumerable<Call> Puts => Asked.Where(c => c.Method == "PUT");
}

/// <summary>Discord Linked Roles (docs/server.md#discord-linked-roles) through the real HTTP pipeline, with Discord faked.</summary>
public class LinkedRolesTests : StateTestBase, IAsyncLifetime
{
    const string AppId = "123456789012345678";
    const string Redirect = "/signin/discord?result=";
    readonly FakeDiscord discord = new();
    Loopback server = null!;

    LinkedRoles Roles => server.Services.GetRequiredService<LinkedRoles>();

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        state.CreateAccount(8, "second");
        state.CreateAccount(9, "third");
        var cfg = new JsonObject(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))))
        {
            ["discord_client_id"] = AppId, ["discord_client_secret"] = "test-secret",
        };
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), cfg, state, tmp), tmp, discordHttp: new HttpClient(discord));
    }

    protected override ValueTask StopServersAsync() => server.DisposeAsync();

    // ---- helpers

    string SessionOf(long account) => state.Store.CreateWebSession(account, TimeSpan.FromHours(1));

    Task<Reply> Get(string path, string? cookie = null) => server.SendAsync("GET", "web.test", path, cookie is null ? [] : [("Cookie", "hr_session=" + cookie)]);

    static string Location(Reply r) => r.Headers.Single(h => h.Name == "Location").Value;

    /// <summary>Discord's redirect back to the callback, with the state this session was given (or another) and a code.</summary>
    async Task<string> Callback(string cookie, string? stateOf = null, string code = "code-1") =>
        Location(await Get($"/discord/callback?code={code}&state={Uri.EscapeDataString(stateOf ?? Roles.StateFor(cookie))}", cookie));

    double Gm(long account) => Py.D(state.Wallet(account)["gm"]);

    void SetHunterscore(long account, long score)
    {
        using var con = state.Store.Open();
        con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE id = ?", score, account);
    }

    void Unlink(long account)
    {
        using var con = state.Store.Open();
        con.Exec("DELETE FROM discord_links WHERE account_id = ?", account);
    }

    void Expire(long account)
    {
        using var con = state.Store.Open();
        con.Exec("UPDATE discord_links SET expires_ts = 1 WHERE account_id = ?", account);
    }

    object?[] LinkRow(long account)
    {
        using var con = state.Store.Open();
        return con.Rows("SELECT discord_id, access_token, refresh_token, expires_ts, pushed_score FROM discord_links WHERE account_id = ?", account).SingleOrDefault() ?? [];
    }

    long Count(string table, string where = "1")
    {
        using var con = state.Store.Open();
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE {where}")!;
    }

    long Paid(long account)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT COUNT(*) FROM wallet_log WHERE account_id = $a AND summary = ?", LinkedRoles.RewardSummary)!;
    }

    /// <summary>The texts of the account's notice thread, newest first.</summary>
    string[] Inbox(long account) =>
        [.. JsonNode.Parse(state.ConversationReply(account, Encoding.UTF8.GetBytes($"user_id={State.SYSTEM_SENDER}")))!["messages"]!.AsArray().Select(m => (string)m!["content"]!)];

    // ---- the verification URL and the state

    [Fact]
    public async Task TestTheVerificationUrlSendsASignedOutPlayerToSteamAndASignedInOneToDiscord()
    {
        var signedOut = await Get("/discord/link");
        Assert.Equal(("/auth/steam/login?next=/discord/link", 302), (Location(signedOut), signedOut.Status));
        var cookie = SessionOf(8);
        var url = new Uri(Location(await Get("/discord/link", cookie)));
        Assert.Equal("https://discord.com/oauth2/authorize", url.GetLeftPart(UriPartial.Path));
        var q = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal((AppId, "code", "identify role_connections.write", "consent", "https://web.test/discord/callback"), (q["client_id"], q["response_type"], q["scope"], q["prompt"], q["redirect_uri"]));
        Assert.Contains("scope=identify%20role_connections.write", url.Query);
        var ends = long.Parse(q["state"]![..q["state"]!.IndexOf('.')]);
        Assert.Equal(Roles.StateFor(cookie, ends), q["state"]);  // bound to this session ...
        Assert.NotEqual(Roles.StateFor(SessionOf(9), ends), q["state"]);  // ... and to no other
        Assert.InRange(ends - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 500, 600);  // ten minutes
        Assert.Empty(discord.Asked);
    }

    [Fact]
    public async Task TestTheCallbackNeedsTheSessionAndTheStateOfThatSession()
    {
        var cookie = SessionOf(8);
        var good = Roles.StateFor(cookie);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal("/auth/steam/login?next=/discord/link", Location(await Get($"/discord/callback?code=c&state={good}")));  // no session: sign in and start again
        Assert.Equal("/auth/steam/login?next=/discord/link", Location(await Get($"/discord/callback?code=c&state={good}", "0000")));
        foreach (var bad in new[]
        {
            Roles.StateFor(SessionOf(9)),                       // another session's state
            Roles.StateFor(cookie, now - 1),                    // ended
            good[..^1] + (good[^1] == '0' ? '1' : '0'),         // changed
            good.Replace(good[..good.IndexOf('.')], (now + 99999).ToString()),  // a later end the HMAC does not cover
            "", "x", ".", "1.2", good + "0",
        })
            Assert.Equal(Redirect + "error", await Callback(cookie, bad));
        Assert.Equal(Redirect + "error", Location(await Get("/discord/callback?code=c", cookie)));  // no state
        Assert.Equal(Redirect + "error", Location(await Get($"/discord/callback?state={good}", cookie)));  // no code
        Assert.Equal(Redirect + "error", Location(await Get($"/discord/callback?error=access_denied&state={good}", cookie)));  // the player said no
        Assert.Equal(Redirect + "error", Location(await Get($"/discord/callback?code=a&code=b&state={good}", cookie)));  // a repeated field
        Assert.Empty(discord.Asked);  // none of it reached Discord
        Assert.Equal((0L, 0L, 33.0), (Count("discord_links"), Count("discord_rewards"), Gm(8)));
        Assert.Equal(Redirect + "linked", await Callback(cookie));  // the right one does
    }

    [Fact]
    public async Task TestADiscordAnswerWithoutBothScopesOrAUserIsAnErrorAndLinksNothing()
    {
        var cookie = SessionOf(8);
        discord.Scope = "identify";
        Assert.Equal(Redirect + "error", await Callback(cookie));
        discord.Scope = "identify role_connections.write";
        discord.UserId = "not-a-snowflake";
        Assert.Equal(Redirect + "error", await Callback(cookie));
        discord.Down = true;
        Assert.Equal(Redirect + "error", await Callback(cookie));
        Assert.Equal((0L, 33.0), (Count("discord_links"), Gm(8)));
    }

    // ---- the link, the reward and the role data

    [Fact]
    public async Task TestALinkPaysOnceAndPushesTheScoreAndKeepsOnlySealedTokens()
    {
        SetHunterscore(8, 20500);
        Assert.Equal(Redirect + "linked", await Callback(SessionOf(8)));
        // Discord was asked, in order: the token, the user, then the role connection with the user's own token
        Assert.Equal(["POST /api/v10/oauth2/token", "GET /api/v10/users/@me", $"PUT /api/v10/users/@me/applications/{AppId}/role-connection"], discord.Asked.Select(c => c.Line));
        var token = discord.Asked[0].Form;
        Assert.Equal(("authorization_code", "code-1", AppId, "test-secret", "https://web.test/discord/callback"),
                     (token["grant_type"], token["code"], token["client_id"], token["client_secret"], token["redirect_uri"]));
        Assert.Equal(["access-1", "access-1"], discord.Asked.Skip(1).Select(c => c.Bearer));
        Eq("""{"platform_name": "Hunter Resurgence", "platform_username": "second", "metadata": {"hunterscore": 20500}}""", discord.Asked[2].Json);

        // 2500 gm$ through the wallet: a wallet_log row with the reason, base = amount, no bonus event; a message in the notice thread
        Assert.Equal(33.0 + 2500, Gm(8));
        using (var con = state.Store.Open(8))
        {
            var row = con.Rows("SELECT currency, amount, after, base, bonus_event FROM wallet_log WHERE account_id = $a AND summary = ?", LinkedRoles.RewardSummary).Single();
            Assert.Equal(("gm", 2500.0, 2533.0, 2500.0), ((string)row[0], (double)row[1], (double)row[2], (double)row[3]));
            Assert.IsType<DBNull>(row[4]);
        }
        Assert.Contains("2500 gm$", Assert.Single(Inbox(8)));

        // the link keeps the Discord id and the two tokens sealed (never as they came), and what was pushed
        var link = LinkRow(8);
        Assert.Equal((discord.UserId, 20500L), ((string)link[0], (long)link[4]));
        Assert.DoesNotContain("access-1", (string)link[1]);
        Assert.DoesNotContain("refresh-1", (string)link[2]);
        Assert.NotEqual(link[1], link[2]);
        Assert.InRange((long)link[3] - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 604000, 604800);  // expires_in
        // the ledger holds a hash of the id, not the id, and the account that was paid
        using var ledger = state.Store.Open();
        var paid = ledger.Rows("SELECT hash, account_id FROM discord_rewards").Single();
        Assert.Matches("^[0-9a-f]{64}$", (string)paid[0]);
        Assert.DoesNotContain(discord.UserId, (string)paid[0]);
        Assert.Equal(8L, paid[1]);
    }

    [Fact]
    public async Task TestOneDiscordAccountPerHunterAndOneHunterPerDiscordAccount()
    {
        var (a, b) = (SessionOf(8), SessionOf(9));
        Assert.Equal(Redirect + "linked", await Callback(a));
        discord.Asked.Clear();
        // the Discord account of another hunter is refused, with nothing written or pushed
        Assert.Equal(Redirect + "taken", await Callback(b));
        Assert.Equal((0L, 33.0, 0L), (Count("discord_links", "account_id = 9"), Gm(9), Paid(9)));
        // a hunter with a Discord account does not take another
        discord.UserId = "222222222222222222";
        Assert.Equal(Redirect + "other", await Callback(a));
        Assert.Equal(("111111111111111111", 0L), ((string)LinkRow(8)[0], Count("discord_links", "discord_id = '222222222222222222'")));
        Assert.Empty(discord.Puts);
        // the same pair again is fine: fresh tokens, fresh data, no second reward
        discord.UserId = "111111111111111111";
        Assert.Equal(Redirect + "rewarded", await Callback(a, code: "code-2"));
        Assert.Single(discord.Puts);
        Assert.Equal((33.0 + 2500, 1L), (Gm(8), Paid(8)));
        Assert.Equal(1L, Count("discord_links"));
    }

    [Fact]
    public async Task TestTheRewardIsPaidOnceForTwoRequestsAtTheSameTime()
    {
        var cookie = SessionOf(8);
        async Task<string> Raw(string code)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://web.test/discord/callback?code={code}&state={Uri.EscapeDataString(Roles.StateFor(cookie))}");
            request.Headers.TryAddWithoutValidation("Cookie", "hr_session=" + cookie);
            using var reply = await server.Http.SendAsync(request);
            return reply.Headers.Location!.ToString();
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Raw("code-" + i)));
        Assert.Equal(1, results.Count(r => r == Redirect + "linked"));
        Assert.Equal(5, results.Count(r => r == Redirect + "rewarded"));
        Assert.Equal((33.0 + 2500, 1L, 1, 1L), (Gm(8), Paid(8), Inbox(8).Length, Count("discord_rewards")));
    }

    [Fact]
    public async Task TestNeitherAnUnlinkAndRelinkNorASecondHunterNorAnotherDiscordAccountPaysTwice()
    {
        var (a, b) = (SessionOf(8), SessionOf(9));
        Assert.Equal(Redirect + "linked", await Callback(a));
        Unlink(8);
        Assert.Equal(Redirect + "rewarded", await Callback(a));  // the same pair again after an unlink
        Assert.Equal(33.0 + 2500, Gm(8));
        Unlink(8);
        Assert.Equal(Redirect + "rewarded", await Callback(b));  // a second hunter with the Discord account that was paid: linked, not paid
        Assert.Equal((33.0, 1L), (Gm(9), Count("discord_links", "account_id = 9")));
        Unlink(9);
        discord.UserId = "222222222222222222";  // the hunter that was paid, with another Discord account: linked, not paid
        Assert.Equal(Redirect + "rewarded", await Callback(a));
        Assert.Equal((33.0 + 2500, 1L), (Gm(8), Paid(8)));
        Assert.Equal(0L, Paid(9));
        Assert.Equal(1L, Count("discord_rewards"));
    }

    [Fact]
    public async Task TestDeleteMyDataRemovesTheLinkAndTheTokensAndKeepsOnlyTheHashOfTheId()
    {
        var cookie = SessionOf(8);
        Assert.Equal(Redirect + "linked", await Callback(cookie));
        var reply = await server.SendAsync("POST", "web.test", "/account/delete",
            [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + cookie)], "confirm=delete"u8.ToArray());
        Assert.Equal(200, reply.Status);
        Assert.Equal(0L, Count("discord_links"));  // the id and the tokens
        using (var con = state.Store.Open())
        {
            var kept = con.Rows("SELECT hash, account_id FROM discord_rewards").Single();  // the hash stays, tied to nobody
            Assert.Matches("^[0-9a-f]{64}$", (string)kept[0]);
            Assert.IsType<DBNull>(kept[1]);
        }
        // a recreated account with the same Discord account is linked and not paid again
        state.CreateAccount(10, "again");
        Assert.Equal(Redirect + "rewarded", await Callback(SessionOf(10)));
        Assert.Equal((33.0, 0L, 1L), (Gm(10), Paid(10), Count("discord_links")));
        Assert.Equal(1L, Count("discord_rewards"));
    }

    // ---- the refresh token and the hunterscore

    [Fact]
    public async Task TestAnExpiredAccessTokenIsRefreshedWithTheStoredRefreshTokenAndTheNewOnesAreKept()
    {
        Assert.Equal(Redirect + "linked", await Callback(SessionOf(8)));
        discord.Asked.Clear();
        SetHunterscore(8, 21000);
        Assert.True(await Roles.PushAsync(8));  // the access token still holds: no refresh
        Assert.Equal(["PUT"], discord.Asked.Select(c => c.Method));
        Assert.Equal(("access-1", 21000), (discord.Asked[0].Bearer, (int)discord.Asked[0].Json!["metadata"]!["hunterscore"]!));

        foreach (var (n, score) in new[] { (1, 22000), (2, 23000) })
        {
            discord.Asked.Clear();
            Expire(8);
            SetHunterscore(8, score);
            Assert.True(await Roles.PushAsync(8));
            Assert.Equal(["POST /api/v10/oauth2/token", $"PUT /api/v10/users/@me/applications/{AppId}/role-connection"], discord.Asked.Select(c => c.Line));
            var refresh = discord.Asked[0].Form;  // the stored refresh token, as Discord gave it, with the application's credentials; every refresh rotates it
            Assert.Equal(("refresh_token", $"refresh-{n}", AppId, "test-secret"), (refresh["grant_type"], refresh["refresh_token"], refresh["client_id"], refresh["client_secret"]));
            Assert.Equal(($"access-{n + 1}", score), (discord.Asked[1].Bearer, (int)discord.Asked[1].Json!["metadata"]!["hunterscore"]!));
            Assert.InRange((long)LinkRow(8)[3] - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 604000, 604800);
            Assert.Equal((long)score, LinkRow(8)[4]);
        }
    }

    [Fact]
    public async Task TestARevokedRefreshTokenIsDroppedLoggedAndBreaksNothing()
    {
        var cookie = SessionOf(8);
        Assert.Equal(Redirect + "linked", await Callback(cookie));
        Expire(8);
        SetHunterscore(8, 30000);
        discord.Asked.Clear();
        // a refused client (a wrong secret) is the operator's to fix: the player's tokens stay
        (discord.RefreshStatus, discord.RefreshError) = (401, "invalid_client");
        Assert.False(await Roles.PushAsync(8));
        Assert.NotNull(LinkRow(8)[2]);
        // Discord no longer accepts the refresh token: the tokens go, the link stays
        (discord.RefreshStatus, discord.RefreshError) = (400, "invalid_grant");
        Assert.False(await Roles.PushAsync(8));
        var link = LinkRow(8);
        Assert.Equal((discord.UserId, true, true), ((string)link[0], link[1] is DBNull, link[2] is DBNull));
        discord.Asked.Clear();
        Assert.False(await Roles.PushAsync(8));  // nothing left to try
        Assert.Equal(0, await Roles.SweepAsync());
        Assert.Empty(discord.Asked);
        Assert.Equal(1L, Count("discord_links"));  // the rules and the reward stand
        // linking again with new tokens takes the player back in
        Assert.Equal(Redirect + "rewarded", await Callback(cookie, code: "code-9"));
        Assert.Equal((false, 30000L), (LinkRow(8)[2] is DBNull, (long)LinkRow(8)[4]));
        Assert.Equal(33.0 + 2500, Gm(8));
    }

    [Fact]
    public async Task TestTheSweepPushesTheLinkedAccountsWhoseHunterscoreChangedAndOnlyThem()
    {
        var (a, b) = (SessionOf(8), SessionOf(9));
        SetHunterscore(8, 100);
        SetHunterscore(9, 300);
        Assert.Equal(Redirect + "linked", await Callback(a));
        discord.UserId = "222222222222222222";
        Assert.Equal(Redirect + "linked", await Callback(b));
        discord.Asked.Clear();
        Assert.Equal(0, await Roles.SweepAsync());  // nothing changed since the pushes of the link
        Assert.Empty(discord.Asked);

        SetHunterscore(8, 20000);  // passes the Veteran threshold later, with no new link
        Assert.Equal(1, await Roles.SweepAsync());
        var put = Assert.Single(discord.Asked);
        Eq("""{"platform_name": "Hunter Resurgence", "platform_username": "second", "metadata": {"hunterscore": 20000}}""", put.Json);
        Assert.Equal("access-1", put.Bearer);  // the user's own token of account 8
        Assert.Equal(0, await Roles.SweepAsync());  // pushed: not again until it changes
        SetHunterscore(8, 19000);  // and a lower value is pushed too
        SetHunterscore(9, 301);
        Assert.Equal(2, await Roles.SweepAsync());
        Assert.Equal([19000, 301], discord.Puts.Skip(1).Select(c => (int)c.Json!["metadata"]!["hunterscore"]!));
    }

    [Fact]
    public async Task TestAFailedPushIsTriedAgainAtTheNextSweepAndNeverBreaksTheLink()
    {
        discord.PutStatus = 500;  // Discord is failing while the player links: the link and the reward stand, the push waits
        Assert.Equal(Redirect + "linked", await Callback(SessionOf(8)));
        Assert.Equal(33.0 + 2500, Gm(8));
        Assert.IsType<DBNull>(LinkRow(8)[4]);
        Assert.Equal(0, await Roles.SweepAsync());
        discord.Down = true;
        Assert.False(await Roles.PushAsync(8));
        Assert.Equal(0, await Roles.SweepAsync());
        (discord.Down, discord.PutStatus) = (false, 200);
        Assert.Equal(1, await Roles.SweepAsync());
        Assert.NotEqual(DBNull.Value, LinkRow(8)[4]);
        // a 401 on the push means the access token is not good: the next push refreshes it
        discord.PutStatus = 401;
        SetHunterscore(8, 5);
        Assert.False(await Roles.PushAsync(8));
        Assert.Equal(0L, LinkRow(8)[3]);
        discord.PutStatus = 200;
        discord.Asked.Clear();
        Assert.True(await Roles.PushAsync(8));
        Assert.Equal("POST", discord.Asked[0].Method);
    }

    // ---- the schema and the settings

    [Fact]
    public void TestTheSchemaOfVersion37GetsTheDiscordTables()
    {
        var file = Path.Combine(tmp, "v37.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open())
        {
            con.Exec("DROP TABLE discord_rewards");
            con.Exec("DROP TABLE discord_links");
            con.Exec("PRAGMA user_version = 37");
        }
        var migrated = new Store(file);
        using var check = migrated.Open();
        Assert.Equal(2L, check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('discord_links', 'discord_rewards')"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
        Assert.Equal(5L, check.Scalar("SELECT id FROM accounts WHERE handle = 'old'"));
    }
}
