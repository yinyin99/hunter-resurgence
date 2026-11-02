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
    async Task<(string Login, string ReturnPath)> Begin(string host, string query = "")
    {
        var start = await Get(host, "/auth/steam/login" + query);
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
        Eq("""{"handle": "two", "hunterscore": 50, "rank": 3}""", JsonNode.Parse((await Get("web.test", "/hunters/TWO")).Body));
        Assert.Equal(404, (await Get("web.test", "/hunters/nobody")).Status);
        Assert.Equal(404, (await Get("web.test", "/hunters/banned")).Status);
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
        var cookie = await SignIn("99999999999999991", "Hunter_One", "signup.test");
        page = await Get("signup.test", "/", Ck("hr_session", cookie));
        Assert.Contains("Hello, Hunter_One", Body(page));
        Assert.Contains("id=\"play\"", Body(page));
        Assert.DoesNotContain("=>", Body(page));   // ES5 only: the launcher's browser is Chromium 25
        Assert.DoesNotContain("let ", Body(page));
        Assert.DoesNotContain("const ", Body(page));
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
