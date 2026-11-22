using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The species boards by season (docs/server.md#leaderboards-and-seasons): the season of a harvest, what is left out, the pages, the Hall of Fame and the season start. Invented data, written to the tables.</summary>
public class SeasonTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    static readonly long Second = Store.SecondSeasonStart;  // 2026-11-12 00:00:00 UTC
    const long Deer = 1, Rabbit = 17;
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, avatars: (JsonArray)JsonNode.Parse("""[{"id": 1000000, "url": "https://avatar.test/256/1000000.png", "gender": "m"}]""")!);
        var tick = Py.Now();
        state.Clock = () => tick += 90000;  // every board read is past the kept boards, so a test sees its latest data; the tests of the kept boards fix the clock
        var table = new Replay.HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Replay.Hit(2, "", 200, [], "{}"u8.ToArray());
        table.Entries[new("POST", "api.test", Replay.APPLICATION, null)] = new Replay.Hit(2, "", 200, [("Content-Type", "application/json")], """{"season": 46, "items": []}"""u8.ToArray());
        server = await Loopback.StartAsync(new Replay.Game(table, Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    static string Iso(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>An account whose backup was restored: its saved all-time bests for a species (one kill for the score and the range), killed at `ts`.</summary>
    void Restored(long id, string handle, long species, string score, long mm, long ts)
    {
        state.CreateAccount(id, handle);
        var rec = $$"""{"distance": {{mm}}, "confirm_ts": {{ts}}, "weapon_id": 5, "animal_id": 900, "gender": 0, "texture": 0, "score": "{{score}}", "species": {{species}}}""";
        using var con = state.Store.Open(id);
        con.Begin();
        PlayerRows.SetExtra(con, "Leaderboard_personal__user_id=8__season_no=0.json", JsonNode.Parse($$"""{"status": "done", "data": [{"specie": {{species}}, "score": {{rec}}, "distance": {{rec}}}]}"""));
        con.Commit();
    }

    /// <summary>A harvest of this server (the kill, its last hit `cm` centimetres away with the rifle 5, the page with its score and time).</summary>
    void Harvest(long id, long kill, long species, string score, long ts, long cm = 0, bool hidden = false)
    {
        var e3 = $$"""{"event": 3, "individual": {{kill}}, "extended_species": {{species}}}""";
        using var con = state.Store.Open(id);
        if (cm > 0)
            con.Exec("INSERT INTO events (account_id, event, body) VALUES ($a, 1, ?)", $$"""{"event": 1, "individual": {{kill}}, "distance_cms": {{cm}}, "weapon_id": 5}""");
        con.Exec("INSERT INTO events (account_id, event, body) VALUES ($a, 3, ?)", e3);
        con.Exec("INSERT INTO kills (account_id, kill_id, individual, species, harvest) VALUES ($a, ?, ?, ?, ?)", kill, kill, species, e3);
        con.Exec("INSERT INTO harvests (account_id, kill_id, body, hidden) VALUES ($a, ?, ?, ?)", kill, $$"""{"score": {{score}}, "species": {{species}}, "timestamp": "{{Iso(ts)}}"}""", hidden ? 1 : 0);
    }

    /// <summary>A GET on the website's host; without a cookie it goes as a signed-in visitor, the first account: the boards and the profiles are for signed-in players only.</summary>
    async Task<Reply> Get(string path, params (string, string)[] headers) => await server.SendAsync("GET", "web.test", path,
        !headers.Any(h => h.Item1 == "Cookie") ? [.. headers, Visitor()] : headers);

    (string, string) Visitor()
    {
        using var con = state.Store.Open();
        return ("Cookie", "hr_session=" + state.Store.CreateWebSession((long)con.Scalar("SELECT id FROM accounts WHERE banned = 0 ORDER BY id LIMIT 1")!, TimeSpan.FromHours(1)));
    }

    /// <summary>A GET without a session.</summary>
    async Task<Reply> Anon(string path) => await server.SendAsync("GET", "web.test", path, []);

    (string, string) Session(long id) => ("Cookie", "hr_session=" + state.Store.CreateWebSession(id, TimeSpan.FromHours(1)));

    async Task<JsonNode> Json(string path, params (string, string)[] headers) => JsonNode.Parse((await Get(path, headers)).Body)!;

    /// <summary>The handles of a species' board in rank order, "handle:value".</summary>
    async Task<string> Board(string kind, long species, long? season = null) =>
        string.Join(" ", (await Json($"/leaderboards/{kind}/{species}" + (season is { } s ? $"?season={s}" : "")))["rows"]!.AsArray()
            .Select(r => $"{r!["handle"]}:{r["value"]!.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

    /// <summary>Four hunters of the same species: a restored one of 2025, a restored one on the last second of Season 1, a hunter of this server on the first second of Season 2, and the left out ones.</summary>
    void Scene()
    {
        Restored(20, "alpha", Deer, "60.0000", 120000, new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        Restored(21, "bravo", Deer, "50.0000", 80000, Second - 1);
        state.CreateAccount(22, "charlie");
        Harvest(22, 1, Deer, "70", Second, cm: 10000);
        state.CreateAccount(23, "delta");
        Harvest(23, 1, Deer, "99", Second + 5, cm: 99000, hidden: true);  // hidden by its owner
        state.CreateAccount(24, "echo");
        Harvest(24, 1, Deer, "0", Second + 5, cm: 99000);  // refused: no score
        state.CreateAccount(25, "foxtrot");
        Harvest(25, 1, Deer, "80", Second + 5, cm: 99000);
        using var con = state.Store.Open();
        con.Exec("UPDATE accounts SET banned = 1 WHERE id = 25");
    }

    [Fact]
    public void TestAHarvestIsInTheSeasonItsKillTimeFallsIn()
    {
        var seasons = state.Store.Seasons();
        Assert.Equal([new Store.Season(1, 0, Second - 1), new Store.Season(2, Second, null)], seasons);
        Assert.Equal(1, Store.SeasonAt(seasons, Second - 1));  // 2026-11-11 23:59:59 UTC
        Assert.Equal(2, Store.SeasonAt(seasons, Second));      // 2026-11-12 00:00:00 UTC
        Assert.Equal(1, Store.SeasonAt(seasons, 0));
        Assert.Equal(2, Store.SeasonAt(seasons, Second + 86400L * 3650));
    }

    [Fact]
    public async Task TestTheBoardsOfEachSeasonHoldTheirOwnHarvests()
    {
        Scene();
        // the restored 2025 harvest and the one of the last second: Season 1 and all time; the first second of this server: Season 2 and all time
        Assert.Equal("alpha:60 bravo:50", await Board("score", Deer, 1));
        Assert.Equal("charlie:70", await Board("score", Deer, 2));
        Assert.Equal("charlie:70 alpha:60 bravo:50", await Board("score", Deer, 0));
        Assert.Equal("charlie:70", await Board("score", Deer));  // no season: the current one
        Assert.Equal("alpha:120 bravo:80", await Board("range", Deer, 1));
        Assert.Equal("charlie:100", await Board("range", Deer, 2));
        Assert.Equal("alpha:120 charlie:100 bravo:80", await Board("range", Deer, 0));
        // a harvest of this server made before its first day is a Season 2 one: Season 1 holds the restored harvests only
        Harvest(22, 2, Rabbit, "10", Second - 1, cm: 500);
        Assert.Equal("", await Board("score", Rabbit, 1));
        Assert.Equal("charlie:10", await Board("score", Rabbit, 2));
        Assert.Equal(404, (await Get("/leaderboards/score/1?season=9")).Status);
        Assert.Equal(404, (await Get("/leaderboards/score/1?season=x")).Status);
        Assert.Equal(404, (await Get("/leaderboards/weight/1")).Status);
    }

    /// <summary>A claimed account: no saved bests, a trophy list and a lodge that mounts the animals `mounted` (id, species, score, time).</summary>
    void Claimed(long id, string handle, (long Id, long Species, double Score, long Ts)[] listed, (long Id, long Species, double Score, long Ts)[] mounted)
    {
        state.CreateAccount(id, handle);
        static string Num(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string Details((long Id, long Species, double Score, long Ts) t) => $$"""{"species": {{t.Species}}, "score": {{Num(t.Score)}}, "timestamp": {{t.Ts}}}""";
        using var con = state.Store.Open(id);
        con.Begin();
        foreach (var t in listed) PlayerRows.AddTrophy(con, "/game/user/trophies", t.Id.ToString(), JsonNode.Parse($$"""{"id": {{t.Id}}, "hidden": false, "species": {{t.Species}}, "score": {{Num(t.Score)}}, "timestamp": {{t.Ts}}}"""));
        var slots = string.Join(",", mounted.Select((t, i) => $$"""{"slot_id": {{i}}, "trophy_id": {{t.Id}}, "pose_id": 1, "trophy_details": {{Details(t)}}}"""));
        PlayerRows.AddLodge(con, "/game/user/lodges", "3", JsonNode.Parse($$"""{"id": 3, "name": "three", "animal_slots": [{{slots}}], "weapon_slots": []}"""));
        con.Commit();
    }

    [Fact]
    public async Task TestAClaimedAccountRanksItsRestoredTrophiesOnTheScoreBoardsOnly()
    {
        var kill = new DateTimeOffset(2025, 5, 21, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        // the 70.4 deer is in the list and in the lodge (once); the 40 deer is in the list only; the rabbit is mounted only
        Claimed(40, "hotel", [(1, Deer, 70.401, kill), (2, Deer, 40, kill)], [(1, Deer, 70.401, kill), (3, Rabbit, 12, kill)]);
        Restored(41, "india", Deer, "60.0000", 90000, kill);  // saved bests win: a claimed rule does not apply
        Assert.Equal("hotel:70.401 india:60", await Board("score", Deer, 1));
        Assert.Equal("hotel:70.401 india:60", await Board("score", Deer, 0));
        Assert.Equal("india:90", await Board("range", Deer, 1));  // no shot distance: off the range board
        Assert.Equal("hotel:12", await Board("score", Rabbit, 1));
        Assert.Equal("", await Board("range", Rabbit, 1));
        var hall = (await Json("/leaderboards/halloffame?season=1"))["rows"]!.AsArray();
        Eq("""["hotel", "india"]""", T(hall[0]!["score"]!["handle"], hall[0]!["range"]!["handle"]));
        Eq("""["hotel", null]""", T(hall[1]!["score"]!["handle"], hall[1]!["range"]));
        var top = (await Json($"/leaderboards/score/{Deer}?season=1"))["rows"]![0]!;
        Eq("""["", 70.401]""", T(top["weaponName"], top["value"]));  // weapon unknown
        // a trophy the owner hid is in the list as hidden while its lodge still shows it: it ranks nowhere
        Claimed(42, "juliet", [(5, Rabbit, 99, kill)], [(5, Rabbit, 99, kill)]);
        using (var con = state.Store.Open(42))
            con.Exec("UPDATE trophies SET node = json_set(node, '$.hidden', json('true')) WHERE account_id = $a");
        Assert.Equal("hotel:12", await Board("score", Rabbit, 1));
    }

    [Fact]
    public async Task TestTheOverviewListsTheTopThreeOfEachSpeciesAndEachRowCarriesItsKill()
    {
        Scene();
        for (var i = 0; i < 4; i++)
        {
            state.CreateAccount(30 + i, "rabbit" + i);
            Harvest(30 + i, 1, Rabbit, (10 + i).ToString(), Second + 60, cm: 1000 + i);
        }
        var board = (await Json("/leaderboards/score?season=2"))["boards"]!.AsArray();
        Eq("[1, 17]", T(board[0]!["species"], board[1]!["species"]));
        Eq("""["Test Deer", "Test Rabbit"]""", T(board[0]!["name"], board[1]!["name"]));
        Eq("""["rabbit3", "rabbit2", "rabbit1"]""", new JsonArray([.. board[1]!["top"]!.AsArray().Select(r => (JsonNode?)r!["handle"]!.DeepClone())]));
        var row = board[0]!["top"]![0]!;
        Eq("""[1, "charlie", 70.0, "Test Rifle", 1]""", T(row["rank"], row["handle"], row["value"], row["weaponName"], row["kill"]));
        Assert.Null(row["weapon"]);
        Assert.Equal(Second, (long)row["ts"]!);
        var restored = (await Json("/leaderboards/range?season=1"))["boards"]![0]!["top"]![0]!;
        Eq("""["alpha", 120.0, 0]""", T(restored["handle"], restored["value"], restored["kill"]));  // a restored kill has no sheet here
    }

    [Fact]
    public async Task TestASpeciesBoardIsTheTopHundredInPagesOf25AndTheSignedInHunterSeesItsPlace()
    {
        for (var i = 1; i <= 27; i++)
        {
            state.CreateAccount(100 + i, "p" + i);
            Harvest(100 + i, 1, Deer, i.ToString(), Second + i);
        }
        var page2 = await Json($"/leaderboards/score/{Deer}?page=2");
        Eq("[2, 2]", T(page2["page"], page2["pages"]));
        Assert.False(page2.AsObject().ContainsKey("total"));  // no count of the players
        Eq("""["p2", "p1"]""", new JsonArray([.. page2["rows"]!.AsArray().Select(r => (JsonNode?)r!["handle"]!.DeepClone())]));
        Assert.Equal(26, (long)page2["rows"]![0]!["rank"]!);
        Eq("[1, 1]", T((await Json($"/leaderboards/score/{Rabbit}"))["page"], (await Json($"/leaderboards/score/{Rabbit}"))["pages"]));
        var cookie = ("Cookie", "hr_session=" + state.Store.CreateWebSession(103, TimeSpan.FromHours(1)));
        var me = JsonNode.Parse((await Get($"/leaderboards/score/{Deer}/me", cookie)).Body)!;
        Eq("""[25, "p3", 3.0]""", T(me["rank"], me["handle"], me["value"]));
        Assert.Equal(404, (await Get($"/leaderboards/score/{Rabbit}/me", cookie)).Status);  // not ranked
        Assert.Equal(401, (await Anon($"/leaderboards/score/{Deer}/me")).Status);
        // beyond the 100: the place is still told (only 27 here, so the board's last)
        var last = ("Cookie", "hr_session=" + state.Store.CreateWebSession(101, TimeSpan.FromHours(1)));
        Assert.Equal(27, (long)JsonNode.Parse((await Get($"/leaderboards/score/{Deer}/me", last)).Body)!["rank"]!);
    }

    [Fact]
    public async Task TestTheHallOfFameOfAnEndedSeasonHasTheBestScoreAndRangeOfEachSpecies()
    {
        Scene();
        Harvest(22, 2, Rabbit, "10", Second + 10, cm: 500);
        state.CreateAccount(26, "golf");
        Harvest(26, 1, Rabbit, "5", Second + 20, cm: 2500);
        // season 0 and the current one have none
        Eq("[]", (await Json("/leaderboards/halloffame?season=0"))["rows"]);
        Eq("[]", (await Json("/leaderboards/halloffame"))["rows"]);
        // Season 1 has ended
        var hall = (await Json("/leaderboards/halloffame?season=1"))["rows"]!.AsArray();
        Eq("""[1, "Test Deer", "alpha", "alpha"]""", T(hall[0]!["id"], hall[0]!["species"], hall[0]!["score"]!["handle"], hall[0]!["range"]!["handle"]));
        Assert.Single(hall);
        // Season 2 ends when the next starts
        Assert.NotNull(state.StartSeason(Second + 1000));
        Harvest(22, 3, Deer, "75", Second + 1000, cm: 20000);  // the first second of Season 3
        var two = (await Json("/leaderboards/halloffame?season=2"))["rows"]!.AsArray();
        Eq("""[[1, "charlie", "charlie"], [17, "charlie", "golf"]]""", new JsonArray([.. two.Select(r => (JsonNode?)new JsonArray(r!["id"]!.DeepClone(), r["score"]!["handle"]!.DeepClone(), r["range"]!["handle"]!.DeepClone()))]));
        Eq("[70.0, 100.0]", T(two[0]!["score"]!["value"], two[0]!["range"]!["value"]));
        Assert.Equal("charlie:75", await Board("score", Deer, 3));
        Assert.Equal("charlie:70", await Board("score", Deer, 2));
        Assert.Equal(404, (await Get("/leaderboards/halloffame?season=4")).Status);
        var seasons = await Json("/leaderboards/seasons");
        Eq($$"""{"current": 3, "seasons": [{"no": 1, "start": 0, "end": {{Second - 1}}}, {"no": 2, "start": {{Second}}, "end": {{Second + 999}}}, {"no": 3, "start": {{Second + 1000}}, "end": null}]}""", seasons);
    }

    Task<Reply> Post(string path, string body) => server.SendAsync("POST", "api.test", path, [("Content-Type", "application/x-www-form-urlencoded")], System.Text.Encoding.ASCII.GetBytes(body));

    /// <summary>A call of the original's pages with the token of the first account: the boards need a player's.</summary>
    async Task<JsonNode> Call(string path, string body)
    {
        long first;
        using (var con = state.Store.Open())
            first = (long)con.Scalar("SELECT id FROM accounts WHERE banned = 0 ORDER BY id LIMIT 1")!;
        return JsonNode.Parse((await Post(path, (body == "" ? "" : body + "&") + "oauth_access_token=" + state.Store.CreateToken(first))).Body)!;
    }

    [Fact]
    public async Task TestTheCallsOfTheOriginalsPagesAnswerFromTheSameBoards()
    {
        Scene();
        Eq("""{"season": 2, "items": []}""", await Call(Replay.APPLICATION, ""));  // the current season, not the recorded one
        var all = (await Call(Replay.LEADERBOARDS_ALL, "type=1")).AsArray();
        var block = all[0]!;
        Eq("""[1, "mule_deer", "Test Deer (Typical)", 1, 1000]""", T(block["id"], block["define"], block["name"], block["scoretype"], block["total"]));
        Assert.Single(all);
        var record = block["records"]![0]!;
        Eq("""[1, 1, 1, 1, 70.0, null, 22, "charlie"]""", T(record["id"], record["animal_id"], record["rank"], record["scoretype"], record["score"], record["scoreType"], record["user"]!["id"], record["user"]!["handle"]));
        Assert.Equal($"{BASE}/media/avatar/256/1000000.png", (string?)record["user"]!["avatar"]);  // the game's page: the game host, not the website's copy
        var range = await Call(Replay.LEADERBOARD_DETAILS, "type=2&species=1");
        Eq("""[2, null, 100.0]""", T(range["scoretype"], range["position"], range["records"]![0]!["score"]));
        Eq("[]", (await Call(Replay.LEADERBOARD_DETAILS, "type=1&species=17"))["records"]);
        // the Hall of Fame: the season before the current one by default; a score is a text, a range a number
        var hall = await Call(Replay.HALL_OF_FAME, "");
        Eq("""[1, "60", null, 120.0]""", T(hall[0]!["speciesId"], hall[0]!["score"]!["score"], hall[0]!["range"]!["scoretype"], hall[0]!["range"]!["score"]));
        Eq("[]", await Call(Replay.HALL_OF_FAME, "season_no=2"));
        Eq("[]", await Call(Replay.HALL_OF_FAME, "season_no=0"));
        var mine = (await Call(Replay.CURRENT_PERSONAL, "user_id=22")).AsArray();
        Eq("""[1, 1, 1, 70.0, 100.0]""", T(mine[0]!["species_id"], mine[0]!["score"]!["total"], mine[0]!["score"]!["rank"], mine[0]!["score"]!["score"], mine[0]!["distance_min"]!["score"]));
        Eq("[]", await Call(Replay.CURRENT_PERSONAL, "user_id=25"));  // banned
        Eq("[]", await Call(Replay.CURRENT_PERSONAL, "user_id=999"));
        // without a player's token: the expired-token reply, no board
        foreach (var (path, body) in new[] { (Replay.LEADERBOARDS_ALL, "type=1"), (Replay.LEADERBOARD_DETAILS, "type=1&species=1"), (Replay.HALL_OF_FAME, ""), (Replay.CURRENT_PERSONAL, "user_id=22") })
            Eq("""{"errorCode": 5, "message": "token expired"}""", JsonNode.Parse((await Post(path, body)).Body));
        Assert.NotNull(state.StartSeason(Second + 1000));
        Eq("""{"season": 3, "items": []}""", await Call(Replay.APPLICATION, ""));
    }

    [Fact]
    public async Task TestARestoredKillIsSeasonOneWhateverTimeItsBackupSaysAndAPersonalBestIsPerSeason()
    {
        // a backup's time is the player's: one in 2030 is still a restored kill, so Season 1 (not Season 2), and a weapon id that is no number leaves the board whole
        Restored(20, "alpha", Deer, "60.0000", 120000, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        using (var con = state.Store.Open(20))
            con.Exec("UPDATE extras SET body = replace(body, '\"weapon_id\": 5', '\"weapon_id\": \"x\"') WHERE account_id = $a");
        Harvest(20, 1, Deer, "70", Second + 5, cm: 5000);
        Assert.Equal("alpha:60", await Board("score", Deer, 1));
        Assert.Equal("alpha:70", await Board("score", Deer, 2));
        Assert.Equal("alpha:70", await Board("score", Deer, 0));
        var first = (await Json($"/leaderboards/score/{Deer}?season=1"))["rows"]![0]!;
        Eq("""["alpha", ""]""", T(first["handle"], first["weaponName"]));
        // /hunters/<handle>/best: all time by default, a season on request, 404 for none
        async Task<JsonNode> Best(string query) => (await Json("/hunters/alpha/best" + query))["rows"]![0]!;
        Eq("[70.0, 50.0]", T((await Best(""))["score"], (await Best("?season=0"))["range"]));
        Eq("[60.0, 120.0]", T((await Best("?season=1"))["score"], (await Best("?season=1"))["range"]));
        Eq("[70.0, 50.0]", T((await Best("?season=2"))["score"], (await Best("?season=2"))["range"]));
        Assert.Equal(404, (await Get("/hunters/alpha/best?season=9")).Status);
        Assert.Equal(404, (await Get("/hunters/nobody/best?season=1")).Status);
    }

    [Fact]
    public async Task TestTheBoardsAreKept15Minutes()
    {
        Scene();
        var now = Py.Now();
        state.Clock = () => now;
        var first = await Get($"/leaderboards/score/{Deer}");
        Assert.Equal("no-store", first.Headers.Single(h => h.Name == "Cache-Control").Value);  // a board is for one signed-in player
        Assert.Equal("charlie:70", await Board("score", Deer));
        state.CreateAccount(30, "hotel");
        Harvest(30, 1, Deer, "90", Second + 10, cm: 1000);
        now += 899;
        Assert.Equal("charlie:70", await Board("score", Deer));  // kept
        Assert.Same(state.Boards(false, 2), state.Boards(false, 2));
        now += 2;
        Assert.Equal("hotel:90 charlie:70", await Board("score", Deer));  // expired: the new harvest shows
        Assert.Equal("no-store", (await Get("/hunters/alpha/best")).Headers.Single(h => h.Name == "Cache-Control").Value);
        // a season start drops the kept boards; an ended season's are kept 15 minutes like the others
        Assert.NotNull(state.StartSeason((long)now - 10));
        Assert.Equal("hotel:90 charlie:70", await Board("score", Deer, 2));
        state.CreateAccount(31, "india");
        Harvest(31, 1, Deer, "95", Second + 20, cm: 1000);
        now += 899;
        Assert.Equal("hotel:90 charlie:70", await Board("score", Deer, 2));
        now += 2;
        Assert.Equal("india:95 hotel:90 charlie:70", await Board("score", Deer, 2));
    }

    [Fact]
    public async Task TestARefreshComputesAwayFromTheReadsAndAReadAfterItComputesNothing()
    {
        Scene();
        var now = Py.Now();
        state.Clock = () => now;
        await Task.Run(state.RefreshBoards);  // the first computation, as at the start
        state.CreateAccount(30, "hotel");
        Harvest(30, 1, Deer, "90", Second + 10, cm: 1000);
        Assert.Equal("charlie:70", await Board("score", Deer));  // a read waits for no computation: the new harvest is not in the kept boards
        now += State.BoardRefresh.TotalSeconds;
        await Task.Run(state.RefreshBoards);  // the replacement, 14 minutes on: the old boards answered until it was ready
        state.CreateAccount(31, "india");
        Harvest(31, 1, Deer, "95", Second + 20, cm: 1000);
        now += 100;  // past the first computation's 15 minutes, inside the replacement's
        Assert.Equal("hotel:90 charlie:70", await Board("score", Deer));  // the replacement is kept: no read computed india's harvest
    }

    [Fact]
    public async Task TestStartingASeasonDropsTheKeptBoardsAndReadsAtOnceComputeOnce()
    {
        Scene();
        state.Clock = () => Second + 100;
        Assert.Equal("charlie:70", await Board("score", Deer));
        state.CreateAccount(30, "hotel");
        Harvest(30, 1, Deer, "90", Second + 10, cm: 1000);
        Assert.Equal("charlie:70", await Board("score", Deer));  // kept
        Assert.NotNull(state.StartSeason(Second + 50));
        Assert.Equal("hotel:90 charlie:70", await Board("score", Deer, 2));
        // hiding a harvest takes it off the boards at once
        Assert.Equal(200, state.HideTrophy(30, 1, """{"hidden": true}"""u8.ToArray()));
        Assert.Equal("charlie:70", await Board("score", Deer, 2));
        var hall = await Get("/leaderboards/halloffame?season=2");
        Assert.Equal("no-store", hall.Headers.Single(h => h.Name == "Cache-Control").Value);
        var boards = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => state.Boards(true, 0))));
        Assert.All(boards, b => Assert.Same(boards[0], b));
    }

    [Fact]
    public void TestASeasonStartsAfterTheCurrentOnesStartOnly()
    {
        Assert.Null(state.StartSeason(Second));
        Assert.Null(state.StartSeason(Second - 5));
        Assert.Equal(new Store.Season(3, Second + 1, null), state.StartSeason(Second + 1));
        Assert.Equal(Second, state.Store.Seasons()[1].End);  // closed one second before the next starts
    }

    static long[] Ranks(JsonNode? rows) => [.. rows!.AsArray().Select(r => (long)r!["rank"]!)];

    static long[] Places(int from, int count) => [.. Enumerable.Range(from, count).Select(n => (long)n)];

    [Fact]
    public async Task TestABoardIsItsTop100AndThePlayersOwnPlaceWith50AboveAndBelow()
    {
        for (var i = 1; i <= 170; i++)  // p<i> scores i: its place is 171 - i
        {
            state.CreateAccount(1000 + i, "p" + i);
            Harvest(1000 + i, 1, Deer, i.ToString(), Second + i);
        }
        var low = Session(1011);  // p11: place 160
        // signed out: no board, no seasons
        var anon = await Anon($"/leaderboards/score/{Deer}");
        Eq("[401, \"signed_out\"]", T(anon.Status, JsonNode.Parse(anon.Body)!["status"]));
        foreach (var path in new[] { "/leaderboards/seasons", "/leaderboards/score", "/leaderboards/halloffame?season=1", "/leaderboards/lanebandit", "/hunters" })
            Assert.Equal(401, (await Anon(path)).Status);
        // the top 100 in 4 pages and no count; a page past them is the last one
        List<long> top = [];
        for (var p = 1; p <= 4; p++)
            top.AddRange(Ranks((await Json($"/leaderboards/score/{Deer}?page={p}", low))["rows"]));
        Assert.Equal(Places(1, 100), top);
        var past = await Json($"/leaderboards/score/{Deer}?page=9", low);
        Eq("[4, 4]", T(past["page"], past["pages"]));
        Assert.False(past.AsObject().ContainsKey("total"));
        // the window: places 110 to 170 (the board's end), the player's own at 160; places 101 to 109 are not his to see
        Assert.Equal(Places(110, 61), Ranks(past["around"]));
        Eq("""["p11", 160]""", T(past["around"]![50]!["handle"], past["around"]![50]!["rank"]));
        Eq("[]", (await Json($"/leaderboards/score/{Deer}", Session(1150)))["around"]);  // place 21: the top holds the window
        Assert.Equal(Places(101, 70), Ranks((await Json($"/leaderboards/score/{Deer}", Session(1051)))["around"]));  // place 120
        // the game's page: the top 100, then the window; without a token nothing
        var details = await Post(Replay.LEADERBOARD_DETAILS, "type=1&species=1&oauth_access_token=" + state.Store.CreateToken(1011));
        Assert.Equal([.. Places(1, 100), .. Places(110, 61)], Ranks(JsonNode.Parse(details.Body)!["records"]));
        Assert.DoesNotContain("p11", System.Text.Encoding.UTF8.GetString((await Post(Replay.LEADERBOARD_DETAILS, "type=1&species=1")).Body));
    }

    [Fact]
    public async Task TestHunterscoreBoardAndCanSeeGiveTop100OwnWindowAndFriendsOnly()
    {
        for (var i = 1; i <= 200; i++)  // all at hunterscore 0: the board is in account id order, h<i> at place i
            state.CreateAccount(1000 + i, "h" + i);
        var board = await Json("/hunters?page=9", Session(1160));
        Eq("[4, 4]", T(board["page"], board["pages"]));
        Assert.Equal(Places(91, 10), Ranks(board["rows"]));
        Assert.Equal(Places(110, 91), Ranks(board["around"]));  // 160 - 50 to the board's end
        Assert.False(board.AsObject().ContainsKey("total"));
        Assert.True(state.CanSee(1160, 1160));
        Assert.True(state.CanSee(1160, 1100));  // the top 100
        Assert.True(state.CanSee(1160, 1110));  // his window
        Assert.True(state.CanSee(1160, 1200));
        Assert.False(state.CanSee(1160, 1105));  // place 105: past the top, 55 above him
        Assert.False(state.CanSee(1001, 1105));
        state.Store.AddFriend(1160, 1105);
        state.Store.AddFriend(1105, 1160);
        Assert.True(state.CanSee(1160, 1105));  // a friend
        Assert.False(state.CanSee(1001, 1105));
        // the game's places of a player: for one the caller sees only
        Harvest(1150, 1, Deer, "5", Second + 1);
        Assert.Equal("[]", System.Text.Encoding.UTF8.GetString((await Post(Replay.CURRENT_PERSONAL, "user_id=1105&oauth_access_token=" + state.Store.CreateToken(1001))).Body));
        Assert.Contains("\"species_id\": 1", System.Text.Encoding.UTF8.GetString((await Post(Replay.CURRENT_PERSONAL, "user_id=1150&oauth_access_token=" + state.Store.CreateToken(1001))).Body));
        Assert.False(state.CanSee(1160, 99999));  // no such account
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 1002");
        Assert.False(state.CanSee(1160, 1002));  // banned: on no board, though place 2 before
    }

    [Fact]
    public async Task TestAMinigameBoardIsItsTop100AndThePlayersWindow()
    {
        for (var i = 1; i <= 120; i++)  // g<i> scores i at Lane Bandit: its place is 121 - i
        {
            state.CreateAccount(1000 + i, "g" + i);
            using var con = state.Store.Open(1000 + i);
            PlayerRows.SetStatistics(con, new JsonObject(), 0, 0, null, i);
        }
        var board = await Json("/leaderboards/lanebandit?page=9", Session(1005));  // place 116
        Eq("[4, 4]", T(board["page"], board["pages"]));
        Assert.Equal(Places(91, 10), Ranks(board["rows"]));
        Assert.Equal(Places(101, 20), Ranks(board["around"]));  // 66 to 166: past the top, to the board's end
        Eq("[]", (await Json("/leaderboards/lanebandit", Session(1120)))["around"]);  // place 1
    }

    [Fact]
    public void TestTheSchemaOfVersion28GetsTheSeasons()
    {
        var file = Path.Combine(tmp, "v28.db");
        var store = new Store(file);
        using (var con = store.Open())
        {
            con.Exec("DROP TABLE seasons");
            con.Exec("PRAGMA user_version = 28");
        }
        var check = new Store(file);
        Assert.Equal([1L, 2L], check.Seasons().Select(s => s.No));
        using var con2 = check.Open();
        Assert.Equal((long)Store.Version, con2.Scalar("PRAGMA user_version"));
    }
}
