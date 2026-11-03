using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

public class SpawnerTests : StateTestBase
{
    // reserve 2 (client reserve id 1): population 101 = mule_deer; license 2 (absent) for mule_deer (1) and wolf (36)
    const string RESERVE = """
        {"populations": [{"key": "k", "index": 101, "animal": "mule_deer", "density_map": "mule_deer", "endangered": false,
           "reserve": 2, "random_groups": null,
           "groups": [{"pos_x": 0, "pos_z": 0, "radius": 0, "males": 8, "females": 8, "enabled": null, "feature": -1}]}],
         "species_ammo": []}
        """;

    public SpawnerTests()
    {
        var connect = CONNECT;
        connect["spawn"] = new JsonArray();
        connect["spawn_licenses"] = new JsonArray(Enumerable.Range(0, 59).Select(i => (JsonNode?)(i is 1 or 36 ? 2 : 1)).ToArray());
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES,
                          reserveConnects: new() { [2] = Encoding.ASCII.GetBytes(RESERVE) });
        state.NoteLaunch(ME, "start=100&loctype=LODGE&reserve=2"u8.ToArray());
    }

    static Spawner.Request Req(string animal = "mule_deer", int males = 3, bool dormant = false) =>
        new(2, animal, males, 0, -7200, 1200, 15, 120, 120, false, dormant);

    JsonObject Connect() => (JsonObject)Reply(state.ConnectReply(ME));

    [Fact]
    public void AddedGroupChangesOnlyPopulationsAndLicenses()
    {
        var before = Connect();
        var entry = state.Spawns.Add(Req());
        var after = Connect();
        var (pops, lic) = (after["populations"]!.DeepClone(), after["spawn_licenses"]!.DeepClone());
        foreach (var r in new[] { before, after })
        {
            r.Remove("populations");
            r.Remove("spawn_licenses");
        }
        Eq(before, after);
        var groups = pops[0]!["groups"]!.AsArray();
        Assert.Equal(2, groups.Count);  // the original record 0 stays first, ours is record 1
        Eq("""{"pos_x": -7200, "pos_z": 1200, "radius": 15, "males": 3, "females": 0, "wounded": 0, "enabled": 1, "feature": -1, "min_weight": 120, "max_weight": 120}""", groups[1]);
        Eq("1", lic[1]);
        Eq("2", lic[36]);
        var applied = Assert.Single(state.Spawns.LastApplied);
        Assert.Equal(new Spawner.Applied(entry.Id, 101, 1), applied);
    }

    [Fact]
    public void NewSpeciesGetsARowWithADummyRecordZero()
    {
        state.Spawns.Add(Req("wolf", 2) with { MinWeight = null, MaxWeight = null, X = null, Z = null });
        var reply = Connect();
        var row = reply["populations"]!.AsArray().Single(p => (string?)p!["animal"] == "wolf")!;
        Eq("180", row["index"]);
        Eq("2", row["reserve"]);
        var groups = row["groups"]!.AsArray();
        Eq("[0, 0]", T(groups[0]!["males"], groups[0]!["females"]));
        Eq(T(reply["expedition"]!["x"], reply["expedition"]!["z"], 2, 0), T(groups[1]!["pos_x"], groups[1]!["pos_z"], groups[1]!["males"], groups[1]!["females"]));  // the start
        Assert.Null(groups[1]!["min_weight"]);
        Eq("1", reply["spawn_licenses"]![36]);
        Assert.Equal(1, state.Spawns.LastApplied.Single().Group);
    }

    [Fact]
    public void OtherReservesAndRemovedEntriesAreNotApplied()
    {
        state.Spawns.Add(Req() with { Reserve = 3 });
        var gone = state.Spawns.Add(Req());
        Assert.Equal(1, state.Spawns.Remove(gone.Id));
        Assert.Single(Connect()["populations"]!.AsArray()[0]!["groups"]!.AsArray());
        Assert.Empty(state.Spawns.LastApplied);
        Assert.Equal(1, state.Spawns.Remove(null));
    }

    [Fact]
    public void DormantGroupSleepsAndWakeQueuesUpdate5()
    {
        state.Spawns.Add(Req(dormant: true));
        var groups = Connect()["populations"]![0]!["groups"]!.AsArray();
        Eq("0", groups[1]!["enabled"]);
        var a = state.Spawns.LastApplied.Single();
        state.WakeGroup(ME, a.PopulationId, a.Group);
        var update = Hb().Single(u => Py.Eq(u!["update"], 5))!;
        Eq("""{"update": 5, "population_id": 101, "group": 1}""", update);
    }

    [Fact]
    public void ValidationNamesTheBadFields()
    {
        Assert.Empty(Spawner.Validate(Req()));
        Assert.Contains("animal", Spawner.Validate(Req("mule")).Keys);
        Assert.Contains("reserve", Spawner.Validate(Req() with { Reserve = 0 }).Keys);
        Assert.Contains("males", Spawner.Validate(Req(males: 0)).Keys);
        Assert.Contains("males", Spawner.Validate(Req(males: 201)).Keys);
        Assert.Contains("minWeight", Spawner.Validate(Req() with { MinWeight = 130 }).Keys);  // min > max
        Assert.Contains("minWeight", Spawner.Validate(Req() with { MinWeight = null }).Keys);  // one side only: the client ignores it
        Assert.Contains("x", Spawner.Validate(Req() with { X = 0, Z = 0 }).Keys);
        Assert.Contains("x", Spawner.Validate(Req() with { X = 20000 }).Keys);
        Assert.Contains("radius", Spawner.Validate(Req() with { Radius = 500 }).Keys);
    }

    [Fact]
    public void AnimalsBeyondAReservesTwentyFreeSlotsAreSkipped()
    {
        Assert.True(Spawner.Animals.Length >= 21);
        var sp = new Spawner(Path.Combine(tmp, "s.json"));
        foreach (var a in Spawner.Animals.Take(21)) sp.Add(Req(a));
        var reply = O("""{"populations": [], "spawn_licenses": [], "expedition": {"x": 1, "z": 1}}""");
        sp.Apply(reply, 2);
        var pops = reply["populations"]!.AsArray();
        Assert.Equal(Enumerable.Range(180, 20).Select(i => (long)i), pops.Select(p => Py.L(p!["index"])));
        Assert.Equal(20, sp.LastApplied.Count);
    }

    [Fact]
    public void IdsRestartAfterTheLastOneAndRemoveCountsWhatWent()
    {
        var sp = new Spawner(Path.Combine(tmp, "t.json"));
        Assert.Empty(sp.List());
        sp.Add(Req());
        sp.Add(Req());
        Assert.Equal(1, sp.Remove(1));
        Assert.Equal(3, sp.Add(Req()).Id);
        Assert.Equal(0, sp.Remove(99));
    }

    [Fact]
    public void ADirectLaunchAppliesTheMostCommonReserveAndPadsLicenses()
    {
        var sp = new Spawner(Path.Combine(tmp, "u.json"));
        var mine = sp.Add(Req(Spawner.Animals[1]));
        sp.Add(Req(Spawner.Animals[1]) with { Reserve = 3 });
        var reply = O($$"""
            {"populations": [{"index": 101, "animal": "{{Spawner.Animals[1]}}", "reserve": 2, "groups": []},
                             {"index": 102, "animal": "x", "reserve": 2, "groups": []},
                             {"index": 201, "animal": "y", "reserve": 3, "groups": []},
                             {"index": -1, "animal": "z", "reserve": -1, "groups": []}],
             "spawn_licenses": []}
            """);
        sp.Apply(reply, null);
        Assert.Equal([new Spawner.Applied(mine.Id, 101, 0)], sp.LastApplied);
        Eq("[2, 1]", reply["spawn_licenses"]);
    }
}
