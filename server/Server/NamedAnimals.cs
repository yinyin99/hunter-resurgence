// Named mission animals: the animal a title names is no animal the game marks, so the server places it. When a hunt starts, the reply's `spawn` gets one entry (one animal of the
// objective's species, gender and coat, at its coordinates) for each open named-animal objective of the account's active missions in that reserve; the harvest or the spot of an animal
// of that population meets the objective, whatever its distance (docs/server.md, "Named animals"). Only with the setting `spawn_named_animals`.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    // a reserve's population ids are `index * 100 + n`: the game's rows use 0 to 14 (the events' too), the dog templates are 996, 998 and 999, the admin spawner takes 80 and up (<see cref="Spawner"/>)
    const int NamedFirst = 60, NamedSlots = 20;

    /// <summary>What a named animal is: the reserve (the catalogue's number), its species, gender (0 male, 1 female), the coat to force (null: any) and where it is (x, z in metres).</summary>
    sealed record NamedAnimal(long Reserve, long Species, long Gender, long? Coat, double X, double Z);

    readonly bool placesNamedAnimals;  // the setting `spawn_named_animals`
    readonly Dictionary<NamedAnimal, long> namedPopulations = [];  // each named animal's population id; objectives that name the same animal share it

    /// <summary>The named animal an objective's rule asks for, or null when the server cannot place it and the 1000 m guess stays: not a harvest or a spot, no title place, no single species,
    /// a coat that is refused or that several coats could be, no single reserve (the rule's, else the mission's), or a place the client reads as none or outside its map.
    /// ponytail: a coat word shared by several coats (`dark`, `piebald`) is not forced, as the species' own id is not known; the operator's file states one (`coat 32`).</summary>
    NamedAnimal? NamedAnimalOf(Rule rule, long[] missionReserves)
    {
        var tests = rule.Tests ?? [];
        var named = tests.Where(t => t.Check == Check.Named).ToArray();
        var reservesNamed = tests.Where(t => t.Check == Check.Reserve).ToArray();
        var coats = tests.Where(t => t.Check == Check.Coat).ToArray();
        var reserve = reservesNamed.Length == 1 && reservesNamed[0].Ids!.Count == 1 ? reservesNamed[0].Ids!.Single() : reservesNamed.Length == 0 && missionReserves.Length == 1 ? missionReserves[0] : -1;
        if (rule.Unknown || rule.Kind is not (Kind.Harvest or Kind.Spot) || named.Length != 1 || double.IsInfinity(named[0].Min) || reserve < 1 || coats.Length > 1 || tests.Any(t => t.Check == Check.NotCoat)
            || rule.Species is not { } speciesId || speciesId >= facts.Animals.Length || coats.Length == 1 && coats[0].Ids!.Count != 1)
            return null;
        var (x, z) = (Math.Round(named[0].Min), Math.Round(named[0].Max));
        return Math.Abs(x) > Spawner.Limit || Math.Abs(z) > Spawner.Limit || x == 0 && z == 0 ? null  // (0, 0) means no position to the client
            : new(reserve, speciesId, rule.Gender ?? 0, coats.Length == 1 ? coats[0].Ids!.Single() : null, x, z);
    }

    /// <summary>Give every objective that names an animal the server can place its population: the rule's named condition then holds for an animal of that population only. Called at the start,
    /// after the operator's rules are in. An animal takes the next free place of its reserve the first time it is placed and keeps it in `file` (`named_animals.json` in the data folder), so
    /// an id never moves when a rule or the catalogue changes: harvests already stored stay judged by it, and an animal that is no longer named still holds its place. A reserve has
    /// <see cref="NamedSlots"/> places; an objective past them keeps the guess.</summary>
    void PlaceNamedAnimals(string file)
    {
        var given = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(file)) ?? [] : [];
        var changed = false;
        var ofMission = Arr(G(application, "missions", null)).OfType<JsonObject>().Where(d => TryKey(d["id"], out _)).ToDictionary(d => Py.L(d["id"]), d => Ids(d["reserves"]));
        foreach (var m in missions.Values.OrderBy(m => m.Id))
            for (var i = 0; i < m.Objectives.Length; i++)
            {
                var o = m.Objectives[i];
                if (NamedAnimalOf(o.Rule, ofMission.GetValueOrDefault(m.Id, [])) is not { } animal)
                    continue;
                if (!namedPopulations.TryGetValue(animal, out var population))
                {
                    var key = FormattableString.Invariant($"{animal.Reserve}|{animal.Species}|{animal.Gender}|{animal.Coat}|{animal.X}|{animal.Z}");
                    if (!given.TryGetValue(key, out population))
                    {
                        var free = Enumerable.Range(NamedFirst, NamedSlots).Select(n => (long?)((animal.Reserve - 1) * 100 + n)).FirstOrDefault(id => !given.ContainsValue(id!.Value));
                        if (free is not { } place)
                            continue;
                        given[key] = population = place;
                        changed = true;
                    }
                    namedPopulations[animal] = population;
                }
                m.Objectives[i] = o with { Rule = o.Rule with { Tests = [.. o.Rule.Tests!.Select(t => t.Check == Check.Named ? t with { Ids = [population] } : t)] } };
            }
        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, JsonSerializer.Serialize(given));
        }
    }

    /// <summary>The client's group record of an animal: `x` and `z` 0 mean no position, a `texture_id` of -1 and features of -1 a random one (a missing key reads as 0).</summary>
    static JsonObject NamedGroup(long males, long females, double x, double z, long coat) => new()
    {
        ["radius"] = 0, ["male_count"] = males, ["female_count"] = females, ["min_weight"] = 0, ["max_weight"] = 0, ["min_feature"] = -1, ["max_feature"] = -1,
        ["x"] = (long)x, ["z"] = (long)z, ["interest_x"] = 0, ["interest_z"] = 0, ["interest_time"] = 0, ["wounded_state"] = 0, ["texture_id"] = coat, ["active"] = 1,
    };

    /// <summary>Add to the reply of a hunt in `reserve` (the catalogue's number) a `spawn` entry for each named animal of an open objective of the account's active missions there. An objective
    /// is open when it is not finished and, when ordered, every one before it is. The entry is a new population of the reserve (its id is the objective's, <see cref="PlaceNamedAnimals"/>) of one
    /// animal after the client's dummy record 0 (it never applies that one); its species must live on the reserve (its population row names the density map the client loads), and
    /// not be unlicensed (2). Nothing is added, and the reply stays as it was, when there is none.</summary>
    void AddNamedAnimals(long account, long reserve, JsonObject reply)
    {
        if (namedPopulations.Count == 0 || reply["populations"] is not JsonArray rows)
            return;
        Dictionary<long, (long State, List<long> Done)> missionRows;
        using (var con = Con(account))
        {
            con.Begin();
            EnsureMissions(con);
            missionRows = MissionRows(con);
            con.Commit();
        }
        var wanted = new SortedSet<long>();
        foreach (var (id, (state, done)) in missionRows)
            if (state == 2 && missions.TryGetValue(id, out var m))
                for (var i = 0; i < m.Objectives.Length; i++)
                    if (m.Objectives[i] is var o && !done.Contains(o.Id) && !(o.Rule.Ordered && m.Objectives[..i].Any(p => !done.Contains(p.Id)))
                        && o.Rule.Tests?.FirstOrDefault(t => t.Check == Check.Named)?.Ids is { } population)
                        wanted.UnionWith(population);
        List<JsonObject> entries = [];
        foreach (var (animal, population) in namedPopulations.Where(p => wanted.Contains(p.Value) && p.Key.Reserve == reserve).OrderBy(p => p.Value))
        {
            var name = facts.Animals[animal.Species];
            var home = rows.OfType<JsonObject>().FirstOrDefault(p => Py.L(p["index"]) / 100 == reserve - 1 && Py.Str(p["animal"]) == name);
            var map = home is null ? -1 : Array.IndexOf(facts.Animals, Py.Str(home["density_map"]));
            if (map < 0 || rows.OfType<JsonObject>().Any(p => Py.L(p["index"]) == population)  // a row of that id would be replaced by the entry
                || reply["spawn_licenses"] is JsonArray licenses && (animal.Species >= licenses.Count || Py.L(licenses[(int)animal.Species]) == 2))
                continue;
            entries.Add(new JsonObject
            {
                ["population_id"] = population, ["species"] = animal.Species, ["spawn_map_id"] = map,
                ["group"] = new JsonArray(NamedGroup(0, 0, 0, 0, -1), NamedGroup(animal.Gender == 0 ? 1 : 0, animal.Gender == 1 ? 1 : 0, animal.X, animal.Z, animal.Coat ?? -1)),
            });
        }
        if (entries.Count == 0)
            return;
        if (reply["spawn"] is not JsonArray spawn)
            reply["spawn"] = spawn = [];
        foreach (var entry in entries)
            spawn.Add(entry);
    }
}
