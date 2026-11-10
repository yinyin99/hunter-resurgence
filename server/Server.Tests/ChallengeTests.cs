using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Challenges on a community server, with made-up definitions: the rules a recorded list holds, the completions the hunt's events make, the replies and the restored rows.</summary>
public class ChallengeTests : StateTestBase
{
    // species 2 is a test fox, 17 a test hare (permitted ammunition 241 and 14); items 801 and 802 are arrows, 803 a bow; collectable types 7 and 8 are fungi, 9 a rock
    static readonly (int Id, string Type, string Conditions)[] Rules =
    [
        (901, "KILL", "a.texture() == c.texture.ALBINO"),
        (902, "KILL", "e.animalCountWithFilters(c.species.TEST_FOX, seconds=300) >= 3"),
        (903, "KILL", "(e.animalCountWithFilters(c.species.TEST_HARE, same_shot=True) >= 2 or e.animalCountWithFilters(c.species.TEST_FOX, same_shot=True) >= 2) and a.wounds.state({c.state.FLYING, c.state.FLYING_NOT_SPOOKED})"),
        (904, "CONFIRMKILL", "a.ethical() and a.variant() == c.variant.TEST_HARE and a.range('maximum') >= 250000"),
        (905, "CONFIRMKILL", "a.ethical() and a.species() == c.species.TEST_FOX and e.animalCount(300) >= 2"),
        (906, "SHOT_HIT", "a.wounds.count() >= 3 and a.wounds.ammo(set([c.item.TEST_ARROW_A, c.item.TEST_ARROW_B]))"),
        (907, "CLUE", "e.trackType() == c.clue.BLOODTRAIL and e.individualCount() >= 3"),
        (908, "COLLECTABLE", "e.collectableCountWithFilters(collectable_category=c.collectableCategory.TEST_FUNGUS) >= 2"),
        (909, "CONFIRMKILL", "a.wounds.weapon({c.item.TEST_BOW}) and a.wounds.scope(False) and a.range('minimum') >= 150000 and a.wounds.organ([c.organ.HEART]) and not a.wounds.scope(True)"),
        (910, "KILL", "a.texture() == c.texture.ALBINO or a.species() == c.species.NO_SUCH_SPECIES"),  // not understood: the whole objective is left out
        (911, "KILL", "a.texture("),
        (912, "FLY", "True"),
        (913, "KILL", "True or a.nothing()"),  // a call that is not known: the objective is left out, though `True` would end it
    ];

    const long P = 8;
    const double T0 = 1_800_000_000;  // 2027-01-15 08:00 UTC
    double now = T0;

    static JsonObject Def(int id, string type, string conditions) => new()
    {
        ["id"] = id, ["title"] = $"Test Challenge {id}", ["description"] = "A made-up goal.", ["image"] = "https://photos.test/challenge.png",
        ["update_message"] = $"Challenge: Test {id} completed", ["objectives_hash"] = id.ToString("x32"),
        ["objectives"] = Py.Dumps(new JsonArray(new JsonObject
        {
            ["objective_data"] = new JsonObject { ["id"] = 1, ["conditions"] = conditions, ["title"] = $"Goal {id}", ["update_message"] = $"Goal {id} done" },
            ["event_type"] = type,
        })),
    };

    static JsonArray Definitions() => new(Rules.Select(r => (JsonNode?)Def(r.Id, r.Type, r.Conditions)).ToArray());

    State New(string name, bool offline = false)
    {
        var app = APP;
        foreach (var s in app["species"]!.AsArray().OfType<JsonObject>().Where(s => (long)s["id"]! == 17))
            s["define"] = "TEST_HARE";
        app["species"]!.AsArray().Add(O("""{"id": 2, "name": "Test Fox", "define": "TEST_FOX"}"""));
        foreach (var item in new[] { (801, "TEST_ARROW_A"), (802, "TEST_ARROW_B"), (803, "TEST_BOW") })
            app["items"]!.AsArray().Add(O($$"""{"id": {{item.Item1}}, "name": "Test {{item.Item2}}", "define": "{{item.Item2}}"}"""));
        app["collectables"] = JsonNode.Parse("""[{"id": 7, "cat": "TEST_FUNGUS"}, {"id": 8, "cat": "TEST_FUNGUS"}, {"id": 9, "cat": "TEST_ROCK"}]""");
        var made = new State(Path.Combine(tmp, name), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: offline, challenges: Definitions());
        made.Clock = () => now;
        return made;
    }

    public ChallengeTests()
    {
        state = New("state.db");
        state.CreateAccount(P, "player");  // the recorded player's own account is never shown: a public read needs another one
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };  // pinned, so no test reads files
    }

    JsonObject Rows(long account = P) => (JsonObject)Reply(state.ChallengesReply(account.ToString()));

    /// <summary>The ids of the account's completed challenges.</summary>
    string Done(long account = P) => string.Join(",", Rows(account).Select(kv => kv.Key).Order(StringComparer.Ordinal));

    /// <summary>A heartbeat of the player with these events; its updates.</summary>
    new JsonArray Hb(params JsonNode[] events) => Reply(state.HeartbeatReply(P, Events(events)))["updates"]!.AsArray();

    static string Kinds(JsonArray updates) => string.Join(",", updates.Select(u => (long)u!["update"]!));

    static JsonNode Spawn(int individual, long species, int texture = 0) =>
        O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": 0, "weight_grams": 50000, "texture": {{texture}}}""");

    static JsonNode Hit(int individual, int shot, int ammo = 10, int weapon = 5, int scope = 2, long organ = 2, int cms = 1000, int animalState = 3) =>
        O($$"""{"event": 1, "individual": {{individual}}, "shot_id": {{shot}}, "distance_cms": {{cms}}, "organ1": 1, "organ2": {{organ}}, "weapon_id": {{weapon}}, "ammunition_id": {{ammo}}, "scope_id": {{scope}}, "animal_state": {{animalState}}}""");

    static JsonNode Death(int individual, int animalState = 3) => O($$"""{"event": 2, "individual": {{individual}}, "animal_state": {{animalState}}}""");

    static JsonNode Harvest(int individual, long species) => O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}}""");

    static JsonNode Track(int individual, int type) => O($$"""{"event": 12, "individual": {{individual}}, "track_type": {{type}}}""");

    static JsonNode Pickup(int collectable, int type) => O($$"""{"event": 24, "collectable_id": {{collectable}}, "type": {{type}}, "x": 1, "y": 2, "z": 3, "scale": 1}""");

    [Fact]
    public void TestWithoutDefinitionsOrOnALocalServerNothingIsTracked()
    {
        var none = new State(Path.Combine(tmp, "none.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        none.CreateAccount(P, "player");
        Eq("{}", Reply(none.ChallengesReply(P.ToString())));
        none.HeartbeatReply(P, Events(Spawn(1, 2, 2), Death(1)));
        Eq("{}", Reply(none.ChallengesReply(P.ToString())));
    }

    [Fact]
    public void TestAKillOfAnAlbinoCompletesTheChallengeWithABadgeAndNoPay()
    {
        Assert.Equal("", Done());
        var (gm, em) = (state.Wallet(P)["gm"]!.GetValue<double>(), state.Wallet(P)["em"]!.GetValue<double>());
        Hb(Spawn(1, 2, texture: 1), Death(1));  // a common one
        Assert.Equal("", Done());
        Assert.Empty(Hb());
        Assert.Empty(Hb(Spawn(2, 2, texture: 2), Death(2)));  // nothing is paid: no update 15
        Assert.Equal("901", Done());
        var row = Rows()["901"]!;
        Eq($$"""[901, {{P}}, 1, "2027-01-15", "{{901.ToString("x32")}}"]""", T(row["challenge_id"], row["user_id"], row["completed"], row["challenge_date"], row["objectives_hash"]));
        Assert.True((long)row["id"]! > 0);
        var objectives = JsonNode.Parse((string)row["objectives"]!)!.AsArray();
        Eq("""{"id": 1, "conditions": "a.texture() == c.texture.ALBINO", "title": "Goal 901", "update_message": "Goal 901 done", "completed": true}""", objectives[0]!["objective_data"]);
        Eq("\"KILL\"", objectives[0]!["event_type"]);
        var toasts = Hb();  // the announcement
        Assert.Equal("3", Kinds(toasts));
        Assert.Equal("Challenge: Test 901 completed", (string)toasts[0]!["text"]!);
        Eq("[100, -1, 4294967040]", T(toasts[0]!["type"], toasts[0]!["timer"], toasts[0]!["colour"]));
        Assert.Empty(Hb());
        Eq($"[{gm}, {em}]", T(state.Wallet(P)["gm"], state.Wallet(P)["em"]));
        Assert.Empty(state.WalletPage(P, "gm", 0));
        // a completed challenge stays: another kill changes neither the row nor the announcements
        var before = Rows();
        Hb(Spawn(3, 17, texture: 2), Death(3));
        Eq(before, Rows());
        Assert.Empty(Hb());
    }

    [Fact]
    public void TestThreeKillsOfASpeciesWithinTheWindowCompleteIt()
    {
        void Kill(int individual) => Hb(Spawn(individual, 2), Death(individual));
        Kill(10);
        now += 100;
        Kill(11);
        now += 250;  // 350 s after the first: it left the window
        Kill(12);
        Assert.DoesNotContain("902", Done());
        now += 10;
        Kill(13);  // 100, 350 and 360 s: three within 300 s
        Assert.Contains("902", Done());
        Assert.Contains(Hb(), u => (string?)u!["text"] == "Challenge: Test 902 completed");
    }

    [Fact]
    public void TestKillsOfOneShotCountTogetherAndOnlyWhenTheyFlew()
    {
        // two hares die from one shot but on the ground, then two from different shots in the air
        Hb(Spawn(20, 17), Spawn(21, 17), Hit(20, 3, ammo: 241), Hit(21, 3, ammo: 241), Death(20), Death(21));
        Hb(Spawn(22, 17), Spawn(23, 17), Hit(22, 4, ammo: 241, animalState: 6), Hit(23, 5, ammo: 241, animalState: 6), Death(22, 6), Death(23, 6));
        Assert.DoesNotContain("903", Done());
        Hb(Spawn(24, 17), Spawn(25, 17), Hit(24, 6, ammo: 241, animalState: 11), Hit(25, 6, ammo: 241, animalState: 11), Death(24, 11), Death(25, 11));
        Assert.Contains("903", Done());
    }

    [Fact]
    public void TestAHarvestFromFarAwayWithAPermittedAmmunitionCompletesIt()
    {
        // too short, then from far away with an ammunition the species does not permit, then permitted: the longest hit counts
        Hb(Spawn(30, 17), Hit(30, 1, ammo: 241, cms: 20000), Harvest(30, 17));
        Hb(Spawn(31, 17), Hit(31, 1, ammo: 10, cms: 30000), Harvest(31, 17));
        Assert.DoesNotContain("904", Done());
        Hb(Spawn(32, 17), Hit(32, 1, ammo: 241, cms: 1000), Hit(32, 2, ammo: 14, cms: 30000), Harvest(32, 17));
        Assert.Contains("904", Done());
    }

    [Fact]
    public void TestHarvestsOfASpeciesWithinTheWindowAreCounted()
    {
        void Take(int individual) => Hb(Spawn(individual, 2), Harvest(individual, 2));
        Take(40);
        now += 400;
        Take(41);
        Assert.DoesNotContain("905", Done());
        now += 50;
        Take(42);  // 41 and 42 are within 300 s
        Assert.Contains("905", Done());
    }

    [Fact]
    public void TestSeveralHitsOnOneAnimalWithTheListedAmmunitionCompleteIt()
    {
        Hb(Spawn(50, 2), Hit(50, 1, ammo: 801), Hit(50, 2, ammo: 10), Hit(50, 3, ammo: 802));  // three hits, one with another ammunition
        Assert.DoesNotContain("906", Done());
        Hb(Spawn(51, 2), Hit(51, 1, ammo: 801), Hit(51, 2, ammo: 802));
        Assert.DoesNotContain("906", Done());
        Hb(Hit(51, 3, ammo: 801));
        Assert.Contains("906", Done());
    }

    [Fact]
    public void TestBloodTracksOfOneAnimalAreCounted()
    {
        Hb(Spawn(60, 2), Spawn(61, 2), Track(60, 0), Track(60, 3), Track(61, 0), Track(60, 0), Track(60, 3));
        Assert.DoesNotContain("907", Done());
        Hb(Track(60, 0));
        Assert.Contains("907", Done());
    }

    [Fact]
    public void TestPickupsOfACategoryAreCountedOnceEach()
    {
        Hb(Pickup(1, 7), Pickup(1, 7), Pickup(2, 9));  // one fungus (the game repeats an event), one rock
        Assert.DoesNotContain("908", Done());
        Hb(Pickup(3, 8));
        Assert.Contains("908", Done());
    }

    [Fact]
    public void TestWeaponScopeOrganAndShortestHitOfAHarvest()
    {
        var shots = new[] { Hit(70, 1, weapon: 803, scope: -1, organ: 1, cms: 20000), Hit(71, 1, weapon: 803, scope: 2, organ: 1, cms: 20000),
                            Hit(72, 1, weapon: 803, scope: -1, organ: 1, cms: 10000), Hit(73, 1, weapon: 803, scope: -1, organ: 2, cms: 20000), Hit(74, 1, weapon: 5, scope: -1, organ: 1, cms: 20000) };
        var announced = new List<JsonNode?>();
        for (var i = 1; i < shots.Length; i++)  // each of these lacks one of them
        {
            announced.AddRange(Hb(Spawn(70 + i, 2), shots[i], Harvest(70 + i, 2)));
            Assert.DoesNotContain("909", Done());
        }
        announced.AddRange(Hb(Spawn(70, 2), shots[0], Harvest(70, 2)));
        announced.AddRange(Hb());
        Assert.Contains("909", Done());
        Assert.Single(announced, u => (string?)u!["text"] == "Challenge: Test 909 completed");
    }

    [Fact]
    public void TestOnlyTheAnimalsOfTheRulesCountAndAnUnreadableConditionNeverCompletes()
    {
        Hb(Spawn(80, 2, texture: 2), Death(80));
        Assert.Equal("901", Done());  // 910 would hold if its unknown species were read; 911, 912 and the other kills do not
        Hb(Hit(80, 1));
        Hb(Spawn(81, 17), Death(81));
        Assert.Equal("901", Done());
    }

    [Fact]
    public void TestAnyAccountsChallengesAreReadAndAnUnknownBannedOrRecordedOneHasNone()
    {
        state.CreateAccount(9, "other");
        Hb(Spawn(1, 2, texture: 2), Death(1));
        Assert.Equal("901", Done(P));
        Assert.Equal("", Done(9));
        Assert.Equal(P, (long)Rows(P)["901"]!["user_id"]!);
        using (var con = state.Store.Open())
            con.Exec("UPDATE accounts SET banned = 1 WHERE id = ?", P);
        foreach (var none in new[] { "999", "", "x", ME.ToString(), P.ToString() })  // none, no number, the recorded player's, banned
            Eq("{}", Reply(state.ChallengesReply(none)));
    }

    // ---- restored rows

    const string Saved = """
        {"901": {"id": 5, "user_id": 1001, "challenge_id": 901, "objectives": "[{\"objective_data\": {\"id\": 1, \"completed\": true}, \"event_type\": \"KILL\"}]", "objectives_hash": "oldhash", "completed": true, "challenge_date": "2024-02-03"},
         "902": {"id": 6, "user_id": 1001, "challenge_id": 902, "objectives": "[]", "objectives_hash": "oldhash", "completed": 0, "challenge_date": null},
         "903": {"id": 7, "user_id": 1001, "challenge_id": 903, "objectives": 5, "objectives_hash": null, "completed": 1, "challenge_date": 20240203},
         "x": {"completed": 1}, "904": 7}
        """;

    [Fact]
    public void TestARestoreCarriesTheCompletedChallengesOverAndTheNextRestoreReplacesThem()
    {
        var saved = TestZip.Own().Reply("Challenge_get_user_challenges__user_id=1001.json", "/v1/Challenge/get_user_challenges", Saved, ("user_id", "1001"));
        var account = Restorer.Import(state, saved.Write(tmp, "a.zip"), null, tmp).Account;
        Assert.Equal("901,903", Done(account));  // completed as true or 1; 902 was not, x is no challenge, 904 is no row
        var row = Rows(account)["901"]!;
        Eq("""[1, "oldhash", "2024-02-03"]""", T(row["completed"], row["objectives_hash"], row["challenge_date"]));
        Eq("""[{"objective_data": {"id": 1, "completed": true}, "event_type": "KILL"}]""", JsonNode.Parse((string)row["objectives"]!));
        Eq("""["[]", "", null]""", T(Rows(account)["903"]!["objectives"], Rows(account)["903"]!["objectives_hash"], Rows(account)["903"]!["challenge_date"]));  // what is no text is kept as none
        // it stays completed: the same kill announces nothing and changes nothing
        var before = Rows(account);
        state.HeartbeatReply(account, Events(Spawn(1, 2, texture: 2), Death(1)));
        Eq(before, Rows(account));
        Assert.DoesNotContain(Reply(state.HeartbeatReply(account, Events()))["updates"]!.AsArray(), u => (string?)u!["text"] == "Challenge: Test 901 completed");
        Restorer.Import(state, TestZip.Own().Write(tmp, "b.zip"), account, tmp);  // a zip without the page
        Assert.Equal("", Done(account));
    }

    [Fact]
    public void TestTheSchemaOfVersion13ReadsTheRestoredChallengesOfEveryAccount()
    {
        var file = Path.Combine(tmp, "v13.db");
        var store = new Store(file);
        store.CreateAccount(5, "h5", new JsonObject(), seed: con => PlayerRows.SetExtra(con, "Challenge_get_user_challenges__user_id=5.json", JsonNode.Parse(Saved)));
        store.CreateAccount(6, "h6", new JsonObject(), seed: con => PlayerRows.SetExtra(con, "Challenge_get_user_challenges__user_id=6.json", JsonNode.Parse("[1, 2]")));
        using (var con = store.Open())
        {
            con.Exec("DROP TABLE challenges");
            con.Exec("ALTER TABLE events DROP COLUMN ts");
            con.Exec("PRAGMA user_version = 13");
        }
        var migrated = new Store(file);
        string Rows(long account)
        {
            using var con = migrated.Open(account);
            return string.Join(";", con.Rows("SELECT challenge_id, objectives_hash, challenge_date FROM challenges WHERE account_id = $a ORDER BY challenge_id").Select(r => string.Join("|", r)));
        }
        Assert.Equal("901|oldhash|2024-02-03;903||", Rows(5));
        Assert.Equal("", Rows(6));  // a file that is not an object
        using var check = migrated.Open(5);
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
        Assert.Empty(check.Rows("SELECT ts FROM events WHERE account_id = $a"));
    }
}

/// <summary>Challenge/get_user_challenges over HTTP on a community server.</summary>
public class ChallengeApiTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        var list = JsonNode.Parse("""[{"id": 901, "update_message": "Challenge: Test completed", "objectives_hash": "h", "objectives": "[{\"objective_data\": {\"id\": 1, \"conditions\": \"a.texture() == c.texture.ALBINO\"}, \"event_type\": \"KILL\"}]"}]""")!.AsArray();
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, challenges: list);
        state.CreateAccount(8, "player");
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    async Task<JsonNode?> Call(string method, string body) => method == "GET"
        ? JsonNode.Parse((await server.SendAsync("GET", "api.test", $"{Replay.USER_CHALLENGES}?{body}", [], [])).Body)
        : JsonNode.Parse((await server.SendAsync("POST", "api.test", Replay.USER_CHALLENGES, [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes(body))).Body);

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task TestTheCallAnswersFromTheAccountsRowsWithoutATokenAndAnUnknownUserGetsAnEmptyReply(string method)
    {
        Eq("{}", await Call(method, "user_id=8"));
        state.HeartbeatReply(8, Events(O("""{"event": 7, "individual": 1, "species": 1, "gender": 0, "weight_grams": 50000, "texture": 2}"""), O("""{"event": 2, "individual": 1, "animal_state": 3}""")));
        var reply = (await Call(method, "user_id=8"))!;
        Eq("""[901, 8, 1, "h"]""", T(reply["901"]!["challenge_id"], reply["901"]!["user_id"], reply["901"]!["completed"], reply["901"]!["objectives_hash"]));
        Eq("{}", await Call(method, "user_id=999"));
    }

    [Fact]
    public async Task TestALocalServerTracksNothingAndLeavesTheCallToItsRecordings()
    {
        var local = new State(Path.Combine(tmp, "local.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);
        await using var other = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, local, tmp), tmp);
        var reply = await other.SendAsync("POST", "api.test", Replay.USER_CHALLENGES, [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes("user_id=8"));
        Assert.Null(JsonNode.Parse(reply.Body));  // a miss: no recording here
    }
}
