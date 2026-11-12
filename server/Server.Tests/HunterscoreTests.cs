using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

public class HunterscoreTests : StateTestBase
{
    const long B = 8;

    // animal species 1, animal species 17, weapon item 5
    static readonly Facts Ranks = Facts.Parse("""
        {"format": 1, "ranks": {"cap": 1000, "rows": [
          {"id": 1, "title": "One", "category": "animal", "species": 1},
          {"id": 2, "title": "Rabbit", "category": "animal", "species": 17},
          {"id": 3, "title": "Five", "category": "weapon", "items": [5]}]}}
        """);

    public HunterscoreTests()
    {
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };
    }

    State Online() => new(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: Ranks);

    long Stored(long account) => state.Store.AccountJson(account)["hunterscore"] is { } h ? Py.L(h) : 0;

    void Kill(long account, long individual, long species, long ammo) => state.HeartbeatReply(account, Events(
        O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
        O($$"""{"event": 1, "individual": {{individual}}, "weapon_id": 5, "ammunition_id": {{ammo}}}"""),
        O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}}""")));

    [Fact]
    public void TestScoreSumsTheCappedRows()
    {
        var facts = Facts.Parse("""
            {"format": 1, "ranks": {"cap": 3, "rows": [
              {"id": 1, "title": "One", "category": "animal", "species": 1},
              {"id": 2, "title": "Rabbit", "category": "animal", "species": 17},
              {"id": 3, "title": "Guns", "category": "weapon", "items": [5, 706]},
              {"id": 4, "title": "Finds", "category": "collectable", "types": [0, 1]}]}}
            """);
        var stats = O("""
            {"animal": {"101": {"ethical_kills": 2}, "1": {"ethical_kills": 2}, "17": {"kills": 5}, "x": {"ethical_kills": 9}},
             "weapon": {"5": {"10": {"ethical_kills": 1}, "11": {"ethical_kills": 1}}, "706": {"10": {"ethical_kills": 1}}, "999": {"10": {"ethical_kills": 4}}},
             "collectables": {"0": {"collected": 1}, "1": {"collected": 1}}}
            """);
        // species 1: 2 + 2 (its variant 101), capped to 3; species 17: no ethical_kills, 0; the weapon row 1 + 1 + 1 with 706
        // (999 is in no row); collectables 1 + 1
        Assert.Equal(3 + 0 + 3 + 2, State.Score(facts, stats));
    }

    [Fact]
    public void TestAKillIsCreditedWhenItsHuntCloses()
    {
        state = Online();
        var local = Stored(ME);
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        Assert.Equal(0, Stored(B));  // mid-hunt: nothing yet
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(2, Stored(B));  // the species row and the weapon row
        // a second hunt: ammunition 10 is not permitted on species 17, so the kill is not ethical
        state.ConnectReply(B);
        Kill(B, 82, 17, 10);
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(2, Stored(B));
        Assert.Equal(local, Stored(ME));  // the recorded account keeps the recorded player's value
    }

    [Fact]
    public void TestTheTimerClosesAStaleHuntWithoutAGameCall()
    {
        state = Online();
        double now = Py.Now();
        state.Clock = () => now;
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        now += State.HEARTBEAT_TIMEOUT - 1;
        state.CloseStaleHunts();
        Assert.Equal(0, Stored(B));  // a heartbeat came within the timeout
        now += 2;
        state.CloseStaleHunts();
        Assert.Equal(2, Stored(B));
        state.CloseStaleHunts();
        Assert.Equal(2, Stored(B));  // closed once, credited once
    }

    [Fact]
    public void TestALateHeartbeatReopensAStaleClosedHunt()
    {
        state = Online();
        double now = Py.Now();
        state.Clock = () => now;
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        now += 200;
        state.CloseStaleHunts();
        Assert.Equal(2, Stored(B));
        Kill(B, 82, 1, 10);  // the late heartbeat: the same hunt is open again
        using (var con = state.Store.Open(B))
            Assert.Equal(1L, con.Scalar("SELECT COUNT(*) FROM expeditions WHERE account_id = $a AND end_ts IS NULL"));
        now += 200;
        state.CloseStaleHunts();
        Assert.Equal(4, Stored(B));  // both kills, each once
    }

    /// <summary>A fresh state with account B holding one open hunt with a kill; the hunt closes only through <paramref name="close"/>.</summary>
    void ClosedBy(Action<State> close, bool stale)
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        Assert.Equal(0, Stored(B));
        if (stale)
        {
            using var con = state.Store.Open(B);
            con.Exec("UPDATE expeditions SET last_seen = last_seen - 200 WHERE account_id = $a");
        }
        close(state);
        Assert.Equal(2, Stored(B));
    }

    [Fact]
    public void TestTheNextLaunchCreditsTheHunt() => ClosedBy(s => s.NoteLaunch(B, "start=101&reserve=2"u8.ToArray()), stale: false);

    [Fact]
    public void TestAConnectAfterAStaleHeartbeatCreditsTheHunt() => ClosedBy(s => s.ConnectReply(B), stale: true);

    [Fact]
    public void TestTheExpeditionPageAfterAStaleHeartbeatCreditsTheHunt() => ClosedBy(s => s.ExpeditionReply(B, "{}"u8.ToArray()), stale: true);

    [Fact]
    public void TestTheExpeditionListAfterAStaleHeartbeatCreditsTheHunt() =>
        ClosedBy(s => s.ExpeditionListReply(B, "{}"u8.ToArray(), "limit=10&offset=0"u8.ToArray()), stale: true);

    [Fact]
    public void TestEveryAccountIsRecomputedAtStart()
    {
        state = Online();
        state.CreateAccount(B, "second");
        using (var con = state.Store.Open())
        {
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 5000) WHERE id = ?", B);
            con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', 777) WHERE id = ?", ME);
        }
        state = Online();
        Assert.Equal((0L, 777L), (Stored(B), Stored(ME)));
    }

    [Fact]
    public void TestValuesAPlayerSentNeverStopTheScore()
    {
        // a restored counter too large for a number of 64 bits counts the cap
        var stats = O("""{"animal": {"1": {"ethical_kills": 99999999999999999999}}, "weapon": {"5": {"10": {"ethical_kills": 9223372036854775807}, "11": {"ethical_kills": 9223372036854775807}}}}""");
        Assert.Equal(2000, State.Score(Ranks, stats));

        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.HeartbeatReply(B, Events(
            O("""{"event": 7, "individual": 81, "species": 1, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
            O("""{"event": 1, "individual": 81, "weapon_id": "x", "ammunition_id": 10}"""),
            O("""{"event": 3, "individual": 81, "extended_species": 1}"""),
            O("""{"event": 3, "individual": 82}"""),  // no species: skipped
            O("""{"event": 7, "individual": 83, "species": 1, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
            O("""{"event": 1, "individual": 83, "weapon_id": 5, "ammunition_id": 99999999999999999999}"""),
            O("""{"event": 3, "individual": 83, "extended_species": 17}""")));  // an ammo id beyond 64 bits is not permitted on 17
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(1, Stored(B));  // the species row only: "x" is no weapon
        // a kill the database cannot read back (a number that is not finite) is skipped, the others still count, and the
        // close, the next launch, a restore and the start still work
        state.ConnectReply(B);
        state.HeartbeatReply(B, """{"events": [{"event": 3, "individual": 84, "extended_species": 1, "trophy_integrity": 1e999}]}"""u8.ToArray());
        Kill(B, 85, 1, 10);
        state.Disconnect(B, "{}"u8.ToArray());
        Assert.Equal(3, Stored(B));  // 81: the species row; 85: the species and the weapon row
        // hunts with a hit the database cannot read back, a hit without a distance or a miss with a weapon that is a list count
        // nothing, and their close still works
        foreach (var bad in new[] { """{"event": 1, "individual": 86, "weapon_id": 5, "ammunition_id": 10, "distance_cms": 1e999}""",
                                     """{"event": 1, "individual": 86, "weapon_id": 5, "ammunition_id": 10, "distance_cms": null}""",
                                     """{"event": 21, "weapon_id": [1], "ammunition_id": 10}""" })
        {
            state.ConnectReply(B);
            state.HeartbeatReply(B, Encoding.UTF8.GetBytes($$"""{"events": [{{bad}}]}"""));
            Kill(B, 87, 1, 10);
            state.Disconnect(B, "{}"u8.ToArray());
            Assert.Equal(3, Stored(B));
        }
        state.NoteLaunch(B, "start=101&reserve=2"u8.ToArray());
        state.NoteLaunch(B, "start=101&reserve=2"u8.ToArray());
        // the zip's own hunterscore (1234) never stays: the value is always the computed one
        Restorer.Import(state, TestZip.Own().Write(tmp), B, tmp);
        Assert.Equal(3, Stored(B));
        state = Online();
        Assert.Equal(3, Stored(B));
    }

    [Fact]
    public void TestRestoredStatisticsCountAndUnreadableOnesCountNone()
    {
        state = Online();
        Restorer.Import(state, TestZip.Own().Write(tmp), null, tmp);  // account 1001; its Me/me says 1234
        long Profile()
        {
            using var con = state.Store.Open(1001);
            return (long)con.Scalar("SELECT json_extract(body, '$.hunterscore') FROM profile WHERE account_id = $a")!;
        }
        void Stats(string userStats)
        {
            using var con = state.Store.Open(1001);
            con.Exec("UPDATE statistics SET user_stats = ? WHERE account_id = $a", userStats);
            state.UpdateHunterscore(con, 1001);
        }
        Assert.Equal((0L, 0L), (Stored(1001), Profile()));  // the zip's statistics hold no ethical kills
        // as a restore stores them: 4 of species 1 (2 of its variant 101) and 3 with item 5 over two ammo
        Stats("""{"animal": {"1": {"ethical_kills": 2}, "101": {"ethical_kills": 2}}, "weapon": {"5": {"10": {"ethical_kills": 2}, "11": {"ethical_kills": 1}}}}""");
        Assert.Equal((7L, 7L), (Stored(1001), Profile()));
        // statistics the database cannot read back count none: the value is 0, not the last one nor the zip's
        Stats("""{"animal": {"1": {"ethical_kills": Infinity}}}""");
        Assert.Equal((0L, 0L), (Stored(1001), Profile()));
        state = Online();
        Assert.Equal(0L, Stored(1001));
    }

    JsonNode Total(long account) => Reply(state.StatisticsReply(account, Encoding.UTF8.GetBytes($"user_id={account}"))!);

    JsonNode ConnectStats(long account) => Reply(state.ConnectReply(account))["user_stats"]!["user_stats"]!;

    [Fact]
    public void TestEachClosedHuntAddsToTheStatistics()
    {
        state = Online();
        state.CreateAccount(B, "second");
        Eq("""{"duration": 0, "distance": 0, "data": {"collectables": {}, "weapon": {}, "animal": {}, "daily_missions": {}}}""", Total(B));
        foreach (var individual in new[] { 81L, 82L })
        {
            state.ConnectReply(B);
            Kill(B, individual, 1, 10);
            state.Disconnect(B, """{"distance_travelled": 466}"""u8.ToArray());
        }
        const string data = """
            {"collectables": {}, "weapon": {"5": {"10": {"ethical_kills": 2, "hits": 2, "kills": 2}}},
             "animal": {"1": {"ethical_kills": 2, "kills": 2}}, "daily_missions": {}}
            """;
        Eq(data, Total(B)["data"]);
        Eq("932", Total(B)["distance"]);
        var connect = ConnectStats(B);
        Eq("932", connect["distance"]);
        connect.AsObject().Remove("distance");
        connect.AsObject().Remove("duration");
        Eq(data, connect);
        Assert.Equal(4, Stored(B));  // each kill counts once: 2 in the species row, 2 in the weapon row

        // a restore replaces the base only: the server hunts stay and still add on top
        Restorer.Import(state, TestZip.Own().Write(tmp), B, tmp);
        Eq("""
           {"collectables": {}, "weapon": {"5": {"10": {"ethical_kills": 2, "hits": 2, "kills": 2}}},
            "animal": {"5": {"kills": 2}, "1": {"ethical_kills": 2, "kills": 2}}, "daily_missions": {}}
           """, Total(B)["data"]);
        Eq("944.5", Total(B)["distance"]);  // the zip's 12.5 plus the hunts'
        Assert.Equal(4, Stored(B));

        // a hunt closed before its statistics were stored gets the same ones at the next start (its events end where the next hunt's begin)
        var before = Total(B);
        using (var con = state.Store.Open(B))
            con.Exec("UPDATE expeditions SET stats = NULL WHERE account_id = $a");
        state = Online();
        Assert.Equal(4, Stored(B));
        Eq(before, Total(B));
    }

    [Fact]
    public void TestAHuntsPickupsCountInTheStatisticsOnceItCloses()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.HeartbeatReply(B, Events(
            O("""{"event": 24, "collectable_id": 77, "type": 9, "x": 1, "y": 2, "z": 3, "scale": 100}"""),
            O("""{"event": 36, "collectable_id": 78, "species_id": 5, "double_antler": 0, "x": 1, "y": 2, "z": 3}""")));
        Eq("{}", Total(B)["data"]!["collectables"]);  // an open hunt counts nothing yet
        state.Disconnect(B, "{}"u8.ToArray());
        const string counted = """{"9": {"collected": 1}, "2005": {"collected": 1}}""";
        Eq(counted, Total(B)["data"]!["collectables"]);
        Eq(counted, ConnectStats(B)["collectables"]);
    }

    [Fact]
    public void TestAnyAccountsStatisticsAreReadByAnotherUserIdAndShownAsATab()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.CreateAccount(9, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 9");
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        state.Disconnect(B, """{"distance_travelled": 466}"""u8.ToArray());

        // the closed hunt is in the statistics another account asks for; a banned, an unknown and the recorded player's user id are not served
        Eq(Total(B), Reply(state.StatisticsReply(ME, "user_id=8"u8.ToArray())!));
        foreach (var id in new[] { 9, 99, 7 })
            Assert.Null(state.StatisticsReply(B, Encoding.UTF8.GetBytes($"user_id={id}")));

        var page = state.StatisticsPage(B);
        var w = page["weapons"]!.AsArray().Single()!;
        Assert.Equal(("Test Rifle", "Test Bullet", 1, 100.0, 1, 100.0), ((string)w["weapon"]!, (string)w["ammo"]!, (int)w["shots"]!, (double)w["accuracy"]!, (int)w["kills"]!, (double)w["killsPerHit"]!));
        Assert.Null(w["longest"]);
        Assert.Equal((5L, 10L), ((long)w["weaponId"]!, (long)w["ammoId"]!));
        var a = page["animals"]!.AsArray().Single()!;
        Assert.Equal(("Test Deer", 0, 0, 1), ((string)a["name"]!, (int)a["spotted"]!, (int)a["tracked"]!, (int)a["harvested"]!));
        Assert.Equal(0.466, (double)page["distanceKm"]!, 6);
        Assert.Empty(page["collectables"]!.AsArray());
    }

    [Fact]
    public void TestRanksAreTheCountsTheScoreAddsUpAndMilestonesTheirThresholds()
    {
        var connect = CONNECT;
        connect["achievements"] = JsonNode.Parse("""
            [{"id": 0, "category": 0, "group": 0, "group_title": "Total", "icon_url": "0/1_90.png", "value": "animal.*.ethical_kills", "triggers": [1, 2, 50]},
             {"id": 1, "category": 0, "group": 1, "group_title": "Mule Deer", "icon_url": "0/2_90.png", "value": "animal.MULE_DEER.ethical_kills", "triggers": [1, 25]},
             {"id": 13, "category": 1, "group": 0, "group_title": "Test Rifle", "icon_url": "1/3_90.png", "value": "weapon.TEST_RIFLE.*.ethical_kills", "triggers": [1, 25]},
             {"id": 25, "category": 2, "group": 0, "group_title": "Distance", "icon_url": "", "value": "distance", "triggers": [10, 42]},
             {"id": 90, "category": 3, "group_title": "No such tab", "value": "distance", "triggers": [1]},
             {"id": 91, "category": 0, "group_title": "No path", "value": null, "triggers": [1]}]
            """);
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, itemDefs: ITEM_DEFS, offline: false, facts: Ranks);
        state.CreateAccount(B, "second");
        using (var con = state.Store.Open(B))  // a restored base of 1500 kills with the species 1 and the weapon 5
            PlayerRows.SetStatistics(con, O("""{"animal": {"1": {"ethical_kills": 1500}}, "weapon": {"5": {"10": {"ethical_kills": 1500}}}}"""), 0, 0, null, null);
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        state.Disconnect(B, """{"distance_travelled": 12000}"""u8.ToArray());

        // the base plus the closed hunt, before the cap: the hunterscore adds up 1000 + 0 + 1000
        Assert.Equal(2000, Stored(B));
        Eq("""[{"id": 1, "category_id": 1, "value": 1501}, {"id": 2, "category_id": 1, "value": 0}, {"id": 3, "category_id": 2, "value": 1501}]""", JsonNode.Parse(state.RanksReply(B)));
        Eq("""
            {"cap": 1000,
             "ranks": [{"id": 1, "category": "animals", "name": "One", "value": 1501}, {"id": 2, "category": "animals", "name": "Rabbit", "value": 0},
                       {"id": 3, "category": "weapons", "name": "Five", "value": 1501}],
             "milestones": [{"category": "animals", "title": "Total", "icon": "0/1_90", "steps": [1, 2, 50], "value": 1501}, {"category": "animals", "title": "Mule Deer", "icon": "0/2_90", "steps": [1, 25], "value": 1501},
                            {"category": "weapons", "title": "Test Rifle", "icon": "1/3_90", "steps": [1, 25], "value": 1501}, {"category": "exploration", "title": "Distance", "icon": null, "steps": [10, 42], "value": 12}]}
            """, state.PublicRanks(B));
        state.CreateAccount(9, "third");
        Eq("""[{"id": 1, "category_id": 1, "value": 0}, {"id": 2, "category_id": 1, "value": 0}, {"id": 3, "category_id": 2, "value": 0}]""", JsonNode.Parse(state.RanksReply(9)));  // an account without a count
    }

    [Fact]
    public void TestAnyAccountsHuntsAreReadByAnotherUserIdAndShownAsAHistory()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.CreateAccount(9, "banned");
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = 9");
        state.NoteLaunch(B, "start=101&reserve=2"u8.ToArray());
        Kill(B, 81, 1, 10);
        state.Disconnect(B, """{"distance_travelled": 466}"""u8.ToArray());
        state.NoteLaunch(B, "start=101&reserve=2"u8.ToArray());  // the second hunt is still open

        // the player's own account without a user id or with its own, another player's by user id; a banned, an unknown and the recorded player's are nobody's
        Assert.Equal(B, state.HuntsOf(B, []));
        Assert.Equal(B, state.HuntsOf(B, "user_id=8"u8.ToArray()));
        Assert.Equal(B, state.HuntsOf(ME, "user_id=8"u8.ToArray()));
        foreach (var id in new[] { 9, 99, 7 })
            Assert.Null(state.HuntsOf(B, Encoding.UTF8.GetBytes($"user_id={id}")));

        var page = state.PublicHunts(B, 1);
        Eq("[1, 1, 2]", T(page["page"], page["pages"], page["total"]));
        var (open, closed) = (page["rows"]![0]!, page["rows"]![1]!);
        Eq("""[0, "Test Reserve", "Lodge 1", 0, 0]""", T(open["seconds"], open["reserve"], open["lodge"], open["collectables"], open["kills"]));
        Eq("""["Test Reserve", "Lodge 1", 1]""", T(closed["reserve"], closed["lodge"], closed["kills"]));
        Assert.True((long)open["id"]! > (long)closed["id"]!);
        Assert.Equal(page["rows"]!.ToJsonString(), state.PublicHunts(B, 9)["rows"]!.ToJsonString());  // a page past the last is the last

        var hunt = state.PublicHunt(B, (long)closed["id"]!)!;
        Assert.NotNull(hunt["end"]);
        var w = hunt["weapons"]!.AsArray().Single()!;
        Eq("""["Test Rifle", "Test Bullet", 1]""", T(w["weapon"], w["ammo"], w["kills"]));
        Eq("""["Test Deer", 1]""", T(hunt["animals"]![0]!["name"], hunt["animals"]![0]!["harvested"]));
        Assert.Equal(0.466, (double)hunt["distanceKm"]!, 6);
        var kill = hunt["kills"]!.AsArray().Single()!;
        Eq("""["Test Deer", "Test Rifle", "Test Bullet", true]""", T(kill["species"], kill["weapon"], kill["ammo"], kill["sheet"]));
        // without an id the newest hunt, which is open: no end, no statistics
        var latest = state.PublicHunt(B, null)!;
        Eq("[null, 0, 0]", T(latest["end"], latest["weapons"]!.AsArray().Count, latest["kills"]!.AsArray().Count));
        Assert.Null(state.PublicHunt(B, 999));

        // the score sheet of the kill: no antler features, so a sheet without rows; an unknown kill has none
        var sheet = state.PublicSheet(B, (long)kill["id"]!)!;
        Eq("""["antler", "Test Deer", "Test Rifle", "Test Bullet", false, 1, "None"]""", T(sheet["kind"], sheet["species"], sheet["weapon"], sheet["ammo"], sheet["scope"], sheet["shots"], sheet["platform"]));
        Assert.Empty(sheet["rows"]!.AsArray());
        Assert.Null(state.PublicSheet(B, 999));
    }

    [Fact]
    public void TestAHarvestAfterItsHuntClosedStillCounts()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.Disconnect(B, "{}"u8.ToArray());
        Kill(B, 81, 1, 10);  // a heartbeat after the disconnect: the harvest lands in the closed hunt
        Assert.Equal(2, Stored(B));
        Eq("""{"5": {"10": {"ethical_kills": 1, "hits": 1, "kills": 1}}}""", Total(B)["data"]!["weapon"]);
    }

    [Fact]
    public void TestAHitDistanceBeyondADoubleCountsNoneAndTheHuntStillCounts()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.HeartbeatReply(B, Events(
            O("""{"event": 7, "individual": 81, "species": 1, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
            O("""{"event": 1, "individual": 81, "weapon_id": 5, "ammunition_id": 10, "distance_cms": 1e308}"""),
            O("""{"event": 3, "individual": 81, "extended_species": 1}""")));
        state.Disconnect(B, "{}"u8.ToArray());
        Eq("""{"5": {"10": {"ethical_kills": 1, "hits": 1, "kills": 1}}}""", Reply(state.ExpeditionReply(B, "{}"u8.ToArray()))["stats"]!["data"]!["weapon"]);
        Eq("""{"5": {"10": {"ethical_kills": 1, "hits": 1, "kills": 1}}}""", Total(B)["data"]!["weapon"]);
        Assert.Equal(2, Stored(B));
    }

    [Fact]
    public void TestTheSchemaOfVersion4IsMigratedToTheHuntStatistics()
    {
        var file = Path.Combine(tmp, "v4.db");
        using (var con = new Store(file).Open())
        {
            con.Exec("ALTER TABLE expeditions DROP COLUMN stats");
            con.Exec("DROP TABLE friends");
            con.Exec("ALTER TABLE harvests DROP COLUMN hidden");
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 4");
        }
        var migrated = new Store(file);
        migrated.CreateAccount(5, "five", new JsonObject());
        using var check = migrated.Open(5);
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM expeditions WHERE account_id = $a AND stats IS NULL"));
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
        Assert.Empty(migrated.FriendsOf(5, null).Friends);  // version 6: the friends table
        Assert.Equal(0L, check.Scalar("SELECT COUNT(*) FROM wallet_log WHERE account_id = $a"));  // version 11: the wallet log
    }

    [Fact]
    public void TestAnyAccountsAchievementsAreServedToEveryone()
    {
        var connect = CONNECT;
        connect["achievements"] = JsonNode.Parse("""
            [{"id": 0, "category": 0, "category_title": "Animals", "group": 0, "title": "%d Harvests", "description": "%d animals harvested.", "value": "animal.*.ethical_kills", "triggers": [50, 250, 500]},
             {"id": 1, "category": 0, "category_title": "Animals", "group": 1, "title": "%d Mule Deer", "description": "%d Mule Deer harvested.", "value": "animal.MULE_DEER.ethical_kills", "triggers": [1, 25]},
             {"id": 25, "category": 2, "category_title": "Exploration", "group": 0, "title": "%d km", "description": "%d km walked.", "value": "distance", "triggers": [10]}]
            """);
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, itemDefs: ITEM_DEFS, offline: false, facts: Ranks);
        state.CreateAccount(B, "second");
        state.CreateAccount(9, "third");
        using (var con = state.Store.Open(9))
        {
            PlayerRows.AddAchievement(con, 0, 0, 0, 300, O("""{"50": 1700000000, "250": 1700086400, "500": 0}"""));  // a threshold without a time is not earned
            PlayerRows.AddAchievement(con, 1, 0, 1, 30, O("""{"1": 1600000000, "25": 1700086400}"""));
            PlayerRows.AddAchievement(con, 25, 2, 0, 3, O("""{"10": 1700000000000}"""));  // a restored time in milliseconds: the last day there is
        }
        // the other player's own call: the rows of user id 9 (the earn times), ordered by id; an unknown user id none
        Eq("""
            [{"category_id": 0, "group_id": 0, "id": 0, "triggers": {"50": 1700000000, "250": 1700086400, "500": 0}, "value": 300},
             {"category_id": 0, "group_id": 1, "id": 1, "triggers": {"1": 1600000000, "25": 1700086400}, "value": 30},
             {"category_id": 2, "group_id": 0, "id": 25, "triggers": {"10": 1700000000000}, "value": 3}]
            """, JsonNode.Parse(state.AchievementReply(B, "user_id=9"u8.ToArray())!));
        Assert.Null(state.AchievementReply(B, "user_id=424242"u8.ToArray()));
        // animals: 4 of 5 thresholds (the deer's definition is complete, the total's lacks 500); the walk's one has a time in milliseconds
        Eq("""
            {"categories": [{"name": "Animals", "level": 1, "done": 4, "total": 5}, {"name": "Exploration", "level": 1, "done": 1, "total": 1}],
             "unlocked": 5,
             "recent": [{"title": "10 km", "description": "10 km walked.", "date": "9999-12-31"},
                        {"title": "250 Harvests", "description": "250 animals harvested.", "date": "2023-11-15"},
                        {"title": "25 Mule Deer", "description": "25 Mule Deer harvested.", "date": "2023-11-15"},
                        {"title": "50 Harvests", "description": "50 animals harvested.", "date": "2023-11-14"},
                        {"title": "1 Mule Deer", "description": "1 Mule Deer harvested.", "date": "2020-09-13"}]}
            """, state.PublicAchievements(9));
        Eq("""{"categories": [{"name": "Animals", "level": 0, "done": 0, "total": 5}, {"name": "Exploration", "level": 0, "done": 0, "total": 1}], "unlocked": 0, "recent": []}""",
           state.PublicAchievements(B));
    }

    [Fact]
    public void TestTheSchemaOfVersion6RecountsTheClosedHuntsForTheirPickups()
    {
        var file = Path.Combine(tmp, "v6.db");
        var store = new Store(file);
        store.CreateAccount(5, "five", new JsonObject());
        using (var con = store.Open(5))
        {
            con.Exec("INSERT INTO expeditions (account_id, start_ts, end_ts, stats) VALUES ($a, 1, 2, '{}')");
            con.Exec("INSERT INTO expeditions (account_id, start_ts, stats) VALUES ($a, 3, '{}')");
            con.Exec("ALTER TABLE harvests DROP COLUMN hidden");
            con.Exec("DROP TABLE loadouts");
            con.Exec("DROP TABLE wallet_log");
            con.Exec("DROP TABLE missions");
            con.Exec("DROP TABLE mission_prefs");
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 6");
        }
        using var check = new Store(file).Open(5);
        Assert.Equal(["", "{}"], check.Rows("SELECT COALESCE(stats, '') FROM expeditions WHERE account_id = $a ORDER BY id").Select(r => (string)r[0]));
    }

    [Fact]
    public void TestAVariantAntlerAndAMalformedPickupCountForTheirTypeOrNothing()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.HeartbeatReply(B, Events(
            O("""{"event": 36, "collectable_id": 1, "species_id": 105, "double_antler": 0, "x": 1, "y": 2, "z": 3}"""),
            O("""{"event": 24, "collectable_id": 2, "type": "x", "x": 1, "y": 2, "z": 3, "scale": 100}"""),
            O("""{"event": 24, "collectable_id": 3, "x": 1, "y": 2, "z": 3, "scale": 100}""")));
        state.Disconnect(B, "{}"u8.ToArray());
        Eq("""{"2005": {"collected": 1}}""", Total(B)["data"]!["collectables"]);
        Assert.NotNull(Reply(state.ExpeditionReply(B, "{}"u8.ToArray()))["stats"]);
        Eq("1", Reply(state.ExpeditionListReply(B, "{}"u8.ToArray(), "limit=10&offset=0"u8.ToArray()))["expeditions"]![0]!["collectables"]);
    }

    [Fact]
    public void TestAPickupAfterItsHuntClosedStillCountsAndAHugeTypeCountsNothing()
    {
        state = Online();
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.Disconnect(B, "{}"u8.ToArray());
        state.HeartbeatReply(B, Events(
            O("""{"event": 24, "collectable_id": 1, "type": 9, "x": 1, "y": 2, "z": 3, "scale": 100}"""),
            O("""{"event": 24, "collectable_id": 2, "type": 100000000000000000000000000000, "x": 1, "y": 2, "z": 3, "scale": 100}""")));
        Eq("""{"9": {"collected": 1}}""", Total(B)["data"]!["collectables"]);
    }

    [Fact]
    public void TestAHuntsCloseAwardsTheAchievementsItsStatisticsReach()
    {
        var connect = CONNECT;
        connect["achievements"] = JsonNode.Parse("""
            [{"id": 0, "category": 0, "group": 0, "value": "animal.*.ethical_kills", "triggers": [50]},
             {"id": 1, "category": 0, "group": 1, "value": "animal.MULE_DEER.ethical_kills", "triggers": [1, 25]},
             {"id": 13, "category": 1, "group": 0, "value": "weapon.TEST_RIFLE.*.ethical_kills", "triggers": [1, 25]},
             {"id": 25, "category": 2, "group": 0, "value": "distance", "triggers": [10, 42]},
             {"id": 26, "category": 2, "group": 1, "value": "duration", "triggers": [1, 24]},
             {"id": 40, "category": 0, "group": 9, "value": "animal.[MULE_DEER,COTTONTAIL_RABBIT].kills", "triggers": [1]},
             {"id": 41, "category": 0, "group": 10, "value": "animal.COTTONTAIL_RABBIT.kills", "triggers": [1]}]
            """);
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, itemDefs: ITEM_DEFS, offline: false, facts: Ranks);
        double now = 1700000000;
        state.Clock = () => now;
        state.CreateAccount(B, "second");
        using (var con = state.Store.Open(B))  // earned before, as a restore stores it
        {
            PlayerRows.AddAchievement(con, 1, 0, 1, 5, O("""{"1": 1600000000}"""));
            PlayerRows.AddAchievement(con, 40, 0, 9, 0, O("""{"1": 0}"""));  // a threshold stored without a time gets one when it is reached
        }
        state.ConnectReply(B);
        Kill(B, 81, 1, 10);
        Assert.Equal(2, JsonNode.Parse(state.AchievementReply(B, "user_id=8"u8.ToArray())!)!.AsArray().Count);  // mid-hunt: nothing new yet
        using (var con = state.Store.Open(B))  // a hunt of 3700 s: past one hour
            con.Exec("UPDATE expeditions SET start_ts = start_ts - 3700 WHERE account_id = $a AND end_ts IS NULL");
        state.Disconnect(B, """{"distance_travelled": 12000}"""u8.ToArray());
        // the restored time and its larger value stay; the weapon, the distance (12 km), the duration (1 h) and the listed species
        // are earned now; the total keeps its count without a time (50 not reached); the rabbit has nothing
        var earned = """
            [{"category_id": 0, "group_id": 0, "id": 0, "value": 1},
             {"category_id": 0, "group_id": 1, "id": 1, "triggers": {"1": 1600000000}, "value": 5},
             {"category_id": 1, "group_id": 0, "id": 13, "triggers": {"1": 1700000000}, "value": 1},
             {"category_id": 2, "group_id": 0, "id": 25, "triggers": {"10": 1700000000}, "value": 12},
             {"category_id": 2, "group_id": 1, "id": 26, "triggers": {"1": 1700000000}, "value": 1},
             {"category_id": 0, "group_id": 9, "id": 40, "triggers": {"1": 1700000000}, "value": 1}]
            """;
        Eq(earned, JsonNode.Parse(state.AchievementReply(B, "user_id=8"u8.ToArray())!));
        Eq(earned, Reply(state.ConnectReply(B))["user_stats"]!["achievement_stats"]);
        // a later close keeps every time and counts on
        now += 100;
        Kill(B, 82, 1, 10);
        state.Disconnect(B, "{}"u8.ToArray());
        Eq("""{"category_id": 1, "group_id": 0, "id": 13, "triggers": {"1": 1700000000}, "value": 2}""",
           JsonNode.Parse(state.AchievementReply(B, "user_id=8"u8.ToArray())!)![2]);
        Assert.Null(state.AchievementReply(B, "user_id=7"u8.ToArray()));  // the recorded player's user id: not a player of this server
    }
}
