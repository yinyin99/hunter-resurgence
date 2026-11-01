// Admin spawner: groups added to the `populations` of the next connect reply. Entries live in spawn.json in the data
// folder (read on every use, so the CLI and the server share them).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class Spawner(string path)
{
    /// <summary>The game's animal ids in id order (the facts file); the index is the catalogue id (emote_id) and the index of spawn_licenses.</summary>
    public static string[] Animals => Facts.Default.Animals;

    public const int MaxAnimals = 200, MaxRadius = 200, Limit = 16384;

    public sealed record Request(int? Reserve, string? Animal, int? Males, int? Females, int? X, int? Z, int? Radius,
                                 int? MinWeight, int? MaxWeight, bool? Wounded, bool? Dormant);

    public sealed record Entry(int Id, int Reserve, string Animal, int Males, int Females, int? X, int? Z, int Radius,
                               int MinWeight, int MaxWeight, bool Wounded, bool Dormant);

    /// <summary>Where the last connect put each entry: the population id and group record index (1-based, the `group` of update 5).</summary>
    public sealed record Applied(int Id, long PopulationId, int Group);

    public List<Applied> LastApplied { get; private set; } = [];

    public List<Entry> List() => File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [] : [];

    void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    /// <summary>Field errors (empty when valid). The client enforces: weights both > 0 or both unset; position inside the map.</summary>
    public static Dictionary<string, string[]> Validate(Request r)
    {
        var e = new Dictionary<string, string[]>();
        if (r.Reserve is not (>= 1 and <= 13)) e["reserve"] = ["1 to 13 (the `reserve` of the populations rows)"];
        if (r.Animal is null || Array.IndexOf(Animals, r.Animal) < 0) e["animal"] = ["an animal_types name, e.g. mule_deer"];
        var (m, f) = (r.Males ?? 0, r.Females ?? 0);
        if (m < 0 || f < 0 || m + f is < 1 or > MaxAnimals) e["males"] = [$"males + females: 1 to {MaxAnimals}"];
        if (r.Radius is < 0 or > MaxRadius) e["radius"] = [$"0 to {MaxRadius}"];
        if (r.X is not null != (r.Z is not null)) e["x"] = ["x and z together"];
        else if (r.X is { } x && r.Z is { } z && (Math.Abs(x) > Limit || Math.Abs(z) > Limit || (x == 0 && z == 0)))
            e["x"] = [$"x and z within ±{Limit}, not both 0 (0, 0 means no position to the client)"];
        var (lo, hi) = (r.MinWeight ?? 0, r.MaxWeight ?? 0);
        if (lo < 0 || hi < 0 || (lo > 0) != (hi > 0) || lo > hi) e["minWeight"] = ["minWeight <= maxWeight, both > 0 (integer kg) or both unset"];
        return e;
    }

    public Entry Add(Request r)
    {
        var all = List();
        var entry = new Entry(all.Count == 0 ? 1 : all.Max(x => x.Id) + 1, r.Reserve!.Value, r.Animal!, r.Males ?? 0, r.Females ?? 0, r.X, r.Z,
                              r.Radius ?? 15, r.MinWeight ?? 0, r.MaxWeight ?? 0, r.Wounded ?? false, r.Dormant ?? false);
        all.Add(entry);
        Save(all);
        return entry;
    }

    /// <summary>Remove one entry (or all); how many went.</summary>
    public int Remove(int? id)
    {
        var all = List();
        var n = all.RemoveAll(e => id is null || e.Id == id);
        Save(all);
        return n;
    }

    /// <summary>Add the entries of the reply's reserve to its `populations` and release their species in `spawn_licenses`.
    /// launched: the reserve of Launch/start (null after a direct launch: the reserve the populations rows carry).</summary>
    public void Apply(JsonObject reply, long? launched)
    {
        LastApplied = [];
        var entries = List();
        if (entries.Count == 0 || reply["populations"] is not JsonArray pops)
            return;
        var reserve = launched ?? pops.OfType<JsonObject>().Select(p => Py.L(p["reserve"])).Where(r => r >= 0)
            .GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var (sx, sz) = reply["expedition"] is JsonObject ex ? ((int)Py.D(ex["x"]), (int)Py.D(ex["z"])) : (0, 0);
        var applied = new List<Applied>();
        foreach (var e in entries.Where(e => e.Reserve == reserve))
        {
            var r0 = e.Reserve - 1;
            var row = pops.OfType<JsonObject>().FirstOrDefault(p => (string?)p["animal"] == e.Animal && Py.L(p["index"]) / 100 == r0);
            if (row is null)
            {
                var index = Enumerable.Range(r0 * 100 + 80, 20).First(i => !pops.OfType<JsonObject>().Any(p => Py.L(p["index"]) == i));
                row = new JsonObject
                {
                    ["key"] = $"spawner-{index}", ["index"] = index, ["animal"] = e.Animal, ["density_map"] = e.Animal,
                    ["endangered"] = false, ["spawn_license"] = 1, ["active_start"] = null, ["active_end"] = null,
                    ["reserve"] = e.Reserve, ["random_groups"] = null,
                    // the client never applies record 0: a 0 / 0 dummy keeps the real group at >= 1
                    ["groups"] = new JsonArray(Group(0, 0, 0, 0, 0, 0, 0, false, true)),
                };
                pops.Add(row);
            }
            var groups = (JsonArray)row["groups"]!;
            groups.Add(Group(e.X ?? sx, e.Z ?? sz, e.Radius, e.Males, e.Females, e.MinWeight, e.MaxWeight, e.Wounded, !e.Dormant));
            applied.Add(new(e.Id, Py.L(row["index"]), groups.Count - 1));
            if (reply["spawn_licenses"] is JsonArray lic)
            {
                var id = Array.IndexOf(Animals, e.Animal);
                while (lic.Count <= id) lic.Add(2);
                lic[id] = 1;
            }
        }
        LastApplied = applied;
    }

    // exact keys of the client's group rows; no health / sense scales / texture_id (a present null reads as 0)
    static JsonObject Group(int x, int z, int radius, int males, int females, int lo, int hi, bool wounded, bool enabled)
    {
        var g = new JsonObject
        {
            ["pos_x"] = x, ["pos_z"] = z, ["radius"] = radius, ["males"] = males, ["females"] = females,
            ["wounded"] = wounded ? 1 : 0, ["enabled"] = enabled ? 1 : 0, ["feature"] = -1,
        };
        if (lo > 0)
        {
            g["min_weight"] = lo;
            g["max_weight"] = hi;
        }
        return g;
    }
}
