using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A community server whose catalogue is made of invented missions, with a few species, weapons, ammunition and sights, and the events of a kill, a spot and a clue.</summary>
public abstract class MissionTestBase : StateTestBase
{
    protected MissionTestBase()
    {
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };  // pinned, so no test reads files
    }

    /// <summary>Missions with these titles: ids 1 to n, each with one objective (id 0) that is its title; `extra` is more JSON of every mission.</summary>
    protected static string Catalogue(IEnumerable<string> titles, string extra = "") =>
        "{\"missions\": [" + string.Join(",", titles.Select((t, i) =>
            $"{{\"id\": {i + 1}, \"title\": {Py.Dumps(JsonValue.Create(t))}, \"dependencies\": [], \"rewards\": [{{\"text\": \"10 gm$\"}}], \"objectives\": [{{\"id\": 0, \"title\": {Py.Dumps(JsonValue.Create(t))}}}]{extra}}}")) + "]}";

    // ids 50 to 53: a rifle and a shotgun, two ammunitions; 60 to 65: weapons of other classes; 70 to 74: ammunition; 80 and 81: sights
    static readonly string[] ExtraSpecies =
    [
        """{"id": 3, "name": "Plains Elk"}""", """{"id": 4, "name": "Hill Elk"}""", """{"id": 5, "name": "Test Fox"}""", """{"id": 6, "name": "Spring Pintail"}""", """{"id": 7, "name": "Test Mallard"}""",
        """{"id": 9, "name": "Snow Ptarmigan"}""", """{"id": 10, "name": "Rock Ptarmigan"}""",
    ];

    static readonly string[] ExtraItems =
    [
        """{"id": 50, "name": "Test Carbine", "cat": 1, "subcat": {"name": "Rifles"}}""", """{"id": 51, "name": "Test Shotgun", "cat": 1, "subcat": {"name": "Shotguns"}}""",
        """{"id": 52, "name": "Test Round", "cat": 3}""", """{"id": 53, "name": "Test Slug Shells", "cat": 3}""",
        """{"id": 60, "name": ".243 Test Rifle", "cat": 1, "subcat": {"name": "Rifles"}}""", """{"id": 61, "name": ".223 Test Rifle", "cat": 1, "subcat": {"name": "Rifles"}}""",
        """{"id": 62, "name": "Test Longbow", "cat": 1, "subcat": {"name": "Bows & Crossbows"}}""", """{"id": 63, "name": "Test Compound Bow", "cat": 1, "subcat": {"name": "Bows & Crossbows"}}""",
        """{"id": 64, "name": "Test Crossbow", "cat": 1, "subcat": {"name": "Bows & Crossbows"}}""", """{"id": 65, "name": "Test Muzzleloader", "cat": 1, "subcat": {"name": "Muzzleloaders"}}""",
        """{"id": 70, "name": "Test Tracer Arrows", "cat": 3}""", """{"id": 71, "name": "Wooden Arrows", "cat": 3}""",
        """{"id": 72, "name": "7mm Test Ammunition (HV)", "cat": 3}""", """{"id": 73, "name": "7mm Test Ammunition (HP)", "cat": 3}""", """{"id": 74, "name": "12 GA Test Buckshot Shells", "cat": 3}""",
        """{"id": 80, "name": "Test Dot", "cat": 2, "subcat": {"name": "Rifle Sights"}}""", """{"id": 81, "name": "Test Rifle Scope", "cat": 2, "subcat": {"name": "Rifle Sights"}}""",
    ];

    /// <summary>A state on `file` whose catalogue is made of these missions (the JSON of the catalogue's `missions`) and the species and items above. `permitted`: no ammunition rule
    /// (every kill is permitted), or these ammunition ids only for species 1.</summary>
    protected State New(string file, string missions, int[]? permitted = null)
    {
        var app = APP;
        app["missions"] = JsonNode.Parse(missions)!["missions"]!.DeepClone();
        foreach (var species in ExtraSpecies)
            app["species"]!.AsArray().Add(JsonNode.Parse(species));
        foreach (var item in ExtraItems)
            app["items"]!.AsArray().Add(JsonNode.Parse(item));
        app["locations"]![0]!["name"] = "Test Lodge";
        var connect = CONNECT;
        connect["ethical"] = permitted is null ? new JsonArray() : new JsonArray([.. Enumerable.Range(0, 59).Select(i => (JsonNode?)new JsonArray([.. permitted.Where(_ => i == 1).Select(a => (JsonNode?)a)]))]);
        connect["species"] = new JsonArray(Enumerable.Range(0, 59).Select(i => (JsonNode?)new JsonObject { ["scale_factor"] = 1.0, ["variant_id"] = i }).ToArray());
        return new State(Path.Combine(tmp, file), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES, offline: false);
    }

    /// <summary>One kill of a deer (species 1): the spawn, `hits` shots of the same settings (or one for each of `organs`) and the harvest. `pose` is the player's stance with the shot's flag.</summary>
    protected static JsonNode[] Shot(int individual, int weapon = 50, int ammunition = 52, int distance = 15000, long organ = 6, int animalState = 3, int location = 0, int scope = -1,
                                     int value = 100000, int grams = 90000, int hits = 1, int special = 0, int species = 1, int x = 0, int z = 0, int pose = 2048, int gender = 0, long[]? organs = null)
    {
        var events = new List<JsonNode> { O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": {{gender}}, "weight_grams": {{grams}}, "texture": 0, "special": {{special}}}""") };
        for (var i = 0; i < (organs?.Length ?? hits); i++)
            events.Add(O($$"""
                {"event": 1, "individual": {{individual}}, "weapon_id": {{weapon}}, "ammunition_id": {{ammunition}}, "distance_cms": {{distance}}, "organ1": 33, "organ2": {{organs?[i] ?? organ}},
                 "animal_state": {{animalState}}, "location": {{location}}, "scope_id": {{scope}}, "pose": {{pose}}, "species": {{species + 1}}, "x": {{x}}, "z": {{z}}}
                """));
        events.Add(O($$"""{"event": 3, "individual": {{individual}}, "extended_species": {{species}}, "harvest_value": {{value}}, "trophy_integrity": 100000}"""));
        return [.. events];
    }

    /// <summary>An animal seen from a stand (`location`): its spawn and the spot.</summary>
    protected static JsonNode[] Spot(int individual, int species = 1, int gender = 0, int location = 0) =>
    [
        O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": {{gender}}, "weight_grams": 90000, "texture": 0, "special": 0}"""),
        O($$"""{"event": 11, "individual": {{individual}}, "location": {{location}}}"""),
    ];

    /// <summary>A clue (`trackType` 9 is a heard call, 2 footprints, 1 droppings, 0 a blood trail) of an animal that is a deer (species 1) and a male unless said.</summary>
    protected static JsonNode[] Clue(int individual, int trackType, int species = 1, int gender = 0) =>
    [
        O($$"""{"event": 7, "individual": {{individual}}, "species": {{species}}, "gender": {{gender}}, "weight_grams": 90000, "texture": 0, "special": 0}"""),
        O($$"""{"event": 12, "individual": {{individual}}, "track_type": {{trackType}}}"""),
    ];

    /// <summary>The player enters a place of the reserve (event 101).</summary>
    protected static JsonNode Enter(string place) => O($$"""{"event": 101, "location_name": "{{place}}", "direction": "enter", "player_x": 1, "player_y": 2, "player_z": 3}""");

    protected void Launch(int reserve = 2) => state.NoteLaunch(ME, System.Text.Encoding.ASCII.GetBytes($"start=101&reserve={reserve}"));

    protected string Completed(long account = ME, State? of = null) => string.Join(",", Reply((of ?? state).MissionsReply(account)!)["completed"]!.AsArray().Select(i => (long)i!));

    protected double Gm(State? of = null, long account = ME) => (of ?? state).Wallet(account)["gm"]!.GetValue<double>();

    protected void ActivateAll(State? of = null) => (of ?? state).MissionCall(ME, "activate_all_available", []);
}
