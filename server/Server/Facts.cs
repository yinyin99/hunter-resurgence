// The facts file (server/facts/facts.json): game facts the operator's install does not carry, in this project's own
// format (docs/server.md#the-facts-file). Read once; the code asks it for its tables instead of keeping them.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class Facts
{
    /// <summary>The file shipped next to the server.</summary>
    public static Facts Default => defaultFacts.Value;
    static readonly Lazy<Facts> defaultFacts = new(() => Load(Path.Combine(AppContext.BaseDirectory, "facts", "facts.json")));

    readonly JsonObject root;

    Facts(JsonObject root)
    {
        this.root = root;
        Animals = [.. Arr("animals").Select(a => (string)a!)];
        foreach (var (family, ids) in Obj("families"))
            foreach (var id in ids!.AsArray())
                Families[(int)Py.L(id)] = family;
        var ranks = Obj("ranks");
        RankCap = ranks["cap"] is { } cap ? Py.L(cap) : 0;
        Ranks = [.. (ranks["rows"] as JsonArray ?? []).Select(r => (string?)r!["category"] switch
        {
            "animal" => new Rank(Py.L(r["id"]), (string?)r["title"] ?? "", "animal", [Py.L(r["species"])]),
            "weapon" => new Rank(Py.L(r["id"]), (string?)r["title"] ?? "", "weapon", [.. r["items"]!.AsArray().Select(Py.L)]),
            "collectable" => new Rank(Py.L(r["id"]), (string?)r["title"] ?? "", "collectable", [.. r["types"]!.AsArray().Select(Py.L)]),
            var c => throw new InvalidDataException($"facts: unknown rank category {c}"),
        })];
        var daily = Obj("daily_missions");
        DailyCycle = [.. (daily["cycle"] as JsonArray ?? []).Select(Py.L)];
        DailyDifficulties = [.. (daily["difficulties"] as JsonArray ?? []).Select(d => new DailyDifficulty(Py.L(d!["id"]), (string)d["title"]!, (string)d["alias"]!,
            Py.L(d["cooldown"]), (string)d["color"]!, Py.L(d["reward_id"]), Py.L(d["reward"])))];
        DailyTemplates = [.. (daily["templates"] as JsonArray ?? []).Select(t => new DailyTemplate(Py.L(t!["id"]), Py.L(t["difficulty"]), (string)t["title"]!,
            Py.L(t["giver"]!["id"]), (string)t["giver"]!["alias"]!, [.. t["objectives"]!.AsArray().Select(o => (string)o!)]))];
    }

    public static Facts Load(string path) => Parse(File.ReadAllText(path));

    public static Facts Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("facts: not a JSON object");
        return root["format"] is JsonValue f && Py.Eq(f, JsonValue.Create(1)) ? new Facts(root) : throw new InvalidDataException("facts: unknown format");
    }

    JsonObject Obj(string key) => root[key] as JsonObject ?? [];
    JsonArray Arr(string key) => root[key] as JsonArray ?? [];

    /// <summary>The animal ids in id order: the index is the catalogue id and the index of `spawn_licenses`.</summary>
    public readonly string[] Animals;

    /// <summary>Species id → score family (Scoring).</summary>
    public readonly Dictionary<int, string> Families = [];

    /// <summary>A row of the hunterscore: its `id` (the game's ranks reply names it so) and `title`; `animal` (Ids = the species id), `weapon` (the weapon
    /// item ids, all ammo summed) or `collectable` (the collectable type ids). Its value counts up to <see cref="RankCap"/>.</summary>
    public sealed record Rank(long Id, string Title, string Category, long[] Ids);
    /// <summary>The rank rows of the hunterscore (`ranks.rows`); empty when the file has none.</summary>
    public readonly Rank[] Ranks;
    /// <summary>The most a rank row adds to the hunterscore (`ranks.cap`); 0 when the file has no table.</summary>
    public readonly long RankCap;

    /// <summary>A difficulty of the daily missions (`daily_missions.difficulties`): its id, names, the cooldown and colour the calendar shows, the id of its reward row and its gm$ reward in the first 16-day cycle.</summary>
    public sealed record DailyDifficulty(long Id, string Title, string Alias, long Cooldown, string Color, long RewardId, long Reward);
    /// <summary>A daily mission template (`daily_missions.templates`): its id (a sequence of its own), difficulty id, title, the giver's character id and alias, and the English titles of its objectives.</summary>
    public sealed record DailyTemplate(long Id, long Difficulty, string Title, long Giver, string GiverAlias, string[] Objectives);
    /// <summary>The difficulty id of each day of the streak cycle (`daily_missions.cycle`); empty when the file has no daily missions.</summary>
    public readonly long[] DailyCycle;
    public readonly DailyDifficulty[] DailyDifficulties;
    public readonly DailyTemplate[] DailyTemplates;

    /// <summary>The names of the reserves (in id order, joined by ", ") whose `species` holds the species id.</summary>
    public string ReserveNames(long species) => string.Join(", ", Arr("reserves").Where(r => (r!["species"] as JsonArray ?? []).Any(s => Py.L(s) == species))
        .OrderBy(r => Py.L(r!["id"])).Select(r => (string)r!["name"]!));

    /// <summary>{texture id: name} of the kill page.</summary>
    public Dictionary<long, string> Textures => Obj("textures").ToDictionary(kv => long.Parse(kv.Key), kv => (string)kv.Value!);

    /// <summary>{locale key: English text} of the kill page.</summary>
    public JsonObject Labels => (JsonObject)Obj("labels").DeepClone();

    /// <summary>The ids of the reserves that have populations.</summary>
    public IEnumerable<int> Reserves => Arr("reserves").Select(r => (int)Py.L(r!["id"]));

    /// <summary>`reserves` of the application catalogue: id and name.</summary>
    public JsonArray ApplicationReserves() =>
        new(Arr("reserves").Select(r => (JsonNode?)new JsonObject { ["id"] = r!["id"]!.DeepClone(), ["name"] = r["name"]!.DeepClone() }).ToArray());

    /// <summary>The world box the reserve's map picture covers (`reserves[].map`: world x and z of its left, top, right and bottom edges), or null when the file has none or an empty one.</summary>
    public double[]? Map(long reserve) =>
        Arr("reserves").FirstOrDefault(r => Py.L(r!["id"]) == reserve)?["map"] is JsonArray { Count: 4 } m && m.All(Py.IsNum) && Py.D(m[2]) > Py.D(m[0]) && Py.D(m[3]) > Py.D(m[1])
            ? [.. m.Select(Py.D)] : null;

    /// <summary>`locations` of the application catalogue: the start places with their position.</summary>
    public JsonArray ApplicationLocations() => new(Arr("locations").Select(l => (JsonNode?)new JsonObject
    {
        ["id"] = l!["id"]!.DeepClone(), ["type"] = l["type"]?.DeepClone(), ["reserve"] = l["reserve"]?.DeepClone(),
        ["pos"] = new JsonObject { ["x"] = l["x"]!.DeepClone(), ["y"] = l["y"]!.DeepClone(), ["z"] = l["z"]!.DeepClone() },
    }).ToArray());

    /// <summary>The `populations` and `species_ammo` of a connect reply for this reserve, as the client reads them (the
    /// keys of State's per-reserve data); ammo is the species' entry of `ethical` (the connect's permitted ammo by species
    /// id). Null for a reserve the file does not know.</summary>
    public byte[]? ReserveConnect(int reserve, JsonArray ethical)
    {
        var r = Arr("reserves").FirstOrDefault(r => Py.L(r!["id"]) == reserve);
        if (r is null) return null;
        var populations = new JsonArray();
        foreach (var p in r["populations"]!.AsArray())
            populations.Add(Population(p!.AsObject(), reserve));
        var ammo = new JsonArray();
        foreach (var id in r["species"]!.AsArray())
            ammo.Add(new JsonObject
            {
                ["species_id"] = Py.Str(id), ["ammo"] = Py.L(id) < ethical.Count ? ethical[(int)Py.L(id)]?.DeepClone() : new JsonArray(),
            });
        return Py.DumpBytes(new JsonObject { ["populations"] = populations, ["species_ammo"] = ammo });
    }

    /// <summary>What the event <paramref name="name"/> adds on the game's reserve number <paramref name="reserve"/> (`events.<name>.reserves.<reserve>`): the permitted ammo
    /// every species of the reserve lists in `species_ammo`, and populations as the client reads them (their `reserve` is the web's number, the game's + 1). Null for an event or a reserve
    /// the file has nothing for.</summary>
    public (long[] Ammo, JsonObject[] Populations)? Event(string name, int reserve)
    {
        if (Obj("events")[name]?["reserves"]?[reserve.ToString(CultureInfo.InvariantCulture)] is not JsonObject content) return null;
        long[] ammo = [.. (content["ammo"] as JsonArray ?? []).Select(Py.L)];
        JsonObject[] populations = [.. (content["populations"] as JsonArray ?? []).Select(p => Population(p!.AsObject(), reserve + 1))];
        return (ammo, populations);
    }

    /// <summary>The facts of the event <paramref name="name"/> (`events.<name>`): `title`, `missions`, `community`, `items`, `reserves`; null for an event the file lacks.</summary>
    public JsonObject? EventOf(string name) => Obj("events")[name] as JsonObject;

    /// <summary>The `index` of every population any event adds on the game's reserve number <paramref name="reserve"/>.</summary>
    public IEnumerable<long> EventIndexes(int reserve) => Obj("events").SelectMany(e => (e.Value?["reserves"]?[reserve.ToString(CultureInfo.InvariantCulture)]?["populations"] as JsonArray ?? [])
        .Select(p => Py.L(p!["index"])));

    // the client's population row; fields the file leaves out are the client's defaults (null / -1)
    static JsonObject Population(JsonObject p, int reserve)
    {
        var index = Py.L(p["index"]);
        var groups = new JsonArray();
        foreach (var g in p["groups"]!.AsArray())
            groups.Add(new JsonObject
            {
                ["pos_x"] = g!["x"]!.DeepClone(), ["pos_z"] = g["z"]!.DeepClone(), ["radius"] = g["radius"]!.DeepClone(),
                ["males"] = g["males"]!.DeepClone(), ["females"] = g["females"]!.DeepClone(), ["enabled"] = g["enabled"]?.DeepClone(),
                ["feature"] = -1, ["texture_id"] = -1, ["min_weight"] = null, ["max_weight"] = null, ["health"] = null,
                ["wounded"] = null, ["smell_sense_scale"] = null, ["hearing_sense_scale"] = null, ["vision_sense_scale"] = null,
            });
        JsonNode? random = null;
        if (p["random"] is JsonObject r)
            random = new JsonObject
            {
                ["males"] = r["males"]!.DeepClone(), ["females"] = r["females"]!.DeepClone(),
                ["min_group_size"] = r["min_size"]!.DeepClone(), ["max_group_size"] = r["max_size"]!.DeepClone(),
                ["average_group_size"] = r["avg_size"]!.DeepClone(), ["max_males_in_group"] = r["max_males"]!.DeepClone(),
                ["max_females_in_group"] = r["max_females"]!.DeepClone(), ["radius"] = r["radius"]!.DeepClone(),
                ["single_gender_group"] = r["single_gender"]!.DeepClone(),
            };
        return new JsonObject
        {
            ["key"] = $"facts-{index}", ["index"] = index, ["animal"] = p["animal"]!.DeepClone(),
            ["density_map"] = (p["density"] ?? p["animal"])!.DeepClone(), ["endangered"] = p["endangered"]!.DeepClone(),
            ["spawn_license"] = p["license"]!.DeepClone(), ["active_start"] = null, ["active_end"] = null,
            ["reserve"] = p["reserve"]?.DeepClone() ?? reserve, ["groups"] = groups, ["random_groups"] = random,
        };
    }

    /// <summary>The application catalogue's `items` from the install's item definitions (item.json): the numeric type,
    /// group, category and client type from the file's enum tables, the slots and capacity of the item's type.
    /// ponytail: no `fit` lists (empty), and an item is not for sale when it costs 0 (that matches the catalogue for
    /// all but a dozen items); both only matter to the store pages and to the definitions of bought items.</summary>
    public JsonArray ApplicationItems(JsonArray definitions)
    {
        var types = Obj("types");
        var items = new JsonArray();
        foreach (var d in definitions.OfType<JsonObject>())
        {
            var type = types[(string?)d["type"] ?? ""] as JsonObject;
            var price = d["price"]?.DeepClone();
            var units = d["units"] is { } u && Py.IsNum(u) && Py.D(u) > 0
                ? new JsonObject { ["count"] = u.DeepClone(), ["name"] = d["unit_name"]?.DeepClone() } : null;
            var weight = (d["inventory"] as JsonObject)?["weight"]?.DeepClone() ?? 0;
            items.Add(new JsonObject
            {
                ["id"] = d["id"]!.DeepClone(), ["define"] = d["define"]?.DeepClone(), ["name"] = d["name"]?.DeepClone(),
                ["description"] = d["description"]?.DeepClone(), ["variationname"] = d["variationname"]?.DeepClone(),
                ["shortname"] = d["shortname"]?.DeepClone(), ["image"] = d["image"]?.DeepClone(),
                ["sponsor_image"] = d["sponsor_image"]?.DeepClone(),
                ["type"] = type?["id"]?.DeepClone(),
                ["group"] = Obj("groups")[(string?)d["group"] ?? ""]?.DeepClone(),
                ["cat"] = Obj("categories")[(string?)d["cat"] ?? ""]?.DeepClone(),
                ["client_type"] = (d["client_type"] is { } ct ? Obj("client_types")[(string?)ct ?? ""] : type?["id"])?.DeepClone(),
                ["price"] = price, ["silver_price"] = d["silver_price"]?.DeepClone(),
                ["notbuyable"] = price is not null && Py.D(price) == 0 ? 1 : 0,
                ["units"] = units, ["fit"] = new JsonObject(),
                ["inventory"] = new JsonObject
                {
                    ["weight"] = weight, ["slots"] = type?["slots"]?.DeepClone() ?? new JsonArray(), ["capacity"] = type?["capacity"]?.DeepClone() ?? 0,
                    ["inbackpack"] = true, ["slotUnlock"] = null,
                },
            });
        }
        return items;
    }

    /// <summary>Where a start item is worn or held: the first slot of its type above the backpack (0) and the pockets (1) that is not in <paramref name="taken"/>; 0 (the backpack) when there is none.</summary>
    public long StartSlot(string? type, ISet<long> taken) =>
        (Obj("types")[type ?? ""]?["slots"] as JsonArray ?? []).Select(Py.L).FirstOrDefault(s => s > 1 && !taken.Contains(s));

    /// <summary>The item ids and the wallet of a new account.</summary>
    public (long[] Items, JsonObject Wallet) Start()
    {
        var start = Obj("start");
        return ([.. (start["items"] as JsonArray ?? []).Select(Py.L)], (JsonObject)(start["wallet"] as JsonObject ?? []).DeepClone());
    }
}
