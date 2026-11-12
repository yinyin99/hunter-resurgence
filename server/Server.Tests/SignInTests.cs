using System.Buffers.Text;
using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Data.Sqlite;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Steam's answer to check_authentication, and what it was sent.</summary>
sealed class FakeSteam : HttpMessageHandler
{
    public string Answer = "ns:http://specs.openid.net/auth/2.0\nis_valid:true\n";
    public bool Down;
    public readonly List<Dictionary<string, string>> Asked = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(SteamOpenId.Endpoint, request.RequestUri!.ToString());
        if (Down) throw new HttpRequestException("down");
        Asked.Add(HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync(cancellationToken).Result).AllKeys.ToDictionary(k => k!,
            k => HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync(cancellationToken).Result)[k]!));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) });
    }

    /// <summary>What Steam sends the browser back with: a query string for this return_to.</summary>
    public static string Assertion(string returnTo, string steamId = "99999999999999991", string? nonce = null, string? claimed = null,
        string signed = "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle", string? endpoint = null)
    {
        claimed ??= $"https://steamcommunity.com/openid/id/{steamId}";
        nonce ??= DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + Guid.NewGuid().ToString("N");
        (string, string)[] p =
        [
            ("openid.ns", "http://specs.openid.net/auth/2.0"), ("openid.mode", "id_res"),
            ("openid.op_endpoint", endpoint ?? SteamOpenId.Endpoint), ("openid.claimed_id", claimed), ("openid.identity", claimed),
            ("openid.return_to", returnTo), ("openid.response_nonce", nonce), ("openid.assoc_handle", "1234567890"),
            ("openid.signed", signed), ("openid.sig", "c2ln+/="),
        ];
        return string.Join('&', p.Select(x => x.Item1 + "=" + Uri.EscapeDataString(x.Item2)));
    }
}

public class SteamOpenIdTests
{
    const string ReturnTo = "https://web.test/auth/steam/return?login=abc";
    readonly FakeSteam fake = new();
    DateTimeOffset clock = DateTimeOffset.UtcNow;
    readonly SteamOpenId steam;

    public SteamOpenIdTests() => steam = new SteamOpenId(new HttpClient(fake), () => clock);

    string Query(string? nonce = null, string? claimed = null, string? signed = null, string? endpoint = null, string? returnTo = null) =>
        "login=abc&" + FakeSteam.Assertion(returnTo ?? ReturnTo, nonce: nonce ?? clock.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "x" + Guid.NewGuid().ToString("N"),
            claimed: claimed, signed: signed ?? "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle", endpoint: endpoint);

    [Fact]
    public async Task TestAGoodAssertionGivesTheSteamIdAfterSteamConfirmedIt()
    {
        Assert.Equal("99999999999999991", await steam.VerifyAsync(Query(), ReturnTo));
        var asked = Assert.Single(fake.Asked);
        Assert.Equal("check_authentication", asked["openid.mode"]);  // what was signed goes back unchanged, only the mode differs
        Assert.Equal("c2ln+/=", asked["openid.sig"]);
        Assert.Equal(ReturnTo, asked["openid.return_to"]);
    }

    [Fact]
    public async Task TestOnlyASteamCommunityIdOf17DigitsIsAccepted()
    {
        foreach (var claimed in new[]
        {
            "https://steamcommunity.com/openid/id/9999999999999999",       // 16 digits
            "https://steamcommunity.com/openid/id/999999999999999912",     // 18
            "https://steamcommunity.com/openid/id/99999999999999991/x",
            "http://steamcommunity.com/openid/id/99999999999999991",
            "https://steamcommunity.com.evil.test/openid/id/99999999999999991",
            "https://evil.test/https://steamcommunity.com/openid/id/99999999999999991",
            "https://steamcommunity.com/openid/id/9999999999999999a",
        })
            Assert.Null(await steam.VerifyAsync(Query(claimed: claimed), ReturnTo));
        Assert.Empty(fake.Asked);  // refused before Steam is asked
    }

    [Fact]
    public async Task TestTheAssertionMustBeForThisServersReturnUrlAndSteamsEndpoint()
    {
        Assert.Null(await steam.VerifyAsync(Query(returnTo: "https://evil.test/auth/steam/return?login=abc"), ReturnTo));
        Assert.Null(await steam.VerifyAsync(Query(), "https://web.test/auth/steam/return?login=other"));
        Assert.Null(await steam.VerifyAsync(Query(endpoint: "https://evil.test/openid/login"), ReturnTo));
        Assert.Empty(fake.Asked);
    }

    [Fact]
    public async Task TestTheSignatureMustCoverTheIdentityAndTheNonce()
    {
        foreach (var missing in new[] { "claimed_id", "identity", "return_to", "response_nonce", "op_endpoint", "assoc_handle" })
            Assert.Null(await steam.VerifyAsync(Query(signed: "signed," + string.Join(',',
                new[] { "op_endpoint", "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle" }.Where(f => f != missing))), ReturnTo));
    }

    [Fact]
    public async Task TestARepeatedFieldIsRefused()
    {
        Assert.Null(await steam.VerifyAsync(Query() + "&openid.claimed_id=" + Uri.EscapeDataString("https://steamcommunity.com/openid/id/99999999999999992"), ReturnTo));
    }

    [Fact]
    public async Task TestANonceIsAcceptedOnceAndOnlyWhileFresh()
    {
        var nonce = clock.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "salt";
        Assert.NotNull(await steam.VerifyAsync(Query(nonce), ReturnTo));
        Assert.Null(await steam.VerifyAsync(Query(nonce), ReturnTo));  // the replay: Steam would say valid again, the server does not
        Assert.Null(await steam.VerifyAsync(Query(clock.AddMinutes(-6).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "old"), ReturnTo));
        Assert.Null(await steam.VerifyAsync(Query(clock.AddMinutes(6).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "future"), ReturnTo));
        Assert.Null(await steam.VerifyAsync(Query("not a time"), ReturnTo));
    }

    [Fact]
    public async Task TestSteamSayingNoOrBeingDownMeansNoSignIn()
    {
        fake.Answer = "ns:http://specs.openid.net/auth/2.0\nis_valid:false\n";
        Assert.Null(await steam.VerifyAsync(Query(), ReturnTo));
        fake.Down = true;
        Assert.Null(await steam.VerifyAsync(Query(), ReturnTo));
    }

    [Fact]
    public void TestTheLoginUrlAsksSteamToPickTheIdentityAndReturnHere()
    {
        var url = new Uri(SteamOpenId.LoginUrl(ReturnTo, "https://web.test/"));
        var q = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal(SteamOpenId.Endpoint, url.GetLeftPart(UriPartial.Path));
        Assert.Equal((ReturnTo, "https://web.test/", "checkid_setup"), (q["openid.return_to"], q["openid.realm"], q["openid.mode"]));
    }
}

public class HandleTests
{
    [Theory]
    [InlineData("Hunter_One")]
    [InlineData("abc")]
    [InlineData("a-b")]
    [InlineData("12345678901234567890")]
    public void TestGoodHandles(string handle) => Assert.Null(Handles.Problem(handle));

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("123456789012345678901")]
    [InlineData("_abc")]
    [InlineData("-abc")]
    [InlineData("two words")]
    [InlineData("naïve")]
    [InlineData("a<b>c")]
    [InlineData("abc\n")]
    public void TestBadHandles(string handle) => Assert.NotNull(Handles.Problem(handle));
}

/// <summary>The sign-in, the start page and the tokens through the real HTTP pipeline, with Steam faked.</summary>
public class SignInFlowTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly (string, string)[] Form = [("Content-Type", "application/x-www-form-urlencoded")];
    static readonly (string, string)[] Json = [("Content-Type", "application/json")];
    readonly FakeSteam fake = new();
    Loopback server = null!;

    public async ValueTask InitializeAsync() => await StartAsync(offline: false);

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    async Task StartAsync(bool offline, params (string Method, string Host, string Path, string Body)[] recorded)
    {
        if (server is not null) await server.DisposeAsync();
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, discounts: Fixtures.DISCOUNTS, offline: offline);
        var table = new HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Hit(2, "", 200, [], "{}"u8.ToArray());
        foreach (var (method, host, path, body) in recorded)
            table.Entries[new(method, host, path, null)] = new Hit(2, "", 200, [("Content-Type", "application/json")], Encoding.UTF8.GetBytes(body));
        server = await Loopback.StartAsync(new Game(table, Cfg, state, tmp), tmp, steamHttp: new HttpClient(fake));
    }

    // ---- helpers

    static string? Cookie(Reply r, string name) =>
        r.Headers.Where(h => h.Name == "Set-Cookie" && h.Value.StartsWith(name + "=")).Select(h => h.Value.Split(';')[0][(name.Length + 1)..]).FirstOrDefault();

    static string Location(Reply r) => r.Headers.Single(h => h.Name == "Location").Value;

    static string Body(Reply r) => Encoding.UTF8.GetString(r.Body);

    static (string, string) Ck(string name, string? value) => ("Cookie", $"{name}={value}");

    Task<Reply> Get(string host, string path, params (string, string)[] headers) => server.SendAsync("GET", host, path, headers);

    Task<Reply> PostForm(string host, string path, string body, params (string, string)[] headers) =>
        server.SendAsync("POST", host, path, [.. Form, ("Origin", "https://" + host), .. headers], Encoding.UTF8.GetBytes(body));

    Task<Reply> Xhr(string method, string host, string path, params (string, string)[] headers) =>
        server.SendAsync(method, host, path, [("X-Requested-With", "XMLHttpRequest"), .. headers], method == "POST" ? [] : null);

    /// <summary>The first half: the browser at /auth/steam/login, and Steam's return URL (path and query on the host) it will come back to.</summary>
    async Task<(string Login, string ReturnPath)> Begin(string host, string query = "", string path = "/auth/steam/login")
    {
        var start = await Get(host, path + query);
        Assert.Equal(302, start.Status);
        Assert.StartsWith(SteamOpenId.Endpoint + "?", Location(start));
        var returnTo = HttpUtility.ParseQueryString(new Uri(Location(start)).Query)["openid.return_to"]!;
        Assert.StartsWith($"https://{host}/auth/steam/return?login=", returnTo);
        return (Cookie(start, "hr_login")!, returnTo[("https://" + host).Length..]);
    }

    /// <summary>The second half: Steam confirms steamId and the browser comes back.</summary>
    Task<Reply> Return(string host, (string Login, string ReturnPath) begun, string steamId = "99999999999999991", string? nonce = null) =>
        Get(host, begun.ReturnPath + "&" + FakeSteam.Assertion("https://" + host + begun.ReturnPath, steamId, nonce), Ck("hr_login", begun.Login));

    /// <summary>A whole sign-in on the host: the session cookie of the browser (the handle is asked for at the first one).</summary>
    async Task<string> SignIn(string steamId = "99999999999999991", string handle = "Hunter_One", string host = "web.test", string query = "")
    {
        var begun = await Begin(host, query);
        var back = await Return(host, begun, steamId);
        Assert.Equal(302, back.Status);
        if (Location(back) == "/auth/handle")
        {
            Assert.Contains("Choose your name", Body(await Get(host, "/auth/handle", Ck("hr_login", begun.Login))));
            back = await PostForm(host, "/auth/handle", $"csrf={begun.Login}&handle={handle}", Ck("hr_login", begun.Login));
        }
        return Cookie(back, "hr_session") ?? throw new InvalidOperationException($"no session: {Location(back)}");
    }

    async Task<JsonNode?> Me(string cookie, string host = "web.test")
    {
        var me = await Get(host, "/auth/me", Ck("hr_session", cookie));
        return me.Status == 200 ? JsonNode.Parse(me.Body) : null;
    }

    // ---- the Steam sign-in

    [Fact]
    public async Task TestTheFirstSignInCreatesOneAccountAndTheSecondReusesIt()
    {
        var first = await SignIn();
        var me = await Me(first);
        Assert.Equal((Store.FirstServerId, "Hunter_One"), ((long)me!["userId"]!, (string)me["handle"]!));
        Assert.Equal((0L, 1L), ((long)me["hunterscore"]!, (long)me["rank"]!));  // the board leaves out the local account
        Assert.Equal(Store.FirstServerId, state.Store.AccountBySteam("99999999999999991"));
        Assert.Equal(Store.FirstServerId, state.Store.AccountByHandle("hunter_one"));
        // a new account starts with the server's starting data
        Eq("182.0", state.Wallet(Store.FirstServerId)["em"]);
        Assert.Equal((182L, 33L), ((long)me["ems"]!, (long)me["gms"]!));  // /auth/me carries the starting wallet in whole numbers

        var begun = await Begin("web.test", "?next=%2Ffeed");
        var again = await Return("web.test", begun);
        Assert.Equal(("/feed", 302), (Location(again), again.Status));  // no name is asked for again
        Assert.Equal(Store.FirstServerId, await Me(Cookie(again, "hr_session")!) is { } m ? (long)m["userId"]! : -1);
        Assert.Equal(Store.FirstServerId, state.Store.AccountBySteam("99999999999999991"));
        using var con = state.Store.Open();
        Assert.Equal(2L, con.Scalar("SELECT COUNT(*) FROM accounts"));  // the local account and this one
    }

    [Fact]
    public async Task TestTheBoardListsHuntersByHunterscore()
    {
        await SignIn("99999999999999991", "one");
        await SignIn("99999999999999992", "two");
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open())
        {
            foreach (var (handle, score) in new[] { ("one", 50), ("two", 50), ("third", 70), ("banned", 900) })
                con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE handle = ?", score, handle);
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        }
        var board = JsonNode.Parse((await Get("web.test", "/hunters")).Body)!;
        // equal hunterscores: the lower account id first
        Eq("""{"page": 1, "pages": 1, "total": 3, "rows": [{"rank": 1, "handle": "third", "hunterscore": 70}, {"rank": 2, "handle": "one", "hunterscore": 50}, {"rank": 3, "handle": "two", "hunterscore": 50}]}""", board);
        Eq("1", JsonNode.Parse((await Get("web.test", "/hunters?page=9")).Body)!["page"]);
        var two = JsonNode.Parse((await Get("web.test", "/hunters/TWO")).Body)!;
        Eq("""["two", 50, 3, "Hunter"]""", T(two["handle"], two["hunterscore"], two["rank"], two["membership"]));
        Assert.EndsWith($"/256/{State.DefaultAvatar}.png", (string)two["avatar"]!);  // a new account's picture
        Assert.True(two.AsObject().ContainsKey("background") && two["background"] is null);  // and no header picture
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned")).Status);
    }

    [Fact]
    public async Task TestTheStatisticsTabIsAnyAccountsStatisticsByHandle()
    {
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        using (var con = state.Store.Open(9))
            PlayerRows.SetStatistics(con, O("""
                {"collectables": {}, "daily_missions": {}, "animal": {"1": {"spots": 3, "tracks": 2, "kills": 1}, "99": {"kills": 4}},
                 "weapon": {"5": {"10": {"hits": 3, "misses": 1, "kills": 1, "distance": 123000}}}}
                """), 5000, 7200, null, null);
        var reply = await Get("web.test", "/hunters/THIRD/statistics");
        Assert.Equal(200, reply.Status);
        var page = JsonNode.Parse(reply.Body)!;
        var w = page["weapons"]!.AsArray().Single()!;
        Assert.Equal(("Test Rifle", "Test Bullet", 4L, 75.0, 1L, 33.3, 123.0), ((string)w["weapon"]!, (string)w["ammo"]!, (long)w["shots"]!, (double)w["accuracy"]!, (long)w["kills"]!, (double)w["killsPerHit"]!, (double)w["longest"]!));
        // by name, species the catalogue does not know keep their id
        Assert.Equal([("99", 0L, 0L, 4L), ("Test Deer", 3L, 2L, 1L)],
            page["animals"]!.AsArray().Select(a => ((string)a!["name"]!, (long)a["spotted"]!, (long)a["tracked"]!, (long)a["harvested"]!)));
        Assert.Equal((5.0, 7200L, 0), ((double)page["distanceKm"]!, (long)page["huntSeconds"]!, page["collectables"]!.AsArray().Count));
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody/statistics")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned/statistics")).Status);
    }

    // a restore keeps the backup's statistics as they are: members of another type or counters of any size must not fail the tab
    [Fact]
    public async Task TestTheStatisticsTabSurvivesOddRestoredStatistics()
    {
        state.CreateAccount(9, "odd");
        using (var con = state.Store.Open(9))
            PlayerRows.SetStatistics(con, O("""
                {"collectables": [], "daily_missions": {}, "animal": {"1": 5, "2": {"kills": 99999999999, "spots": -3}}, "weapon": {"5": 3, "6": [], "7": {"10": {"hits": 1e30, "misses": "x"}}}}
                """), 5000, 1e30, null, null);
        var reply = await Get("web.test", "/hunters/odd/statistics");
        Assert.Equal(200, reply.Status);
        var page = JsonNode.Parse(reply.Body)!;
        Assert.Equal(int.MaxValue, (int)page["huntSeconds"]!);
        Assert.Equal([("2", 0, 0, int.MaxValue), ("Test Deer", 0, 0, 0)],  // a negative counter is 0, a member that is no object counts nothing
                     page["animals"]!.AsArray().Select(a => ((string)a!["name"]!, (int)a["spotted"]!, (int)a["tracked"]!, (int)a["harvested"]!)));
        Assert.Equal(int.MaxValue, (int)page["weapons"]!.AsArray().Single()!["shots"]!);
    }

    [Fact]
    public async Task TestTheHuntsTabsAreAnyAccountsHuntsAndScoreSheetsByHandle()
    {
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        state.NoteLaunch(9, "start=101&reserve=2"u8.ToArray());
        state.HeartbeatReply(9, Events(AccountIsolationTests.Harvest(81, 72270)));
        state.Disconnect(9, """{"distance_travelled": 466}"""u8.ToArray());

        var list = await Get("web.test", "/hunters/THIRD/hunts");
        Assert.Equal(200, list.Status);
        var page = JsonNode.Parse(list.Body)!;
        var row = page["rows"]!.AsArray().Single()!;
        Eq("""[1, 1, 1, "Test Reserve", "Lodge 1", 1]""", T(page["page"], page["pages"], page["total"], row["reserve"], row["lodge"], row["kills"]));
        Eq("1", JsonNode.Parse((await Get("web.test", "/hunters/third/hunts?page=7")).Body)!["page"]);  // a page past the last is the last

        var id = (long)row["id"]!;
        var hunt = JsonNode.Parse((await Get("web.test", $"/hunters/third/hunts/{id}")).Body)!;
        Assert.Equal(id, (long)hunt["id"]!);
        var kill = hunt["kills"]!.AsArray().Single()!;
        Eq("""["Test Deer", "Test Rifle", "Test Bullet", 96.14, 72.27, true]""", T(kill["species"], kill["weapon"], kill["ammo"], kill["distanceM"], kill["weightKg"], kill["sheet"]));
        Assert.Equal(id, (long)JsonNode.Parse((await Get("web.test", "/hunters/third/hunts/latest")).Body)!["id"]!);

        var sheet = await Get("web.test", $"/hunters/THIRD/score/{(long)kill["id"]!}");
        Assert.Equal(200, sheet.Status);
        var s = JsonNode.Parse(sheet.Body)!;
        Eq("""["Test Deer", "Test Rifle", true, "Idle", "Prone", 1]""", T(s["species"], s["weapon"], s["scope"], s["state"], s["stance"], s["shots"]));
        Eq("""[96.14, 72.27, 97, "Test Reserve (-9593, 8198)"]""", T(s["distanceM"], s["weightKg"], s["integrity"], s["shotLocation"]));

        foreach (var path in new[] { "/hunters/nobody/hunts", "/hunters/banned/hunts", "/hunters/third/hunts/999", "/hunters/third/hunts/x", "/hunters/banned/hunts/1", "/hunters/nobody/score/1",
                                     "/hunters/banned/score/1", "/hunters/third/score/999", "/hunters/third/score/x" })
            Assert.Equal((404, path), ((await Get("web.test", path)).Status, path));
    }

    [Fact]
    public async Task TestAnyAccountsSkillsAreServedToEveryone()
    {
        await SignIn("99999999999999991", "one");
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open(9))
        {  // the fixture's connect has no skills: a new account has the rows its hunts earn (species 0 and 1)
            foreach (var weapon in new[] { 5, 99 })
                con.Exec("INSERT OR REPLACE INTO skills (account_id, kind, key, xp) VALUES ($a, 'weapon', ?, ?)", weapon, Skills.Xp(1.14));
            foreach (var kind in new[] { "spotting", "tracking" })
                for (var key = 0; key < 60; key++)
                    con.Exec("INSERT OR REPLACE INTO skills (account_id, kind, key, xp) VALUES ($a, ?, ?, ?)", kind, key, kind == "spotting" && key == 1 ? Skills.Threshold(3) : Skills.FRESH_XP);
        }
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        var third = JsonNode.Parse((await Get("web.test", "/hunters/third/skills")).Body)!;
        Assert.Contains(third["species"]!.AsArray(), s => (double)s!["spotting"]! == 3 && (double)s["tracking"]! > 1);
        Assert.Equal(["mule_deer", "cottontail_rabbit"], third["species"]!.AsArray().Select(s => (string)s!["image"]!));  // the dog species and species 56 are left out
        Assert.Equal("""[{"id": 5, "name": "Test Rifle", "variation": null, "group": null, "value": 1.14}, {"id": 99, "name": "#99", "variation": null, "group": null, "value": 1.14}]""", Py.Dumps(third["weapons"]));  // an item the catalogue lacks is named by its id
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody/skills")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned/skills")).Status);
        // Skill/list of another account's user id: its numbers, not the empty reply
        var own = Store.FirstServerId;
        Assert.Equal(3.0, (double)JsonNode.Parse(state.SkillListReply(own, "user_id=9"u8.ToArray())!)!["spotting"]![1]!);
        Assert.Null(state.SkillListReply(own, "user_id=10"u8.ToArray()));  // banned
        Assert.Null(state.SkillListReply(own, "user_id=424242"u8.ToArray()));  // unknown
    }

    [Fact]
    public async Task TestAnyAccountsDogsAreServedToEveryone()
    {
        await SignIn("99999999999999991", "one");
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open(9))
        {
            PlayerRows.AddItem(con, 1, 700, 0, null, O("""{"dog_name": "Rex", "experience": 150.5}"""));
            PlayerRows.AddItem(con, 2, 700, 0, null, O("""{}"""));
            PlayerRows.AddItem(con, 3, 700, 0, null, O("""{"dog_name": "Old", "experience": 300}"""));
            PlayerRows.AddItem(con, 6, 700, 0, null, O("""{"dog_name": "Max", "experience": 900}"""));
            PlayerRows.AddItem(con, 4, 701, 0, null, O("""{"experience": 999}"""));  // not a dog
            PlayerRows.AddItem(con, 5, 5, 0, null, O("""{}"""));
        }
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        var dogs = JsonNode.Parse((await Get("web.test", "/hunters/third/dogs")).Body)!;
        // 150.5 of 100..300: level 2 and a quarter of the way; no experience: level 1; 300 starts the span to the row that is level 5 (its lvl, not the fourth row's number)
        // 700 is in no breed group: its unlocked skills, each once at the number of unlocks
        Eq("""
            {"dogs": [{"id": 700, "name": "Rex", "type": "Test Hound", "variation": null, "level": 2, "percent": 25, "ribbons": 1,
                       "skills": [{"skill": "LOYALTY_ONE", "title": "Loyalty One", "type": "loyalty", "level": 1}, {"skill": "POINT_TWO", "title": "Point Two", "type": "pointing", "level": 1}]},
                      {"id": 700, "name": "", "type": "Test Hound", "variation": null, "level": 1, "percent": 0, "ribbons": 0, "skills": [{"skill": "LOYALTY_ONE", "title": "Loyalty One", "type": "loyalty", "level": 1}]},
                      {"id": 700, "name": "Old", "type": "Test Hound", "variation": null, "level": 3, "percent": 0, "ribbons": 1,
                       "skills": [{"skill": "LOYALTY_ONE", "title": "Loyalty One", "type": "loyalty", "level": 1}, {"skill": "POINT_TWO", "title": "Point Two", "type": "pointing", "level": 1}]},
                      {"id": 700, "name": "Max", "type": "Test Hound", "variation": null, "level": 5, "percent": 100, "ribbons": 1,
                       "skills": [{"skill": "LOYALTY_ONE", "title": "Loyalty One", "type": "loyalty", "level": 1}, {"skill": "POINT_TWO", "title": "Point Two", "type": "pointing", "level": 1}]}]}
            """, dogs);
        Eq("""{"dogs": []}""", JsonNode.Parse((await Get("web.test", "/hunters/one/dogs")).Body));  // an account without a dog
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody/dogs")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned/dogs")).Status);
    }

    [Fact]
    public async Task TestAnyAccountsRanksAreServedToEveryone()
    {
        await SignIn("99999999999999991", "one");
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open(9))  // the species 19 (and its variant 119): the first rank row; the weapon 369: row 41; a count counts before the cap
            PlayerRows.SetStatistics(con, O("""{"animal": {"19": {"ethical_kills": 1500}, "119": {"ethical_kills": 4}}, "weapon": {"369": {"10": {"ethical_kills": 7}}}}"""), 0, 0, null, null);
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");

        var game = await Get("apiv2.test", "/game/user/9/ranks");
        Assert.Equal("application/vnd.game_data.user.rank+json; type=collection", game.Headers.Single(h => h.Name == "Content-Type").Value);
        var rows = JsonNode.Parse(game.Body)!.AsArray();
        Assert.Equal(Facts.Default.Ranks.Length, rows.Count);
        Eq("""{"id": 1, "category_id": 1, "value": 1504}""", rows.Single(r => (long)r!["id"]! == 1));
        Eq("""{"id": 41, "category_id": 2, "value": 7}""", rows.Single(r => (long)r!["id"]! == 41));
        Eq("""{"id": 38, "category_id": 3, "value": 0}""", rows.Single(r => (long)r!["id"]! == 38));
        foreach (var id in new[] { 7, 10, 99 })  // the recorded player's, a banned and an unknown user id
            Assert.Equal(404, (await Get("apiv2.test", $"/game/user/{id}/ranks")).Status);

        var site = JsonNode.Parse((await Get("web.test", "/hunters/third/ranks")).Body)!;
        Assert.Equal(1000, (long)site["cap"]!);
        Eq("""{"id": 1, "category": "animals", "name": "Alpine Ibex", "value": 1504}""", site["ranks"]!.AsArray().Single(r => (long)r!["id"]! == 1));
        Assert.Equal(rows.Count, site["ranks"]!.AsArray().Count);
        Assert.IsType<JsonArray>(site["milestones"]);
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody/ranks")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned/ranks")).Status);
    }

    [Fact]
    public async Task TestAnyAccountsAchievementsAreServedToEveryone()
    {
        await SignIn("99999999999999991", "one");
        state.CreateAccount(10, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        Eq("""{"categories": [], "unlocked": 0, "recent": []}""", JsonNode.Parse((await Get("web.test", "/hunters/one/achievements")).Body));  // no definitions in the sample connect
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody/achievements")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned/achievements")).Status);
    }

    [Fact]
    public async Task TestTheRestoredMessagesAreReadOnlyByTheirOwner()
    {
        var ck = Ck("hr_session", await SignIn("99999999999999991", "one"));
        var me = state.Store.Listed("one", null)!.Value;
        state.CreateAccount(9, "Other");
        const string List = """[{"handle": "Fern", "user_id": 501, "read": 0, "ts": 1700000200, "preview": "see you"}, {"handle": "Moss", "user_id": "502", "read": 1, "ts": 1700000100, "preview": "hello"}]""";
        const string Fern = """{"total": 2, "messages": [{"handle": "Fern", "user_id": 501, "content": "see you", "ts": 1700000200}, {"handle": "one", "user_id": 7, "content": "where?", "ts": 1700000150}]}""";
        using (var con = state.Store.Open(me))
        {
            PlayerRows.SetExtra(con, "Chat_conversations.json", JsonNode.Parse(List));
            PlayerRows.SetExtra(con, "Chat_conversation__user_id=501.json", O(Fern));
        }
        // the game's own replies: the restored conversations as saved, a thread as saved (reading marks nothing)
        static JsonNode Parse(byte[] body) => JsonNode.Parse(body)!;
        Eq("""["Fern", "Moss"]""", T(Parse(state.ConversationsReply(me))[0]!["handle"], Parse(state.ConversationsReply(me))[1]!["handle"]));
        Eq(Fern, Parse(state.ConversationReply(me, "user_id=501"u8.ToArray())));
        Eq("""{"total": 0, "messages": []}""", Parse(state.ConversationReply(me, "user_id=502"u8.ToArray())));  // a conversation whose thread was not saved
        Eq("[]", Parse(state.ConversationsReply(9)));  // another account sees none of them
        Eq("""{"total": 0, "messages": []}""", Parse(state.ConversationReply(9, "user_id=501"u8.ToArray())));

        var all = JsonNode.Parse((await Get("web.test", "/account/messages", ck)).Body)!["conversations"]!.AsArray();
        Eq("""[["Fern", "see you", 1700000200, true], ["Moss", "hello", 1700000100, false]]""", new JsonArray([.. all.Select(c => (JsonNode?)new JsonArray(c!["handle"]!.DeepClone(), c["preview"]!.DeepClone(), c["ts"]!.DeepClone(), c["unread"]!.DeepClone()))]));
        var thread = await Get("web.test", "/account/messages/thread?handle=fern", ck);
        Assert.Equal(200, thread.Status);
        Eq("""{"messages": [{"mine": false, "content": "see you", "ts": 1700000200}, {"mine": true, "content": "where?", "ts": 1700000150}]}""", JsonNode.Parse(thread.Body));
        Assert.Equal(200, (await Get("web.test", "/account/messages/thread?handle=Moss", ck)).Status);
        Assert.Equal(404, (await Get("web.test", "/account/messages/thread?handle=nobody", ck)).Status);
        Assert.Equal(401, (await Get("web.test", "/account/messages")).Status);
        Assert.Equal(401, (await Get("web.test", "/account/messages/thread?handle=Fern")).Status);
    }

    [Fact]
    public async Task TestTheWebsitesFriendsAreReadAndChangedByTheSignedInPlayer()
    {
        var ck = Ck("hr_session", await SignIn("99999999999999991", "one"));
        state.CreateAccount(9, "Third");
        state.CreateAccount(10, "banned");
        state.CreateAccount(11, "Fourth");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        var me = state.Store.Listed("one", null)!.Value;
        async Task<string[]> Handles(string path, string key)
        {
            var reply = await Get("web.test", path, ck);
            return [.. JsonNode.Parse(reply.Body)![key]!.AsArray().Select(h => (string)h!["handle"]!)];
        }
        Task<Reply> Act(string action, string handle, params (string, string)[] headers) => server.SendAsync("POST", "web.test", "/account/friends/" + action,
            [("Content-Type", "application/x-www-form-urlencoded"), .. headers], Encoding.UTF8.GetBytes("handle=" + handle));
        var xhr = new[] { ck, ("X-Requested-With", "XMLHttpRequest") };

        Assert.Equal(200, (await Act("add", "third", xhr)).Status);  // any case
        Assert.Equal(["Third"], await Handles("/account/friends", "pending"));
        state.Store.AddFriend(11, me);
        Assert.Equal(["Fourth"], await Handles("/account/friends", "requests"));
        Assert.Equal(200, (await Act("add", "Fourth", xhr)).Status);  // accepts
        Assert.Empty(await Handles("/account/friends", "requests"));
        Assert.Equal(["Fourth"], await Handles("/account/friends", "friends"));
        Assert.Equal(["Fourth"], await Handles("/hunters/one/friends", "rows"));
        Assert.Equal(["one"], await Handles("/hunters/fourth/friends", "rows"));
        var past = JsonNode.Parse((await Get("web.test", "/hunters/Fourth/friends?page=9")).Body)!;  // a page past the end is the last
        Assert.Equal((1, 1, 1), ((int)past["page"]!, (int)past["pages"]!, (int)past["total"]!));
        Assert.Equal(["Fourth", "Third"], await Handles("/account/friends/search?text=r", "hunters"));  // contains, any case; not "one", not the banned
        Assert.Empty(await Handles("/account/friends/search?text=banned", "hunters"));
        Assert.Empty(await Handles("/account/friends/search?text=one", "hunters"));  // never the player itself
        Assert.Equal(200, (await Act("remove", "FOURTH", xhr)).Status);
        Assert.Empty(await Handles("/account/friends", "friends"));
        Assert.Empty(await Handles("/hunters/fourth/friends", "rows"));
        Assert.Equal(200, (await Act("remove", "Third", xhr)).Status);  // cancels the request
        Assert.Empty(await Handles("/account/friends", "pending"));
        // not another player: the own handle, a banned or an unknown one
        foreach (var handle in new[] { "one", "banned", "nobody", "" })
            Assert.Equal(404, (await Act("add", handle, xhr)).Status);
        // signed out, or not from a page of this server
        Assert.Equal(401, (await Get("web.test", "/account/friends")).Status);
        Assert.Equal(401, (await Get("web.test", "/account/friends/search?text=r")).Status);
        Assert.Equal(401, (await Act("add", "Third", ("X-Requested-With", "XMLHttpRequest"))).Status);
        Assert.Equal(400, (await Act("add", "Third", ck)).Status);
        Assert.Empty(await Handles("/account/friends", "pending"));
        Assert.Equal(404, (await Get("web.test", "/hunters/banned/friends")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody/friends")).Status);
    }

    [Fact]
    public async Task TestThePlayerDeletesAndDownloadsItsOwnGalleryOnTheWebsite()
    {
        var ck = Ck("hr_session", await SignIn("99999999999999991", "one"));
        state.CreateAccount(9, "Third");
        var me = state.Store.Listed("one", null)!.Value;
        long Add(long account, string file)
        {
            Directory.CreateDirectory(Path.Combine(tmp, "photos", account.ToString()));
            File.WriteAllText(Path.Combine(tmp, "photos", account.ToString(), file), "jpeg " + file);
            using var con = state.Store.Open(account);
            con.Begin();
            var id = con.Insert("INSERT INTO photos (account_id, hash, photo_type, file) VALUES ($a, 1, 2, ?)", file);
            con.Commit();
            return id;
        }
        var (first, second, foreign) = (Add(me, "1-2.jpg"), Add(me, "2-2.jpg"), Add(9, "3-2.jpg"));
        Task<Reply> Delete(long id, params (string, string)[] headers) => server.SendAsync("POST", "web.test", "/account/gallery/delete",
            [("Content-Type", "application/x-www-form-urlencoded"), .. headers], Encoding.UTF8.GetBytes("id=" + id));
        var xhr = new[] { ck, ("X-Requested-With", "XMLHttpRequest") };

        // the zip holds the player's pictures at full size, no one else's
        var zip = await Get("web.test", "/account/gallery/zip", ck);
        Assert.Equal(("application/zip", 200), (zip.Headers.Single(h => h.Name == "Content-Type").Value, zip.Status));
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip.Body)))
            Assert.Equal(["1-2.jpg", "2-2.jpg"], archive.Entries.Select(e => e.FullName).Order());

        Assert.Equal(404, (await Delete(foreign, xhr)).Status);  // another player's photo
        Assert.Equal(404, (await Delete(99999, xhr)).Status);
        Assert.True(File.Exists(Path.Combine(tmp, "photos", "9", "3-2.jpg")));
        Assert.Equal(200, (await Delete(first, xhr)).Status);
        Assert.False(File.Exists(Path.Combine(tmp, "photos", me.ToString(), "1-2.jpg")));
        Assert.Equal(404, (await Delete(first, xhr)).Status);
        Assert.Equal([second], JsonNode.Parse((await Get("web.test", "/hunters/one/gallery")).Body)!["rows"]!.AsArray().Select(r => (long)r!["id"]!));

        // signed out, or not from a page of this server
        Assert.Equal(401, (await Get("web.test", "/account/gallery/zip")).Status);
        Assert.Equal(401, (await Delete(second, ("X-Requested-With", "XMLHttpRequest"))).Status);
        Assert.Equal(400, (await Delete(second, ck)).Status);
        Assert.Equal(1, (int)JsonNode.Parse((await Get("web.test", "/hunters/one/gallery")).Body)!["total"]!);
    }

    [Fact]
    public async Task TestTheMinigameBoardsListTheBestScores()
    {
        state.CreateAccount(9, "third");
        state.CreateAccount(10, "fourth");
        state.CreateAccount(11, "nogame");
        state.CreateAccount(13, "fifth");
        state.CreateAccount(12, "banned");
        foreach (var (id, lane, bust) in new (long, long?, long?)[] { (9, 300, null), (10, 300, 40), (12, 900, 900) })
        {
            using var con = state.Store.Open(id);
            PlayerRows.SetStatistics(con, new JsonObject(), 0, 0, bust, lane);
        }
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'banned'");
        // event 17 scores: higher than the restored one wins (third, Lane Bandit 500), lower does not (fourth, Bust Through 10), and a
        // player who never restored is listed (nogame's Bust Through 70); the connect reply's best follows
        foreach (var (id, game, score) in new (long, int, int)[] { (9, 1, 500), (9, 1, 20), (10, 0, 10), (11, 0, 70), (13, 1, 300), (11, 1, 0), (10, 1, 999) })
        {
            using var con = state.Store.Open(id);
            con.Exec("INSERT INTO events (account_id, event, body) VALUES ($a, 17, ?)", $$"""{"event": 17, "game_id": {{game}}, "score": {{(score == 999 ? "12.5" : score)}}}""");
        }
        var reply = new JsonObject();
        using (var con = state.Store.Open(11))
            RestoredReads.Connect(con, reply, new JsonObject(), null);
        Eq("[70, 0]", T(reply["user_stats"]!["bust_through_score"], reply["user_stats"]!["lane_bandit_score"]));
        // equal scores: the lower account id first (fourth restored 300, fifth played 300); a 0, no score and a banned account are not on it
        Eq("""{"page": 1, "pages": 1, "total": 3, "rows": [{"rank": 1, "handle": "third", "value": 500}, {"rank": 2, "handle": "fourth", "value": 300}, {"rank": 3, "handle": "fifth", "value": 300}]}""",
            JsonNode.Parse((await Get("web.test", "/leaderboards/lanebandit")).Body));
        Eq("""{"page": 1, "pages": 1, "total": 2, "rows": [{"rank": 1, "handle": "nogame", "value": 70}, {"rank": 2, "handle": "fourth", "value": 40}]}""",
            JsonNode.Parse((await Get("web.test", "/leaderboards/bustthrough?page=5")).Body));
        Assert.Equal(404, (await Get("web.test", "/leaderboards/score")).Status);
    }

    [Fact]
    public async Task TestEveryNewAccountGetsTheNextIdOfTheServersRange()
    {
        await SignIn("99999999999999991", "one");
        await SignIn("99999999999999992", "two");
        Assert.Equal(Store.FirstServerId + 1, state.Store.AccountBySteam("99999999999999992"));
        Assert.Equal(Store.FirstServerId + 2, state.Store.NextServerId());
        Assert.True(Store.FirstServerId + 1 < int.MaxValue && Fixtures.ME < Store.FirstServerId);  // the local account and a restored id sit below the range
        state.CreateAccount(1234567, "restored");  // an id from a backup does not move the allocator
        Assert.Equal(Store.FirstServerId + 2, state.Store.NextServerId());
    }

    [Fact]
    public async Task TestAHandleIsCheckedAndUniqueWhateverTheCase()
    {
        await SignIn("99999999999999991", "Hunter_One");
        var begun = await Begin("web.test");
        await Return("web.test", begun, "99999999999999992");
        foreach (var (handle, text) in new[] { ("hunter_one", "taken"), ("HUNTER_ONE", "taken"), ("x", "3 to 20"), ("two words", "3 to 20") })
        {
            var page = await PostForm("web.test", "/auth/handle", $"csrf={begun.Login}&handle={HttpUtility.UrlEncode(handle)}", Ck("hr_login", begun.Login));
            Assert.Equal(200, page.Status);
            Assert.Contains(text, Body(page));
        }
        Assert.Null(state.Store.AccountBySteam("99999999999999992"));
        var ok = await PostForm("web.test", "/auth/handle", $"csrf={begun.Login}&handle=Second", Ck("hr_login", begun.Login));
        Assert.Equal(302, ok.Status);
        Assert.NotNull(state.Store.AccountBySteam("99999999999999992"));
    }

    [Fact]
    public async Task TestTheReturnIsBoundToTheBrowserThatStartedIt()
    {
        var begun = await Begin("web.test");
        var url = begun.ReturnPath + "&" + FakeSteam.Assertion("https://web.test" + begun.ReturnPath);
        Assert.Equal(400, (await Get("web.test", url)).Status);                                  // no cookie: another browser
        Assert.Equal(400, (await Get("web.test", url, Ck("hr_login", "0000"))).Status);          // not its login
        Assert.Empty(fake.Asked);
        Assert.Equal(302, (await Get("web.test", url, Ck("hr_login", begun.Login))).Status);
        Assert.Equal(400, (await Get("web.test", url, Ck("hr_login", begun.Login))).Status);     // the same URL again
        // an assertion for the other host's return URL is not this one's
        var other = await Begin("web.test");
        var wrong = await Get("web.test", other.ReturnPath + "&" + FakeSteam.Assertion("https://signup.test" + other.ReturnPath), Ck("hr_login", other.Login));
        Assert.Equal(400, wrong.Status);
    }

    [Fact]
    public async Task TestSteamNotConfirmingMeansNoAccountAndNoCookie()
    {
        fake.Answer = "is_valid:false\n";
        var back = await Return("web.test", await Begin("web.test"));
        Assert.Equal(400, back.Status);
        Assert.Null(Cookie(back, "hr_session"));
        Assert.Null(state.Store.AccountBySteam("99999999999999991"));
    }

    [Fact]
    public async Task TestThePagesCarryTheirSecurityHeaders()
    {
        var begun = await Begin("signup.test");
        await Return("signup.test", begun);
        foreach (var page in new[] { await Get("signup.test", "/auth/handle", Ck("hr_login", begun.Login)), await Get("signup.test", "/") })
        {
            Assert.Equal(200, page.Status);
            foreach (var header in new[] { ("Referrer-Policy", "same-origin"), ("X-Frame-Options", "DENY"), ("Cache-Control", "no-store"),
                                           ("X-Content-Type-Options", "nosniff"), ("Content-Type", "text/html; charset=utf-8") })
                Assert.Contains(header, page.Headers);
        }
    }

    [Fact]
    public async Task TestATypedHandleIsEscapedWhenTheFormComesBack()
    {
        var begun = await Begin("web.test");
        await Return("web.test", begun);
        var page = await PostForm("web.test", "/auth/handle", $"csrf={begun.Login}&handle={HttpUtility.UrlEncode("\"><script>alert(1)</script>")}", Ck("hr_login", begun.Login));
        Assert.Equal(200, page.Status);
        Assert.Contains("&quot;&gt;&lt;script&gt;", Body(page));
        Assert.DoesNotContain("<script>alert", Body(page));
    }

    [Fact]
    public async Task TestOnePlayersJourneyFromSignInToTheBoard()
    {
        var cookie = await SignIn("99999999999999991", "Hunter_One", "signup.test");
        var launch = await Xhr("POST", "signup.test", "/auth/launch", Ck("hr_session", cookie));
        var session = System.Text.RegularExpressions.Regex.Match((string)JsonNode.Parse(launch.Body)!["url"]!, @"game_web\.test/([0-9a-f]{40})/").Groups[1].Value;
        Assert.NotEmpty(session);
        Func<string, byte[], Task<Reply>> game = (path, body) => server.SendAsync("POST", "gameserver.test", path, [("X-Hun-Session", session)], body);

        var connect = await game(Replay.CONNECT, "{}"u8.ToArray());
        Assert.Equal((200, "state"), (connect.Status, connect.Source));
        Assert.Equal("Hunter_One", (string)JsonNode.Parse(connect.Body)!["account"]!["handle"]!);
        var start = await server.SendAsync("POST", "api.test", LAUNCH_START, Form, Encoding.ASCII.GetBytes($"start=100&reserve=2&oauth_access_token={session}"));
        Assert.Equal("state", start.Source);
        var beat = await game(HEARTBEAT, Events(AccountIsolationTests.Harvest(81, 72270)));
        Assert.Equal("state", beat.Source);
        Eq("""{"err": 0}""", JsonNode.Parse((await game(DISCONNECT, "{}"u8.ToArray())).Body));

        var page = await Get("web.test", "/user/Hunter_One/score/?id=1");
        Assert.Equal(200, page.Status);
        Assert.Contains("text/html", page.Headers.Single(h => h.Name == "Content-Type").Value);
        var hunter = JsonNode.Parse((await Get("web.test", "/hunters/Hunter_One")).Body)!;
        var me = (await Me(cookie, "signup.test"))!;
        var earned = (long)hunter["hunterscore"]!;
        Assert.True(earned > 0);
        Assert.Equal(earned, (long)me["hunterscore"]!);
        Assert.Equal(earned, Py.L(state.Store.AccountJson(Store.FirstServerId)["hunterscore"]));
    }

    /// <summary>Sends a service's requests through the loopback server, whatever host they name.</summary>
    sealed class Via(HttpClient server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return server.SendAsync(copy, ct);
        }
    }

    [Fact]
    public async Task TestTheWebsitesServicesReadTheServersOwnReplies()
    {
        var cookie = await SignIn("99999999999999991", "Hunter_One", "web.test");
        state.NoteLaunch(Store.FirstServerId, "start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply(Store.FirstServerId, Events(AccountIsolationTests.Harvest(81, 72270)));
        state.Disconnect(Store.FirstServerId, "{}"u8.ToArray());
        var served = (long)JsonNode.Parse((await Get("web.test", "/hunters/Hunter_One")).Body)!["hunterscore"]!;
        Assert.True(served > 0);

        var http = new HttpClient(new Via(server.Http)) { BaseAddress = new Uri("http://web.test/") };
        var browser = new Resurgence.Services.ServerCookie { Value = cookie, Host = "web.test" };
        var sessions = new Resurgence.Services.ServerSessionService(http, browser, Microsoft.Extensions.Logging.Abstractions.NullLogger<Resurgence.Services.ServerSessionService>.Instance);
        Assert.Equal(new Resurgence.Services.Session("Hunter_One", (int)served), await sessions.CurrentAsync());

        var boards = new Resurgence.Services.ServerLeaderboardsService(http, browser, Microsoft.Extensions.Logging.Abstractions.NullLogger<Resurgence.Services.ServerLeaderboardsService>.Instance);
        var row = Assert.Single((await boards.RowsAsync("hunterscore", 0, 1)).Rows);
        Assert.Equal(new Resurgence.Services.Ranked(1, "Hunter_One", served), row);

        var profiles = new Resurgence.Services.ServerProfileService(http, browser, sessions, new Resurgence.Services.Mocks.MockProfileService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Resurgence.Services.ServerProfileService>.Instance);
        var profile = await profiles.ProfileAsync("Hunter_One");
        Assert.True(profile!.Own);
        Assert.Equal((int)served, profile.HunterScore);
    }

    [Fact]
    public async Task TestTheNextAddressStaysOnThisSite()
    {
        await SignIn("99999999999999999", "nine");
        foreach (var next in new[] { "https://evil.test/", "//evil.test/x", "/\\evil.test", "feed", "/a\nb" })
            Assert.Equal("/", Location(await Return("web.test", await Begin("web.test", "?next=" + HttpUtility.UrlEncode(next)), "99999999999999999")));
        Assert.Equal("/store", Location(await Return("web.test", await Begin("web.test", "?next=%2Fstore"), "99999999999999999")));
    }

    [Fact]
    public async Task TestACookieIsTheSessionUntilSignOut()
    {
        var cookie = await SignIn();
        Assert.Equal(401, (await Get("web.test", "/auth/me")).Status);
        Assert.Equal(401, (await Get("web.test", "/auth/me", Ck("hr_session", "0000"))).Status);
        // sign-out is for pages of this site: no Origin and no XHR header, or another origin, is refused
        Assert.Equal(400, (await server.SendAsync("POST", "web.test", "/auth/signout", [Ck("hr_session", cookie)], [])).Status);
        Assert.Equal(400, (await server.SendAsync("POST", "web.test", "/auth/signout", [Ck("hr_session", cookie), ("Origin", "https://evil.test")], [])).Status);
        Assert.NotNull(await Me(cookie));
        var out_ = await Xhr("POST", "web.test", "/auth/signout", Ck("hr_session", cookie));
        Assert.Equal(204, out_.Status);
        Assert.Contains(out_.Headers, h => h.Name == "Set-Cookie" && h.Value.StartsWith("hr_session=; ") && h.Value.Contains("Max-Age=0"));
        Assert.Null(await Me(cookie));
    }

    [Fact]
    public async Task TestTheSessionCookieIsHttpOnlySecureAndAGameTokenIsNotACookie()
    {
        var begun = await Begin("web.test");
        var back = await Return("web.test", begun);
        var named = await PostForm("web.test", "/auth/handle", $"csrf={begun.Login}&handle=four", Ck("hr_login", begun.Login));
        var set = named.Headers.Single(h => h.Name == "Set-Cookie" && h.Value.StartsWith("hr_session=")).Value;
        Assert.Contains("HttpOnly", set);
        Assert.Contains("Secure", set);
        Assert.Contains("SameSite=Lax", set);
        Assert.Contains("Path=/;", set);
        var cookie = Cookie(named, "hr_session")!;
        // a game token is no session, and a session no game token
        Assert.Null(await Me(state.Store.CreateToken(state.Store.AccountBySteam("99999999999999991")!.Value)));
        Assert.Null(state.Store.ResolveToken(cookie));
        Assert.Equal("/auth/handle", Location(back));
    }

    [Fact]
    public async Task TestABannedAccountGetsNoSession()
    {
        await SignIn();
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE steam_id64 = ?", "99999999999999991");
        var back = await Return("web.test", await Begin("web.test"));
        Assert.Equal(403, back.Status);
        Assert.Null(Cookie(back, "hr_session"));
    }

    // ---- the launcher's start page

    [Fact]
    public async Task TestTheStartPageOffersSteamSignedOutAndPlaySignedIn()
    {
        var page = await Get("signup.test", "/");
        Assert.Equal(200, page.Status);
        Assert.Contains("Sign in with Steam", Body(page));
        Assert.DoesNotContain("id=\"play\"", Body(page));
        AssertNewLauncherLinks(Body(page));
        var cookie = await SignIn("99999999999999991", "Hunter_One", "signup.test");
        page = await Get("signup.test", "/", Ck("hr_session", cookie));
        Assert.Contains("Hello, Hunter_One", Body(page));
        Assert.Contains("id=\"play\"", Body(page));
        AssertNewLauncherLinks(Body(page));
        Assert.DoesNotContain("=>", Body(page));   // ES5 only: the launcher's browser is Chromium 25
        Assert.DoesNotContain("let ", Body(page));
        Assert.DoesNotContain("const ", Body(page));
    }

    [Fact]
    public void TestThePagesAfterSignInLinkTheDiscordOnlyWhenSet()
    {
        const string Url = "https://discord.test/invite";
        foreach (var page in new[] { AuthPages.Done(Url), AuthPages.Start("Hunter_One", "/", Url), AuthPages.Confirm("c", "Hunter_One", "AB-12", Url), AuthPages.ConfirmApp("c", "Hunter_One", Url) })
            Assert.Contains($"href=\"{Url}\" target=\"_blank\" rel=\"noopener\"", page);
        foreach (var page in new[] { AuthPages.Done(), AuthPages.Start("Hunter_One"), AuthPages.Confirm("c", "Hunter_One", "AB-12"), AuthPages.ConfirmApp("c", "Hunter_One") })
            Assert.DoesNotContain("discord", page, StringComparison.OrdinalIgnoreCase);
    }

    static void AssertNewLauncherLinks(string html)
    {
        Assert.Contains("Open the new launcher", html);
        Assert.Contains("Open this page in the original launcher to open the new one.", html);
        Assert.Contains("ewlauncher.openURL('hunter-resurgence://open');ewlauncher.exitLauncher()", html);
        Assert.Contains("href=\"https://github.com/yinyin99/hunter-resurgence/releases/latest\">Download it", html);
    }

    [Fact]
    public async Task TestTheStartPageGivesALaunchUrlWhoseTokensAreThePlayers()
    {
        var cookie = await SignIn("99999999999999991", "Hunter_One", "signup.test");
        Assert.Equal(401, (await Xhr("POST", "signup.test", "/auth/launch")).Status);
        Assert.Equal(400, (await server.SendAsync("POST", "signup.test", "/auth/launch", [Ck("hr_session", cookie)], [])).Status);
        var reply = await Xhr("POST", "signup.test", "/auth/launch", Ck("hr_session", cookie));
        Assert.Equal(200, reply.Status);
        var url = (string)JsonNode.Parse(reply.Body)!["url"]!;
        var m = System.Text.RegularExpressions.Regex.Match(url,
            $@"\A{ProtocolLiterals.LaunchScheme}://game_web\.test/([0-9a-f]{{40}})/lang/en_US/access_token/([\w-]+\.[\w-]+\.[\w-]+)/refresh_token/([\w-]+\.[\w-]+\.[\w-]+)\z");
        Assert.True(m.Success, url);
        foreach (var token in new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value })
            Assert.Equal(Store.FirstServerId, state.Store.ResolveToken(token));
        // the session is what the game server reads
        var hb = await server.SendAsync("POST", "gameserver.test", HEARTBEAT, [("X-Hun-Session", m.Groups[1].Value)], "{}"u8.ToArray());
        Assert.Equal("state", hb.Source);
        Assert.NotEqual(m.Groups[1].Value, JsonNode.Parse((await Xhr("POST", "signup.test", "/auth/launch", Ck("hr_session", cookie))).Body)!["url"]!.ToString());
    }

    [Fact]
    public async Task TestTheLauncherSignsInThroughThePlayersBrowser()
    {
        var link = await Xhr("POST", "signup.test", "/auth/link");
        var j = JsonNode.Parse(link.Body)!;
        var (code, userCode) = ((string)j["code"]!, (string)j["userCode"]!);
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", userCode);
        Assert.Equal($"https://signup.test/auth/steam/login?link={code}", (string)j["url"]!);
        Assert.Equal("pending", (string)JsonNode.Parse((await Get("signup.test", "/auth/link/" + code)).Body)!["status"]!);

        // in the player's browser: Steam, the first sign-in's name, then the confirmation of the code
        var begun = await Begin("signup.test", "?link=" + code);
        var back = await Return("signup.test", begun);
        Assert.Equal("/auth/handle", Location(back));
        var named = await PostForm("signup.test", "/auth/handle", $"csrf={begun.Login}&handle=Launched", Ck("hr_login", begun.Login));
        Assert.Equal("/auth/confirm", Location(named));
        Assert.Null(Cookie(named, "hr_session"));  // this browser is not signed in on the start page: the launcher is
        var ask = Body(await Get("signup.test", "/auth/confirm", Ck("hr_login", begun.Login)));
        Assert.Contains(userCode, ask);
        Assert.Contains("Launched", ask);
        Assert.Equal("pending", (string)JsonNode.Parse((await Get("signup.test", "/auth/link/" + code)).Body)!["status"]!);  // not before the player says yes
        Assert.Equal(400, (await PostForm("signup.test", "/auth/confirm", "csrf=0", Ck("hr_login", begun.Login))).Status);
        Assert.Equal(200, (await PostForm("signup.test", "/auth/confirm", "csrf=" + begun.Login, Ck("hr_login", begun.Login))).Status);

        var poll = await Get("signup.test", "/auth/link/" + code);
        Assert.Equal("ok", (string)JsonNode.Parse(poll.Body)!["status"]!);
        var cookie = Cookie(poll, "hr_session");
        Assert.Equal("Launched", (string?)(await Me(cookie!, "signup.test"))!["handle"]);
        Assert.Equal(404, (await Get("signup.test", "/auth/link/" + code)).Status);  // the code works once
        Assert.Equal(404, (await Get("signup.test", "/auth/link/" + new string('0', 32))).Status);
    }

    [Fact]
    public async Task TestALinkNeedsALiveCode()
    {
        var start = await Get("signup.test", "/auth/steam/login?link=" + new string('a', 32));
        Assert.Equal(400, start.Status);
        Assert.Equal(400, (await server.SendAsync("POST", "signup.test", "/auth/link", [], [])).Status);
    }

    // ---- an app on the player's PC (loopback redirect and PKCE)

    const string AppState = "abcdefghijklmnop0123456789";

    static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    Task<Reply> Exchange(string code, string verifier, params (string, string)[] headers) =>
        server.SendAsync("POST", "web.test", "/auth/app/exchange", [.. Form, .. headers], Encoding.UTF8.GetBytes($"code={code}&verifier={verifier}"));

    /// <summary>The browser's half of an app's sign-in up to the confirmation: the reply to the confirming POST (the redirect to the app).</summary>
    async Task<Reply> AppConfirm(string challenge, string handle = "Looper", string host = "web.test")
    {
        var begun = await Begin(host, $"?challenge={challenge}&port=51234&state={AppState}", "/auth/app/start");
        var back = await Return(host, begun);
        if (Location(back) == "/auth/handle") back = await PostForm(host, "/auth/handle", $"csrf={begun.Login}&handle={handle}", Ck("hr_login", begun.Login));
        Assert.Equal("/auth/confirm", Location(back));
        return await PostForm(host, "/auth/confirm", "csrf=" + begun.Login, Ck("hr_login", begun.Login));
    }

    static string CodeOf(Reply redirect) => HttpUtility.ParseQueryString(new Uri(Location(redirect)).Query)["code"]!;

    [Fact]
    public async Task TestAnAppSignsInThroughTheLoopbackRedirectAndPkce()
    {
        var (verifier, challenge) = Pkce();
        var begun = await Begin("web.test", $"?challenge={challenge}&port=51234&state={AppState}", "/auth/app/start");
        Assert.Equal("/auth/handle", Location(await Return("web.test", begun)));
        var named = await PostForm("web.test", "/auth/handle", $"csrf={begun.Login}&handle=Looper", Ck("hr_login", begun.Login));
        Assert.Equal("/auth/confirm", Location(named));
        Assert.Null(Cookie(named, "hr_session"));  // this browser is not signed in: the app is
        Assert.Contains("Looper", Body(await Get("web.test", "/auth/confirm", Ck("hr_login", begun.Login))));
        Assert.Equal(400, (await PostForm("web.test", "/auth/confirm", "csrf=0", Ck("hr_login", begun.Login))).Status);
        Assert.Equal(400, (await server.SendAsync("POST", "web.test", "/auth/confirm", [.. Form, Ck("hr_login", begun.Login)], Encoding.UTF8.GetBytes("csrf=" + begun.Login))).Status);  // a cross-site form carries no Origin and no header

        var sent = await PostForm("web.test", "/auth/confirm", "csrf=" + begun.Login, Ck("hr_login", begun.Login));
        Assert.Equal(302, sent.Status);
        var redirect = System.Text.RegularExpressions.Regex.Match(Location(sent), $@"\Ahttp://127\.0\.0\.1:51234/\?code=([0-9a-f]{{64}})&state={AppState}\z");
        Assert.True(redirect.Success, Location(sent));
        Assert.Equal(400, (await PostForm("web.test", "/auth/confirm", "csrf=" + begun.Login, Ck("hr_login", begun.Login))).Status);  // one confirmation, one code

        var code = redirect.Groups[1].Value;
        Assert.Equal(400, (await Exchange(code, verifier)).Status);  // not from our own page: no X-Requested-With
        var ok = await Exchange(code, verifier, ("X-Requested-With", "launcher"));
        Assert.Equal(200, ok.Status);
        Assert.Equal("Looper", (string?)(await Me(Cookie(ok, "hr_session")!))!["handle"]);
        Assert.Equal(400, (await Exchange(code, verifier, ("X-Requested-With", "launcher"))).Status);  // the code works once
    }

    [Fact]
    public async Task TestAWrongVerifierSignsNothingInAndSpendsTheCode()
    {
        var (verifier, challenge) = Pkce();
        var xhr = ("X-Requested-With", "launcher");
        var code = CodeOf(await AppConfirm(challenge));
        var wrong = await Exchange(code, Pkce().Verifier, xhr);
        Assert.Equal(400, wrong.Status);
        Assert.Null(Cookie(wrong, "hr_session"));
        Assert.Equal(400, (await Exchange(code, verifier, xhr)).Status);  // a guess costs the code

        var again = CodeOf(await AppConfirm(challenge));
        Assert.Equal(400, (await Exchange(again, challenge, xhr)).Status);  // the challenge is not the verifier
        Assert.Equal(400, (await Exchange(CodeOf(await AppConfirm(challenge)), "short", xhr)).Status);
        Assert.Equal(400, (await Exchange(new string('0', 64), verifier, xhr)).Status);
        Assert.Equal(400, (await Exchange("", "", xhr)).Status);
    }

    [Fact]
    public async Task TestAnAppSignInOnlyStartsWithAValidChallengePortAndState()
    {
        var (_, challenge) = Pkce();
        foreach (var query in new[]
        {
            "", $"?port=51234&state={AppState}", $"?challenge={challenge}&state={AppState}", $"?challenge={challenge}&port=51234",
            $"?challenge={challenge[..42]}&port=51234&state={AppState}", $"?challenge={challenge}A&port=51234&state={AppState}",
            $"?challenge={challenge[..42]}%2B&port=51234&state={AppState}",
            $"?challenge={challenge}&port=1023&state={AppState}", $"?challenge={challenge}&port=65536&state={AppState}", $"?challenge={challenge}&port=http&state={AppState}",
            $"?challenge={challenge}&port=-5&state={AppState}", $"?challenge={challenge}&port=51234.evil.test&state={AppState}",
            $"?challenge={challenge}&port=51234&state=short", $"?challenge={challenge}&port=51234&state={AppState}%26code%3Dx",
            $"?challenge={challenge}&port=51234&state={AppState}/%2F@evil.test", $"?challenge={challenge}&port=51234&state={new string('a', 129)}",
        })
        {
            var start = await Get("web.test", "/auth/app/start" + query);
            Assert.Equal(400, start.Status);
            Assert.Null(Cookie(start, "hr_login"));
        }
        Assert.Equal(302, (await Get("web.test", $"/auth/app/start?challenge={challenge}&port=1024&state={AppState}")).Status);
        Assert.Equal(302, (await Get("web.test", $"/auth/app/start?challenge={challenge}&port=65535&state={AppState}")).Status);
    }

    [Fact]
    public async Task TestALinkCodeIsNotAnAppCode()
    {
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await AppConfirm(challenge));
        Assert.Equal(404, (await Get("web.test", "/auth/link/" + code)).Status);
        var link = (string)JsonNode.Parse((await Xhr("POST", "web.test", "/auth/link")).Body)!["code"]!;
        Assert.Equal(400, (await Exchange(link, verifier, ("X-Requested-With", "launcher"))).Status);
        Assert.Equal(200, (await Exchange(code, verifier, ("X-Requested-With", "launcher"))).Status);
    }

    // ---- the tokens the game is given

    [Fact]
    public async Task TestLaunchStartIssuesTheTokensOfTheRequestsAccount()
    {
        var cookie = await SignIn("99999999999999991", "Hunter_One", "signup.test");
        var player = state.Store.AccountBySteam("99999999999999991")!.Value;
        var token = state.Store.CreateToken(player, TimeSpan.FromHours(1));
        var reply = await server.SendAsync("POST", "api.test", LAUNCH_START, Form, Encoding.ASCII.GetBytes($"start=100&reserve=2&loctype=LODGE&oauth_access_token={token}"));
        Assert.Equal((200, "state"), (reply.Status, reply.Source));
        var tokens = JsonNode.Parse(reply.Body)!;
        Assert.Equal("ok", (string)tokens["status"]!);
        foreach (var key in new[] { "session", "access_token", "refresh_token" })
            Assert.Equal(player, state.Store.ResolveToken((string)tokens[key]!));
        Assert.Equal(3, new[] { "session", "access_token", "refresh_token" }.Select(k => (string)tokens[k]!).Distinct().Count());
        // the launch is noted as before, for this account
        Assert.Equal("denied", (await server.SendAsync("POST", "api.test", LAUNCH_START, Form, "start=100&reserve=2"u8.ToArray())).Source);
        Assert.NotNull(cookie);
    }

    [Fact]
    public async Task TestTheJwtIsThreePartsWithAnExpiryAndResolvesToTheAccount()
    {
        state.CreateAccount(Store.FirstServerId, "jwtuser");
        var access = state.Store.CreateToken(Store.FirstServerId, Lifetimes.Access);
        foreach (var (host, headers, body) in new[]
        {
            ("gameserver.test", Json, $$"""{"oauth_access_token": "{{access}}"}"""),
            ("api.test", Form, $"oauth_access_token={access}"),
        })
        {
            var reply = await server.SendAsync("POST", host, JWT, headers, Encoding.ASCII.GetBytes(body));
            Assert.Equal((200, "state"), (reply.Status, reply.Source));
            var jwt = (string)JsonNode.Parse(reply.Body)!["token"]!;
            var parts = jwt.Split('.');
            Assert.Equal(3, parts.Length);
            var payload = JsonNode.Parse(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')))!;
            var left = (long)payload["exp"]! - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.InRange(left, 29 * 60, 30 * 60);
            Assert.Equal("2000000000", (string)payload["sub"]!);
            Assert.Equal(Store.FirstServerId, state.Store.ResolveToken(jwt));
            // and the game API v2 takes it as the Bearer token
            Assert.NotEqual("denied", (await server.SendAsync("GET", "apiv2.test", "/game/user/harvest/1", [("Authorization", "Bearer " + jwt)])).Source);
        }
        Assert.Equal("denied", (await server.SendAsync("POST", "gameserver.test", JWT, Json, """{"oauth_access_token": "nope"}"""u8.ToArray())).Source);
        Assert.Equal("denied", (await server.SendAsync("POST", "gameserver.test", JWT, Json, "{}"u8.ToArray())).Source);
    }

    [Fact]
    public async Task TestARefreshTokenRenewsTheAccessToken()
    {
        state.CreateAccount(Store.FirstServerId, "renewer");
        var refresh = state.Store.CreateToken(Store.FirstServerId, Lifetimes.Refresh, Lifetimes.RefreshPrefix);
        var reply = await server.SendAsync("POST", "api.test", OAUTH_TOKEN, Form, Encoding.ASCII.GetBytes($"oauth_consumer_key=k&oauth_refresh_token={refresh}"));
        var renewed = JsonNode.Parse(reply.Body)!;
        Assert.Equal(Store.FirstServerId, state.Store.ResolveToken((string)renewed["access_token"]!));
        Assert.Equal((refresh, Store.FirstServerId), ((string)renewed["refresh_token"]!, (long)renewed["user_id"]!));
        var bad = await server.SendAsync("POST", "api.test", OAUTH_TOKEN, Form, "oauth_refresh_token=nope"u8.ToArray());
        Assert.Equal("denied", bad.Source);
        // an access token or a JWT is not a refresh token
        foreach (var other in new[] { state.Store.CreateToken(Store.FirstServerId, Lifetimes.Access), state.Store.CreateToken(Store.FirstServerId, Lifetimes.Jwt, "eyJ.x.") })
            Assert.Equal("denied", (await server.SendAsync("POST", "api.test", OAUTH_TOKEN, Form, Encoding.ASCII.GetBytes("oauth_refresh_token=" + other))).Source);
    }

    [Fact]
    public async Task TestTheLaunchersOwnCallsAreAnsweredWithoutRecordings()
    {
        string P(string name) => ProtocolLiterals.LauncherBase + "/" + name;
        var token = await server.SendAsync("GET", "gameserver.test", P("token/") + "?session_ticket=AB12", []);
        Assert.Equal("state", token.Source);
        var t = JsonNode.Parse(token.Body)!;
        Assert.Equal((0, "none"), ((int)t["err"]!, (string)t["access_token"]!));  // not "0": the launcher's pages take it as signed in
        Assert.Equal(0, (int)JsonNode.Parse((await server.SendAsync("GET", "gameserver.test", P("initialise/") + "?session_id=0&appname=x&fetch_file_list=1", [])).Body)!["err"]!);
        var compat = await server.SendAsync("POST", "gameserver.test", P("compatibleHardware/") + "?appname=x&client_version=1", Form, "hardware=%7B%7D"u8.ToArray());
        Assert.Equal(1, (int)JsonNode.Parse(compat.Body)!["compatible"]!);
        Assert.Equal(0, (int)JsonNode.Parse((await server.SendAsync("GET", "gameserver.test", P("ping/sid/abc/") + "?ping_data=256", [])).Body)!["err"]!);
        Assert.Equal("miss", (await server.SendAsync("GET", "gameserver.test", P("other/"), [])).Source);
    }

    // ---- offline and online

    [Fact]
    public async Task TestOnlineNothingWorksWithoutASignedInAccount()
    {
        foreach (var (host, path) in new[] { ("gameserver.test", HEARTBEAT), ("gameserver.test", JWT), ("api.test", LAUNCH_START), ("api.test", BUY) })
            Assert.Equal("denied", (await server.SendAsync("POST", host, path, Form, "start=1&reserve=2"u8.ToArray())).Source);
        Assert.Equal("denied", (await server.SendAsync("POST", "gameserver.test", Replay.CONNECT, [], [])).Source);
    }

    [Fact]
    public async Task TestOfflineEverythingStaysAsItWas()
    {
        await StartAsync(offline: true,
            ("POST", "api.test", LAUNCH_START, """{"session": "recorded"}"""),
            ("POST", "gameserver.test", JWT, """{"token": "recorded"}"""),
            ("POST", "gameserver.test", ProtocolLiterals.LauncherBase + "/token/", """{"access_token": "recorded"}"""),
            ("GET", "signup.test", "/", "<html>recorded</html>"));
        Assert.Equal("""{"session": "recorded"}""", Body(await server.SendAsync("POST", "api.test", LAUNCH_START, Form, "start=100&reserve=2"u8.ToArray())));
        Assert.Equal("""{"token": "recorded"}""", Body(await server.SendAsync("POST", "gameserver.test", JWT, Json, "{}"u8.ToArray())));
        Assert.Equal("""{"access_token": "recorded"}""", Body(await server.SendAsync("POST", "gameserver.test", ProtocolLiterals.LauncherBase + "/token/", [], [])));
        Assert.Equal("<html>recorded</html>", Body(await Get("signup.test", "/")));  // no start page: the local player needs no sign-in
        Assert.Equal("state", (await server.SendAsync("POST", "gameserver.test", HEARTBEAT, [], "{}"u8.ToArray())).Source);  // the local account
        Assert.Equal("miss", (await Get("web.test", "/auth/me")).Source);  // no sign-in on a local server
        Assert.Equal("miss", (await Get("web.test", "/hunters")).Source);
        Assert.Equal("miss", (await server.SendAsync("POST", "signup.test", "/auth/link", [("X-Requested-With", "x")], [])).Source);
    }

    [Fact]
    public async Task TestAnAccountIdIsReplaceableUntilItPlays()
    {
        // the rule the restore follows (docs/server.md#sign-in-and-tokens-and-tokens): the account of a first sign-in has only the starting rows, no hunt
        await SignIn();
        var id = state.Store.AccountBySteam("99999999999999991")!.Value;
        using var con = state.Store.Open(id);
        Assert.Equal(0L, con.Scalar("SELECT COUNT(*) FROM expeditions WHERE account_id = $a"));
        Assert.Equal(0L, con.Scalar("SELECT COUNT(*) FROM events WHERE account_id = $a"));
    }

    [Fact]
    public void TestTheSchemaOfVersion1IsMigratedToTheWebSessionsTable()
    {
        var file = Path.Combine(tmp, "old.db");
        var old = new Store(file);
        using (var con = old.Open())
        {
            con.Exec("DROP TABLE web_sessions");
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
            con.Exec("DROP TABLE claim_lookups");
            con.Exec("DROP TABLE claims");
            con.Exec("PRAGMA user_version = 1");
        }
        var migrated = new Store(file);
        migrated.CreateAccount(5, "five", new JsonObject());
        Assert.Equal(5L, migrated.ResolveWebSession(migrated.CreateWebSession(5, TimeSpan.FromDays(1))));
        using var check = migrated.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TestExpiredTokensAndCookiesArePurged()
    {
        state.CreateAccount(9, "nine");
        var (live, dead) = (state.Store.CreateToken(9, TimeSpan.FromHours(1)), state.Store.CreateToken(9, TimeSpan.FromSeconds(-5)));
        var cookie = state.Store.CreateWebSession(9, TimeSpan.FromSeconds(-5));
        state.Store.PurgeExpired();
        using var con = state.Store.Open();
        Assert.Equal(0L, con.Scalar("SELECT COUNT(*) FROM web_sessions"));
        Assert.Equal(9L, state.Store.ResolveToken(live));
        Assert.Null(state.Store.ResolveToken(dead));
        Assert.Null(state.Store.ResolveWebSession(cookie));
        Assert.Equal(1L, con.Scalar("SELECT COUNT(*) FROM tokens WHERE account_id = 9"));  // only the live one is left
    }
}

/// <summary>The lifetimes of a sign-in and of a launcher's code, and what a flood of them does, on SignIn with a clock the test sets.</summary>
public class SignInClockTests : StateTestBase
{
    const string Host = "web.test";
    readonly FakeSteam fake = new();
    DateTimeOffset clock = DateTimeOffset.UtcNow;
    readonly SignIn signIn;

    public SignInClockTests()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        signIn = new SignIn(state, new SteamOpenId(new HttpClient(fake), () => clock), () => clock);
    }

    static Req Rq(string method, string path, string query = "", string? cookie = null) => new(method, Host, path, query,
        k => k == "Origin" ? "https://" + Host : k == "Cookie" ? cookie : null, [], true);

    static string LoginOf(Reply start) => start.Headers.Single(h => h.Name == "Set-Cookie" && h.Value.StartsWith("hr_login=")).Value.Split(';')[0]["hr_login=".Length..];

    Task<Reply> Return(string login)
    {
        var returnTo = $"https://{Host}/auth/steam/return?login={login}";
        var nonce = clock.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "x" + Guid.NewGuid().ToString("N");
        return signIn.Return(Rq("GET", "/auth/steam/return", $"login={login}&" + FakeSteam.Assertion(returnTo, nonce: nonce), $"hr_login={login}"));
    }

    [Fact]
    public async Task TestALoginLivesFifteenMinutes()
    {
        var early = LoginOf(signIn.Start(Rq("GET", "/auth/steam/login")));
        var late = LoginOf(signIn.Start(Rq("GET", "/auth/steam/login")));
        clock += TimeSpan.FromMinutes(14);
        Assert.Equal(302, (await Return(early)).Status);
        clock += TimeSpan.FromMinutes(2);
        Assert.Equal(400, (await Return(late)).Status);
    }

    [Fact]
    public void TestALinkCodeLivesTenMinutes()
    {
        var code = (string)JsonNode.Parse(signIn.NewLink(Rq("POST", "/auth/link")).Body)!["code"]!;
        clock += TimeSpan.FromMinutes(9);
        Assert.Equal(200, signIn.PollLink(Rq("GET", "/auth/link/" + code)).Status);
        clock += TimeSpan.FromMinutes(2);
        var expired = signIn.PollLink(Rq("GET", "/auth/link/" + code));
        Assert.Equal(404, expired.Status);
        Eq("""{"status": "expired"}""", JsonNode.Parse(expired.Body));
    }

    [Fact]
    public async Task TestAnAppCodeLivesThreeMinutes()
    {
        state.CreateAccount(state.Store.NextServerId(), "clock_one", "99999999999999991");
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        async Task<string> Code()
        {
            var login = LoginOf(signIn.StartApp(Rq("GET", "/auth/app/start", $"challenge={challenge}&port=51234&state=abcdefghijklmnop0123456789")));
            Assert.Equal(302, (await Return(login)).Status);
            var confirm = signIn.Confirm(new("POST", Host, "/auth/confirm", "", k => k == "Origin" ? "https://" + Host : k == "Cookie" ? "hr_login=" + login : null, Encoding.UTF8.GetBytes("csrf=" + login), true));
            return System.Web.HttpUtility.ParseQueryString(new Uri(confirm.Headers.Single(h => h.Name == "Location").Value).Query)["code"]!;
        }
        Reply Exchange(string code) => signIn.ExchangeApp(new("POST", Host, "/auth/app/exchange", "", k => k == "X-Requested-With" ? "test" : null, Encoding.UTF8.GetBytes($"code={code}&verifier={verifier}"), true));

        var (live, stale) = (await Code(), await Code());
        clock += TimeSpan.FromMinutes(2.9);
        Assert.Equal(200, Exchange(live).Status);
        clock += TimeSpan.FromMinutes(0.2);
        Assert.Equal(400, Exchange(stale).Status);
    }

    [Fact]
    public async Task TestAFloodOfLoginsEvictsTheOldestNotTheNewcomer()
    {
        var ids = new List<string>();
        for (var i = 0; i < 10_001; i++)
        {
            clock += TimeSpan.FromMilliseconds(1);
            var start = signIn.Start(Rq("GET", "/auth/steam/login"));
            Assert.Equal(302, start.Status);
            ids.Add(LoginOf(start));
        }
        Assert.Equal(400, (await Return(ids[0])).Status);
        Assert.Equal(302, (await Return(ids[^1])).Status);
    }

    [Fact]
    public void TestAFloodOfLinkCodesEvictsTheOldestNotTheNewcomer()
    {
        var codes = new List<string>();
        for (var i = 0; i < 10_001; i++)
        {
            clock += TimeSpan.FromMilliseconds(1);
            var made = signIn.NewLink(Rq("POST", "/auth/link"));
            Assert.Equal(200, made.Status);
            codes.Add((string)JsonNode.Parse(made.Body)!["code"]!);
        }
        Assert.Equal(404, signIn.PollLink(Rq("GET", "/auth/link/" + codes[0])).Status);
        Assert.Equal(200, signIn.PollLink(Rq("GET", "/auth/link/" + codes[^1])).Status);
    }
}

public class DogsWithoutATableTests : StateTestBase
{
    [Fact]
    public void TestWithoutDogSkillsADogIsLevelZeroAndItsRingIsEmpty()
    {
        var connect = Fixtures.CONNECT;
        connect.Remove("dog_skills");
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, discounts: Fixtures.DISCOUNTS);
        state.CreateAccount(9, "third");
        using (var con = state.Store.Open(9))
            PlayerRows.AddItem(con, 1, 700, 0, null, O("""{"dog_name": "Rex", "experience": 150}"""));
        Eq("""{"dogs": [{"id": 700, "name": "Rex", "type": "Test Hound", "variation": null, "level": 0, "percent": 0, "skills": [], "ribbons": 0}]}""", state.PublicDogs(9));
    }

    [Fact]
    public void TestABreedGroupDogListsItsTwelveSkillsOnceEachAtOnePlusItsUnlocks()
    {
        var connect = Fixtures.CONNECT;
        connect["dog_skills"] = JsonNode.Parse("""
            [{"lvl": 1, "xp": 0, "unlocks": [{"type": "loyalty", "skill": "DOG_SKILL_LOYALTY_STAMINA"}, {"type": "retrieving", "skill": "DOG_SKILL_RETRIEVE_RABBIT"}, {"type": "trophy", "define": "RIBBON"}]},
             {"lvl": 2, "xp": 100, "unlocks": [{"type": "retrieving", "skill": "DOG_SKILL_RETRIEVE_RABBIT"}, {"type": "pointing", "skill": "DOG_SKILL_DETECTION"}, {"type": "trophy", "define": "RIBBON"}]},
             {"lvl": 3, "xp": 200, "unlocks": [{"type": "retrieving", "skill": "DOG_SKILL_RETRIEVE_RABBIT"}, {"type": "trophy", "define": "RIBBON"}]}]
            """);
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, discounts: Fixtures.DISCOUNTS);
        state.CreateAccount(9, "third");
        using (var con = state.Store.Open(9))
            PlayerRows.AddItem(con, 1, 501, 0, null, O("""{"dog_name": "Bo", "experience": 150}"""));
        // 150 reaches two rows: stamina 1 + 1, rabbit 1 + 2, the pointing skill and the third row's unlocks do not count, two ribbons
        var skills = new (string Type, string Skill, int Level)[]
        {
            ("loyalty", "LOYALTY_STAMINA", 2), ("loyalty", "LOYALTY_FOLLOW_OWNER", 1), ("loyalty", "LOYALTY_CMD", 1), ("loyalty", "LOYALTY_FOLLOW_STANCE", 1), ("loyalty", "LOYALTY_FOCUS", 1), ("loyalty", "LOYALTY_STOICISM", 1),
            ("retrieving", "RETRIEVE_RABBIT", 3), ("retrieving", "RETRIEVE_WATERFOWL", 1), ("retrieving", "RETRIEVE_BLIND", 1), ("retrieving", "RETRIEVE_SOFT_MOUTH", 1), ("retrieving", "RETRIEVE_MARK_BIRD", 1), ("retrieving", "RETRIEVE_HONORING", 1),
        };
        var title = (string s) => string.Join(' ', s.Split('_').Select(w => w[0] + w[1..].ToLowerInvariant()));
        Eq(new JsonObject
        {
            ["dogs"] = new JsonArray(new JsonObject
            {
                ["id"] = 501, ["name"] = "Bo", ["type"] = "Test Retriever", ["variation"] = "Black", ["level"] = 2, ["percent"] = 50, ["ribbons"] = 2,
                ["skills"] = new JsonArray([.. skills.Select(x => (JsonNode?)new JsonObject { ["skill"] = x.Skill, ["title"] = title(x.Skill), ["type"] = x.Type, ["level"] = x.Level })]),
            }),
        }, state.PublicDogs(9));
    }
}
