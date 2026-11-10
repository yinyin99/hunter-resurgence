// Lodge edits and trophy visibility (community server): the lodge rows a restore wrote, edited in place, and the lodge
// reads of any account. A lodge exists when its rows do; nothing here creates one.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    const string LodgeList = "/game/user/lodges", LodgeOne = "/game/user/lodge";
    const int MaxText = 100, MaxSlots = 200;  // generous (the largest lodge has 163 animal slots): other players download the whole lodge
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

    /// <summary>Give each new slot the keys it lacks from the stored slot of the same `slot_id`: all of them when it holds the same trophy or weapon
    /// (`trophy_details`), `is_full_body` (a property of the platform) always. The game saves a lodge without them whenever the player leaves it.</summary>
    static void Carry(JsonArray slots, JsonArray stored)
    {
        foreach (var slot in slots.OfType<JsonObject>())
            if (stored.OfType<JsonObject>().FirstOrDefault(s => Py.Eq(s["slot_id"], slot["slot_id"])) is { } old)
            {
                var same = Py.Eq(old["trophy_id"], slot["trophy_id"]) && Py.Eq(old["weapon_id"], slot["weapon_id"]);
                foreach (var (key, value) in old)
                    if (!slot.ContainsKey(key) && (same || key == "is_full_body"))
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
                if (slot["trophy_id"] is { } id)
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
            if (slot["trophy_id"] is { } id && slot["trophy_details"] is null)
            {
                if (trophies is null)
                {
                    trophies = [];
                    foreach (var row in TrophyRows(account))
                        trophies.TryAdd(Py.Str(row?["id"]), row);
                }
                slot["trophy_details"] = trophies.GetValueOrDefault(Py.Str(id))?.DeepClone();
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
        var rows = con.Rows("SELECT n, node FROM lodges WHERE account_id = $a AND source IN (?, ?) AND id = ?", LodgeList, LodgeOne, id.ToString());
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
                con.Exec("UPDATE lodges SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(node), r[0]);
            }
        con.Commit();
        return rows.Count > 0 ? 200 : 404;
    }

    /// <summary>`PUT trophies/<id>` `{hidden}`: show or hide one of the account's trophies (a restored row, or a harvest made here) on its
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
        var found = con.Exec("UPDATE harvests SET hidden = ? WHERE account_id = $a AND kill_id = ?", hidden ? 1 : 0, id);
        foreach (var r in con.Rows("SELECT n, node FROM trophies WHERE account_id = $a AND source IN ('/game/user/trophies', '/v1/Trophy/list') AND id = ?", id.ToString()))
            if (JsonNode.Parse((string)r[1]) is JsonObject row)
            {
                row["hidden"] = hidden;
                con.Exec("UPDATE trophies SET node = ? WHERE account_id = $a AND n = ?", Py.Dumps(row), r[0]);
                found++;
            }
        con.Commit();
        return found > 0 ? 200 : 404;
    }
}
