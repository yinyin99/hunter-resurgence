using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Resurgence.Server;

using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Multiplayer sessions through the real HTTP pipeline: Launch/start_mp and Launch/join (and the start page's /auth/launch), Network/list, Map/locations, the connect reply's `network` of a host,
/// a joiner and a single-player hunt, heartbeat events 29 and 30, and a session that ends with its host. The players are invented.</summary>
public class MultiplayerTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    const string Api = "api.test", Gs = "gameserver.test", Guid1 = "9007199254740993", Guid2 = "1234567890123456789";
    const long HOST = 11, GUEST = 12, THIRD = 13;
    readonly Dictionary<long, string> tokens = [];
    double now = 1_000_000;
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        var connect = CONNECT;
        connect["network"] = O("""{"singleplayer": true, "host": 7, "user_id": 7, "handle": "x", "ip": "203.0.113.9", "max_players": 8, "clients": null}""");
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, offline: false) { Clock = () => now };
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest"), (THIRD, "third") })
        {
            state.CreateAccount(id, handle);
            tokens[id] = state.Store.CreateToken(id);
        }
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    Task<Reply> Send(string method, string host, string path, long? who = null, string body = "", string? session = null) =>
        server.SendAsync(method, host, path, [("Content-Type", "application/x-www-form-urlencoded"), .. who is { } w ? [("Authorization", "Bearer " + tokens[w])] : Array.Empty<(string, string)>(),
                                              .. session is null ? Array.Empty<(string, string)>() : [("X-Hun-Session", session)]], Encoding.UTF8.GetBytes(body));

    static JsonNode Json(Reply r) => JsonNode.Parse(r.Body)!;

    const string Start = "start=101&loctype=LODGE&reserve=2&time=540&loadout_id=0";

    /// <summary>The host opens a session; its session token.</summary>
    async Task<string> Host(string extra = "", long who = HOST)
    {
        var reply = await Send("POST", Api, "/v1/Launch/start_mp", who, $"{Start}&name=Test+Camp&max_players=3{extra}");
        Assert.Equal(("state", 200, "ok"), (reply.Source, reply.Status, (string)Json(reply)["status"]!));
        return (string)Json(reply)["session"]!;
    }

    async Task<JsonArray> List(string method = "GET") => Json(await Send(method, Api, "/v1/Network/list")).AsArray();

    async Task<long> NetworkId() => (long)(await List())[0]!["id"]!;

    Task<JsonNode> Connect(string session, long who) => Send("POST", Gs, Replay.CONNECT, who, "{}", session).ContinueWith(t => Json(t.Result));

    Task Beat(string session, long who, string events) => Send("POST", Gs, Replay.HEARTBEAT, who, $$"""{"events": [{{events}}]}""", session);

    static string Port(string guid, int port = 32303, string color = "c73378") => $$"""{"event": 29, "port": {{port}}, "guid": "{{guid}}", "color": "{{color}}"}""";

    /// <summary>The host's game reported its port and network id (what the first heartbeat carries).</summary>
    async Task<string> ReadyHost(string extra = "")
    {
        var session = await Host(extra);
        await Beat(session, HOST, Port(Guid1));
        return session;
    }

    async Task<Reply> Join(long who, string extra = "") => await Send("POST", Api, "/v1/Launch/join", who, $"start=101&loctype=LODGE&network_id={await NetworkId()}&loadout_id=0{extra}");

    static void Refused(Reply r)
    {
        Assert.Equal(("state", 200), (r.Source, r.Status));
        Eq("""{"errorCode": 23, "message": "USER_CANT_JOIN"}""", Json(r));
    }

    static string Joined(Reply r)
    {
        Assert.Equal(("state", 200, "ok"), (r.Source, r.Status, (string)Json(r)["status"]!));
        return (string)Json(r)["session"]!;
    }

    [Fact]
    public async Task TestAHostOpensASessionThatIsListedAndItsConnectNamesTheHost()
    {
        var session = await Host("&description=Fun&hs_min=5");
        foreach (var method in new[] { "GET", "POST" })
        {
            var list = await List(method);  // no token, either method
            var game = Assert.Single(list)!;
            Assert.True((long)game["id"]! >= 7_000_000_000L);
            var shown = game.DeepClone().AsObject();
            shown.Remove("id");
            shown["clients"]![0]!.AsObject().Remove("avatar");
            Eq($$"""
                {"name": "Test Camp", "handle": "host", "host": {{HOST}}, "description": "Fun", "password": false, "public": true, "singleplayer": false, "freeplayers": true,
                 "hs_min": 5, "hs_max": null, "max_players": 3, "start_ts": 1000000, "clients_len": 1,
                 "clients": [{"user_id": {{HOST}}, "handle": "host", "guid": "", "color": "", "x": 0, "y": 0, "z": 0, "hunterscore": 0}],
                 "reserve": {"id": 2, "define": "", "name": "Test Reserve", "species": [], "border": [] } }
                """, shown);
        }
        Eq($$"""{"singleplayer": false, "host": {{HOST}}, "user_id": {{HOST}}, "handle": "host", "ip": "127.0.0.1", "max_players": 3, "clients": null}""", (await Connect(session, HOST))["network"]);
        using var con = state.Store.Open(HOST);
        Assert.Equal("2:101", string.Join(",", con.Rows("SELECT reserve, location_id FROM expeditions WHERE account_id = $a AND end_ts IS NULL").Select(r => $"{r[0]}:{r[1]}")));  // the host's hunt is open at the start
    }

    [Fact]
    public async Task TestAGuestJoinsOnceTheHostHasReportedItsPortAndItsConnectNamesTheHost()
    {
        var hostSession = await Host();
        Refused(await Join(GUEST));  // the host's port and network id are not known yet
        await Connect(hostSession, HOST);
        await Beat(hostSession, HOST, Port(Guid1, 32304, "9B9BE4"));
        var first = (await List())[0]!["clients"]![0]!;
        Assert.Equal(("9007199254740993", "9b9be4"), ((string)first["guid"]!, (string)first["color"]!));
        var guestSession = Joined(await Join(GUEST));
        Eq($$"""
            {"singleplayer": false, "host": {{HOST}}, "user_id": {{GUEST}}, "handle": "guest", "ip": "127.0.0.1", "max_players": 3,
             "clients": [{"user_id": {{HOST}}, "handle": "host", "ip": "127.0.0.1", "port": 32304, "guid": "9007199254740993"}]}
            """, (await Connect(guestSession, GUEST))["network"]);
        await Beat(guestSession, GUEST, Port(Guid2, 32303, "c73378"));
        var game = (await List())[0]!;
        Assert.Equal(2L, (long)game["clients_len"]!);
        Assert.Equal([HOST, GUEST], game["clients"]!.AsArray().Select(c => (long)c!["user_id"]!));
        Assert.Equal([Guid1, Guid2], game["clients"]!.AsArray().Select(c => (string)c!["guid"]!));
        using var con = state.Store.Open(GUEST);
        Assert.Equal("2:101", string.Join(",", con.Rows("SELECT reserve, location_id FROM expeditions WHERE account_id = $a AND end_ts IS NULL").Select(r => $"{r[0]}:{r[1]}")));  // the guest hunts in the session's reserve
    }

    [Fact]
    public async Task TestTheHostsPositionsAreListedAndAMissingOrBrokenEventChangesNothing()
    {
        var session = await ReadyHost();
        await Beat(session, HOST, Port("not a number") + "," + Port(Guid2, 0) + "," + Port(Guid2, 70000));
        Assert.Equal(Guid1, (string)(await List())[0]!["clients"]![0]!["guid"]!);
        await Send("POST", Gs, Replay.HEARTBEAT, HOST, $$"""{"events": [], "player_positions": [{"user_id": {{HOST}}, "x": 12.5, "y": 3, "z": -7, "hunterscore": 1}, {"user_id": 999, "x": 1, "y": 1, "z": 1}]}""", session);
        Eq("""[12.5, 3, -7]""", T((await List())[0]!["clients"]![0]!["x"], (await List())[0]!["clients"]![0]!["y"], (await List())[0]!["clients"]![0]!["z"]));
    }

    [Fact]
    public async Task TestAJoinIsRefusedForAWrongPasswordAFullSessionTheHostItselfAndAnUnknownSession()
    {
        await ReadyHost("&password=s3cret");
        Refused(await Join(GUEST));
        Refused(await Join(GUEST, "&password=wrong"));
        Assert.Equal(("ok"), (string)Json(await Join(GUEST, "&password=s3cret"))["status"]!);
        Refused(await Send("POST", Api, "/v1/Launch/join", GUEST, "start=101&loctype=LODGE&network_id=1&loadout_id=0"));  // no such session
        Refused(await Send("POST", Api, "/v1/Launch/join", HOST, $"start=101&loctype=LODGE&network_id={await NetworkId()}&loadout_id=0&password=s3cret"));  // the host's own

        // a third fits (max 3)
        var third = await Join(THIRD, "&password=s3cret");
        Assert.Equal("ok", (string)Json(third)["status"]!);
        state.CreateAccount(14, "fourth");
        tokens[14] = state.Store.CreateToken(14);
        Refused(await Join(14, "&password=s3cret"));  // full
    }

    [Fact]
    public async Task TestAFriendsOnlySessionAdmitsOnlyFriendsAndAHunterscoreRangeOnlyItsPlayers()
    {
        await ReadyHost("&public_game=0&hs_min=100&hs_max=200");
        state.Store.AddFriend(GUEST, HOST);
        Refused(await Join(GUEST));  // a request only
        state.Store.AddFriend(HOST, GUEST);
        Refused(await Join(GUEST));  // friends, but a hunterscore of 0
        using (var con = state.Store.Open(GUEST))
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 150) WHERE id = ?", GUEST);
        Joined(await Join(GUEST));
        using (var con = state.Store.Open(THIRD))
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 150) WHERE id = ?", THIRD);
        Refused(await Join(THIRD));  // in range, not a friend
    }

    [Fact]
    public async Task TestEvent30DropsTheJoinerWhoseConnectionFailedAndNobodyElse()
    {
        var hostSession = await ReadyHost();
        var guestSession = Joined(await Join(GUEST));
        await Beat(hostSession, HOST, $$"""{"event": 30, "guid": "{{Guid1}}"}""");  // the host is never dropped
        await Beat(guestSession, GUEST, $$"""{"event": 30, "guid": "{{Guid2}}"}""");  // not the host's guid
        Assert.Equal(2L, (long)(await List())[0]!["clients_len"]!);
        await Beat(guestSession, GUEST, $$"""{"event": 30, "guid": "{{Guid1}}"}""");
        Assert.Equal([HOST], (await List())[0]!["clients"]!.AsArray().Select(c => (long)c!["user_id"]!));
        Assert.Equal(1L, (long)(await List())[0]!["clients_len"]!);
    }

    [Fact]
    public async Task TestTheHostLeavingEndsTheSessionForAll()
    {
        var hostSession = await ReadyHost();
        var guestSession = Joined(await Join(GUEST));
        await Send("POST", Gs, Replay.DISCONNECT, HOST, "{}", hostSession);
        Assert.Empty(await List());
        Assert.True((bool)(await Connect(guestSession, GUEST))["network"]!["singleplayer"]!);  // the guest's connect is a hunt of its own
    }

    [Fact]
    public async Task TestSilenceEndsAJoinerAndASilentHostEndsTheSession()
    {
        var hostSession = await ReadyHost();
        var guestSession = Joined(await Join(GUEST));
        await Beat(guestSession, GUEST, Port(Guid2));
        now += 100;
        await Beat(hostSession, HOST, "");
        now += 100;
        await Beat(hostSession, HOST, "");  // the guest was silent for 200 s
        Assert.Equal([HOST], (await List())[0]!["clients"]!.AsArray().Select(c => (long)c!["user_id"]!));
        now += 200;  // the host too
        Assert.Empty(await List());
        Refused(await Send("POST", Api, "/v1/Launch/join", GUEST, "start=101&loctype=LODGE&network_id=1&loadout_id=0"));
    }

    [Fact]
    public async Task TestAGameThatNeverStartsEndsAfterItsGrace()
    {
        await Host();
        now += Games.Grace - 1;
        Assert.Single(await List());
        now += 1;
        Assert.Empty(await List());
    }

    [Fact]
    public async Task TestASinglePlayerHuntKeepsItsConnectAndLeavesTheSessionOfItsPlayer()
    {
        await Host();
        var launched = Json(await Send("POST", Api, "/v1/Launch/start", HOST, Start));
        Assert.Empty(await List());  // a hunt of one's own ends the session one hosted
        var network = (await Connect((string)launched["session"]!, HOST))["network"]!;
        Assert.True((bool)network["singleplayer"]!);
        Assert.Null(network["ip"]);
        Assert.Null(network["clients"]);
    }

    [Fact]
    public async Task TestTheStartPageHostsAndJoinsAndNamesWhyAJoinFailed()
    {
        string Cookie(long who) => $"{SignIn.SessionCookie}={state.Store.CreateWebSession(who, TimeSpan.FromHours(1))}";
        async Task<Reply> Page(long who, string form) => await server.SendAsync("POST", "web.test", "/auth/launch",
            [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "XMLHttpRequest"), ("Cookie", Cookie(who))], Encoding.UTF8.GetBytes(form));
        static string Session(Reply r) => Regex.Match((string)Json(r)["url"]!, "/([0-9a-f]{40})/lang/").Groups[1].Value;
        static string Error(Reply r) => (string)Json(r)["error"]!;

        var hosted = await Page(HOST, "mp=host&reserve=2&lodge=101&time=540&loadout=0&name=Web+Camp&description=Hi%26there&public_game=1&free_players=1&password=pw&max_players=2");
        Assert.Equal(200, hosted.Status);
        var game = Assert.Single(await List())!;
        Assert.Equal(("Web Camp", "Hi&there", true, 2L), ((string)game["name"]!, (string)game["description"]!, (bool)game["password"]!, (long)game["max_players"]!));
        var id = (long)game["id"]!;
        Eq($$"""{"singleplayer": false, "host": {{HOST}}, "user_id": {{HOST}}, "handle": "host", "ip": "127.0.0.1", "max_players": 2, "clients": null}""", (await Connect(Session(hosted), HOST))["network"]);

        Assert.Equal("host_not_ready", Error(await Page(GUEST, $"mp=join&network_id={id}&lodge=101&loadout=0&password=pw")));
        await Beat(Session(hosted), HOST, Port(Guid1));
        Assert.Equal("password", Error(await Page(GUEST, $"mp=join&network_id={id}&lodge=101&loadout=0&password=no")));
        Assert.Equal("gone", Error(await Page(GUEST, "mp=join&network_id=5&lodge=101&loadout=0")));
        var joined = await Page(GUEST, $"mp=join&network_id={id}&lodge=101&loadout=0&password=pw");  // no reserve, no time
        Assert.Equal(200, joined.Status);
        var entry = (await Connect(Session(joined), GUEST))["network"]!["clients"]![0]!;
        Assert.Equal(($"{HOST}", "32303", Guid1), (entry["user_id"]!.ToString(), entry["port"]!.ToString(), (string)entry["guid"]!));
        Assert.Equal("full", Error(await Page(THIRD, $"mp=join&network_id={id}&lodge=101&loadout=0&password=pw")));
        Assert.Equal("own_game", Error(await Page(HOST, $"mp=join&network_id={id}&lodge=101&loadout=0&password=pw")));
        Assert.Equal(400, (await Page(GUEST, $"mp=watch&network_id={id}&lodge=101")).Status);
        Assert.Equal(400, (await Page(GUEST, "mp=host&lodge=101")).Status);  // a host needs a reserve
    }

    [Fact]
    public async Task TestTheWebsiteHostListsTheSameGamesWithoutACookie()
    {
        Eq("[]", Json(await Send("GET", "web.test", "/catalog/games")));
        await Host();
        var reply = await Send("GET", "web.test", "/catalog/games");
        Assert.Equal(("catalog", 200), (reply.Source, reply.Status));
        Eq(await List(), Json(reply));
        Assert.Equal("Test Camp", (string)Json(reply)[0]!["name"]!);
    }

    Task<Reply> Online() => Send("GET", "web.test", "/stats/online");

    static string Counts(Reply r) => $"{Json(r)["players"]}/{Json(r)["games"]}";

    [Fact]
    public async Task TestTheOnlineCountsFollowTheHeartbeatsOfTheLastTwoMinutesAndAreKeptFor30Seconds()
    {
        var none = await Online();  // no cookie, no token
        Assert.Equal(("stats", 200, "0/0"), (none.Source, none.Status, Counts(none)));
        Assert.Contains(("Cache-Control", "public, max-age=30"), none.Headers);
        Eq("""{"players": 0, "games": 0}""", Json(none));

        await ReadyHost();  // a hunt and a game, the host's heartbeat now
        Assert.Equal("0/0", Counts(await Online()));  // the answer of a moment ago
        now += 60;
        Assert.Equal("1/1", Counts(await Online()));  // a heartbeat 1 minute old counts
        now += 120;
        Assert.Equal("0/0", Counts(await Online()));  // 3 minutes old: the hunt and its game have gone quiet
    }

    [Fact]
    public async Task TestTheOnlineCountsCountAccountsOnceAndOnlyTheGamesStillOpen()
    {
        await ReadyHost();
        var guestSession = Joined(await Join(GUEST));
        await Beat(guestSession, GUEST, "");
        now += 40;
        Assert.Equal("2/1", Counts(await Online()));  // two players in one game

        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE handle = 'guest'");
        now += 40;
        Assert.Equal("1/1", Counts(await Online()));  // a banned account is not counted

        Assert.Equal(200, (await Send("POST", Api, "/v1/Launch/start", HOST, Start)).Status);  // the host's own hunt ends its game
        now += 40;
        Assert.Equal("1/0", Counts(await Online()));
    }

    [Fact]
    public async Task TestTheAddressIsTheLastForwardedOneOfATrustedProxyAndNeverTheProxysOwn()
    {
        var session = await Host();
        Settings.TryNetwork("127.0.0.0/8", out var loopback);
        await using var proxied = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp, [loopback]);
        async Task<JsonNode?> Ip(string? forwarded) => Json(await proxied.SendAsync("POST", Gs, Replay.CONNECT,
            [("X-Hun-Session", session), .. forwarded is null ? Array.Empty<(string, string)>() : [("X-Forwarded-For", forwarded)]], "{}"u8.ToArray()))["network"]!["ip"];
        Assert.Equal("203.0.113.5", (string?)await Ip("198.51.100.7, 203.0.113.5"));  // the one the proxy appended
        Assert.Equal("2001:db8::1", (string?)await Ip("2001:db8::1"));
        Assert.Null(await Ip(null));  // a proxy that names none
        Assert.Null(await Ip("not an address"));
        var direct = await server.SendAsync("POST", Gs, Replay.CONNECT, [("X-Hun-Session", session), ("X-Forwarded-For", "203.0.113.5")], "{}"u8.ToArray());
        Assert.Equal("127.0.0.1", (string?)Json(direct)["network"]!["ip"]);  // a peer that is no proxy: the header counts for nothing
    }

    [Fact]
    public async Task TestMapLocationsAreThoseOfTheSessionsReserve()
    {
        await Host();
        var id = await NetworkId();
        foreach (var form in new[] { $"network_id={id}", "reserve=2", $"network_id={id}&reserve=9" })
        {
            var rows = Json(await Send("POST", Api, "/v1/Map/locations", HOST, form)).AsArray();
            Eq("""[{"id": 101, "define": "", "name": "", "description": "", "reserve": 2, "type": "LODGE", "pos": {"y": 150, "x": -6420, "z": 3310}, "metadata": {}}]""", rows);
        }
        Eq("[]", Json(await Send("POST", Api, "/v1/Map/locations", HOST, "network_id=5")));
        Eq("[]", Json(await Send("POST", Api, "/v1/Map/locations", HOST, "")));
    }
}
