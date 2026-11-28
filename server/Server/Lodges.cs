// Lodge edits and trophy visibility (community server): the lodge rows a restore wrote or a store purchase made, edited in place, and the
// lodge reads of any account. A lodge exists when its rows do; a restore brings its own and the store item of a lodge type adds one (<see cref="GrantLodge"/>).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    internal const string LodgeList = "/game/user/lodges", LodgeOne = "/game/user/lodge";
    const int MaxText = 100, MaxSlots = 200;  // generous (the largest lodge has 163 animal slots): other players download the whole lodge

    /// <summary>Deliver the items (<see cref="Deliver"/>), and a lodge for each unit of a lodge item of the facts file (inside the caller's transaction); the new rows' guids.</summary>
    long[] Give(Db con, IEnumerable<long> itemIds)
    {
        long[] ids = [.. itemIds];
        var guids = Deliver(con, ids, ItemInfo);
        foreach (var id in ids)
            if (facts.LodgeOfItem(id) is { } type)
                GrantLodge(con, id, type);
        return guids;
    }

    /// <summary>One new lodge of the layout, named for it, unlocked, empty, style 0: the two rows of a lodge (the list's and the single's) under the account's next lodge id.</summary>
    static void GrantLodge(Db con, long item, (long Engine, string Name, string Define) type)
    {
        var id = (long)con.Scalar("SELECT COALESCE(MAX(CAST(id AS INTEGER)), 0) + 1 FROM lodges WHERE account_id = $a")!;
        var row = new JsonObject
        {
            ["id"] = id, ["locked"] = false, ["lodge_engine_id"] = type.Engine, ["lodge_type_define"] = type.Define, ["lodge_type_id"] = item, ["name"] = type.Name, ["style_id"] = 0,
        };
        PlayerRows.AddLodge(con, LodgeList, id.ToString(), row);
        row["weapon_slots_unlocked"] = false;
        row["animal_slots"] = new JsonArray();
        row["weapon_slots"] = new JsonArray();
        PlayerRows.AddLodge(con, LodgeOne, id.ToString(), row);
    }

    /// <summary>At start (community servers): <see cref="BackfillLodges(long)"/> for every account.</summary>
    public void BackfillLodges()
    {
        foreach (var account in Store.AccountIds())
            BackfillLodges(account);
    }

    /// <summary>An account that owns more units of a lodge item than it has lodges of that type (a purchase made before the store added lodges, or a restore's inventory)
    /// gets the missing lodges (<see cref="GrantLodge"/>). A lodge counts once by id, with the first usable `lodge_type_id` of its rows (the list's first; `780`, `780.0` or `"780"`).
    /// Run at start and after each restore. Idempotent: a second run finds none. An account whose data fails is left as it was (its transaction is dropped).</summary>
    public void BackfillLodges(long account)
    {
        try
        {
            using var con = Con(account);
            con.Begin();
            var types = new Dictionary<long, long>();  // lodge id -> type (0: none usable yet)
            foreach (var r in con.Rows("SELECT CAST(id AS INTEGER), node FROM lodges WHERE account_id = $a AND source IN (?, ?) ORDER BY source = ? DESC", LodgeList, LodgeOne, LodgeList))
            {
                var id = (long)r[0];  // the allocator's reading of an id: "03" and "3.0" are one lodge
                if (types.GetValueOrDefault(id) == 0)
                    types[id] = JsonNode.Parse((string)r[1]) is JsonObject node && (TryKey(node["lodge_type_id"], out var type) || Py.IsStr(node["lodge_type_id"]) && long.TryParse((string)node["lodge_type_id"]!, out type)) ? type : 0;
            }
            var have = types.Values.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            foreach (var r in con.Rows("SELECT item_id, COUNT(*) FROM items WHERE account_id = $a GROUP BY item_id"))
                if (facts.LodgeOfItem((long)r[0]) is { } type)
                    for (var missing = (long)r[1] - have.GetValueOrDefault((long)r[0]); missing > 0; missing--)
                        GrantLodge(con, (long)r[0], type);
            FitStored(con);
            NoticeBigLodge(con);
            con.Commit();
        }
        catch (Exception e)  // restored rows are player uploads: whatever shape throws, only that account is skipped
        {
            Console.WriteLine($"lodges: account {account} left as it was ({e.Message})");
        }
    }

    /// <summary>The stored lodges of the account given the slot fields of <see cref="Fit"/> (rows saved before the server set them). A row that already has them is not written.</summary>
    void FitStored(Db con)
    {
        var rows = con.Rows("SELECT n, CAST(id AS INTEGER), node FROM lodges WHERE account_id = $a AND source IN (?, ?)", LodgeList, LodgeOne)
            .Select(r => (N: r[0], Id: (long)r[1], Node: JsonNode.Parse((string)r[2]) as JsonObject)).Where(r => r.Node is not null).ToList();
        var engines = new Dictionary<long, JsonNode?>();  // the list's row and the single's name the layout alike; either may lack it
        foreach (var r in rows)
            if (Py.IsNum(r.Node!["lodge_engine_id"])) engines.TryAdd(r.Id, r.Node["lodge_engine_id"]);
        foreach (var r in rows)
            if (Fit(r.Node!, engines.GetValueOrDefault(r.Id)))
                con.Exec("UPDATE lodges SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(r.Node!), r.N);
    }

    /// <summary>A lodge with this many mounted trophies or more gets its owner the inbox message of <see cref="NoticeBigLodge"/> (#974).</summary>
    internal const int BigLodge = 75;
    /// <summary>The message's opening words are the once-only marker (<see cref="NoticeBigLodge"/> looks for them in the notice sender's messages): never reword them, or every qualifying owner gets the message again. Words after them may change.</summary>
    const string BigLodgeNoticeStart = "Your trophy lodge ";
    const int NoticeNameChars = 60;

    /// <summary>
    /// The account owns a lodge with <see cref="BigLodge"/> mounted trophies or more: one inbox message from the notice sender, naming its largest such lodge (the lowest id on a tie). The game keeps a lodge's trophies in memory after the player
    /// leaves it, and with about 100 or more a hunt can run out of the 32-bit game's memory (docs/server.md, "Lodges and trophy visibility"). Once per account, ever: the message is its own marker (a restore keeps messages), so a later start, restore or save finds it and sends none.
    /// A mounted trophy is an animal slot with a `trophy_id`, counted once per id over the lodge's two rows. Nothing is limited or hidden: the lodge is sent whole. Inside the caller's transaction (start, restore, a lodge save).
    /// </summary>
    static void NoticeBigLodge(Db con)
    {
        // a notice is a courtesy: whatever its reading of restored rows throws, it is logged and the caller's work (a lodge save, the start's lodge pass) goes on
        try
        {
            if (con.Scalar("SELECT 1 FROM messages WHERE account_id = $a AND sender = ? AND content LIKE ?", SYSTEM_SENDER, BigLodgeNoticeStart + "%") is not null) return;
            var mounted = new Dictionary<long, HashSet<string>>();  // lodge id (as the allocator reads it) -> the trophy ids its slots hold
            var names = new Dictionary<long, string>();
            foreach (var r in con.Rows("SELECT CAST(id AS INTEGER), node FROM lodges WHERE account_id = $a AND source IN (?, ?) ORDER BY source = ? DESC, n", LodgeOne, LodgeList, LodgeOne))
            {
                if (JsonNode.Parse((string)r[1]) is not JsonObject node) continue;
                var id = (long)r[0];
                if (Py.IsStr(node["name"])) names.TryAdd(id, ((string)node["name"]!).Trim());
                if (!mounted.TryGetValue(id, out var held)) mounted[id] = held = [];
                foreach (var slot in (node["animal_slots"] as JsonArray ?? []).OfType<JsonObject>())
                    if (Py.Plain(slot["trophy_id"]) is { } trophy) held.Add(Py.Str(trophy));  // only a plain id: an object or an array in a restored slot is no trophy
            }
            var (largest, count) = mounted.OrderByDescending(m => m.Value.Count).ThenBy(m => m.Key).Select(m => (m.Key, m.Value.Count)).FirstOrDefault();
            if (count < BigLodge) return;
            var name = names.GetValueOrDefault(largest) is { Length: > 0 } n ? n : $"#{largest}";
            if (name.Length > NoticeNameChars) name = name[..(char.IsHighSurrogate(name[NoticeNameChars - 1]) ? NoticeNameChars - 1 : NoticeNameChars)] + "…";
            Uploads.Notify(con, $"{BigLodgeNoticeStart}{name} holds {count} trophies. The game keeps a lodge's trophies in memory after you leave it, and with about 100 or more it can close during the next hunt. "
                + "To avoid it: keep a lodge under about 60 trophies (move some to another lodge), or restart the game after visiting it before you hunt.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"lodges: no big lodge notice ({e.Message})");
        }
    }

    /// <summary>The saved `lodge_contents` of a section save that was restored before it was read as lodges: kept whole in `extras` under this name.</summary>
    const string LodgeContentsExtra = "json_lodge_contents.json";

    /// <summary>`admin backfill-lodges`: for every account (or `only`) with <see cref="LodgeContentsExtra"/> in its extras, each listed lodge (a `/game/user/lodges` row) that has no `/game/user/lodge` row gets the
    /// saved contents as that row (the animals it mounts are shown as trophies at read time, <see cref="MountedRows"/>). A lodge that has contents is never touched, so a second run adds nothing. One line per
    /// account and lodge. Without `apply` the same rows are made and dropped: nothing is written.</summary>
    public List<string> BackfillLodgeContents(bool apply, long? only = null)
    {
        var lines = new List<string>();
        foreach (var account in only is { } one ? [one] : Store.AccountIds())
        {
            try
            {
                using var con = Con(account);
                con.Begin();
                if (con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = ?", LodgeContentsExtra) is not string body || JsonNode.Parse(body) is not JsonObject saved) continue;
                var contents = saved.Where(kv => long.TryParse(kv.Key, out _) && kv.Value is JsonObject).ToLookup(kv => long.Parse(kv.Key), kv => (JsonObject)kv.Value!);
                var held = con.Rows("SELECT CAST(id AS INTEGER) FROM lodges WHERE account_id = $a AND source = ?", LodgeOne).Select(r => (long)r[0]).ToHashSet();
                foreach (var r in con.Rows("SELECT id, CAST(id AS INTEGER) FROM lodges WHERE account_id = $a AND source = ? ORDER BY 2, n", LodgeList))
                {
                    var (id, number) = ((string)r[0], (long)r[1]);
                    if (!held.Add(number) || contents[number].FirstOrDefault() is not { } lodge) continue;
                    PlayerRows.AddLodge(con, LodgeOne, id, lodge.DeepClone());
                    var slots = ((lodge["animal_slots"] as JsonArray)?.Count ?? 0) + ((lodge["weapon_slots"] as JsonArray)?.Count ?? 0);
                    lines.Add($"account {account} lodge {number}: {(apply ? "added" : "would add")}, {slots} slots");
                }
                if (apply) con.Commit();
            }
            catch (Exception e)  // restored rows are player uploads: whatever shape throws, only that account is skipped
            {
                lines.Add($"account {account}: left as it was ({e.Message})");
            }
        }
        return lines;
    }

    /// <summary>A reply of `/gameengine/trophylodge/<key>`; null for a key the facts file lacks.</summary>
    public byte[]? LodgeCatalogueReply(string key) => facts.LodgeCatalogue(key) is { } node ? Py.DumpBytes(node) : null;

    static readonly string[] AnimalSlotKeys = ["slot_id", "trophy_id", "pose_id", "rotation", "base_texture", "is_skull"];
    static readonly string[] WeaponSlotKeys = ["slot_id", "weapon_id", "rotation"];

    static readonly (string Key, Func<JsonNode?, JsonNode?> Check)[] SlotFields =
        [("name", Text), ("animal_slots", v => Slots(v, AnimalSlotKeys)), ("weapon_slots", v => Slots(v, WeaponSlotKeys))];

    static readonly (string Key, Func<JsonNode?, JsonNode?> Check)[] SettingFields =
        [("name", Text), ("locked", v => Is(v, JsonValueKind.True, JsonValueKind.False)), ("style_id", v => v is null ? null : Is(v, JsonValueKind.Number))];  // no style: null

    /// <summary>A copy of a value of one of these kinds; anything else (null too) is a bad request.</summary>
    static JsonNode Is(JsonNode? v, params JsonValueKind[] kinds) =>
        v is JsonValue j && kinds.Contains(j.GetValueKind()) ? v.DeepClone() : throw new FormatException();

    static JsonNode Text(JsonNode? v) => Is(v, JsonValueKind.String) is var text && text.GetValue<string>().Length <= MaxText ? text : throw new FormatException();

    /// <summary>The slots of a lodge: at most MaxSlots objects with a numeric `slot_id`, each reduced to the slot's own keys; a value is a number, true or false
    /// (`base_texture` also a short text, the name of its texture).</summary>
    static JsonNode Slots(JsonNode? v, string[] keys)
    {
        var list = v as JsonArray ?? throw new FormatException();
        if (list.Count > MaxSlots) throw new FormatException();
        var copy = new JsonArray();
        foreach (var item in list)
        {
            if (item is not JsonObject slot || !Py.IsNum(slot["slot_id"])) throw new FormatException();
            var kept = new JsonObject();
            foreach (var key in keys)
                if (slot.TryGetPropertyValue(key, out var value))
                    kept[key] = value is null ? null : key == "base_texture" && value is JsonValue { } t && t.GetValueKind() == JsonValueKind.String ? Text(value)
                        : Is(value, JsonValueKind.Number, JsonValueKind.True, JsonValueKind.False);
            copy.Add(kept);
        }
        return copy;
    }

    /// <summary>Slot types (`type` of a `lodge/<layout>` animal slot) that show a trophy as a full body: round displays (4, 8) and platforms (6, 7, 9). The catalogue
    /// does not carry it; every restored lodge agrees with this table, the other types (wall plaques, bird plaque, skull mounts) hang a head.</summary>
    static readonly long[] FullBodyTypes = [4, 6, 7, 8, 9];

    /// <summary>The lodge's animal slots as the game needs them, from the slot's type (the layout's `lodge/<engine>` slot of the same id): `is_full_body` on every slot,
    /// `rotation: 0` on a mounted slot of a type that can rotate (`slot/animal` `can_rotate`) that has none. True when something changed. A layout or slot the
    /// facts lack is left alone.</summary>
    bool Fit(JsonObject lodge, JsonNode? engine)
    {
        if (!TryKey(engine, out var layoutNumber) || facts.LodgeCatalogue($"lodge/{layoutNumber}")?["animal_slots"] is not JsonArray layout || lodge["animal_slots"] is not JsonArray slots) return false;
        var types = new Dictionary<long, long>();  // slot id -> type
        foreach (var place in layout.OfType<JsonObject>())
            if (TryKey(place["id"], out var id) && TryKey(place["type"], out var type)) types.TryAdd(id, type);
        var rotates = (facts.LodgeCatalogue("slot/animal") as JsonArray ?? []).OfType<JsonObject>().Where(t => t["can_rotate"] is JsonValue v && v.TryGetValue(out bool can) && can)
            .Select(t => Py.L(t["id"])).ToHashSet();
        var changed = false;
        foreach (var slot in slots.OfType<JsonObject>())
            if (TryKey(slot["slot_id"], out var slotId) && types.TryGetValue(slotId, out var type))
            {
                var full = FullBodyTypes.Contains(type);
                if (slot["is_full_body"] is not JsonValue flag || !flag.TryGetValue(out bool stored) || stored != full) (slot["is_full_body"], changed) = (full, true);  // a stored 1 or "true" is no flag: rewritten
                if (slot["trophy_id"] is not null && rotates.Contains(type) && !slot.ContainsKey("rotation")) (slot["rotation"], changed) = (0, true);
            }
        return changed;
    }

    /// <summary>Give each new slot the keys it lacks from the stored slot of the same `slot_id` when it holds the same trophy or weapon (`trophy_details`).
    /// The game saves a lodge without them whenever the player leaves it.</summary>
    static void Carry(JsonArray slots, JsonArray stored)
    {
        foreach (var slot in slots.OfType<JsonObject>())
            if (stored.OfType<JsonObject>().FirstOrDefault(s => Py.Eq(s["slot_id"], slot["slot_id"])) is { } old)
            {
                var same = Py.Eq(old["trophy_id"], slot["trophy_id"]) && Py.Eq(old["weapon_id"], slot["weapon_id"]);
                foreach (var (key, value) in old)
                    if (!slot.ContainsKey(key) && same)
                        slot[key] = value?.DeepClone();
            }
    }

    /// <summary>Set `lodge_id`, `slot_id` and `pose_id` of each trophy row from the account's lodges: the slot that holds the trophy now, else (a row
    /// that named a lodge the account has) none, so a trophy taken down or moved shows where it is. A row of no lodge stays as it is.</summary>
    void Mount(long account, List<JsonNode?> trophies)
    {
        var lodges = Restored(account, "lodges", LodgeOne).Concat(Restored(account, "lodges", LodgeList)).OfType<JsonObject>().ToList();
        var mounts = new Dictionary<string, JsonObject>();
        foreach (var lodge in lodges)
            foreach (var slot in (lodge["animal_slots"] as JsonArray ?? []).OfType<JsonObject>())
                if (Py.Plain(slot["trophy_id"]) is { } id)
                    mounts.TryAdd(Py.Str(id), new JsonObject { ["lodge_id"] = C(lodge["id"]), ["slot_id"] = C(slot["slot_id"]), ["pose_id"] = C(slot["pose_id"]) });
        var known = lodges.Select(l => Py.Str(l["id"])).ToHashSet();
        foreach (var row in trophies.OfType<JsonObject>())
            if (mounts.TryGetValue(Py.Str(row["id"]), out var at) || row["lodge_id"] is { } held && known.Contains(Py.Str(held)))
                foreach (var key in new[] { "lodge_id", "slot_id", "pose_id" })
                    row[key] = C(at?[key]);
    }

    /// <summary>`/game/user/<id>/lodges` of an account: its lodges, each animal slot with its `trophy_details`.</summary>
    public byte[] LodgesReply(long account) => Py.DumpBytes(new JsonArray(WithTrophies(account, Restored(account, "lodges", LodgeList)).ToArray()));

    /// <summary>One lodge as `/game/user/<id>/lodges/<lodge>` sends it; null when the account has none with this id.</summary>
    public byte[]? LodgeReply(long account, long id) =>
        WithTrophies(account, Restored(account, "lodges", LodgeOne, id.ToString())).FirstOrDefault() is { } node ? Py.DumpBytes(node) : null;

    /// <summary>Fill each mounted slot's `trophy_details` from the account's trophy row of that id (a hidden one too: the owner mounted it),
    /// unless the slot came with some.</summary>
    List<JsonNode?> WithTrophies(long account, List<JsonNode?> lodges)
    {
        Dictionary<string, JsonNode?>? trophies = null;
        foreach (var slot in lodges.SelectMany(l => l?["animal_slots"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (slot["trophy_id"] is { } sent) slot["trophy_id"] = Py.TrophyId(sent);  // what the game can read, else an empty slot (the stored row stays)
            if (slot["trophy_id"] is { } id && slot["trophy_details"] is null)
            {
                if (trophies is null)
                {
                    trophies = [];
                    foreach (var row in TrophyRows(account, all: true))
                        trophies.TryAdd(Py.Str(row?["id"]), row);
                }
                slot["trophy_details"] = trophies.GetValueOrDefault(Py.Str(id))?.DeepClone();
            }
        }
        return lodges;
    }

    /// <summary>`PUT lodges/<id>`: the slots (and name) of the account's lodge. 200; 400 for a body that is not as sent; 404 for a lodge the account has not.</summary>
    public int SaveLodge(long account, long id, byte[] body) => EditLodge(account, id, body, SlotFields);

    /// <summary>`PUT lodges/<id>/settings`: name, `locked` (hides the lodge from other players, in the game) and `style_id`. Statuses as <see cref="SaveLodge"/>.</summary>
    public int SaveLodgeSettings(long account, long id, byte[] body) => EditLodge(account, id, body, SettingFields);

    /// <summary>Set the keys of the body that are in fields on every stored row of the lodge (the list's and the single's).</summary>
    int EditLodge(long account, long id, byte[] body, (string Key, Func<JsonNode?, JsonNode?> Check)[] fields)
    {
        var edits = new List<(string Key, JsonNode? Value)>();
        try
        {
            if (JsonNode.Parse(body) is not JsonObject sent) return 400;
            foreach (var (key, check) in fields)
                if (sent.TryGetPropertyValue(key, out var value))
                    edits.Add((key, check(value)));
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return 400;
        }
        using var con = Con(account);
        con.Begin();
        StoreMounted(con, account);  // before the slots are rewritten
        var rows = con.Rows("SELECT n, node FROM lodges WHERE account_id = $a AND source IN (?, ?) AND id = ?", LodgeList, LodgeOne, id.ToString());
        var engine = rows.Select(r => (JsonNode.Parse((string)r[1]) as JsonObject)?["lodge_engine_id"]).FirstOrDefault(Py.IsNum);  // either row names the layout
        foreach (var r in rows)
            if (JsonNode.Parse((string)r[1]) is JsonObject node)
            {
                foreach (var (key, value) in edits)
                {
                    var fresh = value?.DeepClone();
                    if (fresh is JsonArray slots && node[key] is JsonArray stored)
                        Carry(slots, stored);
                    node[key] = fresh;
                }
                Fit(node, engine);
                con.Exec("UPDATE lodges SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(node), r[0]);
            }
        NoticeBigLodge(con);
        con.Commit();
        return rows.Count > 0 ? 200 : 404;
    }

    /// <summary>`PUT trophies/<id>` `{hidden}`: show or hide one of the account's trophies (a restored row, an animal of a restored lodge, or a harvest made here) on its
    /// public views. Statuses as <see cref="SaveLodge"/>.</summary>
    public int HideTrophy(long account, long id, byte[] body)
    {
        JsonNode? sent;
        try
        {
            sent = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return 400;
        }
        if (sent is not JsonObject o || o["hidden"] is not JsonValue v || !v.TryGetValue<bool>(out var hidden)) return 400;
        using var con = Con(account);
        con.Begin();
        StoreMounted(con, account);
        var found = con.Exec("UPDATE harvests SET hidden = ? WHERE account_id = $a AND kill_id = ?", hidden ? 1 : 0, id);
        foreach (var r in con.Rows("SELECT n, node FROM trophies WHERE account_id = $a AND source IN ('/game/user/trophies', '/v1/Trophy/list') AND id = ?", id.ToString()))
            if (JsonNode.Parse((string)r[1]) is JsonObject row)
            {
                row["hidden"] = hidden;
                con.Exec("UPDATE trophies SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(row), r[0]);
                found++;
            }
        con.Commit();
        DropBoards();  // a hidden harvest leaves the boards at once
        return found > 0 ? 200 : 404;
    }
}
