// Loadouts (community server): the rows a restore or a purchase made, the page's save of one, and the one a launch applies to the account's item slots.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The store item "Inventory Loadout": every unit an account owns is one loadout, so a delivery adds a default loadout per unit.
    /// A placed item is no part of a loadout (<see cref="PLACED_SLOT"/>); ammunition is the catalogue's category 3.</summary>
    const long LoadoutItem = 811, AmmoCategory = 3;
    const int MinName = 2, MaxName = 50, MaxColor = 11, MaxItems = 200;  // the limits of the page's rename box, its 12 colours, and a loadout's items

    static JsonObject Loadout(long id, string name, JsonNode? color, JsonNode items) => new() { ["name"] = name, ["color"] = color, ["items"] = items, ["id"] = id };

    static bool IntOf(JsonNode? v, out long l)
    {
        l = 0;
        return v is JsonValue j && j.TryGetValue<long>(out l);
    }

    static long Int(JsonNode? v, long min, long max = long.MaxValue) => IntOf(v, out var l) && l >= min && l <= max ? l : throw new FormatException();

    /// <summary>A slot a loadout may name: a whole number from 1, never the placed items' slot.</summary>
    static long Slot(JsonNode? v) => Int(v, 1) is var slot && slot != PLACED_SLOT ? slot : throw new FormatException();

    /// <summary>`GET /game/user/loadout`: the account's loadouts, ascending by id.</summary>
    public byte[] LoadoutsReply(long account)
    {
        using var con = Con(account);
        return Py.DumpBytes(new JsonArray(con.Rows("SELECT id, name, color, entries FROM loadouts WHERE account_id = $a ORDER BY id")
            .Select(r => (JsonNode?)Loadout((long)r[0], (string)r[1], Py.FromSql(r[2]), JsonNode.Parse((string)r[3]) as JsonArray ?? [])).ToArray()));
    }

    /// <summary>`PUT loadout/<id>` `{name, color, items}`: replaces all three of the account's loadout; the inventory is not touched. 200 with the saved loadout;
    /// 400 for a body that is not as sent (a name of 2 to 50 characters, a color 0 to 11, at most MaxItems items of whole numbers, `item_guid` absent or a number);
    /// 404 for a loadout the account has not.</summary>
    public (int Status, byte[] Body) SaveLoadout(long account, long id, byte[] body)
    {
        string name;
        long color;
        var items = new JsonArray();
        try
        {
            if (JsonNode.Parse(body) is not JsonObject sent) return (400, []);
            name = sent["name"] is JsonValue n && n.TryGetValue<string>(out var text) && text.Length is >= MinName and <= MaxName ? text : throw new FormatException();
            color = Int(sent["color"], 0, MaxColor);
            if (sent["items"] is not JsonArray list || list.Count > MaxItems) return (400, []);
            foreach (var entry in list)
            {
                if (entry is not JsonObject e) return (400, []);
                items.Add(new JsonObject
                {
                    ["slot_id"] = Slot(e["slot_id"]), ["item_id"] = Int(e["item_id"], 1),
                    ["item_guid"] = e["item_guid"] is null ? null : Int(e["item_guid"], 1),
                });
            }
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return (400, []);
        }
        using var con = Con(account);
        return con.Exec("UPDATE loadouts SET name = ?, color = ?, entries = ? WHERE account_id = $a AND id = ?", name, color, Py.Dumps(items), id) == 0
            ? (404, [])
            : (200, Py.DumpBytes(Loadout(id, name, color, items)));
    }

    /// <summary>One default loadout (`<handle>'s Loadout #<n>`, color 0, no items) for a unit of <see cref="LoadoutItem"/> delivered; inside the caller's transaction.</summary>
    static void AddLoadout(Db con)
    {
        var handle = con.Scalar("SELECT handle FROM accounts WHERE id = $a");
        var n = (long)con.Scalar("SELECT COUNT(*) FROM loadouts WHERE account_id = $a")! + 1;
        con.Exec("INSERT INTO loadouts (account_id, name, color, entries) VALUES ($a, ?, 0, '[]')", $"{handle}'s Loadout #{n}");
    }

    /// <summary>The launch's `loadout_id`, inside the launch's transaction: the account's loadout of that id sets the item slots as the page does when it is clicked.
    /// Every owned item goes to slot 0 (a placed one stays), then each entry in order takes the owned row of its `item_guid`, else the first row of its `item_id` still in
    /// slot 0, and gives it the entry's slot; an entry with no such row is skipped. While the connect template has unlimited ammunition on, an ammunition entry with no
    /// free row clones a row the account owns of it. 0, a loadout of another account or none: nothing changes.</summary>
    void ApplyLoadout(Db con, string loadoutId)
    {
        if (!long.TryParse(loadoutId, out var id) || con.Scalar("SELECT entries FROM loadouts WHERE account_id = $a AND id = ?", id) is not string list)
            return;
        con.Exec("UPDATE items SET slot = 0 WHERE account_id = $a AND slot <> ?", PLACED_SLOT);
        var unlimited = Py.Truthy(connect["inventory"]?["active_services"]?["UNLIMITED_AMMO"]);
        foreach (var entry in JsonNode.Parse(list) as JsonArray ?? [])
        {
            if (entry is not JsonObject e || !IntOf(e["slot_id"], out var slot) || !IntOf(e["item_id"], out var itemId) || slot < 1 || slot == PLACED_SLOT) continue;
            long? guid = IntOf(e["item_guid"], out var g) ? g : null;
            if (con.Scalar("SELECT guid FROM items WHERE account_id = $a AND slot = 0 AND item_id = ?1 AND (?2 IS NULL OR guid = ?2) ORDER BY guid LIMIT 1", itemId, guid) is long free)
                con.Exec("UPDATE items SET slot = ? WHERE account_id = $a AND guid = ?", slot, free);
            else if (unlimited && items.TryGetValue(itemId, out var definition) && Is(G(definition, "cat", null), AmmoCategory)
                     && con.Scalar("SELECT 1 FROM items WHERE account_id = $a AND item_id = ?", itemId) is not null)
                con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, ?, '{}', ?)",
                         NextId(con, "items", "guid"), itemId, slot, defaultUnits.GetValueOrDefault(itemId));
        }
    }
}
