using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Backup;

/// <summary>The "setups" group: the equipment a player placed on each map, read last and written as map-setups.json beside the raw replies.</summary>
sealed partial class Engine
{
    const string SetupsFile = "map-setups.json";
    const int WorldSlot = 23; // a placed item is an inventory row in slot 23; picked-up rows can keep an old location

    /// <summary>Placed items in map-setups.json; null = the file was not written (both inventory reads failed).</summary>
    public int? SetupItems { get; private set; }
    /// <summary>Maps (reserve groups) in map-setups.json.</summary>
    public int SetupReserves { get; private set; }
    /// <summary>A check found something: sources disagree or a placed item has no position. A failed read is not one: it is in the failure count.</summary>
    public bool SetupChecks { get; private set; }

    static readonly JsonSerializerOptions SetupJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // item kinds by define (patterns of the class table in the client notes); the first match wins
    static readonly (string Kind, Regex Rx)[] Kinds =
    [
        ("tent", new(@"^TENT\d*$", RegexOptions.IgnoreCase)),
        ("tree_stand", new("TREE_STAND", RegexOptions.IgnoreCase)),
        ("tripod", new("TRIPOD_STAND", RegexOptions.IgnoreCase)),
        ("shooting_rest", new("TRIPOD_REST", RegexOptions.IgnoreCase)),
        ("blind", new("GROUND_BLIND|WATERFOWL_BLIND", RegexOptions.IgnoreCase)),
        ("tower", new("DEPLOYABLE_HUNTING_TOWER", RegexOptions.IgnoreCase)),
        ("feeder", new("BAIT_BARREL|FEEDER", RegexOptions.IgnoreCase)),
        ("decoy", new("DECOY", RegexOptions.IgnoreCase)),
        ("caller", new("ELECTRONIC_CALLER", RegexOptions.IgnoreCase)),
        ("target", new("DEPLOYABLE_TARGET", RegexOptions.IgnoreCase)),
        ("mat", new("DEPLOYABLE_SHOOTING_MAT", RegexOptions.IgnoreCase)),
        ("flag", new("MARKER_FLAG", RegexOptions.IgnoreCase)),
        ("climbing", new("CLIMBING_GEAR", RegexOptions.IgnoreCase)),
    ];

    static string KindOf(string? define) => define == null ? "other" : Kinds.FirstOrDefault(k => k.Rx.IsMatch(define)).Kind ?? "other";

    /// <summary>One placed row: <paramref name="Info"/> is the catalogue entry that comes with an Inventory/list row.</summary>
    sealed record Row(string Guid, int? ItemId, JsonObject Node, JsonObject? Info);

    /// <summary>What one inventory reply holds: all rows, the placed rows with a position, and the placed rows without one.</summary>
    sealed class Src(byte[] body)
    {
        public readonly byte[] Body = body;
        public int Rows;
        public readonly List<Row> Placed = [];
        public readonly List<string> NoLocation = [];
        public readonly List<Row> NoLocationRows = [];

        public void Take(JsonNode? n, int? itemId, JsonObject? info)
        {
            if (n is not JsonObject row) return;
            Rows++;
            if (Int(row["slot"]) != WorldSlot) return;
            var guid = row["id"]?.ToString() ?? "";
            if (row["location"] is JsonObject { } loc && loc["pos"] is JsonObject) Placed.Add(new(guid, itemId, row, info));
            else { NoLocation.Add(guid); NoLocationRows.Add(new(guid, itemId, row, info)); }
        }
    }

    static int? Int(JsonNode? n)
    {
        try { return n?.GetValue<int>(); } catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; }
    }

    static double? Dbl(JsonNode? n)
    {
        try { return n?.GetValue<double>(); } catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Inventory/dict: {item id: [rows]}. An empty map can arrive as []. Null = not a reply of that shape.</summary>
    static Src? ReadDict(byte[]? body)
    {
        if (body == null) return null;
        var j = Json(body);
        var s = new Src(body);
        if (j is JsonArray { Count: 0 }) return s;
        if (j is not JsonObject o) return null;
        foreach (var (k, v) in o)
            foreach (var row in v as JsonArray ?? [])
                s.Take(row, int.TryParse(k, out var id) ? id : null, null);
        return s;
    }

    /// <summary>Inventory/list: [{item{...}, own[rows]}].</summary>
    static Src? ReadList(byte[]? body)
    {
        if (body == null || Json(body) is not JsonArray a) return null;
        var s = new Src(body);
        foreach (var e in a)
            foreach (var row in e?["own"] as JsonArray ?? [])
                s.Take(row, Int(e?["item"]?["id"]), e?["item"] as JsonObject);
        return s;
    }

    /// <summary>Item and reserve names from the saved catalogue, when the profile group saved it (this run or an earlier one).</summary>
    (Dictionary<int, (string? Define, string? Name)> Items, Dictionary<int, string?> Reserves)? Catalogue()
    {
        var p = Path.Combine(dir, FileName(Q("Application/application", false, ("lang", "en"))));
        if (!File.Exists(p) || Json(File.ReadAllBytes(p)) is not JsonObject c) return null;
        var items = new Dictionary<int, (string?, string?)>();
        foreach (var i in c["items"] as JsonArray ?? [])
            if (Int(i?["id"]) is { } id) items[id] = (i!["define"]?.ToString(), i["name"]?.ToString());
        var reserves = new Dictionary<int, string?>();
        foreach (var r in c["reserves"] as JsonArray ?? [])
            if (Int(r?["id"]) is { } id) reserves[id] = r!["name"]?.ToString();
        return (items, reserves);
    }

    async Task Setups()
    {
        // both always asked, whatever resume found on disk: the setup may have changed since an earlier run
        var dict = await Get(Q("Inventory/dict", false, ("user_id", uid)) with { Fresh = true });
        var list = await Get(Q("Inventory/list", false, ("user_id", uid)) with { Fresh = true });
        // names and kinds come from the catalogue: the profile group's file, fetched here when the group was off (same request, same file)
        if (s.ConsumerKey is { } key && !File.Exists(Path.Combine(dir, FileName(Q("Application/application", false, ("lang", "en"))))))
        {
            Need(1);
            await Get(Q("Application/application", false, ("oauth_consumer_key", key), ("lang", "en")));
        }
        BuildSetups(ReadDict(dict), ReadList(list));
    }

    void BuildSetups(Src? dict, Src? list)
    {
        var full = Path.Combine(dir, SetupsFile);
        if (dict == null && list == null)
        {
            // nothing fresh to build from: an older file would describe another day
            File.Delete(full);
            lock (files) files.Remove(SetupsFile);
            Fail(new R(SetupsFile), SetupsFile, null, "no inventory reply to build it from");
            return;
        }
        var cat = Catalogue();

        // me: the identity reply's placed items (own account only); it has a position but no direction
        var meRows = own ? me?["worlditems"] as JsonArray : null;
        var seen = new SortedDictionary<string, List<(string Src, JsonNode? Pos)>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, Row>();
        void Add(string src, string guid, JsonNode? pos)
        {
            if (!seen.TryGetValue(guid, out var l)) seen[guid] = l = [];
            l.Add((src, pos));
        }
        // the list wins when both hold a row: the dict can lag a change (a feeder refill showed in the list a minute before the dict); a row only the dict has is kept
        foreach (var r in dict?.Placed ?? []) { Add("dict", r.Guid, r.Node["location"]!["pos"]); rows.TryAdd(r.Guid, r); }
        foreach (var r in list?.Placed ?? []) { Add("list", r.Guid, r.Node["location"]!["pos"]); rows[r.Guid] = r; }
        foreach (var w in meRows ?? []) Add("me", w?["id"]?.ToString() ?? "", w?["pos"]);

        var avail = new List<string>();
        if (dict != null) avail.Add("dict");
        if (list != null) avail.Add("list");
        if (meRows != null) avail.Add("me");
        var onlyIn = avail.ToDictionary(s => s, s => seen.Where(g => g.Value.Any(x => x.Src == s) && avail.Any(t => g.Value.All(x => x.Src != t))).Select(g => g.Key).ToList());
        var differs = seen.Where(g => g.Value.Select(x => (Dbl(x.Pos?["x"]), Dbl(x.Pos?["y"]), Dbl(x.Pos?["z"]))).Where(p => p is (not null, not null, not null)).Distinct().Count() > 1).Select(g => g.Key).ToList();
        // metadata, units or dir that differ between the two replies, compared as the server wrote them
        static string Txt(JsonNode? n) => n?.ToJsonString() ?? "null";
        var dictRows = (dict?.Placed ?? []).GroupBy(r => r.Guid).ToDictionary(g => g.Key, g => g.First().Node);
        var metaDiffers = (list?.Placed ?? []).Where(r => dictRows.TryGetValue(r.Guid, out var d)
                && (Txt(d["metadata"]) != Txt(r.Node["metadata"]) || Txt(d["units"]) != Txt(r.Node["units"]) || Txt(d["location"]?["dir"]) != Txt(r.Node["location"]?["dir"])))
            .Select(r => r.Guid).Distinct().Order(StringComparer.Ordinal).ToList();
        var noLoc = (dict?.NoLocation ?? []).Concat(list?.NoLocation ?? []).Distinct().Order(StringComparer.Ordinal).ToList();

        // a position without a direction: the item is saved, a restore has to invent a direction
        var dirNull = rows.Values.Where(r => r.Node["location"]!["dir"] == null).Select(r => r.Guid).Order(StringComparer.Ordinal).ToList();

        // the whole row of a placed item without a position (list over dict, as above), so the owner can be given the item back
        var lost = new Dictionary<string, Row>();
        foreach (var r in (dict?.NoLocationRows ?? []).Concat(list?.NoLocationRows ?? [])) lost[r.Guid] = r;
        var lostRows = lost.Values.OrderBy(r => r.ItemId).ThenBy(r => r.Guid, StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject
        {
            ["guid"] = r.Guid, ["item_id"] = r.ItemId, ["units"] = r.Node["units"]?.DeepClone(), ["metadata"] = r.Node["metadata"]?.DeepClone(),
        }).ToList();

        var items = rows.Values.OrderBy(r => r.ItemId).ThenBy(r => r.Guid, StringComparer.Ordinal).Select(r =>
        {
            var (define, name) = r.ItemId is { } id && cat?.Items.TryGetValue(id, out var c) == true ? c : (null, null);
            define ??= r.Info?["define"]?.ToString();
            name ??= r.Info?["name"]?.ToString();
            var loc = r.Node["location"]!;
            return (Reserve: Int(loc["reserve"]), Json: new JsonObject
            {
                ["guid"] = r.Guid, ["item_id"] = r.ItemId, ["define"] = define, ["name"] = name, ["kind"] = KindOf(define),
                ["units"] = r.Node["units"]?.DeepClone(), // units = cost of the anchor the path is bound to (20 or 25); other values exist and are valid, all kept as sent
                ["pos"] = loc["pos"]!.DeepClone(), ["dir"] = loc["dir"]?.DeepClone(), ["metadata"] = r.Node["metadata"]?.DeepClone(),
                ["in"] = new JsonArray([.. seen[r.Guid].Select(x => x.Src).Distinct().Select(x => (JsonNode)x)]),
            }, Kind: KindOf(define));
        }).ToList();
        var reserves = items.GroupBy(i => i.Reserve).OrderBy(g => g.Key is null).ThenBy(g => g.Key).ToList();

        JsonNode Source(Src? s, string file) => s == null ? "unavailable" : new JsonObject
        {
            ["file"] = file, ["sha256"] = Hex(SHA256.HashData(s.Body)), ["rows"] = s.Rows, ["slot_23"] = s.Placed.Count + s.NoLocation.Count,
        };
        string meFile = FileName(Q("Me/me", true));
        JsonNode meSource;
        if (!own) meSource = "skipped: public profile";
        else if (meRows == null) meSource = "unavailable";
        else
        {
            string? sha; lock (files) sha = files.GetValueOrDefault(meFile)?.Sha256;
            meSource = new JsonObject { ["file"] = meFile, ["sha256"] = sha, ["rows"] = meRows.Count };
        }
        static JsonArray Guids(IEnumerable<string> g) => new([.. g.Select(x => (JsonNode)x)]);

        var root = new JsonObject
        {
            ["format"] = 1, ["tool_version"] = Version, ["uid"] = uid,
            ["sources"] = new JsonObject
            {
                ["dict"] = Source(dict, FileName(Q("Inventory/dict", false, ("user_id", uid)))),
                ["list"] = Source(list, FileName(Q("Inventory/list", false, ("user_id", uid)))),
                ["me_worlditems"] = meSource,
            },
            ["totals"] = new JsonObject
            {
                ["placed"] = items.Count, ["reserves"] = reserves.Count,
                ["by_kind"] = new JsonObject(items.GroupBy(i => i.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            },
            ["reserves"] = new JsonArray([.. reserves.Select(g => (JsonNode)new JsonObject
            {
                ["reserve"] = g.Key, ["name"] = g.Key is { } k && cat?.Reserves.GetValueOrDefault(k) is { } n ? n : null, ["count"] = g.Count(),
                ["items"] = new JsonArray([.. g.Select(i => (JsonNode)i.Json)]),
            })]),
            ["checks"] = new JsonObject
            {
                ["location_null"] = Guids(noLoc),
                ["location_null_rows"] = new JsonArray([.. lostRows]),
                ["only_in"] = new JsonObject(onlyIn.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Guids(p.Value)))),
                ["pos_differs"] = Guids(differs),
                ["metadata_differs"] = Guids(metaDiffers),
                ["dir_null"] = Guids(dirNull),
            },
        };

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(SetupJson));
        WriteAtomic(full, bytes);
        lock (files) files[SetupsFile] = new(SetupsFile, "derived", [], null, Hex(SHA256.HashData(bytes)), DateTime.UtcNow, null, true);
        Interlocked.Increment(ref saved);
        (SetupItems, SetupReserves) = (items.Count, reserves.Count);
        SetupChecks = noLoc.Count > 0 || differs.Count > 0 || metaDiffers.Count > 0 || dirNull.Count > 0 || onlyIn.Values.Any(v => v.Count > 0);
        if (noLoc.Count > 0) log($"! map setups: {noLoc.Count} placed item(s) without a position");
        foreach (var (src, g) in onlyIn.Where(p => p.Value.Count > 0)) log($"! map setups: {g.Count} item(s) in {src} are missing from another source");
        if (differs.Count > 0) log($"! map setups: {differs.Count} item(s) with different positions in two sources");
        if (dirNull.Count > 0) log($"! map setups: {dirNull.Count} placed item(s) without a direction");
        if (metaDiffers.Count > 0) log($"! map setups: {metaDiffers.Count} item(s) with different metadata, units or direction in the two inventory replies (the list was used)");
        log($"map setups: {items.Count} placed item(s) on {reserves.Count} map(s)");
    }
}
