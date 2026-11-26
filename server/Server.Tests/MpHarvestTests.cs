using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;

using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>In a multiplayer game a harvest counts for the player who shot the animal last, and only for that player (#857): `State.CreditOf` and `CreditHarvest`, and the cleanup of past harvests
/// (`admin mp-harvests`, `State.MoveMpHarvests`). The players are invented; the mule deer buck scores 168.8186 and pays round(66.4) gm$, as in `StateTests`.</summary>
public class MpHarvestTests : StateTestBase
{
    const long HOST = 11, GUEST = 12, THIRD = 13;
    const string Start = "start=101&loctype=LODGE&reserve=2&time=540&loadout_id=0";
    readonly double now = 1_000_000;

    public MpHarvestTests()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false) { Clock = () => now };
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest"), (THIRD, "third") })
            state.CreateAccount(id, handle);
    }

    JsonArray Beat(long who, params JsonNode[] events) => Reply(state.HeartbeatReply(who, Events(events)))["updates"]!.AsArray();

    /// <summary>HOST hosts a game and `guests` join it.</summary>
    void Game(params long[] guests)
    {
        state.HostGame(HOST, "host-session", Encoding.UTF8.GetBytes($"{Start}&name=Camp&max_players=3"));
        Beat(HOST, O("""{"event": 29, "port": 32303, "guid": "9007199254740993", "color": "c73378"}"""));
        var id = (long)Reply(state.NetworkListReply())[0]!["id"]!;
        foreach (var guest in guests)
            Assert.Null(state.JoinGame(guest, Encoding.UTF8.GetBytes($"start=101&loctype=LODGE&network_id={id}&loadout_id=0")).Error);
    }

    static JsonNode Spawn(int individual) =>
        O($$"""{"event": 7, "individual": {{individual}}, "species": 1, "gender": 0, "weight_grams": 98120, "texture": 1}""");

    static JsonNode Hit(int individual, int distance = 8250, int species = 2) =>
        O($$"""{"event": 1, "individual": {{individual}}, "species": {{species}}, "distance_cms": {{distance}}, "organ1": 17, "organ2": 8388610, "weapon_id": 5, "ammunition_id": 10, "animal_state": 3}""");

    static JsonObject MuleAntler(int beam, int[] h, int[] g)
    {
        var antler = new JsonObject { ["circumference_h1"] = h[0], ["circumference_h2"] = h[1], ["circumference_h3"] = h[2], ["circumference_h4"] = h[3] };
        antler["points"] = new JsonArray(new[] { new JsonObject { ["length"] = beam, ["type"] = 0 } }
            .Concat(g.Select((v, i) => new JsonObject { ["length"] = v, ["type"] = i + 1 })).ToArray<JsonNode?>());
        return antler;
    }

    /// <summary>What the player who shoots the buck sends.</summary>
    static JsonNode[] Shot(int individual, int distance = 8250) => [Spawn(individual), Hit(individual, distance)];

    /// <summary>What the player who skins the buck sends: its spawn (unless he sent it with his shot), its trophy measurement and the harvest (the seed makes each event 3 unique, as a real one is).</summary>
    static JsonNode[] Skinned(int individual, int seed = 1, bool spawn = true)
    {
        var trophy = O($$"""{"event": 22, "individual": {{individual}}, "greatest_spread": 640, "tip_to_tip_spread": 600, "inside_spread_of_main_beams": 560}""");
        trophy["right_antler"] = MuleAntler(750, [125, 125, 100, 100], [125, 500, 375, 250]);
        trophy["left_antler"] = MuleAntler(800, [150, 125, 125, 100], [125, 450, 375, 250]);
        var harvest = O($$"""{"event": 3, "individual": {{individual}}, "extended_species": 1, "trophy_integrity": 100000, "antlers_seed": {{seed}}}""");
        return spawn ? [Spawn(individual), trophy, harvest] : [trophy, harvest];
    }

    long Count(long account, string table, string where = "1")
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar($"SELECT COUNT(*) FROM {table} WHERE account_id = $a AND {where}")!;
    }

    double Gm(long account) => Py.D(state.Wallet(account)["gm"]);

    JsonArray KillsOf(long account) => Reply(state.ExpeditionReply(account, "{}"u8.ToArray()))["kills"]!.AsArray();

    static bool Paid(JsonArray updates, double balance) => updates.Any(u => Py.Eq(u!["update"], 15) && Py.D(u["amount"]) == balance);

    [Fact]
    public void TestTheHarvestOfAnAnimalAnotherPlayerShotIsThatPlayersAndTheHarvesterGetsNothing()
    {
        Game(GUEST);
        Beat(GUEST, Shot(460));
        var mine = Beat(HOST, Skinned(460));
        Assert.DoesNotContain(mine, u => Py.Eq(u!["update"], 15));
        Assert.Empty(Beat(HOST));  // no update 6 either
        Assert.Equal((0L, 0L, 33.0), (Count(HOST, "kills"), Count(HOST, "harvests"), Gm(HOST)));
        Assert.Equal(0L, Count(HOST, "events", "event = 3"));  // not in the harvester's events: no statistic, no mission replay
        Assert.Null(state.HarvestReply(HOST, 1));

        var next = Beat(GUEST);  // the shooter's running hunt gets the score and the gold with its next heartbeat
        var score = Assert.Single(next, u => Py.Eq(u!["update"], 6))!;
        Eq("""[1, 168.8186, 66.4]""", T(score["trophy_shot"], score["score"], score["css"]));
        Assert.True(Paid(next, 99));
        Assert.Equal((1L, 1L, 99.0), (Count(GUEST, "kills"), Count(GUEST, "harvests"), Gm(GUEST)));
        Eq("168.8186", Reply(state.HarvestReply(GUEST, 1)!)["score"]);
        var kill = Assert.Single(KillsOf(GUEST))!;
        var hit = Assert.Single(kill["hits"]!.AsArray())!;  // the shooter's weapon and distance are on the score sheet
        Assert.Equal((82500.0, 5.0, 10.0), (Py.D(hit["distance"]), Py.D(hit["weapon_id"]), Py.D(hit["ammo_id"])));
        Assert.Empty(KillsOf(HOST));
    }

    [Fact]
    public void TestAHarvestOfAnAnimalTheHarvesterShotLastIsUnchanged()
    {
        Game(GUEST);
        Beat(GUEST, Shot(460));
        var updates = Beat(HOST, [.. Shot(460, 5000), .. Skinned(460, spawn: false)]);  // the host's hit is the newest
        Assert.True(Paid(updates, 99));
        Assert.Equal((1L, 99.0, 0L, 33.0), (Count(HOST, "kills"), Gm(HOST), Count(GUEST, "kills"), Gm(GUEST)));
        Assert.Equal(50000.0, Py.D(Assert.Single(KillsOf(HOST)[0]!["hits"]!.AsArray())!["distance"]));
    }

    [Fact]
    public void TestWithTwoShootersTheLastOneWins()
    {
        Game(GUEST);
        Beat(GUEST, Shot(460, 8250));
        Beat(HOST, Shot(460, 5000));  // newer
        Beat(GUEST, Skinned(460));  // the guest skins it, the host shot it last
        Assert.Equal((1L, 99.0, 0L, 33.0), (Count(HOST, "kills"), Gm(HOST), Count(GUEST, "kills"), Gm(GUEST)));
        Beat(HOST);
        Assert.Equal(50000.0, Py.D(Assert.Single(KillsOf(HOST)[0]!["hits"]!.AsArray())!["distance"]));
    }

    [Fact]
    public void TestASecondHarvestOfTheSameAnimalIsIgnored()
    {
        Game(GUEST);
        Beat(GUEST, Shot(460));
        Beat(HOST, Skinned(460, 1));  // the guest's
        Beat(HOST, Skinned(460, 2));  // the host skins it again
        var third = Beat(GUEST, Skinned(460, 3));  // and the guest too
        Assert.Equal((1L, 99.0, 1L, 1L), (Count(GUEST, "kills"), Gm(GUEST), Count(GUEST, "events", "event = 3"), Count(GUEST, "harvests")));
        Assert.Equal((0L, 33.0, 0L), (Count(HOST, "kills"), Gm(HOST), Count(HOST, "events", "event = 3")));
        Assert.Equal(1, third.Count(u => Py.Eq(u!["update"], 6)));  // the moved harvest's, delivered once
        // a new animal is a new harvest
        Beat(GUEST, Shot(461));
        Beat(HOST, Skinned(461, 4));
        Assert.Equal(2L, Count(GUEST, "kills"));
    }

    [Fact]
    public void TestAnAnimalNobodyHitIsTheHarvestersAndSoIsOneHitAsAnotherSpecies()
    {
        Game(GUEST);
        Beat(GUEST, Shot(460));
        Beat(HOST, Skinned(461));  // found dead
        Assert.Equal((1L, 0L), (Count(HOST, "kills"), Count(GUEST, "kills")));
        Beat(GUEST, Spawn(462), Hit(462, species: 17));  // a hit on a rabbit with that individual number
        Beat(HOST, Skinned(462, 2));
        Assert.Equal((2L, 0L), (Count(HOST, "kills"), Count(GUEST, "kills")));
    }

    [Fact]
    public void TestTwoDifferentAnimalsWithOneNumberTwentyMinutesApartBothCountForTheHarvester()
    {
        var clock = now;
        state.Clock = () => clock;
        Game(GUEST);
        Beat(HOST, Skinned(460, 1));  // nobody hit it: the host's
        clock += 1200;
        Beat(GUEST, Spawn(460));  // the number is used by another animal, found dead again
        Beat(HOST, Skinned(460, 2));
        Assert.Equal((2L, 0L, 2L), (Count(HOST, "kills"), Count(GUEST, "kills"), Count(HOST, "events", "event = 3")));
        Assert.Equal(33.0 + 66 * 2, Gm(HOST));
    }

    [Fact]
    public void TestASinglePlayerHarvestIsTheHarvestersEvenWhenAnotherPlayerHitTheSameNumber()
    {
        Game(GUEST);
        state.NoteLaunch(THIRD, Encoding.UTF8.GetBytes(Start));  // a hunt of its own: in no game
        Beat(GUEST, Shot(460));
        var updates = Beat(THIRD, Skinned(460));
        Assert.True(Paid(updates, 99));
        Assert.Equal((1L, 99.0, 0L, 33.0), (Count(THIRD, "kills"), Gm(THIRD), Count(GUEST, "kills"), Gm(GUEST)));
    }

    // ---- the cleanup of the harvests stored before the rule

    /// <summary>A database as the live server had it: two players in one reserve, each in a hunt of his own (no game kept), the host skinning what the guest shot.</summary>
    (State State, Func<long, long> Hs) Past()
    {
        var facts = Facts.Parse("""
            {"format": 1, "ranks": {"cap": 1000, "rows": [{"id": 1, "title": "One", "category": "animal", "species": 1}, {"id": 3, "title": "Five", "category": "weapon", "items": [5]}]}}
            """);
        var past = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: facts);
        state = past;
        past.NoteLaunch(HOST, Encoding.UTF8.GetBytes(Start));
        past.NoteLaunch(GUEST, Encoding.UTF8.GetBytes(Start));
        foreach (var who in new[] { HOST, GUEST })  // a game's players send their port and network id (event 29)
            Beat(who, O("""{"event": 29, "port": 32303, "guid": "9007199254740993", "color": "c73378"}"""));
        return (past, account => past.Store.AccountJson(account)["hunterscore"] is { } h ? Py.L(h) : 0);
    }

    [Fact]
    public void TestTheCleanupMovesTheHarvestToItsShooterAndNoWalletChanges()
    {
        var (past, hs) = Past();
        Beat(GUEST, Shot(460));
        Beat(HOST, Skinned(460));  // the host was paid for the guest's animal
        Beat(GUEST, Shot(461));
        Beat(HOST, [.. Shot(461, 5000), .. Skinned(461, 2, spawn: false)]);  // his own animal, shot after the guest's hit: stays
        Beat(HOST, Skinned(462, 3));  // found dead: stays
        Assert.Equal((3L, 0L, 33.0 + 66 * 3, 33.0), (Count(HOST, "kills"), Count(GUEST, "kills"), Gm(HOST), Gm(GUEST)));
        Assert.Equal((4L, 0L), (hs(HOST), hs(GUEST)));  // three kills of the species and one of the weapon (the host's own hit)

        var dry = past.MoveMpHarvests(apply: false).ToList();
        Assert.Equal(2, dry.Count);
        Assert.StartsWith($"would move kill 1, species 1, from account {HOST} to account {GUEST}, gm$ 66 stay with {HOST}", dry[0]);
        Assert.StartsWith("would move 1 harvests (gm$ 66 stay with the harvesters, none are moved), would reopen 0 mission objectives; kept 0 second harvests", dry[1]);
        Assert.Equal((3L, 0L, 4L, 0L), (Count(HOST, "kills"), Count(GUEST, "kills"), hs(HOST), hs(GUEST)));  // a dry run writes nothing

        var applied = past.MoveMpHarvests(apply: true).ToList();
        Assert.StartsWith($"moved kill 1, species 1, from account {HOST} to account {GUEST}, gm$ 66 stay with {HOST} (kill 1 of account {GUEST})", applied[0]);
        Assert.Equal((2L, 1L), (Count(HOST, "kills"), Count(GUEST, "kills")));
        Assert.Equal((2L, 1L), (Count(HOST, "events", "event = 3"), Count(GUEST, "events", "event = 3")));
        Assert.Null(past.HarvestReply(HOST, 1));
        Eq("168.8186", Reply(past.HarvestReply(GUEST, 1)!)["score"]);
        // both hunterscores are made again: the guest has his buck (the species and his weapon), the host has the two he keeps; no wallet moved
        Assert.Equal((3L, 2L), (hs(HOST), hs(GUEST)));
        Assert.Equal((33.0 + 66 * 3, 33.0), (Gm(HOST), Gm(GUEST)));
        using (var con = state.Store.Open(GUEST))
        {
            var paid = Assert.Single(con.Rows("SELECT amount, after, summary FROM wallet_log WHERE account_id = $a AND summary LIKE 'CSS Reward%'"));
            Assert.Equal((0.0, 33.0, "CSS Reward (Animal Id: 1)"), ((double)paid[0], (double)paid[1], (string)paid[2]));  // a row of 0: `rescore` pays the shooter nothing
        }
        // a record per account, and a second run finds nothing
        Assert.Equal((1L, 1L), (Count(HOST, "mission_rollbacks"), Count(GUEST, "mission_rollbacks")));
        Assert.StartsWith("moved 0 harvests", past.MoveMpHarvests(apply: true).Single());
        Assert.Equal((2L, 1L), (Count(HOST, "kills"), Count(GUEST, "kills")));
    }

    [Fact]
    public void TestTheCleanupFindsASwappedFlockAndLeavesAHuntThatWasNoGame()
    {
        var (past, _) = Past();
        past.NoteLaunch(THIRD, Encoding.UTF8.GetBytes(Start));  // a hunt of its own: it sends no network event
        // two players in one game shot the same flock: the guest three birds, the host one; each skinned the other's
        Beat(GUEST, [.. Shot(460), .. Shot(461), .. Shot(462)]);
        Beat(HOST, Shot(463));
        Beat(HOST, Skinned(460, 1));
        Beat(HOST, Skinned(461, 2));
        Beat(HOST, Skinned(462, 3));
        Beat(GUEST, Skinned(463, 4));
        // an animal the host shot, skinned in a hunt that was no game
        Beat(HOST, Shot(470));
        Beat(THIRD, Skinned(470, 5));
        Assert.Equal((3L, 1L, 1L), (Count(HOST, "kills"), Count(GUEST, "kills"), Count(THIRD, "kills")));

        var lines = past.MoveMpHarvests(apply: false).ToList();
        Assert.Equal(5, lines.Count);
        Assert.Equal(3, lines.Count(l => l.Contains($"from account {HOST} to account {GUEST}")));
        Assert.Equal(1, lines.Count(l => l.Contains($"from account {GUEST} to account {HOST}")));
        Assert.DoesNotContain(lines, l => l.Contains($"account {THIRD}"));
        Assert.StartsWith("would move 4 harvests", lines[^1]);

        past.MoveMpHarvests(apply: true).ToList();
        Assert.Equal((1L, 3L, 1L), (Count(HOST, "kills"), Count(GUEST, "kills"), Count(THIRD, "kills")));  // each has the birds he shot; the hunt that was no game is untouched
    }

    [Fact]
    public void TestAHarvestOfAnotherAnimalThatReusesTheNumberIsNotMoved()
    {
        var (past, _) = Past();
        Beat(GUEST, Shot(460));
        var other = Skinned(460);  // the host's animal 460 is a heavier one: the number was reused
        other[0]["weight_grams"] = 51000;
        Beat(HOST, other);
        var lines = past.MoveMpHarvests(apply: false).ToList();
        Assert.StartsWith("would move 0 harvests", Assert.Single(lines));
        Assert.Equal((1L, 0L), (Count(HOST, "kills"), Count(GUEST, "kills")));
    }

    [Fact]
    public void TestAMovedHarvestsKillIdIsNotGivenAgainWhileItsGmPaymentNamesIt()
    {
        var (past, _) = Past();
        Beat(GUEST, Shot(460));
        Beat(HOST, Skinned(460));  // the host's kill 1, paid
        Assert.StartsWith("moved kill 1,", past.MoveMpHarvests(apply: true).First());
        Assert.Equal(0L, Count(HOST, "kills"));
        Beat(HOST, Skinned(461, 2));  // a new harvest of the host's
        var update = Assert.Single(Beat(HOST), u => Py.Eq(u!["update"], 6))!;
        Assert.Equal(2L, (long)update["kill_id"]!);  // not 1: the payment `CSS Reward (Animal Id: 1)` still names it
        Assert.Equal(2L, (long)Assert.Single(KillsOf(HOST))!["id"]!);
        Assert.Null(past.HarvestReply(HOST, 1));
    }

    [Fact]
    public void TestAMoveThatFailsHalfWayIsUndoneWholeAndTheOthersGoOn()
    {
        var (past, hs) = Past();
        Beat(GUEST, [.. Shot(460), .. Shot(461)]);
        Beat(HOST, Skinned(460));
        Beat(HOST, Skinned(461, 2));
        var before = (Count(HOST, "kills"), Count(GUEST, "kills"), Count(HOST, "events", "event = 3"), hs(HOST), hs(GUEST));
        using (var shared = state.Store.Open())  // the guest's record fails after everything else of the move is written
            shared.Exec($"CREATE TRIGGER boom BEFORE INSERT ON mission_rollbacks WHEN NEW.account_id = {GUEST} AND NEW.record LIKE '%\"kill\": 1,%' BEGIN SELECT RAISE(ABORT, 'boom'); END");
        var lines = past.MoveMpHarvests(apply: true).ToList();
        Assert.Contains(lines, l => l.StartsWith("failed to move kill 1,") && l.Contains("boom"));
        Assert.Contains(lines, l => l.StartsWith("moved kill 2,"));
        Assert.EndsWith("; 1 failed and undone", lines[^1]);
        Assert.Equal((before.Item1 - 1, before.Item2 + 1, before.Item3 - 1), (Count(HOST, "kills"), Count(GUEST, "kills"), Count(HOST, "events", "event = 3")));  // only kill 2 moved
        Assert.NotNull(past.HarvestReply(HOST, 1));  // kill 1 is whole where it was
        Assert.Equal(1L, Count(GUEST, "wallet_log", "summary LIKE 'CSS Reward%'"));  // the one of the kill that moved; the failed one left no row
    }

    [Fact]
    public void TestAHarvestTheShooterCannotBeWrittenToStaysWithItsHarvesterAndNothingIsLost()
    {
        Game(GUEST);
        Beat(GUEST, Shot(460));
        using (var shared = state.Store.Open())
            shared.Exec($"CREATE TRIGGER boom BEFORE INSERT ON harvests WHEN NEW.account_id = {GUEST} BEGIN SELECT RAISE(ABORT, 'boom'); END");
        var updates = Beat(HOST, Skinned(460));
        Assert.True(Paid(updates, 99));  // the harvester has it, with its gold
        Assert.Equal((1L, 99.0, 0L, 33.0), (Count(HOST, "kills"), Gm(HOST), Count(GUEST, "kills"), Gm(GUEST)));
        Assert.Equal((1L, 0L), (Count(HOST, "events", "event = 3"), Count(GUEST, "events", "event = 3")));
        Assert.Single(Beat(HOST), u => Py.Eq(u!["update"], 6));
        Assert.Empty(Beat(GUEST));
    }

    [Theory]
    [InlineData(null, 77, true)]  // the shooter never saw it: the harvester's sighting, a possessed coat, judges his normal round (#582)
    [InlineData(null, 1, false)]
    [InlineData(1, 77, false)]  // the shooter's own sighting wins
    [InlineData(77, 1, true)]
    public void TestACreditedHarvestIsJudgedByTheSpawnItWasScoredWith(int? shooterCoat, int harvesterCoat, bool disqualified)
    {
        var connect = CONNECT;
        ((JsonArray)connect["ethical"]!)[1] = new JsonArray(10);  // the mule deer permits the shot's ammunition 10
        state = new State(Path.Combine(tmp, "rule.db"), Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, offline: false) { Clock = () => now };
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest") })
            state.CreateAccount(id, handle);
        Game(GUEST);
        static JsonNode Seen(int coat) => O($$"""{"event": 7, "individual": 460, "species": 1, "gender": 0, "weight_grams": 98120, "texture": {{coat}}}""");
        Beat(GUEST, shooterCoat is { } own ? [Seen(own), Hit(460)] : [Hit(460)]);
        Beat(HOST, [Seen(harvesterCoat), .. Skinned(460, spawn: false)]);
        var score = Assert.Single(Beat(GUEST), u => Py.Eq(u!["update"], 6))!;
        Assert.Equal(disqualified, Py.Eq(score["score_text"], "Disqualified "));
        Assert.Equal(!disqualified, (bool)Assert.Single(KillsOf(GUEST))!["kill"]!["ethical"]!);  // the page judges it the same
    }
}

/// <summary>The mission rollback of a moved harvest reopens only what that harvest finished.</summary>
public class MpHarvestMissionTests : MissionTestBase
{
    const long HOST = 11, GUEST = 12;

    [Fact]
    public void TestOnlyTheObjectivesTheMovedHarvestFinishedAreReopened()
    {
        state = New("mp.db", Catalogue(["Harvest a Test Deer", "Harvest a Test Mallard", "Harvest a Test Fox"]));
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest") })
        {
            state.CreateAccount(id, handle);
            state.NoteLaunch(id, System.Text.Encoding.ASCII.GetBytes("start=101&reserve=2"));
            state.HeartbeatReply(id, Events(O("""{"event": 29, "port": 32303, "guid": "9007199254740993", "color": "c73378"}""")));
            state.MissionCall(id, "activate_all_available", []);
        }
        JsonArray Beat(long who, params JsonNode[] events) => Reply(state.HeartbeatReply(who, Events(events)))["updates"]!.AsArray();
        Beat(GUEST, Shot(1)[..2]);  // the guest shoots the deer, the host skins it
        Beat(HOST, Shot(1, hits: 0));
        Beat(HOST, Shot(2, species: 7));  // the host's own mallard
        using (var con = state.Store.Open(HOST))  // a fox finished earlier by rules that did not read its title: no event meets it, and it is no business of this move
            con.Exec("UPDATE missions SET state = 3, objectives = '[]' WHERE account_id = $a AND mission_id = 3");
        Assert.Equal("1,2,3", Completed(HOST));

        var dry = state.MoveMpHarvests(apply: false).ToList();
        Assert.Equal(3, dry.Count);
        Assert.Contains("would reopen account 11: mission 1 (completed, active again) objectives 0", dry[1]);
        Assert.StartsWith("would move 1 harvests", dry[2]);
        Assert.Equal("1,2,3", Completed(HOST));  // a dry run writes nothing

        Assert.Contains("reopened 1 mission objectives", state.MoveMpHarvests(apply: true).Last());
        Assert.Equal("2,3", Completed(HOST));  // the deer is reopened; the mallard stays, and so does the fox
    }
}
