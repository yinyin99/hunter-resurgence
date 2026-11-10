// Lodge edits and trophy visibility (community server): the lodge rows a restore wrote, edited in place, and the lodge
// reads of any account. A lodge exists when its rows do; nothing here creates one.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    const string LodgeList = "/game/user/lodges", LodgeOne = "/game/user/lodge";
    const int MaxNameLength = 100, MaxSlots = 100;  // generous: a lodge has a few dozen slots; other players download the whole lodge
    static readonly string[] AnimalSlotKeys = ["slot_id", "trophy_id", "pose_id", "rotation", "base_texture", "is_skull"];
    static readonly string[] WeaponSlotKeys = ["slot_id", "weapon_id", "rotation"];

    static readonly (string Key, Func<JsonNode?, JsonNode?> Check)[] SlotFields =
        [("name", Name), ("animal_slots", v => Slots(v, AnimalSlotKeys)), ("weapon_slots", v => Slots(v, WeaponSlotKeys))];

    static readonly (string Key, Func<JsonNode?, JsonNode?> Check)[] SettingFields =
        [("name", Name), ("locked", v => Is(v, JsonValueKind.True, JsonValueKind.False)), ("style_id", v => v is null ? null : Is(v, JsonValueKind.Number))];  // no style: null

    /// <summary>A copy of a value of one of these kinds; anything else (null too) is a bad request.</summary>
    static JsonNode Is(JsonNode? v, params JsonValueKind[] kinds) =>
        v is JsonValue j && kinds.Contains(j.GetValueKind()) ? v.DeepClone() : throw new FormatException();

    static JsonNode Name(JsonNode? v) => Is(v, JsonValueKind.String) is var name && name.GetValue<string>().Length <= MaxNameLength ? name : throw new FormatException();

    /// <summary>The slots of a lodge: at most MaxSlots objects with a numeric `slot_id`, each reduced to the slot's own keys with plain values.</summary>
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
                    kept[key] = value is null ? null : Is(value, JsonValueKind.Number, JsonValueKind.True, JsonValueKind.False);
            copy.Add(kept);
        }
        return copy;
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
                    node[key] = value?.DeepClone();
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
