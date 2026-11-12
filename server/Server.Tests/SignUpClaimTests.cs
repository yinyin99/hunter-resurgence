using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Data.Sqlite;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The first sign-in's "I played before" choice, with a fake archive.db of one made-up player (Alpha, hunterscore 500, one item).</summary>
public class SignUpClaimTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))).Append(KeyValuePair.Create("signup_webhook", (JsonNode?)"https://hooks.test/s/1")));
    static readonly (string, string)[] Form = [("Content-Type", "application/x-www-form-urlencoded")];
    readonly FakeSteam fake = new();
    Loopback server = null!;
    readonly Capture hook = new();
    readonly HttpClient oldShared = Alerts.Shared;

    sealed class Capture : HttpMessageHandler
    {
        public readonly List<string> Texts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Texts) Texts.Add((string)JsonNode.Parse(request.Content!.ReadAsStringAsync(ct).Result)!["content"]!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    async Task<string> OneAlert()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && hook.Texts.Count == 0) await Task.Delay(20);
        await Task.Delay(100);
        return Assert.Single(hook.Texts);
    }

    public async ValueTask InitializeAsync()
    {
        Alerts.Shared = new HttpClient(hook);
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        MakeArchive();
        var table = new HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Hit(2, "", 200, [], "{}"u8.ToArray());
        server = await Loopback.StartAsync(new Game(table, Cfg, state, tmp), tmp, steamHttp: new HttpClient(fake));  // the server looks for archive.db in the work folder
    }

    public async ValueTask DisposeAsync()
    {
        Alerts.Shared = oldShared;
        await server.DisposeAsync();
    }

    void MakeArchive()
    {
        using var con = new SqliteConnection($"Data Source={Path.Combine(tmp, "archive.db")};Pooling=False");
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
        Run("INSERT INTO player VALUES (3001, 'Alpha', 'alpha', 500)");
        void Reply(string path, string endpoint, string body, string key, string value) =>
            Run("INSERT INTO reply VALUES (3001, $p, $e, $q, 200, $b)", ("$p", path), ("$e", endpoint),
                ("$q", new JsonObject { [key] = value }.ToJsonString()), ("$b", Encoding.UTF8.GetBytes(body)));
        Reply("Public_user_getByHostname__hostname=Alpha.json", "/v1/Public_user/getByHostname", """{"id":3001,"handle":"Alpha","hostname":"alpha","hunterscore":500}""", "hostname", "Alpha");
        Reply("Inventory_dict__user_id=3001.json", "/v1/Inventory/dict", """{"500":[{"id":9001,"slot":1,"units":3,"metadata":{}}]}""", "user_id", "3001");
    }

    static string Body(Reply r) => Encoding.UTF8.GetString(r.Body);

    /// <summary>Steam confirms a new Steam id; the browser then holds a login that waits for its name.</summary>
    async Task<string> NewLogin(string steamId)
    {
        var start = await server.SendAsync("GET", "web.test", "/auth/steam/login");
        var login = start.Headers.Where(h => h.Name == "Set-Cookie" && h.Value.StartsWith("hr_login=")).Select(h => h.Value.Split(';')[0]["hr_login=".Length..]).First();
        var returnTo = HttpUtility.ParseQueryString(new Uri(start.Headers.Single(h => h.Name == "Location").Value).Query)["openid.return_to"]!;
        var path = returnTo["https://web.test".Length..];
        var back = await server.SendAsync("GET", "web.test", path + "&" + FakeSteam.Assertion(returnTo, steamId), [("Cookie", "hr_login=" + login)]);
        Assert.Equal("/auth/handle", back.Headers.Single(h => h.Name == "Location").Value);
        return login;
    }

    Task<Reply> Post(string login, string form) =>
        server.SendAsync("POST", "web.test", "/auth/handle", [.. Form, ("Origin", "https://web.test"), ("Cookie", "hr_login=" + login)], Encoding.UTF8.GetBytes($"csrf={login}&{form}"));

    [Fact]
    public async Task TestAReturningPlayerGetsTheArchivedProfileAtTheFirstSignIn()
    {
        var login = await NewLogin("99999999999999991");
        var page = await server.SendAsync("GET", "web.test", "/auth/handle", [("Cookie", "hr_login=" + login)]);
        Assert.Contains("I played before", Body(page));

        var done = await Post(login, "kind=old&old=ALPHA&handle=");  // empty name: the old one, free, any case of the typed old name
        Assert.Equal(302, done.Status);
        var account = state.Store.AccountBySteam("99999999999999991")!.Value;
        Assert.Equal("Alpha", state.Store.HandleOf(account));
        Assert.Contains(state.Inventory(account), i => (string)i!["guid"]! == "9001");
    }

    [Fact]
    public async Task TestAnUnknownOldNameShowsThePageAgainAndMakesNoAccount()
    {
        var login = await NewLogin("99999999999999992");
        var page = await Post(login, "kind=old&old=nobody&handle=Fresh");
        Assert.Equal(200, page.Status);
        Assert.Contains("We did not keep a profile under this name.", Body(page));
        Assert.Null(state.Store.AccountBySteam("99999999999999992"));

        var taken = await Post(login, "kind=new&handle=Fresh");  // "I am new here" still works on the same login
        Assert.Equal(302, taken.Status);
        Assert.Equal("Fresh", state.Store.HandleOf(state.Store.AccountBySteam("99999999999999992")!.Value));
    }

    [Fact]
    public async Task TestASignUpSendsOneSignupAlertWithItsOutcome()
    {
        var fresh = await Post(await NewLogin("99999999999999993"), "kind=new&handle=Newbie");
        Assert.Equal(302, fresh.Status);
        var text = await OneAlert();
        Assert.Contains("New account: Newbie, account ", text);
        Assert.Contains("fresh start", text);
        Assert.Contains("<https://steamcommunity.com/profiles/99999999999999993>", text);

        hook.Texts.Clear();
        await Post(await NewLogin("99999999999999994"), "kind=old&old=Alpha&handle=");
        Assert.Contains("claimed Alpha (hunterscore 500): restored", await OneAlert());

        hook.Texts.Clear();
        Alerts.Signup(new JsonObject(), "unset", new HttpClient(hook));
        await Task.Delay(200);
        Assert.Empty(hook.Texts);
    }
}
