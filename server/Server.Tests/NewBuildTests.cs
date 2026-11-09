using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The current client build: one host, lower-case paths, JSON bodies, signed JWTs. Through the real HTTP pipeline.</summary>
public class NewBuildTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const string Host = "game_web.test";  // the game's own web host; our website is web.test
    const long Account = Store.FirstServerId;
    readonly FakeSteam fake = new();
    Loopback server = null!;
    Game game = null!;

    public async ValueTask InitializeAsync() => await StartAsync(offline: false);

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    async Task StartAsync(bool offline)
    {
        if (server is not null) await server.DisposeAsync();
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, discounts: Fixtures.DISCOUNTS, offline: offline);
        if (!offline) state.CreateAccount(Account, "tester");
        game = new Game(new HarTable(), Cfg, state, tmp);
        server = await Loopback.StartAsync(game, tmp, steamHttp: new HttpClient(fake));
    }

    // ---- helpers

    static (string, string) Bearer(string token) => ("Authorization", "Bearer " + token);

    static (string, string) Cookie(string name, string value) => ("Cookie", $"{name}={value}");

    static JsonNode Body(Reply r) => JsonNode.Parse(r.Body)!;

    Task<Reply> Get(string path, params (string, string)[] headers) => server.SendAsync("GET", Host, path, headers);

    /// <summary>A POST as the current build sends it: a JSON body.</summary>
    Task<Reply> Post(string path, JsonNode body, params (string, string)[] headers) =>
        server.SendAsync("POST", Host, path, [("Content-Type", "application/json; charset=UTF-8"), .. headers], Encoding.UTF8.GetBytes(body.ToJsonString()));

    static JsonNode Part(string jwt, int index)
    {
        var text = jwt.Split('.')[index].Replace('-', '+').Replace('_', '/');
        return JsonNode.Parse(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '=')))!;
    }

    static JsonNode Claims(string jwt) => Part(jwt, 1);

    static string Cookie(Reply r, string name) =>
        r.Headers.Where(h => h.Name == "Set-Cookie" && h.Value.StartsWith(name + "=")).Select(h => h.Value).Single();

    /// <summary>What the launcher page does first: the game's tokens from the refresh cookie.</summary>
    async Task<(string Access, string Refresh)> Tokens(long account = Account)
    {
        var reply = await Post("/auth/jwt/refresh", new JsonObject { ["application_token"] = "x" }, Cookie(ProtocolLiterals.RefreshCookie, game.Tokens!.Refresh(account, 1)), ("X-App-Id", "client"));
        Assert.Equal(200, reply.Status);
        return ((string)Body(reply)["access_token"]!, (string)Body(reply)["refresh_token"]!);
    }

    // ---- tokens

    [Fact]
    public void TestTheJwtsAreSignedAndTheAccessOneLastsFifteenMinutes()
    {
        var tokens = game.Tokens!;
        var access = tokens.Access(Account, GameTokens.GameScope);
        var claims = Claims(access);
        Assert.Equal((Account, ProtocolLiterals.JwtIssuer, 4, "access", Account), ((long)claims["sub"]!, (string)claims["iss"]!, (int)claims["scp"]!, (string)claims["typ"]!, (long)claims["user_id"]!));
        Assert.Equal(900, (long)claims["exp"]! - (long)claims["iat"]!);
        Assert.NotEqual((string)claims["jti"]!, (string)Claims(tokens.Access(Account, 4))["jti"]!);
        Assert.Equal("HS256", (string)Part(access, 0)["alg"]!);
        Assert.Equal(Account, state.Store.ResolveToken(access));  // kept like every token: a ban or a delete ends it

        var refresh = tokens.Refresh(Account, 1);
        Assert.Equal(Account, tokens.RefreshedAccount(refresh));
        Assert.Equal(2_592_000, (long)Claims(refresh)["exp"]! - (long)Claims(refresh)["iat"]!);
        Assert.Null(tokens.RefreshedAccount(access));  // an access token does not refresh
        var parts = refresh.Split('.');
        var tampered = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];  // always differs from the real signature
        Assert.Null(tokens.RefreshedAccount($"{parts[0]}.{parts[1]}.{tampered}"));  // wrong signature
        Assert.Null(tokens.RefreshedAccount(tokens.Sign(Account, "refresh", 1, TimeSpan.FromSeconds(-5))));  // expired
        Assert.Null(tokens.RefreshedAccount("not.a.jwt"));
        Assert.Null(tokens.RefreshedAccount("x"));
        using (var con = state.Store.Open()) con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", Account);
        Assert.Null(tokens.RefreshedAccount(refresh));  // a banned account's tokens end
    }

    [Fact]
    public async Task TestRefreshNeverReturnsAnExpiredToken()
    {
        var refresh = game.Tokens!.Refresh(Account, 1);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (app, scope) in new[] { ("client", 4), ("web", 1) })
        {
            var reply = await Post("/auth/jwt/refresh", new JsonObject { ["application_token"] = "x" }, Cookie(ProtocolLiterals.RefreshCookie, refresh), ("X-App-Id", app));
            Assert.Equal(200, reply.Status);
            var body = Body(reply);
            var (access, newRefresh) = ((string)body["access_token"]!, (string)body["refresh_token"]!);
            Assert.True((long)Claims(access)["exp"]! >= before + 899, "fresh: now + 900 s");
            Assert.Equal(scope, (int)Claims(access)["scp"]!);
            Assert.Equal(Account, state.Store.ResolveToken(access));
            Assert.Equal(Account, game.Tokens!.RefreshedAccount(newRefresh));
            Assert.Equal(63072000, (long)body["expires_in"]!);
            Assert.Matches($@"^{ProtocolLiterals.AccessCookie}=[\w.-]+; HttpOnly; Max-Age=63072000; Path=/; SameSite=lax; Secure$", Cookie(reply, ProtocolLiterals.AccessCookie));
            Assert.Matches($@"^{ProtocolLiterals.RefreshCookie}=[\w.-]+; HttpOnly; Max-Age=2592000; Path=/auth/jwt/refresh; SameSite=lax; Secure$", Cookie(reply, ProtocolLiterals.RefreshCookie));
        }
        // the refresh token may also come in the body
        Assert.Equal(200, (await Post("/auth/jwt/refresh", new JsonObject { ["application_token"] = "x", ["refresh_token"] = refresh })).Status);
        // nothing that refreshes: an expired or foreign token, a bare access token, nothing at all
        var expired = game.Tokens!.Sign(Account, "refresh", 1, TimeSpan.FromSeconds(-5));
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject(), Cookie(ProtocolLiterals.RefreshCookie, expired))).Status);
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject(), Cookie(ProtocolLiterals.RefreshCookie, "not-a-token"))).Status);
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject(), Bearer(game.Tokens!.Access(Account, 4)))).Status);
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject(), ("X-Hun-Session", game.Tokens!.Access(Account, 4)))).Status);  // a JWT is no session
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject())).Status);
        // a session id is no credential either
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject(), ("X-Hun-Session", state.Store.CreateToken(Account, Lifetimes.Session)), ("X-App-Id", "client"))).Status);
        Assert.Equal(401, (await Post("/auth/jwt/refresh", new JsonObject { ["refresh_token"] = game.Tokens!.Access(Account, 4) })).Status);  // nor an access JWT in the body
    }

    [Fact]
    public async Task TestALocalServerRefreshesForItsOneAccount()
    {
        await StartAsync(offline: true);
        var reply = await Post("/auth/jwt/refresh", new JsonObject());
        Assert.Equal(200, reply.Status);
        Assert.Equal(state.LocalAccount, (long)Claims((string)Body(reply)["access_token"]!)["sub"]!);
    }

    // ---- start of a session

    [Fact]
    public async Task TestStartSinglePlayerGivesASessionAndALaunchUrlTheGameCanUse()
    {
        var (access, _) = await Tokens();
        var body = new JsonObject { ["oauth_access_token"] = access, ["time"] = 480, ["loadout_id"] = 0, ["lodge_id"] = 101, ["reserve"] = 2 };
        Assert.Equal("state", (await Post("/v1/session/start_single_player?locale=en_US", body)).Source);  // the token in the body alone
        var reply = await Post("/v1/session/start_single_player?locale=en_US", body, Bearer(access));
        Assert.Equal(("state", 200), (reply.Source, reply.Status));
        var (session, network) = ((string)Body(reply)["session_id"]!, (long)Body(reply)["network_id"]!);
        Assert.Matches("^[0-9a-f]{40}$", session);
        Assert.Equal(Account, state.Store.ResolveToken(session));
        Assert.Contains($"{ProtocolLiterals.LaunchSessionCookie}={session}; Path=/;", reply.Headers.Single(h => h.Value.StartsWith(ProtocolLiterals.LaunchSessionCookie) && h.Value.Contains("Path=/;")).Value);
        Assert.Contains(reply.Headers, h => h.Value.StartsWith($"{ProtocolLiterals.LaunchNetworkCookie}={network}; Path=/v1/session/;"));
        using var con = state.Store.Open(Account);
        Assert.Equal((101L, 2L), ((long)con.Scalar("SELECT location_id FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1")!,
                                  (long)con.Scalar("SELECT reserve FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1")!));  // the lodge of the lodge_id

        // the launch URL the game is started with: its tokens are the player's
        var url = game.Tokens!.LaunchUrl(Account, session);
        var m = System.Text.RegularExpressions.Regex.Match(url, $@"\A{ProtocolLiterals.LaunchScheme}://game_web\.test/{session}/lang/en_US/access_token/([\w.-]+)/refresh_token/([\w.-]+)\z");
        Assert.True(m.Success, url);
        Assert.Equal(Account, state.Store.ResolveToken(m.Groups[1].Value));
        Assert.Equal(Account, game.Tokens.RefreshedAccount(m.Groups[2].Value));
        Assert.Equal((4, 4), ((int)Claims(m.Groups[1].Value)["scp"]!, (int)Claims(m.Groups[2].Value)["scp"]!));
    }

    [Fact]
    public async Task TestConnectAndHeartbeatTakeTheBearerAndTheSessionOnTheWebHost()
    {
        var session = state.Store.CreateToken(Account, Lifetimes.Session);
        var (access, _) = await Tokens();
        var client = new[] { Bearer(access), ("X-Hun-Session", session), ("X-App-Id", "client") };
        var connect = await Post(ProtocolLiterals.WebRpcBase + "/connect", new JsonObject { ["client_version"] = 1, ["g_data"] = new JsonObject(), ["suspicious_dll"] = 0 }, client);
        Assert.Equal(("state", 200), (connect.Source, connect.Status));
        Assert.Equal(Account, (long)Body(connect)["account"]!["user_id"]!);
        // a heartbeat with the session alone, and one with the bearer alone
        var events = new JsonObject { ["events"] = new JsonArray(), ["xpos"] = 0 };
        Assert.Equal("state", (await Post(ProtocolLiterals.WebRpcBase + "/heartbeat", events, ("X-Hun-Session", session))).Source);
        Assert.Equal("state", (await Post(ProtocolLiterals.WebRpcBase + "/heartbeat", events, Bearer(access))).Source);
        Assert.Equal("state", (await Post(ProtocolLiterals.WebRpcBase.ToUpperInvariant() + "/HEARTBEAT", events, Bearer(access))).Source);  // any case
        Assert.Equal(401, (await Post(ProtocolLiterals.WebRpcBase + "/heartbeat", events)).Status);  // no token on a community server
    }

    [Fact]
    public async Task TestTheFirstHeartbeatsZeroQuantityRowsChangeNothing()
    {
        var (access, _) = await Tokens();
        var before = Py.Dumps(state.Inventory(Account));
        var rows = new JsonArray(state.Inventory(Account).Select(i => (JsonNode?)new JsonObject
            { ["event"] = 6, ["item"] = (string)i!["guid"]!, ["parent_item"] = "", ["quantity"] = 0 }).ToArray());
        rows.Add(new JsonObject { ["event"] = 101, ["direction"] = "enter", ["location_name"] = "TEST_LODGE", ["x"] = 1, ["y"] = 2, ["z"] = 3 });
        var reply = await Post(ProtocolLiterals.WebRpcBase + "/heartbeat", new JsonObject { ["events"] = rows }, Bearer(access));
        Assert.Equal(("state", 0), (reply.Source, (int)Body(reply)["err"]!));
        Assert.Equal(before, Py.Dumps(state.Inventory(Account)));
        // the same row with a quantity still uses the item up
        await Post(ProtocolLiterals.WebRpcBase + "/heartbeat", new JsonObject { ["events"] = new JsonArray(new JsonObject { ["event"] = 6, ["item"] = "200", ["parent_item"] = "", ["quantity"] = 3 }) }, Bearer(access));
        Assert.Equal(7, (long)state.Inventory(Account).Single(i => (string)i!["guid"]! == "200")!["units"]!);
    }

    // ---- the pages' calls

    [Fact]
    public async Task TestMeReadWithGetAnswersEachAccountItsOwnProfile()
    {
        const long other = Account + 1;
        state.CreateAccount(other, "second");
        foreach (var (id, handle) in new[] { (Account, "tester"), (other, "second") })
        {
            var me = Body(await Get("/v1/Me/me?locale=en_US", Bearer((await Tokens(id)).Access)));
            Assert.Equal((id, handle), ((long)me["id"]!, (string)me["handle"]!));
        }
        Assert.NotEqual("state", (await Get("/v1/Me/me")).Source);  // no token: no profile
    }

    [Fact]
    public async Task TestLowerCasePathsAndJsonBodiesReachTheHandlers()
    {
        var (access, _) = await Tokens();
        var token = new JsonObject { ["oauth_access_token"] = access };
        var poll = Body(await Post("/v1/me/poll?locale=en_US", token));
        Assert.Equal(["active_campaign", "campaign_stylesheet", "community_campaign_stylesheet", "last_hunt_update_ts"],
            new[] { "active_campaign", "campaign_stylesheet", "community_campaign_stylesheet", "last_hunt_update_ts" }.Where(k => poll[k] is not null));
        Assert.Equal(182, (double)poll["ems"]!);
        Assert.NotNull(Body(await Post("/v1/me/me?locale=en_US", token))["handle"]);

        // the in-game inventory page's move: `mapping` is a string in the JSON body, the reply `null`
        var move = await Post("/v1/inventory/move_multiple2?locale=en_US", new JsonObject
            { ["mapping"] = """[{"item_guid": 200, "slot_id": 5}]""", ["clonedItems"] = "[]", ["oauth_access_token"] = access }, Bearer(access));
        Assert.Equal(("state", "null"), (move.Source, Encoding.UTF8.GetString(move.Body)));
        Assert.Equal(5, (long)state.Inventory(Account).Single(i => (string)i!["guid"]! == "200")!["slot"]!);
        Assert.Equal("""{"success": false}""", Encoding.UTF8.GetString((await Post("/v1/inventory/buy?locale=en_US", new JsonObject { ["item_id"] = 999999, ["amount"] = 1, ["wallet"] = "GOLD" }, Bearer(access))).Body));

        Assert.Equal("""{"active": [], "completed": [], "available": [], "states": {}}""", Encoding.UTF8.GetString((await Post("/v1/mission/missions?locale=en_US", token)).Body));
        Assert.Equal("[]", Encoding.UTF8.GetString((await Post("/v1/network/list", new JsonObject(), Bearer(access))).Body));
        Assert.Equal("[]", Encoding.UTF8.GetString((await Get("/v1/game/user/loadout?locale=en_US", Bearer(access))).Body));
        // any case reaches the same handler
        Assert.Equal("state", (await Post("/v1/Me/Poll", token)).Source);
    }

    [Fact]
    public async Task TestExpeditionListSaysHasMoreInsteadOfTotal()
    {
        var (access, _) = await Tokens();
        for (var i = 0; i < 3; i++)
            await Post("/v1/session/start_single_player", new JsonObject { ["oauth_access_token"] = access, ["lodge_id"] = 101, ["reserve"] = 2 });
        async Task<JsonNode> List(int limit) => Body(await Post("/v1/expedition/list?locale=en_US", new JsonObject { ["user_id"] = Account, ["limit"] = limit, ["offset"] = 0 }, Bearer(access)));
        var page = await List(2);
        Assert.Equal((2, true), (page["expeditions"]!.AsArray().Count, (bool)page["has_more"]!));
        page = await List(10);
        Assert.Equal((3, false), (page["expeditions"]!.AsArray().Count, (bool)page["has_more"]!));
        // another player's list is the empty reply, which says it too
        var other = Body(await Post("/v1/expedition/list", new JsonObject { ["user_id"] = 5, ["limit"] = 10, ["offset"] = 0 }, Bearer(access)));
        Assert.Equal(("[]", false), (other["expeditions"]!.ToJsonString(), (bool)other["has_more"]!));
    }

    [Fact]
    public async Task TestTheInGameCatalogueAndTheNewsFeed()
    {
        var (access, _) = await Tokens();
        var catalogue = Body(await Post("/v1/application/client_application?locale=en_US", new JsonObject { ["lang"] = "en_us" }, Bearer(access)));
        Assert.Equal(["items", "locale", "locations", "mission_groups", "missions", "reserves", "season", "species"], catalogue.AsObject().Select(kv => kv.Key).Order());
        Assert.Equal("en_us", (string)catalogue["locale"]!);
        Assert.Equal("[241,14]", catalogue["species"]!.AsArray().Single(s => (long)s!["id"]! == 17)!["ethicalAmmo"]!.ToJsonString());  // the permitted ammo of the species
        Assert.Equal(APP["items"]!.AsArray().Count, catalogue["items"]!.AsArray().Count);

        var feed = Body(await Get("/v1/splash/feed_data?locale=en_US"));
        Assert.Equal((NoticeSubject, "[]"), ((string)feed["splashes"]![0]!["subject"]!, feed["posts"]!.ToJsonString()));
    }

    [Fact]
    public async Task TestTheLauncherPageIsTheSignInAndTheSteamTicketIsRefused()
    {
        var page = await Get("/launcher");
        Assert.Equal(200, page.Status);
        Assert.Contains("Sign in with Steam", Encoding.UTF8.GetString(page.Body));
        Assert.Contains("next=%2Flauncher", Encoding.UTF8.GetString(page.Body));  // the in-window sign-in comes back to this page
        var ticket = await Post("/auth/emotelauncher/token", new JsonObject { ["applications"] = new JsonArray(), ["session_id"] = "00" });
        Assert.Equal(401, ticket.Status);
        Assert.Equal("auth.invalid_steam_ticket", (string)Body(ticket)["error"]!["code"]!);
    }

    [Fact]
    public async Task TestTheLauncherSignsInOnOurSiteWhileItPollsTheGameHost()
    {
        static (string, string) Xhr() => ("X-Requested-With", "XMLHttpRequest");
        var link = Body(await server.SendAsync("POST", Host, "/auth/link", [Xhr()], []));
        var code = (string)link["code"]!;
        Assert.Equal($"https://web.test/auth/steam/login?link={code}", (string)link["url"]!);  // the player's browser goes to our site, not the game's host
        Assert.Equal("pending", (string)Body(await Get("/auth/link/" + code))["status"]!);

        // in the player's browser, on our site: Steam, the first name, the confirmation
        var start = await server.SendAsync("GET", "web.test", "/auth/steam/login?link=" + code);
        var login = Cookie(start, "hr_login").Split(';')[0]["hr_login=".Length..];
        var returnTo = System.Web.HttpUtility.ParseQueryString(new Uri(start.Headers.Single(h => h.Name == "Location").Value).Query)["openid.return_to"]!;
        var back = await server.SendAsync("GET", "web.test", returnTo["https://web.test".Length..] + "&" + FakeSteam.Assertion(returnTo, "99999999999999992"), [Cookie("hr_login", login)]);
        Assert.Equal("/auth/handle", back.Headers.Single(h => h.Name == "Location").Value);
        Reply Form(string path, string form) => server.SendAsync("POST", "web.test", path,
            [("Content-Type", "application/x-www-form-urlencoded"), ("Origin", "https://web.test"), Cookie("hr_login", login)], Encoding.UTF8.GetBytes(form)).Result;
        Assert.Equal("/auth/confirm", Form("/auth/handle", $"csrf={login}&handle=Launched").Headers.Single(h => h.Name == "Location").Value);
        Assert.Equal(200, Form("/auth/confirm", $"csrf={login}").Status);

        // the launcher, on the game's host, now gets its session and a launch URL on that host
        var poll = await Get("/auth/link/" + code);
        Assert.Equal("ok", (string)Body(poll)["status"]!);
        var session = Cookie(poll, "hr_session").Split(';')[0]["hr_session=".Length..];
        var launch = await server.SendAsync("POST", Host, "/auth/launch", [Xhr(), ("Origin", "https://" + Host), Cookie("hr_session", session)], []);
        Assert.StartsWith($"{ProtocolLiterals.LaunchScheme}://{Host}/", (string)Body(launch)["url"]!);
    }

    [Fact]
    public async Task TestPhotoLinksOnTheGameWebHostReachTheUploadHandler()
    {
        var reply = await server.SendAsync("POST", Host, State.UPLOAD_PATH + "?uploadType=resumable&upload_id=999", [Bearer(game.Tokens!.Access(Account, 4))], [1, 2, 3]);
        Assert.Equal(("state", 404), (reply.Source, reply.Status));  // the state's own "no such upload"
    }

    [Fact]
    public void TestARecordingOfTheCurrentBuildIsFoundUnderTheHandlersKeys()
    {
        var table = new HarTable();
        var hit = new Hit(2, "", 200, [], "{}"u8.ToArray());
        table.Entries[new("POST", "game_web.test", "/v1/me/poll", "x")] = hit;
        table.Entries[new("POST", "game_web.test", "/v1/wallet/bank_transactions", null)] = hit;
        table.Entries[new("POST", "game_web.test", ProtocolLiterals.WebRpcBase + "/connect", null)] = hit;
        table.Entries[new("GET", "game_web.test", "/client", null)] = hit;
        NewBuild.Alias(table, Cfg);
        Assert.Same(hit, table.Entries[new("POST", "api.test", POLL, "x")]);  // the handler's case
        Assert.Same(hit, table.Entries[new("POST", "api.test", "/v1/wallet/bank_transactions", null)]);
        Assert.Same(hit, table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)]);
        Assert.False(table.Entries.ContainsKey(new("GET", "api.test", "/client", null)));  // a page stays where it is
    }

    [Fact]
    public void TestTheConnectReplyNeverCarriesAnEventOrItsAmmo()
    {
        var connect = Fixtures.CONNECT;
        connect["ingame_event"] = new JsonObject { ["name"] = "season", ["define"] = "SEASON", ["reserves"] = new JsonArray(2) };
        connect["species_ammo"] = new JsonArray(new JsonObject { ["species_id"] = "17", ["ammo"] = new JsonArray(999) });  // a silver-only row
        var own = new State(Path.Combine(tmp, "event.db"), Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES);
        var reply = JsonNode.Parse(own.ConnectReply(own.LocalAccount))!;
        Assert.Null(reply["ingame_event"]);
        Assert.Equal("[241,14]", reply["species_ammo"]![0]!["ammo"]!.ToJsonString());
    }

    // ---- what a recording may answer

    [Theory]
    [InlineData("/v1/application/client_application", true)]
    [InlineData("/v1/splash/feed_data", true)]
    [InlineData("/client", true)]
    [InlineData("/launcher", true)]
    [InlineData("/static/app/3.0.81/client.js", true)]
    [InlineData("/media/avatar/doc_64.jpg", true)]
    [InlineData("/v1/game/user/loadout", false)]
    [InlineData("/v1/mission/missions", false)]
    [InlineData("/v1/me/poll", false)]
    [InlineData("/v1/expedition/list", false)]
    [InlineData("/v1/network/list", false)]
    public void TestEveryNewPathIsClassified(string path, bool global)
    {
        var host = path.StartsWith("/v1/", StringComparison.Ordinal) ? game.Api : game.GameWeb;
        var rule = ReplayScope.Find(game, new Req("GET", host, path, "", _ => null, [], true));
        Assert.NotNull(rule);
        Assert.Equal(global, rule.Global);
    }

    [Fact]
    public void TestAPathTheTableDoesNotKnowIsPerPlayer()
    {
        var unknown = new Req("POST", game.Api, "/v1/brand/new", "", _ => null, [], true);
        Assert.Null(ReplayScope.Find(game, unknown));
        Assert.True(ReplayScope.Denied(game, unknown));
    }

    static async Task<(DefaultHttpContext Ctx, byte[] Body)> Rewritten(Game g, string target, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>(new Microsoft.AspNetCore.Http.Features.HttpRequestFeature { RawTarget = target });
        ctx.Request.Host = new HostString("game_web.test");
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = contentType;
        ctx.Request.ContentLength = bytes.Length;
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.Path = target[..target.IndexOf('?')];
        await NewBuild.Rewrite(ctx, g);
        using var read = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(read);
        return (ctx, read.ToArray());
    }

    [Fact]
    public async Task TestAJsonBodyBecomesAFormWhateverItsMembersHold()
    {
        var g = new Game(new HarTable(), Cfg, null, tmp);
        var (ctx, body) = await Rewritten(g, "/v1/me/poll?x=1", """{"a b&c":"x&y=z","n":null,"arr":[1,2],"b":true}""", "application/json");
        var form = Py.ParseQs(Encoding.UTF8.GetString(body));
        Assert.True(form.ContainsKey("a b&c"), Encoding.UTF8.GetString(body));
        Assert.Equal(["x&y=z"], form["a b&c"]);
        Assert.Equal(["[1,2]"], form["arr"]);
        Assert.Equal(["true"], form["b"]);
        Assert.Equal(("api.test", "/v1/Me/poll", "x=1"), (ctx.Request.Host.Host, ctx.Request.Path.Value, ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget.Split('?')[1]));
        foreach (var unchanged in new[] { "not json", "[1,2]" })
            Assert.Equal(unchanged, Encoding.UTF8.GetString((await Rewritten(g, "/v1/me/poll?x=1", unchanged, "application/json")).Body));
    }
}
