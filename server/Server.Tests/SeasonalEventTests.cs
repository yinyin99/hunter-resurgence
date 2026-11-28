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

    /// <summary>The first connect of a new hunt: a hunt's event is decided once, by it, so what a setting or a window changes shows in a hunt that starts after it.</summary>
    static JsonNode NewHunt(State s)
    {
        s.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        return Connect(s);
    }

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
            return NewHunt(s)["ingame_event"] is not null;
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
        Assert.Null(Connect(s)["ingame_event"]);  // the hunt decided none, and keeps it
        Assert.NotNull(NewHunt(s)["ingame_event"]);
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
        Assert.NotNull(NewHunt(s)["ingame_event"]);
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
    /// (an event species), on the roe deer 11 (on no reserve of the event) and on the whitetail 0, the coyote 4, the feral hog 5, the blacktail deer 6, the European rabbit 20, the snowshoe hare 25, the
    /// sasquatch 31 (a species that scores nothing) and the axis deer 54. With <paramref name="db"/> the server starts again on that database and launches nothing.</summary>
    State Hunt(int reserve, bool playsEvents = true, string? db = null)
    {
        var connect = CONNECT;
        var lists = (JsonArray)connect["ethical"]!;
        foreach (var sp in new[] { 0, 4, 5, 6, 11, 20, 25, 31, 54 })
        {
            while (lists.Count <= sp) lists.Add(new JsonArray());
            lists[sp] = new JsonArray(241, 14);
        }
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

    /// <summary>A rabbit (17) without a coat hit with each ammunition in turn, then harvested: the updates of the heartbeat after it (the harvest's update 6, the hits' warnings).</summary>
    static JsonArray Harvest(State s, int individual, params int[] ammo) => Kill(s, 17, null, individual, ammo);

    /// <summary>An animal of <paramref name="species"/> whose sighting wears <paramref name="coat"/> (null: none) hit with each ammunition in turn (weapon 391), then harvested: the updates of the heartbeat after it.</summary>
    static JsonArray Kill(State s, int species, int? coat, int individual, params int[] ammo)
    {
        var sighting = O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 0, "weight_grams": 1100, "feature": 1100250}""");
        if (coat is { } c)
            sighting["texture"] = c;
        JsonNode[] events = [sighting, .. ammo.Select(a => (JsonNode)O($$"""{"event": 1, "individual": {{individual}}, "ammunition_id": {{a}}, "weapon_id": 391}""")),
            O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}}""")];
        s.HeartbeatReply(ME, Events(events));
        return Reply(s.HeartbeatReply(ME, Events()))["updates"]!.AsArray();
    }

    /// <summary>The "Not permitted" toasts among the updates.</summary>
    static int Warnings(JsonArray updates) => updates.Count(u => ((string?)u!["text"])?.StartsWith("Not permitted", StringComparison.Ordinal) == true);

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
    [InlineData(1, false)]  // the player opted out: the normal animals and lists
    [InlineData(5, true)]  // a reserve the event has no content on
    public void TestWithoutTheEventOnItsReserveANormalRoundIsNotDisqualifiedAndSilverIs(int reserve, bool playsEvents)
    {
        var s = Hunt(reserve, playsEvents);
        Eq("[241, 14]", Connect(s)["ethical"]![17]);
        Assert.False(Disqualified(Harvest(s, 401, 14)));
        Assert.True(Disqualified(Harvest(s, 402, 962)));  // his animals are normal: silver is in no normal list
    }

    [Fact]
    public void TestATutorialAfterAnEventHuntIsJudgedByTheNormalList()
    {
        var s = Hunt(1);
        Connect(s);
        Assert.True(Disqualified(Harvest(s, 401, 14, 962)));
        s.NoteTutorial(ME, "tutorial-session", 1);
        Eq("[241, 14]", Reply(s.ConnectReply(ME, "tutorial-session"))["ethical"]![17]);
        var tutorial = Harvest(s, 402, 14);
        Assert.False(Warned(tutorial));
        Assert.False(Disqualified(tutorial));
        Assert.Equal("True,False", KillsEthical(s));  // the tutorial's kill (on the latest hunt's page) by the normal list, the hunt's own by the event's
        var (_, rescored, _, _, _) = Assert.Single(s.Rescore(ME, dryRun: true));
        Assert.Equal(0, rescored);  // the rescore judges both as they were stored
        Eq("[241, 14]", Connect(s)["ethical"]![17]);  // the hunt's game started again after the tutorial: the normal lists, as its events after the tutorial are judged
        s.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());  // the next hunt decides again
        Eq("[962, 963, 1142]", Connect(s)["ethical"]![17]);
        Assert.True(Disqualified(Harvest(s, 403, 14, 962)));
    }

    [Fact]
    public void TestOnlyTheHuntsFirstConnectDecidesItsEventAndTheListItsConnectsSend()
    {
        var s = Hunt(1, playsEvents: false);
        Eq("[241, 14]", Connect(s)["ethical"]![17]);
        Assert.False(Disqualified(Harvest(s, 401, 14)));
        s.Store.SetPlaysEvents(ME, true);
        var again = Connect(s);  // the game started again inside the same hunt
        Assert.Null(again["ingame_event"]);  // and the same event content as its first connect: none (#1032)
        Eq("[241, 14]", again["ethical"]![17]);  // still the hunt's list: the game's hints agree with how its animals whose coat proves nothing are judged
        Assert.False(Disqualified(Harvest(s, 402, 14)));  // an animal whose coat proves nothing follows the hunt's event: none
        Assert.False(Disqualified(Kill(s, 17, 54, 403, 962)));  // a possessed coat proves it: silver
        Assert.Equal("True,True,True", KillsEthical(s));
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
        Assert.True(Disqualified(Harvest(restarted, 402, 14, 962)));  // an animal whose coat proves nothing follows the hunt's event, kept to its end
        restarted.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());  // the next hunt, after the event
        Eq("[241, 14]", Connect(restarted)["ethical"]![17]);
        Assert.False(Disqualified(Harvest(restarted, 403, 14)));
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

    // ---- the ammunition rule judged per animal (#582)

    /// <summary>A new hunt on the game's reserve 1 (an event reserve), with or without the event, started by its connect.</summary>
    static void Launch(State s, bool plays)
    {
        s.Store.SetPlaysEvents(ME, plays);
        s.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Connect(s);
    }

    static long KillId(State s, int individual)
    {
        using var con = s.Store.Open(ME);
        return (long)con.Scalar("SELECT kill_id FROM kills WHERE account_id = $a AND individual = ?", individual)!;
    }

    static long Messages(State s)
    {
        using var con = s.Store.Open(ME);
        return (long)con.Scalar("SELECT COUNT(*) FROM messages WHERE account_id = $a AND content LIKE 'Wrong ammunition%'")!;
    }

    [Fact]
    public void TestAPossessedCoatNeedsSilverInAHuntWithoutTheEvent()
    {
        var s = Hunt(1, playsEvents: false);
        Connect(s);
        Assert.False(Disqualified(Kill(s, 0, 57, 401, 962)));  // a whitetail in its event coat: possessed, whatever the hunt
        var mixed = Kill(s, 0, 57, 402, 14, 962);
        Assert.True(Disqualified(mixed));
        Assert.Equal(1, Warnings(mixed));  // the toast and the inbox message come at the normal hit, not at the silver one
        Assert.Equal(1, Messages(s));
        Assert.Equal("False,True", KillsEthical(s));
    }

    [Fact]
    public void TestANormalCoatTakesItsListInAnEventHuntAndSilverIsUnethical()
    {
        var s = Hunt(1);
        Connect(s);
        Assert.False(Disqualified(Kill(s, 0, 0, 401, 14)));
        var silver = Kill(s, 0, 0, 402, 962);
        Assert.True(Disqualified(silver));
        Assert.Equal(1, Warnings(silver));
        Assert.Equal("False,True", KillsEthical(s));
    }

    [Theory]
    [InlineData(false, 73, 14, false)]  // the hare's coat 73 proves nothing (normal hares wear it too): the hunt decides
    [InlineData(false, 73, 962, true)]
    [InlineData(true, 73, 14, true)]
    [InlineData(true, 73, 962, false)]
    [InlineData(true, 0, 962, true)]  // coats 0 and 2: a normal hare in any hunt
    [InlineData(true, 2, 14, false)]
    [InlineData(false, 2, 962, true)]
    public void TestASnowshoeHareIsJudgedByItsNormalCoatsElseByTheHunt(bool plays, int coat, int ammo, bool disqualified)
    {
        var s = Hunt(1, plays);
        Connect(s);
        Assert.Equal(disqualified, Disqualified(Kill(s, 25, coat, 401, ammo)));
    }

    [Theory]
    [InlineData(20, 0)]  // the European rabbit: no coat decides
    [InlineData(5, 0)]  // a feral hog's base coat
    [InlineData(54, 29)]  // a female axis deer's coat, which the event keeps
    [InlineData(17, null)]  // a sighting without a coat
    public void TestAnAnimalWhoseCoatProvesNothingFollowsTheHunt(int species, int? coat)
    {
        foreach (var plays in new[] { true, false })
        {
            var s = Hunt(1, plays);
            Connect(s);
            Assert.Equal(!plays, Disqualified(Kill(s, species, coat, 401, 962)));
            Assert.Equal(plays, Disqualified(Kill(s, species, coat, 402, 14)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TestAFeralHogInAnEventCoatIsPossessedInAnyHunt(bool plays)
    {
        var s = Hunt(1, plays);
        Connect(s);
        Assert.False(Disqualified(Kill(s, 5, 79, 401, 962)));
        Assert.True(Disqualified(Kill(s, 5, 79, 402, 14)));
    }

    [Theory]
    [InlineData(4, 74)]  // the coyote and the blacktail deer have no possessed version: their event coats mark the session, not the animal
    [InlineData(6, 75)]
    public void TestACoyoteOrBlacktailIsNormalInAnEventHuntAndSilverOnItIsUnethical(int species, int coat)
    {
        var s = Hunt(1);
        var reply = Connect(s);
        Eq("[241, 14]", reply["ethical"]![species]);  // its normal list, no silver
        Assert.False(Disqualified(Kill(s, species, coat, 401, 14)));
        var silver = Kill(s, species, coat, 402, 962);
        Assert.True(Disqualified(silver));
        Assert.Equal(1, Warnings(silver));
        Assert.True(Disqualified(Kill(s, species, coat, 403, 14, 963)));  // also when it is not the last hit
        Assert.Equal("False,False,True", KillsEthical(s));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TestTheEventCreaturesPermitSilverOnlyInAnyHunt(bool plays)
    {
        var s = Hunt(1, plays);
        Eq("[962, 963, 1142]", Connect(s)["ethical"]![50]);
        foreach (var creature in new[] { 50, 58 })
        {
            Assert.False(Disqualified(Kill(s, creature, 0, 400 + creature, 962)));
            Assert.True(Disqualified(Kill(s, creature, 0, 500 + creature, 14)));
        }
    }

    [Theory]
    [InlineData(true, "[962, 963, 1142]", "[962, 963, 1142]")]
    [InlineData(false, "[241, 14]", "[962, 963, 1142]")]
    public void TestTheConnectSendsSilverForThePossessedSpeciesOnlyWithTheEvent(bool plays, string possessable, string creature)
    {
        var reply = Connect(Hunt(1, plays));
        foreach (var sp in new[] { 0, 5, 17, 20, 25, 54 })
            Eq(possessable, reply["ethical"]![sp]);
        foreach (var sp in new[] { 4, 6, 11 })  // the coyote and the blacktail deer, and a species of no event reserve: their normal lists
            Eq("[241, 14]", reply["ethical"]![sp]);
        Eq(creature, reply["ethical"]![50]);
    }

    static long? WeaponXp(State s)
    {
        using var con = s.Store.Open(ME);
        return con.Scalar("SELECT COUNT(*) FROM skills WHERE account_id = $a AND kind = 'weapon' AND key = 391") is long n && n > 0 ? n : null;
    }

    [Theory]
    [InlineData(false, 57, 14, false)]  // a possessed whitetail: a normal round earns no skill, as it gets the toast
    [InlineData(false, 57, 962, true)]
    [InlineData(true, null, 962, true)]  // a sighting without a coat: the hunt's event decides
    [InlineData(false, null, 962, false)]
    public void TestTheSkillOfAHitFollowsTheSameVerdictAsItsToast(bool plays, int? coat, int ammo, bool gains)
    {
        var s = Hunt(1, plays);
        Connect(s);
        var sighting = O("""{"event": 7, "individual": 401, "species": 0, "gender": 0, "weight_grams": 60000}""");
        if (coat is { } c)
            sighting["texture"] = c;
        s.HeartbeatReply(ME, Events(sighting, O($$"""{"event": 1, "individual": 401, "ammunition_id": {{ammo}}, "weapon_id": 391}""")));
        Assert.Equal(gains, WeaponXp(s) is not null);
        Assert.Equal(gains ? 0 : 1, Warnings(Reply(s.HeartbeatReply(ME, Events()))["updates"]!.AsArray()));
    }

    /// <summary>The ethical kills of every hunt's stored statistics (the closed hunts' stored at their close or again after the migration): what the profile's counts and the hunterscore
    /// (<see cref="State.Score"/> of the same statistics) are made of.</summary>
    static long Counted(State s)
    {
        using var con = s.Store.Open(ME);
        s.UpdateHunterscore(con, ME);
        return con.Rows("SELECT stats FROM expeditions WHERE account_id = $a AND stats IS NOT NULL").Select(r => JsonNode.Parse((string)r[0])!["data"]!["animal"] as JsonObject ?? [])
            .Sum(a => a.Sum(kv => kv.Value?["ethical_kills"] is { } n ? Py.L(n) : 0));
    }

    [Fact]
    public void TestTheHuntPageAndTheStatisticsAgreeOnEveryKill()
    {
        var s = Hunt(1, playsEvents: false);
        Connect(s);
        Kill(s, 0, 57, 401, 962);  // possessed by its coat, silver: ethical (the hunt alone would say unethical)
        Kill(s, 0, 0, 402, 14);  // normal, its list: ethical
        Kill(s, 0, 0, 403, 962);  // normal, silver: unethical
        Assert.Equal("False,True,True", KillsEthical(s));
        Assert.Equal(2, Counted(s));  // the open hunt's counts so far
        s.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());  // the hunt closes and its statistics are stored
        Assert.Equal(2, Counted(s));
    }

    /// <summary>A database of schema 40 again: the closed hunts recorded without an event (as every hunt before schema 39; with <paramref name="keepEvents"/> they keep the event their first connect
    /// recorded, as the hunts since server-1.47.0), no flag table, no `schema_marks` and no `event_undecided` or `event_backfilled`; the stored statistics of the closed hunts with a kill made stale (50
    /// ethical kills each), as the old rule's counts are: the step must clear them.</summary>
    static void Downgrade(State s, bool keepEvents = false)
    {
        using (var con = s.Store.Open(ME))
        {
            if (!keepEvents)
                con.Exec("UPDATE expeditions SET event = '' WHERE account_id = $a AND end_ts IS NOT NULL");
            con.Exec("UPDATE expeditions SET stats = json_set(stats, '$.data.animal', json('{\"0\": {\"ethical_kills\": 50}}')) WHERE account_id = $a AND end_ts IS NOT NULL AND stats IS NOT NULL"
                     + " AND id IN (SELECT json_extract(body, '$.expedition_id') FROM harvests WHERE account_id = $a)");
        }
        using var all = s.Store.Open();
        all.Exec("DROP TABLE unethical_kills");
        all.Exec("DROP TABLE schema_marks");
        all.Exec("ALTER TABLE expeditions DROP COLUMN event_undecided");
        all.Exec("ALTER TABLE expeditions DROP COLUMN event_backfilled");
        all.Exec("PRAGMA user_version = 40");
    }

    static string Hunts(State s)
    {
        using var con = s.Store.Open(ME);
        return string.Join(";", con.Rows("SELECT event, COALESCE(event_undecided, '-') FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => $"{r[0]}/{r[1]}"));
    }

    static string Stored(State s)
    {
        using var con = s.Store.Open(ME);
        return string.Join("|", con.Rows("SELECT body FROM harvests WHERE account_id = $a ORDER BY kill_id").Select(r => (string)r[0]))
            + string.Join("|", con.Rows("SELECT n, amount, summary FROM wallet_log WHERE account_id = $a ORDER BY n").Select(r => $"{r[0]},{r[1]},{r[2]}"))
            + con.Scalar("SELECT amount FROM wallet WHERE account_id = $a AND currency = 'gm'");
    }

    static List<string> WalletRows(State s)
    {
        using var con = s.Store.Open(ME);
        return [.. con.Rows("SELECT n, amount, summary FROM wallet_log WHERE account_id = $a ORDER BY n").Select(r => $"{r[0]},{r[1]},{r[2]}")];
    }

    static long FirstHunt(State s)
    {
        using var con = s.Store.Open(ME);
        return (long)con.Scalar("SELECT MIN(id) FROM expeditions WHERE account_id = $a")!;
    }

    [Fact]
    public void TestTheMigrationSetsAnOldHuntsEventFromItsOwnSightingsOnceAndChangesNoScore()
    {
        var db = Path.Combine(tmp, "old.db");
        var s = Hunt(1, db: db);
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        Launch(s, true);  // 1: every decisive sighting possessed; its European rabbit (no coat decides) was killed with silver
        Kill(s, 0, 57, 401, 962);
        Kill(s, 20, 0, 402, 962);
        Launch(s, false);  // 2: a decisive normal sighting beside a hare in coat 73
        Kill(s, 0, 0, 403, 14);
        Kill(s, 25, 73, 404, 14);
        Launch(s, true);  // 3: a mix
        s.HeartbeatReply(ME, Events(O("""{"event": 7, "individual": 405, "species": 0, "texture": 57}"""), O("""{"event": 7, "individual": 406, "species": 0, "texture": 0}""")));
        Launch(s, false);  // 4: no decisive sighting
        Kill(s, 20, 0, 407, 14);
        Launch(s, false);  // 5: open
        var before = Stored(s);
        Assert.Equal(5, Counted(s));  // the closed hunts' statistics are stored; the downgrade makes them stale
        Downgrade(s);
        s = Hunt(1, db: db);
        Assert.Equal("halloween_2026/-;/-;/-;/halloween_2026;/-", Hunts(s));  // 4 is undecided: its kills whose verdict depends on the event get flagged by `admin ethics`
        Assert.Equal("True,True", string.Join(",", Reply(s.ExpeditionReply(ME, "{}"u8.ToArray(), Encoding.ASCII.GetBytes($"expedition_id={FirstHunt(s)}")))["kills"]!.AsArray().Select(k => (bool)k!["kill"]!["ethical"]!)));
        Assert.Equal(before, Stored(s));  // no score, page or gm$ changed
        var counted = Counted(s);
        Assert.Equal(5, counted);  // the statistics were cleared and counted again by the rule
        for (var restart = 0; restart < 2; restart++)  // a second start changes nothing
        {
            s = Hunt(1, db: db);
            Assert.Equal("halloween_2026/-;/-;/-;/halloween_2026;/-", Hunts(s));
            Assert.Equal(before, Stored(s));
            Assert.Equal(counted, Counted(s));
        }
        using var check = s.Store.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }

    /// <summary>Rewrite the ammunition of the hits on <paramref name="individual"/>: the stored kill now reads as one the rule judges otherwise than it was judged at its harvest.</summary>
    static void Reshoot(State s, int individual, int ammo)
    {
        using var con = s.Store.Open(ME);
        con.Exec("UPDATE events SET body = json_set(body, '$.ammunition_id', ?) WHERE account_id = $a AND event = 1 AND individual = ?", ammo, individual);
    }

    static double PageScore(State s, int individual)
    {
        using var con = s.Store.Open(ME);
        return Convert.ToDouble(con.Scalar("SELECT json_extract(body, '$.score') FROM harvests WHERE account_id = $a AND kill_id = ?", KillId(s, individual)));
    }

    [Fact]
    public void TestAdminEthicsDisqualifiesStoredKillsKeepsTheirGmAndMountsAndFlagsWhatIsKept()
    {
        var db = Path.Combine(tmp, "ethics.db");
        var s = Hunt(1, db: db);
        s.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();  // the taxidermy window of the kills is open
        Launch(s, false);
        Harvest(s, 401, 14);  // A: scored and paid; it will read as silver on a normal animal, and it is mounted in a lodge
        Harvest(s, 402, 14);  // B: the same, not mounted
        Harvest(s, 403, 962);  // C: disqualified at its harvest and still unethical: nothing to do
        Harvest(s, 404, 962);  // D: disqualified at its harvest; it will read as ethical
        Harvest(s, 405, 14);  // E: ethical, untouched
        Kill(s, 31, null, 406, 14);  // G: a species with no score: nothing stored to change when it reads as silver on a normal animal
        Harvest(s, 407, 962);  // F: as D, but a gm$ row of its kill exists already
        var (a, b, d, f, g) = (KillId(s, 401), KillId(s, 402), KillId(s, 404), KillId(s, 407), KillId(s, 406));
        long lodge;
        using (var con = s.Store.Open(ME))
        {
            lodge = con.Insert("INSERT INTO lodges (account_id, source, id, node) VALUES ($a, 'test', '1', ?)", $$"""{"animal_slots": [{"slot_id": 5, "trophy_id": {{a}}}]}""");
            con.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids) VALUES ($a, 0, 'gm', 0, 0, ?, '[]')", $"CSS Reward (Animal Id: {f})");
        }
        var scores = (PageScore(s, 401), PageScore(s, 402));
        Assert.True(scores.Item1 > 0 && scores.Item2 > 0);
        long Best() => (long)Assert.Single(s.PublicBest(ME)["rows"]!.AsArray(), r => (string)r!["species"]! != s.BoardName(31))!["kill"]!;  // the rabbit's personal best, from the same candidates as the boards; the earliest kill wins a tie
        Assert.Equal(a, Best());
        Reshoot(s, 401, 962);
        Reshoot(s, 402, 962);
        Reshoot(s, 404, 14);
        Reshoot(s, 406, 962);
        Reshoot(s, 407, 14);
        Downgrade(s, keepEvents: true);  // the kills were made before the schema-41 step
        s = Hunt(1, db: db);
        s.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (before, wallet) = (Stored(s), WalletRows(s));
        string Lines(List<State.JudgedKill> judged) => string.Join(";", judged.Select(j => $"{j.Kill}:{j.Change}:{j.Flag}:{j.Reason}:{j.Ammo}:{j.Basis}:{j.Lodge}:{j.Slot}:{j.Gms}"));
        var unscored = $"{g}:unscored::silver_on_normal:962:species:::0";
        var dry = s.JudgeKills(ME, dryRun: true);
        Assert.Equal($"{a}:disqualify:kept:silver_on_normal:962:hunt:{lodge}:5:{Py.RoundInt(PaidFor(s, a))};{b}:disqualify::silver_on_normal:962:hunt:::{Py.RoundInt(PaidFor(s, b))};"
                     + $"{d}:requalify:::::::{Py.RoundInt(dry.Single(j => j.Kill == d).Gms)};{unscored};{f}:requalify:::::::0", Lines(dry));
        Assert.True(dry.Single(j => j.Kill == d).Gms > 0);
        Assert.Equal(before, Stored(s));  // the dry run writes nothing
        Assert.Empty(s.FlaggedKills(ME, null, null, null, null));
        Assert.Equal(Lines(dry), Lines(s.JudgeKills(ME, dryRun: false)));
        Assert.Equal((0.0, 0.0), (PageScore(s, 401), PageScore(s, 402)));
        Assert.True(PageScore(s, 404) > 0 && PageScore(s, 407) > 0);
        var paid = WalletRows(s);
        Assert.Equal(wallet, paid.Take(wallet.Count));  // every wallet row stays as it was: the gm$ paid for the disqualified kills are kept
        Assert.EndsWith($"CSS Reward (Animal Id: {d})", Assert.Single(paid.Skip(wallet.Count)));  // the requalified kill is paid, as a rescore pays it; F, which has its row, is not
        using (var con = s.Store.Open(ME))
            Assert.Contains($"\"trophy_id\": {a}", (string)con.Scalar("SELECT node FROM lodges WHERE account_id = $a")!);  // the mount stays
        Assert.Equal(d, Best());  // A and B (same score, earlier) no longer rank; D, scored now, does
        Assert.Equal((403, "disqualified"), s.Taxidermize(ME, b, State.TAXIDERMY, "em"));
        Assert.False(s.TaxidermyOffers(ME).ContainsKey(b));
        Assert.True(s.TaxidermyOffers(ME).ContainsKey(d));
        var flag = Assert.Single(s.FlaggedKills(ME, null, null, null, null));
        Assert.Equal((a, "kept", "silver_on_normal", (long?)962, (string?)"hunt", false, (long?)lodge, (long?)5, scores.Item1), (flag.Kill, flag.Kind, flag.Reason, flag.Ammo, flag.Basis, flag.Taxidermied, flag.Lodge, flag.Slot, flag.LostScore));
        Assert.Single(s.FlaggedKills(ME, "kept", "silver_on_normal", 17, (long)s.Clock() - 60));  // the list's filters
        Assert.Empty(s.FlaggedKills(ME, "undecided", null, null, null));
        Assert.Empty(s.FlaggedKills(ME, null, "not_permitted", null, null));
        Assert.Empty(s.FlaggedKills(ME, null, null, 0, null));
        Assert.Empty(s.FlaggedKills(ME, null, null, null, (long)s.Clock() + 60));
        Assert.Equal(unscored, Lines(s.JudgeKills(ME, dryRun: false)));  // a second run changes nothing; the unscored kill is reported again
        using (var con = s.Store.Open(ME))
            con.Exec("UPDATE lodges SET node = ? WHERE account_id = $a", $$"""{"animal_slots": [{"slot_id": 6, "trophy_id": {{a}}}]}""");  // the player moves the mount: the flag follows it
        Assert.Equal($"{a}:flag:kept:silver_on_normal:962:hunt:{lodge}:6:{Py.RoundInt(PaidFor(s, a))};{unscored}", Lines(s.JudgeKills(ME, dryRun: false)));
        using (var con = s.Store.Open(ME))
            con.Exec("DELETE FROM lodges WHERE account_id = $a");  // the player takes the mount down: the flag's cause is gone
        Assert.Equal($"{a}:unflag::silver_on_normal:962:hunt:::{Py.RoundInt(PaidFor(s, a))};{unscored}", Lines(s.JudgeKills(ME, dryRun: false)));
        Assert.Empty(s.FlaggedKills(ME, null, null, null, null));
    }

    static double PaidFor(State s, long kill)
    {
        using var con = s.Store.Open(ME);
        return Convert.ToDouble(con.Scalar("SELECT COALESCE(SUM(amount), 0) FROM wallet_log WHERE account_id = $a AND summary = ?", $"CSS Reward (Animal Id: {kill})"));
    }

    [Fact]
    public void TestAnUndecidedKillKeepsTheVerdictItWasMadeUnderEverywhereAndIsFlagged()
    {
        var db = Path.Combine(tmp, "undecided.db");
        var s = Hunt(1, db: db);
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        Launch(s, true);  // a hunt the migration gives the event
        Kill(s, 0, 57, 401, 962);
        Launch(s, false);  // a hunt with no deciding sighting: hares in coat 73, made under the rule before (their species' list and the event's silver)
        Kill(s, 25, 73, 402, 14);  // normal: ethical; possessed: unethical
        Kill(s, 25, 73, 403, 14);  // will read as silver: normal unethical, possessed ethical
        Kill(s, 25, 73, 404, 14, 241);  // will read as a normal round then silver: unethical as either kind
        Kill(s, 25, 73, 405, 15);  // an ammunition in no list: disqualified at its harvest, under any rule
        Launch(s, false);
        Reshoot(s, 403, 962);
        using (var con = s.Store.Open(ME))
            con.Exec("UPDATE events SET body = json_set(body, '$.ammunition_id', 962) WHERE account_id = $a AND event = 1 AND individual = 404 AND json_extract(body, '$.ammunition_id') = 241");
        Counted(s);
        Downgrade(s);
        s = Hunt(1, db: db);
        Assert.Equal("halloween_2026/-;/halloween_2026;/-", Hunts(s));
        var (k2, k3, k4) = (KillId(s, 402), KillId(s, 403), KillId(s, 404));
        var scores = new[] { 402, 403, 404 }.Select(i => PageScore(s, i)).ToList();
        Assert.All(scores, x => Assert.True(x > 0));
        Assert.Equal(0.0, PageScore(s, 405));
        // the hunt page, the statistics (so the hunterscore) and the taxidermy keep the verdict the kills were made under, as their score does
        Assert.Equal("False,True,True,True", string.Join(",", Reply(s.ExpeditionReply(ME, "{}"u8.ToArray(), Encoding.ASCII.GetBytes($"expedition_id={FirstHunt(s) + 1}")))["kills"]!.AsArray()
            .Select(k => (bool)k!["kill"]!["ethical"]!)));
        Assert.Equal(4, Counted(s));  // the whitetail and the three hares
        Assert.Equal((410, "too_late"), s.Taxidermize(ME, k3, State.TAXIDERMY, "em"));  // not refused as disqualified: only its window is over
        Assert.Equal((403, "disqualified"), s.Taxidermize(ME, KillId(s, 405), State.TAXIDERMY, "em"));
        string Lines(List<State.JudgedKill> judged) => string.Join(";", judged.Select(j => $"{j.Kill}:{j.Change}:{j.Flag}:{j.Reason}:{j.Ammo}:{j.IfNormal}:{j.IfPossessed}"));
        var expected = $"{k2}:flag:undecided:undecided:14:ethical:unethical;{k3}:flag:undecided:undecided:962:unethical:ethical;{k4}:flag:undecided:undecided:962:unethical:unethical";
        var dry = s.JudgeKills(ME, dryRun: true);
        Assert.Equal(expected, Lines(dry));
        Assert.Equal(scores, dry.Select(j => j.Score));  // the line shows the score the kill keeps
        Assert.Equal(expected, Lines(s.JudgeKills(ME, dryRun: false)));
        Assert.Equal(scores, new[] { 402, 403, 404 }.Select(i => PageScore(s, i)));  // they stay as they are
        var flag = Assert.Single(s.FlaggedKills(ME, "undecided", null, null, null), x => x.Kill == k3);
        Assert.Equal(((long?)25, (long?)73, (string?)"unethical", (string?)"ethical"), (flag.Species, flag.Coat, flag.IfNormal, flag.IfPossessed));
        Assert.Empty(s.JudgeKills(ME, dryRun: false));
    }

    [Fact]
    public void TestTheMigrationMarksUndecidedOnlyTheHuntsOnAnEventReserveAfterTheEventStartedAndACreatureDecides()
    {
        var db = Path.Combine(tmp, "limits.db");
        var s = Hunt(1, db: db);
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        void Seen(int individual, int species) => s.HeartbeatReply(ME, Events(O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "texture": 0}""")));
        Launch(s, false);  // 1: no deciding sighting (a European rabbit), ended before the first hunt with the event started
        Seen(501, 20);
        Launch(s, true);  // 2: a werewolf alone: a creature proves the event
        Seen(502, 50);
        Launch(s, false);  // 3: no deciding sighting, on a reserve the event has no content on
        Seen(503, 20);
        Launch(s, false);  // 4: no deciding sighting, on an event reserve, after the event started
        Seen(504, 20);
        Launch(s, false);  // 5: open
        using (var con = s.Store.Open(ME))
        {
            var ids = con.Rows("SELECT id FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => (long)r[0]).ToList();
            for (var i = 0; i < 4; i++)
                con.Exec("UPDATE expeditions SET start_ts = ?, end_ts = ? WHERE account_id = $a AND id = ?", 1000 + 100 * i, 1050 + 100 * i, ids[i]);
            con.Exec("UPDATE expeditions SET reserve = 6 WHERE account_id = $a AND id = ?", ids[2]);
        }
        Downgrade(s);
        s = Hunt(1, db: db);
        Assert.Equal("/-;halloween_2026/-;/-;/halloween_2026;/-", Hunts(s));
    }

    [Fact]
    public void TestUnscoredListsOnlyKillsMadeBeforeTheStepThatTheRuleOfTheirTimePermitted()
    {
        var db = Path.Combine(tmp, "unscored.db");
        var s = Hunt(1, db: db);
        void Doe(int individual, int? coat, params int[] ammo) => Female(0, individual, coat, ammo);  // a whitetail doe scores nothing
        void Female(int species, int individual, int? coat, params int[] ammo)
        {
            var sighting = O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 1, "weight_grams": 50000}""");
            if (coat is { } c)
                sighting["texture"] = c;
            s.HeartbeatReply(ME, Events([sighting, .. ammo.Select(a => (JsonNode)O($$"""{"event": 1, "individual": {{individual}}, "ammunition_id": {{a}}, "weapon_id": 391}""")),
                O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}}""")]));
        }
        Launch(s, true);  // the event recorded by the hunt's first connect (since server-1.47.0): its list (silver) on the whitetail then
        Doe(401, null, 14);  // a normal round: unethical then, and now (the hunt's event decides)
        Doe(402, 0, 962);  // silver: ethical then; a normal coat now
        Female(6, 407, null, 14, 962);  // a blacktail doe, a normal round then silver: unethical then (the list named the blacktail: silver only) and now (silver on a normal animal)
        s.NoteTutorial(ME, "tutorial-session", 1);  // the hunt's event ends at the tutorial (`event_until`): its species' list and silver after it
        Doe(408, 57, 14);  // a possessed coat, a normal round: ethical then, unethical now
        Launch(s, false);  // without the event: its species' list and silver then
        Doe(403, 0, 962);
        Launch(s, false);  // without the event; the step gives it the event its possessed whitetail proves
        s.HeartbeatReply(ME, Events(O("""{"event": 7, "individual": 404, "species": 0, "texture": 57}""")));
        Doe(405, null, 14);  // a normal round: ethical then (its species' list), possessed now
        Launch(s, false);
        Downgrade(s, keepEvents: true);
        s = Hunt(1, db: db);
        Launch(s, false);
        Doe(406, 0, 962);  // made after the step: judged by this rule at its harvest
        Assert.Equal("halloween_2026/-;/-;halloween_2026/-;/-;/-", Hunts(s));
        var expected = string.Join(";", new[] { 402, 408, 403, 405 }.Select(i => $"{KillId(s, i)}:unscored"));
        string Lines(List<State.JudgedKill> judged) => string.Join(";", judged.Select(j => $"{j.Kill}:{j.Change}"));
        var before = Stored(s);
        Assert.Equal(expected, Lines(s.JudgeKills(ME, dryRun: true)));
        Assert.Equal(expected, Lines(s.JudgeKills(ME, dryRun: false)));  // listed on every run, written never
        Assert.Equal(expected, Lines(s.JudgeKills(ME, dryRun: false)));
        Assert.Equal(before, Stored(s));
        Assert.Empty(s.FlaggedKills(ME, null, null, null, null));
    }

    [Fact]
    public void TestApplyReadsTheMountAndThePaymentAgainInsideItsWrite()
    {
        var s = Hunt(1, playsEvents: false);
        s.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Connect(s);
        Harvest(s, 401, 14);  // will read as silver on a normal animal: mounted at the listing, taken down before its write
        Harvest(s, 402, 962);  // disqualified at its harvest, will read as ethical: paid by someone else between the listing and its write
        var (a, d) = (KillId(s, 401), KillId(s, 402));
        using (var con = s.Store.Open(ME))
            con.Insert("INSERT INTO lodges (account_id, source, id, node) VALUES ($a, 'test', '1', ?)", $$"""{"animal_slots": [{"slot_id": 5, "trophy_id": {{a}}}]}""");
        Reshoot(s, 401, 962);
        Reshoot(s, 402, 14);
        Assert.Equal("kept", s.JudgeKills(ME, dryRun: true).Single(j => j.Kill == a).Flag);
        var applied = s.JudgeKills(ME, dryRun: false, beforeWrite: k =>
        {
            using var con = s.Store.Open(ME);
            if (k == a)
                con.Exec("DELETE FROM lodges WHERE account_id = $a");
            else
                con.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids) VALUES ($a, 0, 'gm', 5, 5, ?, '[]')", $"CSS Reward (Animal Id: {d})");
        });
        Assert.Equal($"{a}:disqualify::;{d}:requalify:::0", string.Join(";", applied.Select(j => $"{j.Kill}:{j.Change}:{j.Flag}:{j.Lodge}{(j.Change == "requalify" ? $":{j.Gms}" : "")}")));
        Assert.Empty(s.FlaggedKills(ME, null, null, null, null));  // no mount is kept any more
        Assert.Equal(5.0, PaidFor(s, d));  // paid once, by the row written meanwhile
    }
}
