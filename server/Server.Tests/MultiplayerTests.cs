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
        var app = APP;  // two items that can be placed: a tent and a stand
        foreach (var (id, define) in new[] { (901, "TENT02"), (902, "TREE_STAND01") })
            app["items"]!.AsArray().Add(JsonNode.Parse($$"""
                {"id": {{id}}, "name": "Test {{define}}", "description": "", "define": "{{define}}", "type": 41, "group": 13, "cat": 41, "client_type": 4,
                 "image": "x", "variationname": null, "units": null, "fit": {}, "inventory": {"slots": [0, 23], "capacity": 0, "weight": 1},
                 "price": 10, "silver_price": 100, "notbuyable": 0, "ownMultiple": true, "subcat": {"id": 0}, "hidden": 0}
                """));
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES, offline: false, facts: MapFacts()) { Clock = () => now };
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest"), (THIRD, "third") })
        {
            state.CreateAccount(id, handle);
            tokens[id] = state.Store.CreateToken(id);
        }
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    protected override ValueTask StopServersAsync() => server.DisposeAsync();

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
    public async Task TestTheWebsiteHostListsTheSameGamesToASignedInPlayer()
    {
        (string, string)[] signedIn = [("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(THIRD, TimeSpan.FromHours(1))}")];
        Eq("[]", Json(await server.SendAsync("GET", "web.test", "/catalog/games", signedIn)));
        await Host();
        var refused = await Send("GET", "web.test", "/catalog/games");  // signed out: refused, while the game's own list still answers without a token
        Assert.Equal((401, "signed_out"), (refused.Status, (string)Json(refused)["status"]!));
        Assert.NotEmpty(Json(await Send("GET", "api.test", "/v1/Network/list")).AsArray());
        var reply = await server.SendAsync("GET", "web.test", "/catalog/games", signedIn);
        Assert.Equal(("catalog", 200), (reply.Source, reply.Status));
        var listed = Json(reply);
        var game = listed[0]!.AsObject();
        Assert.Equal((false, "starting"), ((bool)game["joinable"]!, (string?)game["why"]));  // the host's game has not reported its port yet
        Assert.Null(game["clients"]![0]!["mapX"]);  // no position yet
        foreach (var name in new[] { "joinable", "why" })
            game.Remove(name);
        foreach (var client in game["clients"]!.AsArray())
            foreach (var name in new[] { "mapX", "mapY" })
                client!.AsObject().Remove(name);
        // the caller may not join this game yet (`starting`), so the website's list hides the raw coordinates too (null); the game's own list cannot tell who asks and sends 0
        Assert.All(game["clients"]!.AsArray(), c => Assert.Equal((true, true, true), (c!["x"] is null, c["y"] is null, c["z"] is null)));
        foreach (var client in game["clients"]!.AsArray())
            foreach (var name in new[] { "x", "y", "z" })
                client![name] = 0;
        Eq(await List(), listed);  // the game's own list has none of them
        Assert.Equal("Test Camp", (string)listed[0]!["name"]!);
    }

    /// <summary>The first game of `GET /catalog/games` as `who` sees it.</summary>
    async Task<JsonNode> Listed(long who) =>
        Json(await server.SendAsync("GET", "web.test", "/catalog/games", [("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(who, TimeSpan.FromHours(1))}")]))[0]!;

    /// <summary>The list gives `who` this `why` (and `joinable` when it is null), and `Launch/join` lets the player in exactly when it is null: the same answer, for the same player and game.</summary>
    async Task Agrees(long who, string? why)
    {
        var game = await Listed(who);
        Assert.Equal((why is null, why), ((bool)game["joinable"]!, (string?)game["why"]));
        Assert.Equal(why is null, (string?)Json(await Join(who))["status"] == "ok");
    }

    void Score(long who, int score)
    {
        using var con = state.Store.Open(who);
        con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE id = ?", score, who);
    }

    [Fact]
    public async Task TestTheGamesListSaysWhyAPlayerCannotJoinAndTheJoinAgrees()
    {
        await Host();
        await Agrees(GUEST, "starting");  // the host's port and network id are not known yet

        await ReadyHost();  // a new game of the host; max 3
        await Agrees(HOST, "own");
        await Agrees(THIRD, null);
        await Agrees(GUEST, null);
        state.CreateAccount(14, "fourth");
        tokens[14] = state.Store.CreateToken(14);
        await Agrees(14, "full");

        await ReadyHost("&public_game=0");
        await Agrees(THIRD, "friends");
        state.Store.AddFriend(GUEST, HOST);
        await Agrees(GUEST, "friends");  // a request only
        state.Store.AddFriend(HOST, GUEST);
        await Agrees(GUEST, null);

        await ReadyHost("&hs_min=100&hs_max=200");
        await Agrees(THIRD, "hunterscore");  // below
        Score(THIRD, 300);
        await Agrees(THIRD, "hunterscore");  // above
        Score(THIRD, 150);
        await Agrees(THIRD, null);

        await ReadyHost("&password=s3cret");
        await Agrees(GUEST, "password");  // a password game is never joinable, whatever the player
        await Agrees(HOST, "own");
    }

    /// <summary>The shipped facts with a map of our own for reserve 2: x from -1000 to 1000 and z from 500 to 1500 (the box the positions are percent of).</summary>
    static Facts MapFacts()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "facts", "facts.json")))!.AsObject();
        json["reserves"]!.AsArray().Single(r => (int)r!["id"]! == 2)!["map"] = new JsonArray(-1000, 500, 1000, 1500);
        return Facts.Parse(json.ToJsonString());
    }

    /// <summary>The host's heartbeat tells where each player is (game metres).</summary>
    Task Positions(string session, params (long Who, double X, double Z)[] at) =>
        Send("POST", Gs, Replay.HEARTBEAT, HOST, "{\"events\": [], \"player_positions\": [" + string.Join(", ", at.Select(p =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{"user_id": {{p.Who}}, "x": {{p.X}}, "y": 80, "z": {{p.Z}} }"""))) + "]}", session);

    /// <summary>`mapX,mapY` of each client of the first game as `who` sees it ("-" for none).</summary>
    async Task<string[]> Places(long who) =>
        [.. (await Listed(who))["clients"]!.AsArray().Select(c => c!["mapX"] is null ? "-" : $"{Num(c["mapX"])},{Num(c["mapY"])}")];

    static string Num(JsonNode? n) => ((double)n!).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TestEachClientHasItsPositionAsPercentOfTheReservesMapAndNullWithoutOne()
    {
        var session = await ReadyHost();
        Joined(await Join(GUEST));
        Assert.Equal(["-", "-"], await Places(THIRD));  // joined, no heartbeat with positions yet: listed, not placed
        await Positions(session, (HOST, -1000, 500), (GUEST, 0, 1000));  // the top left corner and the centre
        Assert.Equal(["0,0", "50,50"], await Places(THIRD));
        await Positions(session, (HOST, 1000, 1500), (GUEST, 123.456, 1000.1));  // the bottom right corner; two decimals
        Assert.Equal(["100,100", "56.17,50.01"], await Places(THIRD));
        Joined(await Join(THIRD));
        Assert.Equal(["100,100", "56.17,50.01", "-"], await Places(THIRD));
        var host = (await Listed(THIRD))["clients"]![0]!;
        Assert.Equal((1000L, 80L, 1500L), ((long)host["x"]!, (long)host["y"]!, (long)host["z"]!));  // the game's own coordinates stay
        Assert.All((await List())[0]!["clients"]!.AsArray(), c => Assert.False(c!.AsObject().ContainsKey("mapX")));  // the game's own list has none
    }

    [Fact]
    public async Task TestAGameThatIsNotOpenToAllShowsItsPositionsOnlyToThoseWhoMayJoinIt()
    {
        var session = await ReadyHost("&public_game=0");
        await Positions(session, (HOST, 0, 1000));
        state.Store.AddFriend(GUEST, HOST);
        state.Store.AddFriend(HOST, GUEST);
        Assert.Equal(["50,50"], await Places(GUEST));  // a friend may join
        Assert.Equal(["50,50"], await Places(HOST));  // the host is in it
        Assert.Equal(["-"], await Places(THIRD));  // a stranger may not
        var hidden = (await Listed(THIRD))["clients"]![0]!;
        Assert.Equal((true, true, true), (hidden["x"] is null, hidden["y"] is null, hidden["z"] is null));  // not through the raw coordinates either

        session = await ReadyHost("&password=s3cret");
        await Positions(session, (HOST, 0, 1000));
        Assert.Equal(["-"], await Places(GUEST));  // a password game is not joinable without it
        Assert.Equal(["50,50"], await Places(HOST));

        // the game's own list cannot name its caller: a game not open to all sends neutral coordinates, any other the real ones
        await Positions(session, (HOST, 400, 700));
        var own = (await List())[0]!["clients"]![0]!;
        Assert.Equal((0L, 0L, 0L), ((long)own["x"]!, (long)own["y"]!, (long)own["z"]!));
        session = await ReadyHost();
        await Positions(session, (HOST, 400, 700));
        own = (await List())[0]!["clients"]![0]!;
        Assert.Equal((400L, 80L, 700L), ((long)own["x"]!, (long)own["y"]!, (long)own["z"]!));
        await Positions(session, (HOST, 5000, -300));  // off the map: on its edge
        Assert.Equal(["100,0"], await Places(GUEST));
    }

    /// <summary>An item of the account placed in a reserve, `mmX` and `mmZ` millimetres from the origin.</summary>
    void Place(long who, long guid, long item, long reserve, long mmX, long mmZ)
    {
        using var con = state.Store.Open(who);
        con.Begin();
        PlayerRows.AddItem(con, guid, item, 23, null, O($$"""{"__position": {"x": {{mmX}}, "y": 0, "z": {{mmZ}}}, "__reserve": {{reserve}}}"""));
        con.Commit();
    }

    /// <summary>`GET /catalog/games/&lt;id&gt;/placed` of the first game as `who` (null: signed out), with the password the player entered.</summary>
    async Task<Reply> PlacedOf(long? who, string? password = null)
    {
        List<(string, string)> headers = [];
        if (who is { } w) headers.Add(("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(w, TimeSpan.FromHours(1))}"));
        if (password is not null) headers.Add((CatalogApi.GamePassword, Uri.EscapeDataString(password)));
        return await server.SendAsync("GET", "web.test", $"/catalog/games/{await NetworkId()}/placed", [.. headers]);
    }

    /// <summary>The answer is the host's items (ids of the items, in order), or a 403 with this reason.</summary>
    async Task Placed(long who, string? answer, string? password = null)
    {
        var reply = await PlacedOf(who, password);
        if (answer is null)
            Assert.Equal((200, "501,502"), (reply.Status, string.Join(",", Json(reply)["items"]!.AsArray().Select(i => (long)i!["id"]!))));
        else
        {
            Assert.Equal((403, "refused", answer), (reply.Status, (string)Json(reply)["status"]!, (string)Json(reply)["why"]!));
            Assert.Null(Json(reply)["items"]);
        }
    }

    [Fact]
    public async Task TestTheGamesPlacedItemsAreTheHostsInTheGamesReserveAndOnlyForAPlayerWhoMayJoin()
    {
        Place(HOST, 501, 901, 2, 0, 1_000_000);  // the host's tent in the middle of the map
        Place(HOST, 502, 902, 2, -1_000_000, 500_000);  // a stand at the top left corner
        Place(HOST, 503, 902, 1, 0, 0);  // a stand in another reserve
        Place(THIRD, 601, 901, 2, 0, 1_000_000);  // the joining player's own tent is not the host's

        await Host();
        await Placed(THIRD, "starting");  // the host's port and network id are not known yet
        await ReadyHost();
        Eq("""{"items": [{"id": 501, "reserve": 2, "kind": "tent", "name": "Test TENT02", "x": 50, "y": 50}, {"id": 502, "reserve": 2, "kind": "treestand", "name": "Test TREE_STAND01", "x": 0, "y": 0}]}""", Json(await PlacedOf(THIRD)));
        await Placed(GUEST, null);
        await Placed(HOST, "own");
        Assert.Equal(401, (await PlacedOf(null)).Status);
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", "/catalog/games/7000000001/placed", [("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(THIRD, TimeSpan.FromHours(1))}")])).Status);

        // full: the host and two players are in (max 3)
        Joined(await Join(GUEST));
        Joined(await Join(THIRD));
        state.CreateAccount(14, "fourth");
        await Placed(14, "full");

        await ReadyHost("&public_game=0");
        await Placed(THIRD, "friends");
        state.Store.AddFriend(GUEST, HOST);
        state.Store.AddFriend(HOST, GUEST);
        await Placed(GUEST, null);

        await ReadyHost("&hs_min=100&hs_max=200");
        await Placed(THIRD, "hunterscore");
        Score(THIRD, 150);
        await Placed(THIRD, null);

        await ReadyHost("&password=pä ss");  // a password with a character that is no ASCII travels percent-encoded
        await Placed(GUEST, "password");
        await Placed(GUEST, "password", "wrong");
        await Placed(GUEST, null, "pä ss");
        await Placed(GUEST, "password", "pä");
    }

    [Fact]
    public async Task TestAGameHidesItsPositionsFromAPlayerWhoMayNotJoinItWhateverTheReason()
    {
        var session = await ReadyHost("&hs_min=100&hs_max=200");
        await Positions(session, (HOST, 0, 1000));
        Assert.Equal(["-"], await Places(THIRD));  // a public game, but the score is out of its range
        Score(THIRD, 150);
        Assert.Equal(["50,50"], await Places(THIRD));

        session = await ReadyHost();  // max 3: full for a fourth player
        await Positions(session, (HOST, 0, 1000));
        Joined(await Join(GUEST));
        Joined(await Join(THIRD));
        state.CreateAccount(14, "fourth");
        tokens[14] = state.Store.CreateToken(14);
        Assert.Equal(["-", "-", "-"], await Places(14));
        Assert.Equal(["50,50", "-", "-"], await Places(GUEST));  // the ones in it see it
    }

    /// <summary>`POST /auth/launch` of the page, as `who`: the status and the `error` (null on success) or the session token.</summary>
    async Task<(int Status, string? Error, string? Session)> PageJoin(long who, string form)
    {
        var reply = await server.SendAsync("POST", "web.test", "/auth/launch",
            [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "XMLHttpRequest"), ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(who, TimeSpan.FromHours(1))}")], Encoding.UTF8.GetBytes(form));
        return (reply.Status, (string?)Json(reply)["error"], Regex.Match((string?)Json(reply)["url"] ?? "", "/([0-9a-f]{40})/lang/") is { Success: true } m ? m.Groups[1].Value : null);
    }

    [Fact]
    public async Task TestAJoinerMayStartAtTheHostsTentAndAtNoOtherPlayersTent()
    {
        Place(HOST, 501, 901, 2, 500_000, 1_200_000);  // the host's tent, 500 m and 1200 m from the origin
        Place(HOST, 502, 901, 1, 0, 0);  // a tent of the host in another reserve
        Place(HOST, 503, 902, 2, 0, 0);  // a stand of the host: no start
        Place(THIRD, 601, 901, 2, 0, 0);  // another player's tent
        Place(GUEST, 501, 901, 2, -700_000, 600_000);  // the joiner's own tent, with the guid of the host's
        await ReadyHost();
        var id = await NetworkId();
        string Form(string start) => $"mp=join&network_id={id}&{start}&loadout=0";

        // another player's tent, one of the host's in another reserve and a stand are no start; nobody joined
        Assert.Equal("bad_start", (await PageJoin(THIRD, Form("tent=601&host_tent=1"))).Error);
        Assert.Equal("bad_start", (await PageJoin(THIRD, Form("tent=502&host_tent=1"))).Error);
        Assert.Equal("bad_start", (await PageJoin(THIRD, Form("tent=503&host_tent=1"))).Error);
        Assert.Equal("bad_start", (await PageJoin(THIRD, Form("lodge=101&host_tent=1"))).Error);  // `host_tent` without a tent
        Assert.Equal(1L, (long)(await List())[0]!["clients_len"]!);

        // the host's tent is the joiner's start: the hunt is at the host's tent, not at the joiner's tent of the same guid
        var joined = await PageJoin(GUEST, Form("tent=501&host_tent=1"));
        Assert.Equal((200, null), (joined.Status, joined.Error));
        var expedition = (await Connect(joined.Session!, GUEST))["expedition"]!;
        Assert.Equal((500.0, 1200.0), ((double)expedition["x"]!, (double)expedition["z"]!));
        Assert.Equal(2L, (long)(await List())[0]!["clients_len"]!);

        // without `host_tent` a tent is the joiner's own, as before
        var own = await PageJoin(THIRD, Form("tent=601"));
        Assert.Null(own.Error);
        Assert.Equal(0.0, (double)(await Connect(own.Session!, THIRD))["expedition"]!["x"]!);

        // a player the game refuses cannot start at its tent either: no setup of the host reaches it
        await ReadyHost("&public_game=0");
        Assert.Equal("not_allowed", (await PageJoin(THIRD, $"mp=join&network_id={await NetworkId()}&tent=501&host_tent=1&loadout=0")).Error);
    }

    [Fact]
    public async Task TestAJoinersStartFallsBackToTheReservesFirstLocationWhenTheHostPicksUpTheTent()
    {
        Place(HOST, 501, 901, 2, 500_000, 1_200_000);
        await ReadyHost();
        var joined = await PageJoin(GUEST, $"mp=join&network_id={await NetworkId()}&tent=501&host_tent=1&loadout=0");
        Assert.Null(joined.Error);
        var first = (await Connect(joined.Session!, GUEST))["expedition"]!;
        Assert.Equal((500.0, 1200.0), ((double)first["x"]!, (double)first["z"]!));  // at the host's tent

        using (var con = state.Store.Open(HOST))  // the host picks the tent up: it goes back to the stash
            con.Exec("UPDATE items SET slot = 0 WHERE account_id = $a AND guid = 501");
        var expedition = (await Connect(joined.Session!, GUEST))["expedition"]!;
        Assert.NotEqual((500.0, 1200.0), ((double)expedition["x"]!, (double)expedition["z"]!));  // no longer at the tent
        using var guest = state.Store.Open(GUEST);
        Assert.Equal("100:", string.Join(",", guest.Rows("SELECT location_id, tent FROM expeditions WHERE account_id = $a AND end_ts IS NULL").Select(r => $"{r[0]}:{r[1]}")));  // the hunt is at the reserve's first location, with no tent of the joiner's own

        // a refused player sees the refusal of the rule, also with a start that is no tent of the host's
        await ReadyHost("&public_game=0");
        Assert.Equal("not_allowed", (await PageJoin(THIRD, $"mp=join&network_id={await NetworkId()}&lodge=101&host_tent=1&loadout=0")).Error);
        Assert.Equal("not_allowed", (await PageJoin(THIRD, $"mp=join&network_id={await NetworkId()}&tent=999&host_tent=1&loadout=0")).Error);
    }

    /// <summary>`User/ban_player` as the game's page posts it: the host's token and `banned_user_id` in the form; the reply's text.</summary>
    async Task<string> Ban(long host, long banned) =>
        Encoding.UTF8.GetString((await Send("POST", Api, Replay.BAN_PLAYER, body: $"oauth_access_token={tokens[host]}&banned_user_id={banned}")).Body);

    [Fact]
    public async Task TestBanPlayerStoresTheHostsBanOnceAndNeverOfItselfOrAnUnknownPlayer()
    {
        await ReadyHost();
        Joined(await Join(GUEST));
        Assert.Equal("true", await Ban(HOST, GUEST));
        var first = state.Store.GameBansOf(HOST);
        Assert.Equal("true", await Ban(HOST, GUEST));  // again: nothing changes
        Assert.Equal(first, state.Store.GameBansOf(HOST));
        Assert.Equal([GUEST], first.Select(b => b.Account));
        const string refused = """{"errorCode": 400}""";
        Assert.Equal(refused, await Ban(HOST, HOST));
        Assert.Equal(refused, await Ban(HOST, 999));  // no such player
        var unsigned = await Send("POST", Api, Replay.BAN_PLAYER, body: $"banned_user_id={GUEST}");  // without a token: nothing stored
        Assert.NotEqual("true", Encoding.UTF8.GetString(unsigned.Body));
        Assert.Equal(first, state.Store.GameBansOf(HOST));
        Assert.Empty(state.Store.GameBansOf(GUEST));  // the ban is the host's own list
    }

    [Fact]
    public async Task TestABannedPlayerCannotJoinTheHostsGameAndTheListAndPlacedSayWhy()
    {
        await ReadyHost();
        var guestSession = Joined(await Join(GUEST));
        Assert.Equal("true", await Ban(HOST, GUEST));
        await Send("POST", Gs, Replay.DISCONNECT, GUEST, "{}", guestSession);  // the game's own kick follows the ban
        Refused(await Join(GUEST));
        var game = await Listed(GUEST);
        Assert.Equal((false, "banned"), ((bool)game["joinable"]!, (string?)game["why"]));
        var placed = await PlacedOf(GUEST);
        Assert.Equal((403, "banned"), (placed.Status, (string)Json(placed)["why"]!));
        Assert.Equal("banned", (await PageJoin(GUEST, $"mp=join&network_id={await NetworkId()}&lodge=101&loadout=0")).Error);
        await Agrees(THIRD, null);  // another player is unaffected
        Assert.True(state.Store.UnbanFromGames(HOST, GUEST));
        await Agrees(GUEST, null);
    }

    [Fact]
    public async Task TestABanDropsTheBannedPlayerFromTheRunningSessionAndNobodyElse()
    {
        var hostSession = await ReadyHost();
        var guestSession = Joined(await Join(GUEST));
        Joined(await Join(THIRD));
        Assert.Equal(3L, (long)(await List())[0]!["clients_len"]!);
        Assert.Equal("true", await Ban(HOST, GUEST));  // no disconnect of the guest's own game is needed
        Assert.Equal([HOST, THIRD], (await List())[0]!["clients"]!.AsArray().Select(c => (long)c!["user_id"]!));  // he no longer counts toward full
        Assert.True((bool)(await Connect(guestSession, GUEST))["network"]!["singleplayer"]!);  // his session token no longer connects him to the game
        Assert.False((bool)(await Connect(hostSession, HOST))["network"]!["singleplayer"]!);
    }

    [Fact]
    public void TestWhyIsTheFirstRefusalInTheOrderJoinChecksThem()
    {
        var host = new Games.Member(HOST, "s", 0);
        var g = new Games.Game { Host = HOST, Public = false, Password = "pw", HsMin = 100, Max = 2, Members = [host] };
        string? Why(long who, long score, bool friend, string? password = null, bool banned = false) => Games.Why(g, who, score, _ => friend, _ => banned, password);
        Assert.Equal("own", Why(HOST, 50, false, banned: true));
        Assert.Equal("banned", Why(GUEST, 50, false, banned: true));  // before the friends-only refusal
        Assert.Equal("banned", Why(GUEST, 150, true, "pw", banned: true));
        Assert.Equal("friends", Why(GUEST, 50, false));
        Assert.Equal("hunterscore", Why(GUEST, 50, true));
        Assert.Equal("starting", Why(GUEST, 150, true));  // the host's port and network id are not known
        (host.Port, host.Guid) = (32303, Guid1);
        Assert.Equal("password", Why(GUEST, 150, true));  // none given
        Assert.Equal("password", Why(GUEST, 150, true, "wrong"));
        g.Members.Add(new(THIRD, "t", 0));
        Assert.Equal("full", Why(GUEST, 150, true, "pw"));
        g.Members.RemoveAt(1);
        Assert.Null(Why(GUEST, 150, true, "pw"));
    }

    Task<Reply> Online() => Send("GET", "web.test", "/stats/online");

    [Fact]
    public async Task TestTheGameBuildIsNullUntilSetAndCachedForFiveMinutes()
    {
        var unset = await Send("GET", "web.test", "/stats/game");
        Eq("""{"build": null}""", Json(unset));
        Assert.Contains(("Cache-Control", "public, max-age=300"), unset.Headers);
        Cfg["game_build"] = 123;
        Eq("""{"build": 123}""", Json(await Send("GET", "web.test", "/stats/game")));
    }

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

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5", null)]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5", null)]  // an IPv4-mapped peer is an IPv4 host
    [InlineData("2001:db8::1", "2001:db8::1", "network")]
    public async Task TestAHostWhoseConnectCameOverIpv6IsListedNotJoinableAndTheJoinIsRefused(string forwarded, string recorded, string? why)
    {
        var session = await ReadyHost();
        Settings.TryNetwork("127.0.0.0/8", out var loopback);
        await using var proxied = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp, [loopback]);
        var connect = await proxied.SendAsync("POST", Gs, Replay.CONNECT, [("X-Hun-Session", session), ("X-Forwarded-For", forwarded)], "{}"u8.ToArray());
        Assert.Equal(recorded, (string?)Json(connect)["network"]!["ip"]);
        await Agrees(GUEST, why);
        if (why is not null)
        {
            var page = await server.SendAsync("POST", "web.test", "/auth/launch", [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "XMLHttpRequest"),
                ("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(GUEST, TimeSpan.FromHours(1))}")], Encoding.UTF8.GetBytes($"mp=join&network_id={await NetworkId()}&lodge=101&loadout=0"));
            Assert.Equal((400, "host_network"), (page.Status, (string)Json(page)["error"]!));
        }
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
