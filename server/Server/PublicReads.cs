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
            ["online"] = HuntAlive(con),
            ["stats"] = new JsonObject { ["distance"] = C(stats["distance"]), ["duration"] = C(stats["duration"]) },
        };
    }

    /// <summary>Is a hunt of the connection's account open and had a heartbeat in the last HEARTBEAT_TIMEOUT.</summary>
    bool HuntAlive(Db con) =>
        con.Scalar("SELECT 1 FROM expeditions WHERE account_id = $a AND end_ts IS NULL AND COALESCE(last_seen, start_ts) >= ?", Clock() - HEARTBEAT_TIMEOUT) is not null;

    /// <summary>Friend entries of accounts, as Me/friends and User/friendslist send them, sorted by `handle` (upper case, ordinal).</summary>
    List<JsonNode?> FriendEntries(IEnumerable<long> accounts) => [.. accounts.Select(account =>
    {
        using var con = Con(account);
        var me = MeOf(con, account);
        return (JsonNode?)new JsonObject
        {
            ["id"] = account, ["handle"] = C(me["handle"]), ["avatar"] = C(me["avatar"]), ["fullbody_avatar"] = C(me["fullbody_avatar"]) ?? 0,
            ["hostname"] = C(me["hostname"]), ["hunterscore"] = C(me["hunterscore"]), ["online"] = HuntAlive(con),
        };
    }).OrderBy(e => Py.Str(e!["handle"]).ToUpperInvariant(), StringComparer.Ordinal)];

    /// <summary>Me/friends: the account's friends, the players who asked it (`requests`, the same entries) and the user ids it asked (`pending`).</summary>
    public byte[] MeFriendsReply(long account)
    {
        var (friends, requests, pending) = Store.FriendsOf(account, LocalAccount);
        return Py.DumpBytes(new JsonObject
        {
            ["friends"] = new JsonArray([.. FriendEntries(friends)]), ["friendCount"] = friends.Count,
            ["requests"] = new JsonArray([.. FriendEntries(requests)]), ["requestCount"] = requests.Count,
            ["pending"] = new JsonArray([.. pending.Select(id => (JsonNode?)id)]),
        });
    }

    /// <summary>How many players asked the account to be friends: Me/poll's and Me/me's `friend_requests`.</summary>
    public long FriendRequests(long account) => Store.FriendsOf(account, LocalAccount).Requests.Count;

    /// <summary>Friend/add (ask, or accept the other's request) or Friend/remove (decline, cancel, remove) of `friend_user_id`:
    /// `true`; `{"errorCode": 400}` for the account's own id or one that is not a player here (<see cref="PublicAccount(long)"/>).</summary>
    public byte[] FriendReply(long account, byte[] formBody, bool add)
    {
        if (!long.TryParse(Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "friend_user_id"), out var id) || id == account || PublicAccount(id) is not { } other)
            return """{"errorCode": 400}"""u8.ToArray();
        if (add) Store.AddFriend(account, other);
        else Store.RemoveFriend(account, other);
        return "true"u8.ToArray();
    }

    /// <summary>User/friendslist of `user_id` (any player here, no token): `{total, friends}`, one page by `offset` and `limit` (1 to 50;
    /// missing, empty or below 1: every friend). No `user_id`: `{"errorCode": 400}`; an empty, non-positive or unknown one: `null`;
    /// a non-numeric value: the handler error.</summary>
    public byte[] FriendsListReply(byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody), keepBlank: true);
        string? Value(string key) => form.TryGetValue(key, out var v) ? v[0] : null;
        var (user, offset, limit) = (Value("user_id"), Value("offset"), Value("limit"));
        if (new[] { user, offset, limit }.Any(v => v is { Length: > 0 } && !long.TryParse(v, out _)))
            return """{"error": 1, "code": "handlerError"}"""u8.ToArray();
        if (user is null)
            return """{"errorCode": 400}"""u8.ToArray();
        if (!long.TryParse(user, out var id) || id <= 0 || PublicAccount(id) is not { } account)
            return "null"u8.ToArray();
        var all = FriendEntries(Store.FriendsOf(account, LocalAccount).Friends);
        var page = all.Skip(long.TryParse(offset, out var o) && o > 0 ? (int)Math.Min(o, int.MaxValue) : 0);
        return Py.DumpBytes(new JsonObject
        {
            ["total"] = all.Count,
            ["friends"] = new JsonArray([.. long.TryParse(limit, out var l) && l > 0 ? page.Take((int)Math.Min(l, 50)) : page]),
        });
    }

    public const int FriendsPageSize = 20;
    public const int FriendSearchLimit = 20;

    /// <summary>What a profile page's friends tab shows of an account, `GET /hunters/&lt;handle&gt;/friends`: one page of its friends by handle,
    /// `{page, pages, total, rows: [{handle, hunterscore}]}`.</summary>
    public JsonObject PublicFriends(long account, int page)
    {
        var all = FriendEntries(Store.FriendsOf(account, LocalAccount).Friends);
        var pages = Math.Max(1, (all.Count + FriendsPageSize - 1) / FriendsPageSize);
        page = Math.Clamp(page, 1, pages);
        return new JsonObject
        {
            ["page"] = page, ["pages"] = pages, ["total"] = all.Count,
            ["rows"] = new JsonArray([.. all.Skip((page - 1) * FriendsPageSize).Take(FriendsPageSize).Select(e => (JsonNode?)new JsonObject { ["handle"] = C(e!["handle"]), ["hunterscore"] = C(e["hunterscore"]) })]),
        };
    }

    static JsonArray Hunters(IEnumerable<JsonNode?> entries) => new([.. entries.Select(e => (JsonNode?)new JsonObject { ["handle"] = C(e!["handle"]), ["online"] = C(e["online"]) })]);

    /// <summary>The signed-in website player's friends, `GET /account/friends`: `{friends: [{handle, online}], requests: [{handle, online}], pending: [{handle, online}]}`,
    /// each by handle (`requests` asked the account, `pending` were asked by it).</summary>
    public JsonObject WebFriends(long account)
    {
        var (friends, requests, pending) = Store.FriendsOf(account, LocalAccount);
        return new JsonObject { ["friends"] = Hunters(FriendEntries(friends)), ["requests"] = Hunters(FriendEntries(requests)), ["pending"] = Hunters(FriendEntries(pending)) };
    }

    /// <summary>`GET /account/friends/search?text=`: the players whose handle contains the text, never the account itself, `{hunters: [{handle, online}]}`.</summary>
    public JsonObject WebFriendSearch(long account, string text) =>
        new() { ["hunters"] = Hunters(FriendEntries(Store.SearchHandles(text, account, LocalAccount, FriendSearchLimit))) };

    /// <summary>`POST /account/friends/add` (ask, or accept the other's request) or `/remove` (decline, cancel, remove) of a player by handle (any case);
    /// false, and nothing changed, for the account's own handle or one that is not a player here.</summary>
    public bool WebFriend(long account, string handle, bool add)
    {
        if (PublicAccount(handle) is not { } other || other == account) return false;
        if (add) Store.AddFriend(account, other);
        else Store.RemoveFriend(account, other);
        return true;
    }

    /// <summary>/game/user/&lt;id&gt;/profile of an account: `background` and `ribbon` of its restored reply (null without one), its
    /// hunterscore and how many achievement thresholds it has earned (those with an earn time).</summary>
    public byte[] UserProfileReply(long account)
    {
        using var con = Con(account);
        var restored = con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = ?", $"game_user_{account}_profile.json") is string s ? JsonNode.Parse(s) : null;
        return Py.DumpBytes(new JsonObject
        {
            ["background"] = C(restored?["background"]), ["ribbon"] = C(restored?["ribbon"]), ["hunterscore"] = C(MeOf(con, account)["hunterscore"]),
            ["earned_achievements"] = (long)con.Scalar("SELECT COUNT(*) FROM achievements, json_each(triggers) WHERE account_id = $a AND json_each.value > 0")!,
        });
    }

    /// <summary>The statistics tab of an account, `GET /hunters/&lt;handle&gt;/statistics`: `weapons` (one row per weapon and ammo: their item ids `weaponId` and `ammoId`, null when the key is not a number, shots,
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
                    ["weapon"] = Weapon(w), ["weaponId"] = Key(w), ["ammo"] = Weapon(a), ["ammoId"] = Key(a), ["shots"] = Count(shots), ["accuracy"] = shots > 0 ? Math.Round(hits / shots * 100, 1) : 0,
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

    static readonly string[] TrophyFields = ["species", "species_define", "score", "score_type", "is_skull", "gender", "weight", "scale", "reserve_id", "timestamp",
                                             "texture_id", "base_texture", "fur_texture", "antlers_seed", "antlers_score_percentage", "holed"];

    /// <summary>The account's trophies as `/game/user/trophies` sends them: the restored rows, then one row per harvest made on this
    /// server in the restored shape. ponytail: a server harvest is never mounted, so no lodge_id, slot_id or pose_id until lodge edits are saved.
    /// A new kill id is above every restored id (see Heartbeat). Another player's view (`own` false) leaves out the hidden ones.</summary>
    List<JsonNode?> TrophyRows(long account, bool own = true)
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
        return own ? rows : [.. rows.Where(NotHidden)];
    }

    static bool NotHidden(JsonNode? row) => !Py.Truthy(row?["hidden"]);

    /// <summary>`/game/user/trophies`: the restored rows and the harvests made here.</summary>
    public byte[] TrophiesReply(long account) => Py.DumpBytes(new JsonArray(TrophyRows(account).ToArray()));

    /// <summary>Trophy/list for the form's user_id: one page (`offset`, `limit`) of the restored trophies (rows of their own kind) of the player's own
    /// account or, on a community server, of any other account of this server without its hidden ones (<see cref="PublicAccount(long)"/>);
    /// null for anyone else.</summary>
    public byte[]? TrophyListReply(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        long Number(string key) => long.TryParse(Form1(form, key), out var n) && n > 0 ? n : 0;
        var own = IsOwn(account, formBody);
        if (!own)
        {
            if (!long.TryParse(Form1(form, "user_id"), out var id) || PublicAccount(id) is not { } other)
                return null;
            account = other;
        }
        var all = Restored(account, "trophies", "/v1/Trophy/list");
        if (!own)
            all = [.. all.Where(NotHidden)];
        var page = all.Skip((int)Math.Min(Number("offset"), int.MaxValue));
        var limit = Number("limit");
        return Py.DumpBytes(new JsonObject { ["total"] = all.Count, ["trophies"] = new JsonArray((limit > 0 ? page.Take((int)Math.Min(limit, int.MaxValue)) : page).ToArray()) });
    }

    public const int TrophiesPageSize = 24;

    /// <summary>What a profile page's trophies tab shows of an account: one page of its trophies without the hidden ones (the restored, then the
    /// harvests made here), `{page, pages, total, rows: [{name, date}]}`: the species' name and the day of the harvest (UTC).</summary>
    public JsonObject PublicTrophies(long account, int page)
    {
        var all = TrophyRows(account, false);
        var pages = Math.Max(1, (all.Count + TrophiesPageSize - 1) / TrophiesPageSize);
        page = Math.Clamp(page, 1, pages);
        return new JsonObject
        {
            ["page"] = page, ["pages"] = pages, ["total"] = all.Count,
            ["rows"] = new JsonArray([.. all.Skip((page - 1) * TrophiesPageSize).Take(TrophiesPageSize).Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = SpeciesName(t?["species"]) ?? "#" + Py.Str(t?["species"]),
                ["date"] = TrophyDay(t?["timestamp"]),
            })]),
        };
    }

    /// <summary>The day (UTC, yyyy-MM-dd) of a trophy row's `timestamp`: an ISO time (a harvest made here) or Unix seconds; "" for anything else.</summary>
    static string TrophyDay(JsonNode? timestamp) =>
        timestamp is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var iso) ? iso.ToString("yyyy-MM-dd")
        : Py.IsNum(timestamp) ? DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(Py.L(timestamp), 0, 253402300799)).ToString("yyyy-MM-dd") : "";

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

    /// <summary>The slot of a placed item.</summary>
    const long PLACED_SLOT = 23;

    /// <summary>The account's item rows by item id, as (item id, owned rows) in the shape of Inventory/list and Inventory/dict:
    /// `{id, slot, units, metadata, location}`.</summary>
    List<(long ItemId, JsonNode?[] Own)> OwnRows(long account)
    {
        List<object[]> rows;
        using (var con = Con(account))
            rows = con.Rows("SELECT item_id, guid, slot, units, metadata FROM items WHERE account_id = $a ORDER BY item_id, guid");
        return [.. rows.GroupBy(r => (long)r[0]).Select(g => (g.Key, g.Select(r =>
        {
            var metadata = Parse(r[4]);
            JsonNode? Take(string key)
            {
                metadata.TryGetPropertyValue(key, out var value);
                metadata.Remove(key);
                return value;
            }
            // millimetres and x 1000 in the row, both divided by 1000 in the location
            static JsonNode? Scaled(JsonNode? v) => v is JsonObject o
                ? new JsonObject { ["x"] = Py.D(o["x"]) / 1000, ["y"] = Py.D(o["y"]) / 1000, ["z"] = Py.D(o["z"]) / 1000 } : null;
            var (pos, dir, reserve) = (Take(POSITION), Take(DIRECTION), Take(RESERVE));
            JsonNode? location = null;
            if (r[2] is PLACED_SLOT && pos is not null)
            {
                location = new JsonObject { ["reserve"] = reserve, ["pos"] = Scaled(pos) };
                if (dir is not null) location["dir"] = Scaled(dir);
            }
            return (JsonNode?)new JsonObject { ["slot"] = Py.FromSql(r[2]), ["units"] = Py.FromSql(r[3]), ["metadata"] = metadata, ["id"] = Py.FromSql(r[1]), ["location"] = location };
        }).ToArray()))];
    }

    /// <summary>Inventory/list of the account: a row per owned item with its definition and the owned rows. A placed item
    /// (slot 23) has `location` `{reserve, pos: {x, y, z}, dir: {x, y, z}}` (`dir` only with a direction), else null.</summary>
    public byte[] InventoryListReply(long account) =>
        Py.DumpBytes(new JsonArray(OwnRows(account).Select(g => (JsonNode?)new JsonObject
        {
            ["item"] = items.TryGetValue(g.ItemId, out var def) ? C(def) : new JsonObject { ["id"] = g.ItemId },
            ["own"] = new JsonArray(g.Own),
        }).ToArray()));

    /// <summary>Inventory/dict of the account: `{<item id>: [{id, slot, units, metadata, location}]}`.</summary>
    public byte[] InventoryDictReply(long account)
    {
        var dict = new JsonObject();
        foreach (var (itemId, own) in OwnRows(account))
            dict[itemId.ToString()] = new JsonArray(own);
        return Py.DumpBytes(dict);
    }
}
