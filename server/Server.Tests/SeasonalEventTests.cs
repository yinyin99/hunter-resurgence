using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The seasonal event: the connect reply's `ingame_event` and the event's content by window, reserve and player, and the player's setting.</summary>
public class SeasonalEventTests : StateTestBase
{
    // CONNECT starts at lodge 100: the game's reserve 1. The event adds a deer population and silver-like ammo there, and ammo only on reserve 3
    static readonly Facts EventFacts = Facts.Parse("""
        {"format": 1,
         "events": {"season": {"reserves": {
           "1": {"ammo": [962, 963], "populations": [
             {"index": 150, "animal": "deer", "density": "mule_deer", "endangered": false, "license": 1,
              "groups": [{"x": 0, "z": 0, "radius": 0, "males": 2, "females": 3}]}]},
           "3": {"ammo": [962], "populations": []}}}}}
        """);

    static readonly DateTimeOffset Start = new(2031, 3, 10, 0, 0, 0, TimeSpan.Zero), End = Start.AddDays(10);

    static SeasonalEvent Season(params int[] reserves) => new("season", "SEASON", reserves, Start, End);

    static readonly JsonObject WebCfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    State New(SeasonalEvent? season, bool oldEventRow = false)
    {
        var connect = CONNECT;
        connect["populations"] = new JsonArray(O("""{"index": 100, "animal": "mule_deer", "reserve": 1}"""));
        if (oldEventRow)  // what a recorded connect of the event itself carries
            connect["populations"]!.AsArray().Add(O("""{"index": 150, "animal": "deer", "reserve": 2}"""));
        connect["species_ammo"] = new JsonArray(O("""{"species_id": "17", "ammo": [999]}"""));
        var s = new State(Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".db"), Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES,
                          offline: false, facts: EventFacts, seasonal: season);
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        return s;
    }

    static JsonNode Connect(State s) => Reply(s.ConnectReply(ME));

    static string Indexes(JsonNode reply) => string.Join(",", reply["populations"]!.AsArray().Select(p => (long)p!["index"]!));

    [Fact]
    public void TestWithNoEventTheConnectSendsNoneAndNoContent()
    {
        var reply = Connect(New(null, oldEventRow: true));
        Assert.Null(reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));  // a recorded event creature goes too
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestAnEventThatRunsIsAnnouncedAndItsReservesGetItsContent()
    {
        var reply = Connect(New(Season(1, 3), oldEventRow: true));
        Eq("""{"name": "season", "define": "SEASON", "reserves": [1, 3]}""", reply["ingame_event"]);
        Assert.Equal("100,150", Indexes(reply));  // the recorded one is replaced, not doubled
        var row = reply["populations"]![1]!;
        Eq("""["deer", "mule_deer", 2, "2031-03-10T00:00:00", "2031-03-20T00:00:00"]""",
           T(row["animal"], row["density_map"], row["reserve"], row["active_start"], row["active_end"]));
        Eq("[962, 963]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestAnEventForOtherReservesIsAnnouncedButAddsNothingHere()
    {
        var reply = Connect(New(Season(3)));
        Eq("""{"name": "season", "define": "SEASON", "reserves": [3]}""", reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestNoReservesMeansEveryReserve()
    {
        var reply = Connect(New(Season()));
        Eq("""{"name": "season", "define": "SEASON", "reserves": []}""", reply["ingame_event"]);
        Assert.Equal("100,150", Indexes(reply));
    }

    [Fact]
    public void TestTheEventRunsFromItsStartUntilItsEnd()
    {
        var s = New(Season(1));
        bool Runs(double at)
        {
            s.Clock = () => at;
            return Connect(s)["ingame_event"] is not null;
        }
        Assert.False(Runs(Start.ToUnixTimeSeconds() - 1));
        Assert.True(Runs(Start.ToUnixTimeSeconds()));
        Assert.True(Runs(End.ToUnixTimeSeconds() - 1));
        Assert.False(Runs(End.ToUnixTimeSeconds()));
        s.Clock = () => End.ToUnixTimeSeconds();
        Assert.Null(s.EventStatus(ME)["event"]);
    }

    [Fact]
    public void TestAPlayerWhoOptedOutGetsNoEventAndNoContent()
    {
        var s = New(Season(1), oldEventRow: true);
        Assert.True(s.Store.PlaysEvents(ME));  // on by default
        s.Store.SetPlaysEvents(ME, false);
        var reply = Connect(s);
        Assert.Null(reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
        s.Store.SetPlaysEvents(ME, true);
        Assert.NotNull(Connect(s)["ingame_event"]);
    }

    [Fact]
    public void TestATutorialHasNoEvent()
    {
        var s = New(Season(1));
        s.NoteTutorial(ME, "tutorial-session", 1);
        Assert.Null(Reply(s.ConnectReply(ME, "tutorial-session"))["ingame_event"]);
        Assert.NotNull(Connect(s)["ingame_event"]);  // a hunt without that session
    }

    [Fact]
    public async Task TestThePlayerReadsAndChangesTheSettingOnTheWebsite()
    {
        var s = New(Season(1));
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        var form = ("Content-Type", "application/x-www-form-urlencoded");
        var own = ("X-Requested-With", "XMLHttpRequest");
        Task<Reply> Post(string body, params (string, string)[] headers) =>
            server.SendAsync("POST", "web.test", "/account/events", [form, .. headers], Encoding.ASCII.GetBytes(body));

        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/events", [])).Status);
        var read = await server.SendAsync("GET", "web.test", "/account/events", [cookie]);
        Assert.Equal(200, read.Status);
        Eq("""{"play": true, "event": {"name": "season", "define": "SEASON", "reserves": [1]}}""", JsonNode.Parse(read.Body));

        Assert.Equal(401, (await Post("play=0", own)).Status);
        Assert.Equal(400, (await Post("play=0", cookie)).Status);  // not from our page
        Assert.Equal(400, (await Post("play=maybe", cookie, own)).Status);
        Assert.Equal(400, (await Post("", cookie, own)).Status);
        Assert.True(s.Store.PlaysEvents(ME));

        var off = await Post("play=0", cookie, own);
        Assert.Equal(200, off.Status);
        Eq("""{"play": false, "event": {"name": "season", "define": "SEASON", "reserves": [1]}}""", JsonNode.Parse(off.Body));
        Assert.Null(Connect(s)["ingame_event"]);
        Assert.Equal(200, (await Post("play=1", cookie, own)).Status);
        Assert.NotNull(Connect(s)["ingame_event"]);
    }

    [Fact]
    public async Task TestThePanelIsReadOnlyByAPlayerWhoPlaysWhileTheEventRuns()
    {
        var s = New(Season(1));
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Task<Reply> Get(params (string, string)[] headers) => server.SendAsync("GET", "web.test", "/account/events/panel", headers);

        Assert.Equal(401, (await Get()).Status);
        var panel = JsonNode.Parse((await Get(cookie)).Body)!;
        Eq("""["season", "season", [], [], [], []]""", T(panel["name"], panel["title"], panel["community"], panel["contributors"], panel["missions"], panel["items"]));
        s.Store.SetPlaysEvents(ME, false);
        Eq("""{"event": null}""", JsonNode.Parse((await Get(cookie)).Body));
    }

    [Fact]
    public void TestTheShippedHalloweenEventHasItsMissionAndItsStoreItems()
    {
        var content = Facts.Default.EventOf("halloween_2026")!;
        Assert.Equal("Halloween 2026", (string)content["title"]!);
        Eq("[1201]", content["missions"]);
        Eq("[960, 967, 1143, 962, 963, 1142]", content["items"]);
        Eq("[]", content["community"]);  // no number of its community missions is known
    }

    [Fact]
    public async Task TestWithNoEventTheSettingIsStillReadable()
    {
        var s = New(null);
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Eq("""{"play": true, "event": null}""", JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/events", [cookie])).Body));
    }

    [Fact]
    public void TestTheSchemaOfVersion16GetsTheSettingOnForEveryAccount()
    {
        var file = Path.Combine(tmp, "v16.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open())
        {
            con.Exec("ALTER TABLE accounts DROP COLUMN seasonal_events");
            con.Exec("DROP TABLE claim_lookups");
            con.Exec("DROP TABLE claims");
            con.Exec("PRAGMA user_version = 16");
        }
        var migrated = new Store(file);
        Assert.True(migrated.PlaysEvents(5));
        migrated.SetPlaysEvents(5, false);
        Assert.False(migrated.PlaysEvents(5));
        using var check = migrated.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }

    // the shipped event's reserves, as the game announces them; the density maps the game ships, by creature, as the game's reserve numbers (zone n is reserve n - 1)
    static readonly SeasonalEvent Halloween = new("halloween_2026", "HALLOWEEN_2026", [0, 1, 2, 8, 9], Start, End);

    static readonly Dictionary<string, int[]> Maps = new()
    {
        ["werewolf"] = [0, 1, 2, 8, 10], ["feral_hog"] = [0, 1, 2, 6, 8, 9, 11], ["snowshoe_hare"] = [9],
    };

    /// <summary>A hunt on the game's <paramref name="reserve"/> with the shipped facts and the shipped event; the recorded connect it starts from already carries
    /// an event creature of that reserve (and a row that is no object), as a connect recorded during an event does. Its `ethical` permits 241 and 14 on the rabbit 17
    /// (an event species) and on the roe deer 11 (on no reserve of the event); the coyote 4 permits 241 and 14, the blacktail 6 241 and 962 (a silver round already in its normal list). With <paramref name="db"/> the server starts again on that database and launches nothing.</summary>
    State Hunt(int reserve, bool playsEvents = true, string? db = null)
    {
        var connect = CONNECT;
        ((JsonArray)connect["ethical"]!)[11] = new JsonArray(241, 14);
        ((JsonArray)connect["ethical"]!)[4] = new JsonArray(241, 14);
        ((JsonArray)connect["ethical"]!)[6] = new JsonArray(241, 962);
        connect["populations"] = new JsonArray(O("""{"index": 996, "animal": "dog_german_pointer", "reserve": -1}"""), JsonValue.Create("r3"));
        if (reserve == 0)  // the normal row whose index the event's feral hog shares
            connect["populations"]!.AsArray().Add(O("""{"index": 3, "animal": "roosevelt_elk", "density_map": "roosevelt_elk", "reserve": 1}"""));
        if (Facts.Default.EventRows(reserve).FirstOrDefault() is ({ } recorded, { } kind) && recorded > 0)
            connect["populations"]!.AsArray().Add(O($$"""{"index": {{recorded}}, "animal": "{{kind}}", "density_map": "{{kind}}", "reserve": {{reserve + 1}}}"""));
        connect["species_ammo"] = new JsonArray(O("""{"species_id": "17", "ammo": [999]}"""));
        var app = APP;
        app["locations"] = new JsonArray([.. new[] { 0, 100, 200, 500, 800, 900 }.Select(l => (JsonNode?)O(
            $$"""{"id": {{l}}, "pos": {"y": 150, "x": -6420, "z": 3310}, "type": "LODGE", "reserve": {{l / 100 + 1}}}"""))]);
        var s = new State(db ?? Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".db"), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES,
                          offline: false, facts: Facts.Default, seasonal: Halloween);
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        if (db is not null)
            return s;
        s.Store.SetPlaysEvents(ME, playsEvents);
        s.NoteLaunch(ME, Encoding.ASCII.GetBytes($"start={reserve * 100}&reserve={reserve + 1}"));
        return s;
    }

    /// <summary>A rabbit (17) hit with each ammunition in turn, then harvested: the updates of the heartbeat after it (the harvest's update 6, the hits' warnings).</summary>
    static JsonArray Harvest(State s, int individual, params int[] ammo) => HarvestOf(s, 17, individual, ammo);

    /// <summary>The same for a <paramref name="species"/> (a weight in grams the species' scoring accepts).</summary>
    static JsonArray HarvestOf(State s, int species, int individual, params int[] ammo)
    {
        JsonNode[] events = [O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 0, "weight_grams": 1100, "feature": 1100250}"""),
            .. ammo.Select(a => (JsonNode)O($$"""{"event": 1, "individual": {{individual}}, "ammunition_id": {{a}}, "weapon_id": 391}""")),
            O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}}""")];
        s.HeartbeatReply(ME, Events(events));
        return Reply(s.HeartbeatReply(ME, Events()))["updates"]!.AsArray();
    }

    static string ScoreText(JsonArray updates) => (string)updates.Single(u => Py.Eq(u!["update"], 6))!["score_text"]!;

    static bool Disqualified(JsonArray updates) => ScoreText(updates) == "Disqualified ";

    static bool Warned(JsonArray updates) => updates.Any(u => ((string?)u!["text"])?.StartsWith("Not permitted", StringComparison.Ordinal) == true);

    /// <summary>The `ethical` flag of each kill of the latest hunt's page, newest first.</summary>
    static string KillsEthical(State s) => string.Join(",", Reply(s.ExpeditionReply(ME, "{}"u8.ToArray()))["kills"]!.AsArray().Select(k => (bool)k!["kill"]!["ethical"]!));

    [Fact]
    public void TestInAnEventHuntOneNormalRoundDisqualifiesAKillThatSilverFinishes()
    {
        var s = Hunt(1);
        var reply = Connect(s);
        Eq("[962, 963, 1142]", reply["ethical"]![17]);  // a species of the event's reserves: the event's list, which the kills are judged by
        Eq("[241, 14]", reply["ethical"]![11]);  // a species of no event reserve keeps its own
        Assert.True(Disqualified(Harvest(s, 401, 14, 962)));
        Assert.StartsWith("Score: ", ScoreText(Harvest(s, 402, 962, 1142)));
        Assert.Equal("True,False", KillsEthical(s));  // the hunt's page judges them so too
        // the rescore judges a kill by its hunt's list: the disqualified one stays so, and nothing is paid again
        var (_, rescored, paid, _, _) = Assert.Single(s.Rescore(ME, dryRun: false));
        Assert.Equal((0, 0), (rescored, paid));
    }

    [Theory]
    [InlineData(4, "[241, 14, 962, 963, 1142]")]  // the coyote: its normal list, then the event's silver rounds
    [InlineData(6, "[241, 962, 963, 1142]")]  // the blacktail deer: a silver round its normal list holds is not doubled
    public void TestInAnEventHuntASpeciesWithNoPossessedVersionTakesItsNormalAmmunitionAndSilver(int species, string permitted)
    {
        var s = Hunt(1);
        var reply = Connect(s);
        Eq(permitted, reply["ethical"]![species]);
        Eq("[962, 963, 1142]", reply["ethical"]![17]);  // a possessed species: silver only
        var next = 400;
        foreach (var round in new[] { 241, 14, 962, 963, 1142 }.Where(r => reply["ethical"]![species]!.AsArray().Any(a => (int)a! == r)))
            Assert.False(Disqualified(HarvestOf(s, species, ++next, round)));  // a normal round, silver: neither disqualifies
        Assert.True(Disqualified(HarvestOf(s, species, ++next, 241, 15)));  // an ammunition in neither list does
        Assert.True(Warned(HarvestOf(s, species, ++next, 15)));
        var flags = KillsEthical(s).Split(',');  // newest first: the two disqualified kills, then every permitted one
        Assert.Equal(["False", "False"], flags[..2]);
        Assert.All(flags[2..], f => Assert.Equal("True", f));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void TestWithoutTheEventASpeciesWithNoPossessedVersionKeepsItsNormalList(int species)
    {
        var s = Hunt(1, playsEvents: false);
        Eq(species == 4 ? "[241, 14]" : "[241, 962]", Connect(s)["ethical"]![species]);
        Assert.True(Disqualified(HarvestOf(s, species, 401, 15)));  // silver is permitted everywhere; another round is not
    }

    [Theory]
    [InlineData(1, false)]  // the player opted out: the normal animals and lists
    [InlineData(5, true)]  // a reserve the event has no content on
    public void TestWithoutTheEventOnItsReserveTheSameKillIsNotDisqualified(int reserve, bool playsEvents)
    {
        var s = Hunt(reserve, playsEvents);
        Eq("[241, 14]", Connect(s)["ethical"]![17]);
        Assert.False(Disqualified(Harvest(s, 401, 14, 962)));
    }

    [Fact]
    public void TestATutorialAfterAnEventHuntIsJudgedByTheNormalList()
    {
        var s = Hunt(1);
        Connect(s);
        Assert.True(Disqualified(Harvest(s, 401, 14, 962)));
        s.NoteTutorial(ME, "tutorial-session", 1);
        Eq("[241, 14]", Reply(s.ConnectReply(ME, "tutorial-session"))["ethical"]![17]);
        var tutorial = Harvest(s, 402, 14, 962);
        Assert.False(Warned(tutorial));
        Assert.False(Disqualified(tutorial));
        Assert.Equal("True,False", KillsEthical(s));  // the tutorial's kill (on the latest hunt's page) by the normal list, the hunt's own by the event's
        var (_, rescored, _, _, _) = Assert.Single(s.Rescore(ME, dryRun: true));
        Assert.Equal(0, rescored);  // the rescore judges both as they were stored
        s.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());  // the next hunt decides again
        Eq("[962, 963, 1142]", Connect(s)["ethical"]![17]);
        Assert.True(Disqualified(Harvest(s, 403, 14, 962)));
    }

    [Fact]
    public void TestOnlyTheHuntsFirstConnectDecidesItsEvent()
    {
        var s = Hunt(1, playsEvents: false);
        Eq("[241, 14]", Connect(s)["ethical"]![17]);
        Assert.False(Disqualified(Harvest(s, 401, 14, 962)));
        s.Store.SetPlaysEvents(ME, true);
        var again = Connect(s);  // the game started again inside the same hunt
        Assert.NotNull(again["ingame_event"]);
        Eq("[241, 14]", again["ethical"]![17]);  // still the normal list for the whole hunt
        Assert.False(Disqualified(Harvest(s, 402, 14, 962)));
        Assert.Equal("True,True", KillsEthical(s));  // the earlier kill keeps its verdict
    }

    [Theory]
    [InlineData(true)]   // main's schema 38: neither column
    [InlineData(false)]  // a database that already has `event` but not `event_until` gets the missing one and keeps its decisions
    public void TestTheSchemaOfVersion38GetsTheHuntsEventDecidedAsNone(bool withoutEvent)
    {
        var file = Path.Combine(tmp, "v38.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open(5))
            con.Exec("INSERT INTO expeditions (account_id, start_ts, event) VALUES ($a, 1, ?)", withoutEvent ? null : "halloween_2026");  // an open hunt at the upgrade
        using (var con = store.Open())
        {
            if (withoutEvent)
                con.Exec("ALTER TABLE expeditions DROP COLUMN event");
            con.Exec("ALTER TABLE expeditions DROP COLUMN event_until");
            con.Exec("PRAGMA user_version = 38");
        }
        using var migrated = new Store(file).Open(5);
        Assert.Equal(withoutEvent ? "" : "halloween_2026", migrated.Scalar("SELECT event FROM expeditions WHERE account_id = $a"));  // '': decided, no event, so its kills keep their judgement
        Assert.Equal(DBNull.Value, migrated.Scalar("SELECT event_until FROM expeditions WHERE account_id = $a"));
        Assert.Equal((long)Store.Version, migrated.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TestAnEventHuntKeepsItsListAfterTheEventEndsTheSettingChangesAndARestart()
    {
        var db = Path.Combine(tmp, "hunt.db");
        var s = Hunt(1, db: db);
        s.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        s.Clock = () => End.ToUnixTimeSeconds() - 30;
        Connect(s);  // the hunt starts with the event
        s.Clock = () => End.ToUnixTimeSeconds() + 30;
        s.Store.SetPlaysEvents(ME, false);
        Assert.True(Disqualified(Harvest(s, 401, 14, 962)));
        Eq("[962, 963, 1142]", Connect(s)["ethical"]![17]);  // the game of that hunt, started again, gets the same list
        var restarted = Hunt(1, db: db);
        restarted.Clock = () => End.ToUnixTimeSeconds() + 60;
        Assert.True(Disqualified(Harvest(restarted, 402, 14, 962)));
        restarted.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());  // the next hunt, after the event
        Eq("[241, 14]", Connect(restarted)["ethical"]![17]);
        Assert.False(Disqualified(Harvest(restarted, 403, 14, 962)));
    }

    static JsonObject[] Rows(JsonNode reply) => [.. reply["populations"]!.AsArray().OfType<JsonObject>()];

    [Theory]
    [InlineData(0, "feral_hog:3:feral_hog,werewolf:9:werewolf")]
    [InlineData(1, "werewolf:110:werewolf")]
    [InlineData(2, "werewolf:208:werewolf")]
    [InlineData(8, "werewolf:805:werewolf")]
    [InlineData(9, "werewolf:908:snowshoe_hare,wendigo:909:snowshoe_hare,feral_hog:914:feral_hog")]
    public void TestEveryReserveOfTheHalloweenEventGetsItsContentForAPlayerWhoPlaysIt(int reserve, string content)
    {
        var reply = Connect(Hunt(reserve));
        Eq("""{"name": "halloween_2026", "define": "HALLOWEEN_2026", "reserves": [0, 1, 2, 8, 9]}""", reply["ingame_event"]);
        var rows = Rows(reply).Where(p => p["active_start"] is not null).ToArray();
        var pairs = Rows(reply).Select(p => ((long)p["index"]!, (string)p["animal"]!)).ToArray();
        Assert.Equal(pairs.Length, pairs.Distinct().Count());  // the recorded creature is replaced, not doubled
        if (reserve == 0)
            Assert.Contains(Rows(reply), p => (long)p["index"]! == 3 && (string)p["animal"]! == "roosevelt_elk");  // the normal creature that shares the hog's index stays
        Assert.Equal(content, string.Join(",", rows.Select(p => $"{p!["animal"]}:{p["index"]}:{p["density_map"]}")));
        Assert.All(rows, p => Assert.Equal($"event-halloween_2026-{p["index"]}", (string)p["key"]!));  // never a normal row's key
        Assert.All(rows, p =>
        {
            Assert.Equal(reserve + 1, (int)p!["reserve"]!);  // the web's number of the game's reserve
            Assert.Equal(reserve, (int)p["index"]! / 100);
            Assert.Contains(reserve, Maps[(string)p["density_map"]!]);  // a density map the game lacks fails the hunt
            Assert.Equal(("2031-03-10T00:00:00", "2031-03-20T00:00:00"), ((string)p["active_start"]!, (string)p["active_end"]!));
        });
        Eq("[962, 963, 1142]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Theory]
    [InlineData(0, 3, "feral_hog", "12/24/0", "")]
    [InlineData(0, 9, "werewolf", "9/16/0", "22/36/15")]
    [InlineData(1, 110, "werewolf", "18/30/0", "")]
    [InlineData(2, 208, "werewolf", "9/16/0", "22/36/15")]
    [InlineData(8, 805, "werewolf", "", "25/25/30")]
    public void TestTheHalloweenRowsHaveTheOriginalsSizes(int reserve, int index, string animal, string group, string random)
    {
        var row = Rows(Connect(Hunt(reserve))).Single(p => (long)p["index"]! == index && (string)p["animal"]! == animal);
        static string Of(JsonNode n, string a, string b, string c) => $"{n[a]}/{n[b]}/{n[c]}";
        Assert.Equal(group, string.Join(";", row["groups"]!.AsArray().Select(g => Of(g!, "males", "females", "radius"))));
        Assert.Equal(random, row["random_groups"] is { } r ? Of(r, "males", "females", "radius") : "");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(9)]
    public void TestAPlayerWhoOptedOutGetsNoContentOnAnyReserveOfTheHalloweenEvent(int reserve)
    {
        var reply = Connect(Hunt(reserve, playsEvents: false));
        Assert.Null(reply["ingame_event"]);
        Assert.DoesNotContain(Rows(reply), p => p["active_start"] is not null);
        Assert.DoesNotContain(Rows(reply), p => Facts.Default.EventRows(reserve).Contains(((long)p["index"]!, (string)p["animal"]!)));  // the recorded creature goes too
        if (reserve == 0)
            Assert.Contains(Rows(reply), p => (long)p["index"]! == 3 && (string)p["animal"]! == "roosevelt_elk");  // and the normal one stays
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestAReserveOutsideTheHalloweenEventIsAnnouncedButGetsNoContent()
    {
        var reply = Connect(Hunt(5));
        Eq("""{"name": "halloween_2026", "define": "HALLOWEEN_2026", "reserves": [0, 1, 2, 8, 9]}""", reply["ingame_event"]);
        Assert.Equal("996", string.Join(",", Rows(reply).Select(p => (long)p["index"]!)));
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Theory]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [0, 9], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00+01:00"}""", true)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", true)]
    [InlineData("""{"define": "SEASON", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "Season", "define": "SEASON", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "season", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [-1], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [], "start": "soon", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [], "start": "2031-03-20T00:00:00Z", "end": "2031-03-10T00:00:00Z"}""", false)]
    [InlineData("\"season\"", false)]
    public void TestTheSettingIsCheckedWhenTheServerStarts(string json, bool valid)
    {
        if (valid) Assert.NotNull(SeasonalEvent.Parse(JsonNode.Parse(json)));
        else Assert.Throws<InvalidDataException>(() => SeasonalEvent.Parse(JsonNode.Parse(json)));
    }

    [Fact]
    public void TestAnEventWithoutASettingIsNone() => Assert.Null(SeasonalEvent.Parse(null));
}
