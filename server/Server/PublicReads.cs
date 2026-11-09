// What a community server's account shows of itself where no restore filled the rows: Me/me from the account, Inventory/list
// from the items table. A local server never calls these (it answers from its recordings).
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>Does the request ask for this account's own data: no `user_id`, or its own? A local server has one account, so
    /// always. The owner-only areas check this; a public area reads the account of <see cref="PublicAccount(long)"/>.</summary>
    public bool IsOwn(long account, byte[] formBody)
    {
        var userId = Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id");
        return offline || userId == "" || IsUser(account, userId);
    }

    public bool IsUser(long account, string userId) => userId == Py.Str(Account(account)["user_id"]);

    /// <summary>The account a public read shows (a profile page, in the game and on the website), by user id or by handle (any
    /// case): an account of this server that is not banned and is not the recorded player's (LocalAccount: no player here owns
    /// it, so a backup nobody restored onto their account is never shown); else null. A local server has one account: always
    /// it. Read the account's rows with `Con(account)` (Store.Open, the per-account `$a` rule).</summary>
    public long? PublicAccount(long userId) => offline ? LocalAccount : Store.Listed(userId, LocalAccount);

    /// <inheritdoc cref="PublicAccount(long)"/>
    public long? PublicAccount(string handle) => offline ? LocalAccount : Store.Listed(handle, LocalAccount);

    /// <summary>Public_user/getByHostname of an account: who it is (`membership` as its connect `account` has it, the type and
    /// title as its Me/me does), its hunterscore and rank, `online` while a hunt of it is open and alive, and the distance and
    /// duration of its statistics (restored base plus the closed server hunts).</summary>
    public JsonObject PublicUser(long account)
    {
        using var con = Con(account);
        var me = MeOf(con, account);
        var stats = RestoredReads.StatisticsTotal(con);
        return new JsonObject
        {
            ["id"] = account, ["handle"] = C(me["handle"]), ["hostname"] = C(me["hostname"]), ["avatar"] = C(me["avatar"]),
            ["hunterscore"] = C(me["hunterscore"]), ["rank"] = Store.Rank(account, LocalAccount),
            ["membership"] = C(G(Account(account), "membership", 0)), ["membershipType"] = C(me["membershipType"]), ["membershipTitle"] = C(me["membershipTitle"]),
            ["online"] = con.Scalar("SELECT 1 FROM expeditions WHERE account_id = $a AND end_ts IS NULL AND COALESCE(last_seen, start_ts) >= ?",
                                    Clock() - HEARTBEAT_TIMEOUT) is not null,
            ["stats"] = new JsonObject { ["distance"] = C(stats["distance"]), ["duration"] = C(stats["duration"]) },
        };
    }

    /// <summary>/game/user/&lt;id&gt;/profile of an account: `background` and `ribbon` of its restored reply (null without one), its
    /// hunterscore and how many achievements it has earned.</summary>
    public byte[] UserProfileReply(long account)
    {
        using var con = Con(account);
        var restored = con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = ?", $"game_user_{account}_profile.json") is string s ? JsonNode.Parse(s) : null;
        return Py.DumpBytes(new JsonObject
        {
            ["background"] = C(restored?["background"]), ["ribbon"] = C(restored?["ribbon"]), ["hunterscore"] = C(MeOf(con, account)["hunterscore"]),
            ["earned_achievements"] = (long)con.Scalar("SELECT COUNT(*) FROM achievements WHERE account_id = $a AND EXISTS (SELECT 1 FROM json_each(triggers))")!,
        });
    }

    /// <summary>The statistics tab of an account, `GET /hunters/&lt;handle&gt;/statistics`: `weapons` (one row per weapon and ammo: shots,
    /// accuracy % of shots, kills, kills per hit %, longest hit in metres or null), `animals` (spotted, tracked, harvested per species),
    /// `collectables` (always empty: the server has no names for them), `distanceKm` and `huntSeconds`; names from the catalogue,
    /// the id where it has none. Restored base plus closed server hunts, like Statistics/total.</summary>
    public JsonObject StatisticsPage(long account)
    {
        JsonObject total;
        using (var con = Con(account))
            total = RestoredReads.StatisticsTotal(con).AsObject();
        static double N(JsonNode? o, string key) => o is JsonObject x && x[key] is { } n && Py.IsNum(n) ? Py.D(n) : 0;  // a restored backup is the player's: any member can be of another type
        static int Count(double d) => (int)Math.Clamp(d, 0, int.MaxValue);  // and a counter any size: the website holds ints
        static JsonNode? Key(string id) => long.TryParse(id, out var n) ? JsonValue.Create(n) : null;  // the catalogues are keyed by number
        string Weapon(string id) => Py.Str(Lookup(items, Key(id))?["name"] ?? id);
        var weapons = new List<(string Name, JsonObject Row)>();
        foreach (var (w, ammo) in total["data"]!["weapon"] as JsonObject ?? [])
            foreach (var (a, c) in ammo as JsonObject ?? [])
            {
                double hits = N(c, "hits"), shots = hits + N(c, "misses"), mm = N(c, "distance");
                weapons.Add((Weapon(w), new JsonObject
                {
                    ["weapon"] = Weapon(w), ["ammo"] = Weapon(a), ["shots"] = Count(shots), ["accuracy"] = shots > 0 ? Math.Round(hits / shots * 100, 1) : 0,
                    ["kills"] = Count(N(c, "kills")), ["killsPerHit"] = hits > 0 ? Math.Round(N(c, "kills") / hits * 100, 1) : 0, ["longest"] = mm > 0 ? mm / 1000 : null,
                }));
            }
        var animals = new List<(string Name, JsonObject Row)>();
        foreach (var (sp, c) in total["data"]!["animal"] as JsonObject ?? [])
        {
            var name = SpeciesName(Key(sp)) ?? sp;
            animals.Add((name, new JsonObject { ["name"] = name, ["spotted"] = Count(N(c, "spots")), ["tracked"] = Count(N(c, "tracks")), ["harvested"] = Count(N(c, "kills")) }));
        }
        return new JsonObject
        {
            ["weapons"] = new JsonArray([.. weapons.OrderBy(w => w.Name, StringComparer.Ordinal).Select(w => (JsonNode?)w.Row)]),
            ["animals"] = new JsonArray([.. animals.OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => (JsonNode?)a.Row)]),
            ["collectables"] = new JsonArray(), ["distanceKm"] = N(total, "distance") / 1000, ["huntSeconds"] = Count(N(total, "duration")),
        };
    }

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

    /// <summary>Trophy/list: one page (`offset`, `limit`) of the account's restored trophies.</summary>
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
