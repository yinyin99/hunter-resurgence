// What a community server's account shows of itself where no restore filled the rows: Me/me from the account, Inventory/list
// from the items table. A local server never calls these (it answers from its recordings).
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>Does the request ask for this account's own data: no `user_id`, or its own? A local server has one account, so
    /// always. ponytail: another player's page shows the empty reply; per-user reads of other accounts come when a profile page needs them.</summary>
    public bool IsOwn(long account, byte[] formBody)
    {
        var userId = Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id");
        return offline || userId == "" || IsUser(account, userId);
    }

    public bool IsUser(long account, string userId) => userId == Py.Str(Account(account)["user_id"]);

    /// <summary>The reply nodes a restore stored in `trophies` or `lodges` for a source, as stored (none: empty).</summary>
    List<JsonNode?> Restored(long account, string table, string source, string? id = null)
    {
        using var con = Con(account);
        return [.. con.Rows($"SELECT node FROM {table} WHERE account_id = $a AND source = ?{(id is null ? "" : " AND id = ?")} ORDER BY n",
                            id is null ? [source] : [source, id]).Select(r => JsonNode.Parse((string)r[0]))];
    }

    /// <summary>A restored list reply as a JSON array: `[]` when the account has none.</summary>
    public byte[] RowsReply(long account, string table, string source) => Py.DumpBytes(new JsonArray(Restored(account, table, source).ToArray()));

    /// <summary>One restored record as the reply it was read from; null when the account has none with this id.</summary>
    public byte[]? RowReply(long account, string table, string source, string id) =>
        Restored(account, table, source, id).FirstOrDefault() is { } node ? Py.DumpBytes(node) : null;

    static readonly string[] TrophyFields = ["species", "species_define", "score", "score_type", "is_skull", "gender", "weight", "scale", "reserve_id", "timestamp",
                                             "texture_id", "base_texture", "fur_texture", "antlers_seed", "antlers_score_percentage", "holed"];

    /// <summary>The account's own trophies as `/game/user/trophies` sends them: the restored rows, then one row per harvest made on this
    /// server in the restored shape. ponytail: a server harvest is never mounted, so no lodge_id, slot_id or pose_id until lodge edits are saved.
    /// A new kill id is above every restored id (see Heartbeat).</summary>
    List<JsonNode?> TrophyRows(long account)
    {
        var rows = Restored(account, "trophies", "/game/user/trophies");
        using var con = Con(account);
        foreach (var r in con.Rows("SELECT kill_id, body FROM harvests WHERE account_id = $a ORDER BY kill_id"))
        {
            var body = JsonNode.Parse((string)r[1])!;
            var row = new JsonObject { ["id"] = (long)r[0], ["hidden"] = false };
            foreach (var field in TrophyFields)
                row[field] = C(body[field]);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>`/game/user/trophies`: the restored rows and the harvests made here.</summary>
    public byte[] TrophiesReply(long account) => Py.DumpBytes(new JsonArray(TrophyRows(account).ToArray()));

    /// <summary>Trophy/list: one page (`offset`, `limit`) of the account's restored trophies (rows of their own kind).</summary>
    public byte[] TrophyListReply(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        long Number(string key) => long.TryParse(Form1(form, key), out var n) && n > 0 ? n : 0;
        var all = Restored(account, "trophies", "/v1/Trophy/list");
        var page = all.Skip((int)Math.Min(Number("offset"), int.MaxValue));
        var limit = Number("limit");
        return Py.DumpBytes(new JsonObject { ["total"] = all.Count, ["trophies"] = new JsonArray((limit > 0 ? page.Take((int)Math.Min(limit, int.MaxValue)) : page).ToArray()) });
    }

    /// <summary>The account's Me/me as it stands: the restored profile, else what a new account has (no recorded value).</summary>
    JsonObject MeOf(Db con, long account)
    {
        if (RestoredReads.Me(con) is { } restored)
            return restored;
        var a = Account(account);
        return new JsonObject
        {
            ["id"] = C(a["user_id"]), ["uuid"] = "", ["handle"] = C(a["handle"]), ["hostname"] = C(a["hostname"]) ?? Py.Str(a["handle"]).ToLowerInvariant(),
            ["avatar"] = C(G(a, "avatar_url", "")), ["gender"] = C(G(a, "gender", 0)), ["locale"] = "", ["timezone"] = "",
            ["continent"] = "", ["country"] = "", ["hunterscore"] = C(G(a, "hunterscore", 0)),
            ["membershipType"] = "freeplay", ["membershipTitle"] = "Hunter", ["memberSince"] = 0,  // ponytail: no join date shown
            ["membershipExpires"] = 0, ["licenseAccepted"] = 1, ["user_group"] = 1, ["user_services"] = new JsonObject(),
            ["items"] = new JsonObject(), ["worlditems"] = new JsonArray(), ["records"] = new JsonObject(), ["friend_requests"] = 0,
        };
    }

    /// <summary>The picture of an account: Me/poll sends it.</summary>
    public JsonNode? Avatar(long account)
    {
        using var con = Con(account);
        return C(MeOf(con, account)["avatar"]);
    }

    /// <summary>The in-game pages' catalogue: the keys they read of the application catalogue (species with the permitted ammo of
    /// each, items, reserves, locations; the missions only when the catalogue has them).</summary>
    public byte[] ClientApplicationReply()
    {
        var reply = new JsonObject();
        foreach (var key in (string[])["items", "locale", "locations", "mission_groups", "missions", "reserves", "season", "species"])
            reply[key] = C(application[key]) ?? key switch { "locale" => JsonValue.Create("en_us"), "season" => JsonValue.Create(0), _ => new JsonArray() };
        foreach (var sp in reply["species"]!.AsArray().OfType<JsonObject>())
            if (sp["ethicalAmmo"] is null && Py.L(sp["id"]) is >= 0 and var id && id < ethicalAmmo.Count)
                sp["ethicalAmmo"] = C(ethicalAmmo[(int)id]);
        return Py.DumpBytes(reply);
    }

    /// <summary>When the account last hunted (the end of its newest expedition, else the last heartbeat): Me/poll's `last_hunt_update_ts`, 0 before a first hunt.</summary>
    public double LastHuntUpdate(long account)
    {
        using var con = Con(account);
        return Convert.ToDouble(con.Scalar("SELECT COALESCE(MAX(COALESCE(end_ts, last_seen, start_ts)), 0) FROM expeditions WHERE account_id = $a"));
    }

    /// <summary>Inventory/list of the account: a row per owned item with its definition and the owned rows. ponytail: a placed
    /// item's `location` is null (its position is not sent).</summary>
    public byte[] InventoryListReply(long account)
    {
        List<object[]> rows;
        using (var con = Con(account))
            rows = con.Rows("SELECT item_id, guid, slot, units, metadata FROM items WHERE account_id = $a ORDER BY item_id, guid");
        return Py.DumpBytes(new JsonArray(rows.GroupBy(r => (long)r[0]).Select(g => (JsonNode?)new JsonObject
        {
            ["item"] = items.TryGetValue(g.Key, out var def) ? C(def) : new JsonObject { ["id"] = g.Key },
            ["own"] = new JsonArray(g.Select(r =>
            {
                var metadata = Parse(r[4]);
                metadata.Remove(POSITION);
                metadata.Remove(DIRECTION);
                return (JsonNode?)new JsonObject { ["slot"] = Py.FromSql(r[2]), ["units"] = Py.FromSql(r[3]), ["metadata"] = metadata, ["id"] = Py.FromSql(r[1]), ["location"] = null };
            }).ToArray()),
        }).ToArray()));
    }
}
