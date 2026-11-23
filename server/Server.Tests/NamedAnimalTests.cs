using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>`spawn_named_animals`: the hunt's reply places the animal a named objective asks for, and only an animal of that spawn meets it. Invented catalogue and events.</summary>
public class NamedAnimalTests : MissionTestBase
{
    const string Lake = "Harvest the \"Old Mossback\" by the lake (X: -1.200, Y: 3.400)";
    const string OnTest = ", \"species\": [1], \"reserves\": [2]";  // the mission's own species and reserve

    /// <summary>A server on `file` whose catalogue is these missions (the JSON of `missions`), whose reserve 2 has a deer population and whose reserve 3 a lodge; `on`: the setting.</summary>
    State Make(string file, string missions, bool on = true, JsonObject? rules = null, Action<JsonObject>? connectOf = null)
    {
        var app = APP;
        app["missions"] = JsonNode.Parse(missions)!["missions"]!.DeepClone();
        app["locations"]!.AsArray().Add(O("""{"id": 201, "pos": {"y": 150, "x": 100, "z": 200}, "type": "LODGE", "reserve": 3}"""));
        var connect = CONNECT;
        connect["populations"] = JsonNode.Parse("""[{"index": 101, "animal": "mule_deer", "density_map": "mule_deer", "endangered": false, "spawn_license": 1, "reserve": 2, "groups": [], "random_groups": null}]""");
        connect["spawn"] = new JsonArray();
        connectOf?.Invoke(connect);
        return new State(Path.Combine(tmp, file), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES, offline: false, missionRules: rules, spawnNamedAnimals: on);
    }

    static JsonArray Spawn(byte[] connect) => Reply(connect)["spawn"]!.AsArray();

    void Hunt(State s, int lodge = 101, int reserve = 2) => s.NoteLaunch(ME, System.Text.Encoding.ASCII.GetBytes($"start={lodge}&reserve={reserve}"));

    /// <summary>A kill of the deer at the place of `Lake`'s animal, or of the animal of that `population` anywhere.</summary>
    static JsonNode[] KillOf(int individual, long? population, int x = -1200, int z = 3400)
    {
        var events = Shot(individual, x: x, z: z);
        if (population is { } p)
        {
            events[0]!["population"] = p;
            events[^1]!["server_spawn"] = true;
        }
        return events;
    }

    [Fact]
    public void TestTheStartsReplyHasAnEntryOnlyForAnOpenNamedAnimalOfAnActiveMissionInItsReserve()
    {
        state = Make("entry.db", Catalogue([Lake, "Harvest a Test Deer"], OnTest));
        Hunt(state);
        Assert.Empty(Spawn(state.ConnectReply(ME)));  // nothing is active
        Assert.True(state.MissionCall(ME, "activate", "mission_id=2"u8.ToArray()));  // the mission without a named animal
        Hunt(state);
        Assert.Empty(Spawn(state.ConnectReply(ME)));  // no entry for it
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(state, 201, 3);
        Assert.Empty(Spawn(state.ConnectReply(ME)));  // another reserve
        Hunt(state);
        var entry = Assert.Single(Spawn(state.ConnectReply(ME)))!;
        Assert.Equal(160, (long)entry["population_id"]!);  // reserve 2: (2 - 1) * 100 + 60
        Assert.Equal(1, (long)entry["species"]!);
        Assert.Equal(1, (long)entry["spawn_map_id"]!);
        var (dummy, animal) = (entry["group"]![0]!, entry["group"]![1]!);
        Eq("""[0, 0, 0, 0, -1]""", new JsonArray(dummy["male_count"]!.DeepClone(), dummy["female_count"]!.DeepClone(), dummy["x"]!.DeepClone(), dummy["z"]!.DeepClone(), dummy["texture_id"]!.DeepClone()));
        Eq("""[1, 0, -1200, 3400, -1, 0, 1]""", new JsonArray(animal["male_count"]!.DeepClone(), animal["female_count"]!.DeepClone(), animal["x"]!.DeepClone(), animal["z"]!.DeepClone(),
            animal["texture_id"]!.DeepClone(), animal["radius"]!.DeepClone(), animal["active"]!.DeepClone()));
    }

    [Fact]
    public void TestAHuntWithNoNamedAnimalGetsTheReplyItGotBeforeAndSoDoesOneWithTheSettingOff()
    {
        var missions = Catalogue([Lake, "Harvest a Test Deer"], OnTest);
        var (on, off) = (Make("on.db", missions), Make("off.db", missions, on: false));
        foreach (var s in new[] { on, off })
        {
            s.MissionCall(ME, "activate", "mission_id=2"u8.ToArray());
            Hunt(s);
        }
        Assert.Equal(Convert.ToHexString(off.ConnectReply(ME)), Convert.ToHexString(on.ConnectReply(ME)));  // the mission with no named animal
        on.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        off.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Assert.Empty(Spawn(off.ConnectReply(ME)));  // off: the guess stands, nothing is placed
        Assert.Single(Spawn(on.ConnectReply(ME)));
    }

    [Fact]
    public void TestOnlyAnAnimalOfTheSpawnMeetsTheObjectiveAtAnyDistanceAndAnotherOfTheSpeciesAtTheSamePlaceDoesNot()
    {
        state = Make("meet.db", Catalogue([Lake], OnTest));
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hb(KillOf(1, population: null));  // an ordinary deer at the place
        Hb(KillOf(2, population: 101));  // an animal of the reserve's own population there
        Hb(KillOf(3, population: 161, x: -1200));  // another named animal's population
        Assert.Equal("", Completed());
        Hb(KillOf(4, population: 160, x: 0, z: 0));  // the spawned animal, 3.6 km away
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnObjectiveThatFinishedGetsNoEntryAndAnOrderedOneOnlyOnceTheOnesBeforeItAre()
    {
        var two = "{\"missions\": [{\"id\": 1, \"title\": \"Two\", \"dependencies\": [], \"rewards\": [], \"species\": [1], \"reserves\": [2], \"objectives\": ["
            + "{\"id\": 0, \"title\": \"Harvest a Test Deer\"}, {\"id\": 1, \"title\": \"Then harvest the \\\"Old Mossback\\\" by the lake (X: -1.200, Y: 3.400)\"}]}]}";
        state = Make("order.db", two);
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(state);
        Assert.Empty(Spawn(state.ConnectReply(ME)));  // the first objective is open, the second waits
        Hb(Shot(1));
        Hunt(state);
        Assert.Single(Spawn(state.ConnectReply(ME)));
        Hb(KillOf(2, population: 160));
        Assert.Equal("1", Completed());
        Hunt(state);
        Assert.Empty(Spawn(state.ConnectReply(ME)));  // finished: the animal is gone
    }

    [Fact]
    public void TestObjectivesNamingTheSameAnimalShareItsPopulationAndAnotherPlaceTakesTheNextOne()
    {
        state = Make("share.db", Catalogue([Lake, Lake.Replace("Harvest the", "Kill the"), Lake.Replace("3.400", "3.500")], OnTest));
        ActivateAll();
        Hunt(state);
        var spawn = Spawn(state.ConnectReply(ME));
        Eq("[160, 161]", new JsonArray(spawn.Select(e => e!["population_id"]!.DeepClone()).ToArray()));
        Hb(KillOf(1, population: 160));
        Assert.Equal("1,2", Completed());  // both objectives that name it
        Assert.Contains("named animals placed (spawn_named_animals): 3 objectives in 2 populations, 0 named-animal objectives keep the 1000 m guess or are never met", state.MissionCoverage());
    }

    [Fact]
    public void TestAnOperatorEntryGivesTheSpeciesGenderCoatReserveAndPlaceOfATitleWithNoQuotes()
    {
        state = Make("rule.db", Catalogue(["Harvest the angry blonde Test Deer near the lake"], ", \"reserves\": [2, 3]"),
            rules: new() { ["1/0"] = "harvest; species: Test Deer; female; coat: blonde; reserve: Test Reserve; named animal near (500, -700) m" });
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(state);
        var animal = Assert.Single(Spawn(state.ConnectReply(ME)))!["group"]![1]!;
        Eq("[0, 1, 500, -700, 6]", new JsonArray(animal["male_count"]!.DeepClone(), animal["female_count"]!.DeepClone(), animal["x"]!.DeepClone(), animal["z"]!.DeepClone(), animal["texture_id"]!.DeepClone()));
        Hb(Shot(1, gender: 1, texture: 6, x: 500, z: -700));  // a female blonde deer at the place: not the spawn
        Assert.Equal("", Completed());
        var spawned = Shot(2, gender: 1, texture: 6);
        spawned[0]!["population"] = 160;
        Hb(spawned);
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnIdNeverMovesWhenAnEarlierMissionBecomesPlaceableLater()
    {
        var missions = Catalogue(["Harvest Old Mossback the Test Deer", Lake], OnTest);  // 1: a name with no quotes, not read yet; 2: the lake's animal
        state = Make("keep.db", missions);
        ActivateAll();
        Hunt(state);
        Assert.Equal(160, (long)Assert.Single(Spawn(state.ConnectReply(ME)))!["population_id"]!);
        Hb(KillOf(1, population: 160, x: 0, z: 0));
        Assert.Equal("2", Completed());
        // the operator's entry now places mission 1's animal: it takes the next id, and the harvest of 160 stays mission 2's
        var later = Make("keep.db", missions, rules: new() { ["1/0"] = "harvest; species: Test Deer; reserve: Test Reserve; named animal near (500, -700) m" });
        Assert.Equal(161, (long)Assert.Single(Spawn(later.ConnectReply(ME)))!["population_id"]!);
        Assert.Equal(0, later.ConfirmMissions());
        Assert.StartsWith("would reopen 0 objectives in 0 missions", later.RollbackMissions(apply: false).Last());
        Assert.Equal("2", Completed(of: later));
    }

    [Fact]
    public void TestAnAnimalTheServerCannotPlaceKeepsTheGuessAndGetsNoEntry()
    {
        // 1: no reserve (two listed, none in the title); 2: a coat word of several ids; 3: no species; 4: no place at all
        var missions = Catalogue(["Harvest the \"A\" by the lake (X: 1.200, Y: 3.400)", "Harvest the \"B\" by the lake (X: 1.200, Y: 3.400)",
                                  "Harvest the \"C\" by the lake (X: 1.200, Y: 3.400)", "Harvest the \"D\""], ", \"species\": [1], \"reserves\": [2, 3]");
        var rules = new JsonObject
        {
            ["2/0"] = "harvest; species: Test Deer; coat: albino; reserve: Test Reserve; named animal near (10, 20) m",
            ["3/0"] = "harvest; named animal near (10, 20) m; reserve: Test Reserve",
        };
        state = Make("none.db", missions, rules: rules);
        ActivateAll();
        Hunt(state);
        Assert.Empty(Spawn(state.ConnectReply(ME)));
        Hb(Shot(1, x: 1300, z: 3300));  // within 1000 m of the place of 1: the guess is what meets it
        Assert.Contains("1", Completed().Split(','));
        Assert.Contains("named animals placed (spawn_named_animals): 0 objectives in 0 populations, 4 named-animal objectives keep the 1000 m guess or are never met", state.MissionCoverage());
    }

    [Fact]
    public void TestNoEntryWhenTheSpeciesIsUnlicensedOrNotOnTheReserve()
    {
        var missions = Catalogue([Lake], OnTest);
        state = Make("licence.db", missions, connectOf: c => c["spawn_licenses"] = JsonNode.Parse("[1, 2]"));
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(state);
        Assert.Empty(Spawn(state.ConnectReply(ME)));  // species 1 is 2: not available
        var home = Make("home.db", missions, connectOf: c => c["populations"] = new JsonArray());
        home.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(home);
        Assert.Empty(Spawn(home.ConnectReply(ME)));  // no deer population on the reserve: no density map to name
        var taken = Make("taken.db", missions, connectOf: c => c["populations"]!.AsArray().Add(O("""{"index": 160, "animal": "coyote", "density_map": "coyote", "reserve": 2, "groups": []}""")));
        taken.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(taken);
        Assert.Empty(Spawn(taken.ConnectReply(ME)));  // a row has that id: the entry would replace it
    }

    [Fact]
    public void TestASpotOfTheSpawnedAnimalMeetsASpotObjective()
    {
        state = Make("spot.db", Catalogue(["Spot the \"Old Mossback\" by the lake (X: -1.200, Y: 3.400)"], OnTest));
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(state);
        Assert.Single(Spawn(state.ConnectReply(ME)));
        Hb(Spot(1));
        Assert.Equal("", Completed());
        var spawned = Spot(2);
        spawned[0]!["population"] = 160;
        Hb(spawned);
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestTheStartsRecheckTheRollbackAndTheLivePathJudgeAKillTheSame()
    {
        var missions = Catalogue([Lake], OnTest);
        var guess = Make("judge.db", missions, on: false);  // the 1000 m guess finishes it for an ordinary deer
        guess.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        state = guess;
        Hb(KillOf(1, population: null));
        Assert.Equal("1", Completed());
        var placed = Make("judge.db", missions);
        var dry = placed.RollbackMissions(apply: false).ToList();
        Assert.StartsWith("would reopen 1 objectives in 1 missions", dry.Last());  // the spawn is the only animal that meets it now
        Assert.Equal(0, placed.ConfirmMissions());

        var live = Make("live.db", missions);
        live.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        state = live;
        Hb(KillOf(2, population: 160, x: 0, z: 0));
        Assert.Equal("1", Completed());
        Assert.StartsWith("would reopen 0 objectives in 0 missions", Make("live.db", missions).RollbackMissions(apply: false).Last());
    }

    [Fact]
    public void TestTheStartsRecheckFinishesAnObjectiveFromAHarvestOfTheSpawnAlreadyMade()
    {
        var missions = Catalogue(["Harvest the \"Old Mossback\" by the lake (X: -1.200, Y: 3.400) while whistling"], OnTest);
        state = Make("later.db", missions);  // not understood: no kill meets it yet
        state.MissionCall(ME, "activate", "mission_id=1"u8.ToArray());
        Hunt(state);
        Hb(KillOf(1, population: 160));
        Assert.Equal("", Completed());
        var later = Make("later.db", missions, rules: new() { ["1/0"] = "harvest; species: Test Deer; reserve: Test Reserve; named animal near (-1200, 3400) m" });
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal("1", Completed(of: later));
    }
}
