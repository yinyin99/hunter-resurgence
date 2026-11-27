using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A banned Steam id's sign-in attempts (#969): one log line and, rate-limited, one operator alert each, and the count on the ban row.
/// SignIn with a clock, a log and an alert the test sets.</summary>
public class BannedSignInTests : StateTestBase
{
    const string Host = "web.test", Banned = "88888888888888889";
    readonly FakeSteam fake = new();
    readonly List<string> logs = [], alerts = [];
    DateTimeOffset clock = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    readonly SignIn signIn;

    public BannedSignInTests()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        signIn = new SignIn(state, new SteamOpenId(new HttpClient(fake), () => clock), () => clock) { Log = logs.Add, BanAlert = alerts.Add };
    }

    static Req Rq(string method, string path, string query = "", string? cookie = null) => new(method, Host, path, query,
        k => k == "Origin" ? "https://" + Host : k == "Cookie" ? cookie : null, [], true);

    static string LoginOf(Reply start) => start.Headers.Single(h => h.Name == "Set-Cookie" && h.Value.StartsWith("hr_login=")).Value.Split(';')[0]["hr_login=".Length..];

    /// <summary>The start of a sign-in on a route: the browser alone (web), for a launcher's code (link) or for an app on this PC (app); its login.</summary>
    string Begin(string route)
    {
        if (route == "link")
        {
            var code = (string)System.Text.Json.Nodes.JsonNode.Parse(signIn.NewLink(Rq("POST", "/auth/link")).Body)!["code"]!;
            return LoginOf(signIn.Start(Rq("GET", "/auth/steam/login", "link=" + code)));
        }
        if (route == "app")
        {
            var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes("a verifier of the app")));
            return LoginOf(signIn.StartApp(Rq("GET", "/auth/app/start", $"challenge={challenge}&port=51234&state=abcdefghijklmnop0123456789")));
        }
        return LoginOf(signIn.Start(Rq("GET", "/auth/steam/login")));
    }

    Task<Reply> Return(string login, string steamId)
    {
        var returnTo = $"https://{Host}/auth/steam/return?login={login}";
        var nonce = clock.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "x" + Guid.NewGuid().ToString("N");
        return signIn.Return(Rq("GET", "/auth/steam/return", $"login={login}&" + FakeSteam.Assertion(returnTo, steamId, nonce), $"hr_login={login}"));
    }

    static void Refused(Reply r) => Assert.Equal("/signin/problem?reason=banned", r.Headers.Single(h => h.Name == "Location").Value);

    async Task Attempt(string route, string steamId = Banned) => Refused(await Return(Begin(route), steamId));

    [Fact]
    public async Task TestEachRouteLogsTheAttemptCountsItAndAlertsWithItsRoute()
    {
        var account = state.Store.NextServerId();
        state.CreateAccount(account, "banned_one", Banned);
        Assert.Equal(account, AdminApi.Ban(state, "banned_one", "a reason the player reads").Done!.Account);
        var times = new List<string>();
        foreach (var route in new[] { "web", "link", "app" })
        {
            times.Add(clock.ToString("u"));
            await Attempt(route);
            clock += TimeSpan.FromHours(2);  // each attempt is the first after an hour: each alerts
        }
        Assert.Equal(
        [
            $"banned sign-in {times[0]} steam {Banned} account {account} route web attempt 1",
            $"banned sign-in {times[1]} steam {Banned} account {account} route launcher link attempt 2",
            $"banned sign-in {times[2]} steam {Banned} account {account} route app attempt 3",
        ], logs);
        Assert.Equal(3, alerts.Count);
        Assert.StartsWith("\U0001F6AB **Banned Steam id tried to sign in**\nsteam id: `" + Banned + "`\nsteam: ", alerts[0]);
        Assert.Contains($"\naccount: {account}\nroute: web\nattempts: 1\ntime: {times[0]}", alerts[0]);
        Assert.Contains("\nroute: launcher link\nattempts: 2\n", alerts[1]);
        Assert.Contains("\nroute: app\nattempts: 3\n", alerts[2]);
        Assert.DoesNotContain("a reason the player reads", string.Concat(logs) + string.Concat(alerts));  // the reason is the player's, not in the log
        Assert.DoesNotContain("hr_login", string.Concat(logs) + string.Concat(alerts));
        Assert.Equal([(Banned, 3L, clock.AddHours(-2).ToUnixTimeSeconds())], state.Store.BanAttempts());
    }

    [Fact]
    public async Task TestAPlayerWhoRetriesGetsOneAlertPerBurstAndEveryAttemptIsLoggedAndCounted()
    {
        AdminApi.Ban(state, Banned, "r");
        for (var i = 0; i < 5; i++) await Attempt("web");
        Assert.Single(alerts);
        clock += TimeSpan.FromMinutes(30);
        await Attempt("web");
        Assert.Single(alerts);  // the last attempt was half an hour ago: the same burst
        clock += TimeSpan.FromMinutes(61);
        await Attempt("app");
        Assert.Equal(2, alerts.Count);
        Assert.Contains("\nroute: app\nattempts: 7\n", alerts[1]);
        Assert.Equal(7, logs.Count);
        Assert.EndsWith("account none route app attempt 7", logs[^1]);
        Assert.Equal(7L, state.Store.BanAttempts().Single().Attempts);
    }

    [Fact]
    public async Task TestAnAttemptWhileTheBanIsLiftedIsNotCountedAndALiftedBanIsNotListed()
    {
        AdminApi.Ban(state, Banned, "r");
        await Attempt("web");
        Assert.Equal(200, AdminApi.Unban(state, Banned).Status);
        Assert.Empty(state.Store.BanAttempts());
        Assert.Null(state.Store.BanAttempt(Banned, 1));
        AdminApi.Ban(state, Banned, "again");
        Assert.Equal((2L, (long?)clock.ToUnixTimeSeconds()), state.Store.BanAttempt(Banned, clock.ToUnixTimeSeconds() + 1));  // the count of the row goes on
    }

    [Fact]
    public async Task TestAnAccountFlaggedBannedWithoutARowIsLoggedAndEveryAttemptAlerts()
    {
        var account = state.Store.NextServerId();
        state.CreateAccount(account, "flagged", "99999999999999991");
        using (var con = state.Store.Open()) con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", account);
        for (var i = 0; i < 2; i++) Refused(await Return(Begin("web"), "99999999999999991"));  // the session of a banned account is refused: no ban row to count on
        Assert.Equal([$"banned sign-in 2027-01-15 12:00:00Z steam 99999999999999991 account {account} route web"], logs.Distinct());
        Assert.Equal(2, logs.Count);
        Assert.Equal(2, alerts.Count);
        Assert.DoesNotContain("attempts:", alerts[0]);
        Assert.Empty(state.Store.BanAttempts());
    }

    [Fact]
    public async Task TestABanBetweenTheSteamReturnAndTheNameFormIsLoggedToo()
    {
        var login = Begin("web");
        Assert.Equal("/signin/handle", (await Return(login, Banned)).Headers.Single(h => h.Name == "Location").Value);
        Assert.Empty(logs);
        AdminApi.Ban(state, Banned, "r");
        Refused(signIn.ChooseHandle(Rq("GET", "/auth/handle", "", $"hr_login={login}")));
        Assert.Equal([$"banned sign-in 2027-01-15 12:00:00Z steam {Banned} account none route web attempt 1"], logs);
        Assert.Single(alerts);
        Assert.Null(state.Store.AccountBySteam(Banned));  // no account was made
    }

    [Fact]
    public void TestADatabaseAtTheCurrentVersionWithoutTheAttemptColumnsGetsThem()
    {
        var file = Path.Combine(tmp, "current.db");
        var store = new Store(file);
        store.Ban(Banned, "kept", 1_800_000_000);
        using (var con = store.Open())
        {
            con.Exec("ALTER TABLE banned_steam DROP COLUMN attempts");
            con.Exec("ALTER TABLE banned_steam DROP COLUMN last_attempt");  // user_version stays at Store.Version
            Assert.Equal((long)Store.Version, con.Scalar("PRAGMA user_version"));
        }
        var check = new Store(file);
        using var c2 = check.Open();
        Assert.Equal(2L, c2.Scalar("SELECT COUNT(*) FROM pragma_table_info('banned_steam') WHERE name IN ('attempts', 'last_attempt')"));
        Assert.Equal((1L, (long?)null), check.BanAttempt(Banned, 5));  // the ban row survived and starts at 0 attempts
        Assert.Equal((2L, (long?)5), check.BanAttempt(Banned, 6));
    }
}
