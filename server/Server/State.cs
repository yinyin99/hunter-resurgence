// Player state in SQLite, one account per player in the server's one database; a new account is seeded from the
// recorded connect reply. Every method that touches a player takes the account id; the Db it opens is scoped to it.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

/// <summary>State-backed replies; one pooled sqlite connection per call (handlers run on many threads).</summary>
public sealed partial class State
{
    public const string UPLOAD_PATH = "/upload/storage/v1/b/photos/o";  // object-storage upload shape the client uses
    public const string PHOTO_PREFIX = "/photos/";
    public const string TOAST_TEXT = "Local test server\nProgress is not saved to the official servers";
    public const long SHOT_EVIDENCE = 14;  // photo_type of the automatic shot screenshot
    // the client often exits without disconnect: an open expedition with no heartbeat for this long has ended
    // (the client sends heartbeats about every 20 s)
    public const int HEARTBEAT_TIMEOUT = 120;
    /// <summary>The time of the stale checks and heartbeats; a test sets it.</summary>
    public Func<double> Clock { get; set; } = Py.Now;
    public const long SYSTEM_SENDER = 1024;  // the id this server gives its notice sender (Chat inbox)
    public const long SERVICE = 45;  // item type of premium services (unlimited ammo/fuel); no item is delivered for them
    public const long DefaultAvatar = 1000000;  // the picture of a new account: the first one of the avatar list
    /// <summary>Metadata keys of a restored item's placement: the items table has no columns for them (see <see cref="Items"/>).</summary>
    public const string POSITION = "__position", DIRECTION = "__direction";
    // Inventory/buy `wallet` → (wallet currency, catalogue price key); ponytail: SILVER (gm, silver_price) not supported yet
    static readonly Dictionary<string, (string Currency, string Key)> WALLETS = new() { ["GOLD"] = ("em", "price") };
    public static readonly byte[] BUY_FAILED = "{\"success\": false}"u8.ToArray();  // SILVER over the balance
    const int MaxBuyAmount = 1000;
    // connect keys that differ by launched reserve (locations and locations_descr are the
    // same for every reserve); taken from that reserve's recorded connect
    static readonly string[] RESERVE_KEYS = ["populations", "species_ammo"];
    const long COLOUR = 4294967040;

    /// <summary>The starting data of an account: items and wallet from src (a connect reply), skills from its levels and
    /// list (the player's Skill/list reply). The one source an importer's rows replace (call inside the account's transaction).</summary>
    static void Seed(Db con, JsonObject src, JsonNode? list)
    {
        foreach (var i in src["inventory"]!["items"]!.AsArray())
            con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, ?, ?, ?)",
                     long.Parse(Py.Str(i!["guid"])), i["item_id"], i["slot"], Py.Dumps(i["metadata"]), i["units"]);
        foreach (var (currency, amount) in src["wallet"] as JsonObject ?? new JsonObject())
            con.Exec("INSERT INTO wallet (account_id, currency, amount) VALUES ($a, ?, ?)", currency, amount);
        foreach (var (kind, key, xp) in SeedSkills(Py.Truthy(src["skills"]) ? src["skills"]!.AsObject() : new JsonObject(),
                                                   Py.Truthy(list) ? list!.AsObject() : new JsonObject()))
            con.Exec("INSERT OR IGNORE INTO skills (account_id, kind, key, xp) VALUES ($a, ?, ?, ?)", kind, key, xp);
    }

    /// <summary>The next id of a table keyed (account_id, id): ids are per account, and restored rows keep the zip's.
    /// Call inside a transaction.</summary>
    static long NextId(Db con, string table, string column) =>
        (long)con.Scalar($"SELECT COALESCE(MAX({column}), 0) + 1 FROM {table} WHERE account_id = $a")!;

    /// <summary>Rows in the connect reply's inventory.items shape (guid as a string). A restored placed item keeps its
    /// `position` and `direction` in the metadata under <see cref="POSITION"/> and <see cref="DIRECTION"/>; they leave it
    /// here as the row's own members.</summary>
    public static JsonArray Items(Db con) => new(con.Rows("SELECT guid, item_id, slot, metadata, units FROM items WHERE account_id = $a ORDER BY guid").Select(r =>
    {
        var metadata = JsonNode.Parse((string)r[3])!.AsObject();
        var row = new JsonObject { ["item_id"] = Py.FromSql(r[1]), ["slot"] = Py.FromSql(r[2]), ["guid"] = Py.Str(Py.FromSql(r[0])) };
        foreach (var (key, member) in new[] { (POSITION, "position"), (DIRECTION, "direction") })
            if (metadata.TryGetPropertyValue(key, out var value))
            {
                metadata.Remove(key);
                row[member] = value;
            }
        row["metadata"] = metadata;
        row["units"] = Py.FromSql(r[4]);
        return (JsonNode?)row;
    }).ToArray());

    /// <summary>Add amount (negative: spend) to a wallet currency (call inside a transaction); return the new balance, or
    /// null when the currency is missing or the balance would drop below 0.</summary>
    public static JsonNode? AddCurrency(Db con, string currency, object amount)
    {
        if (con.Exec("UPDATE wallet SET amount = amount + ?1 WHERE account_id = $a AND currency = ?2 AND amount + ?1 >= 0", amount, currency) == 0)
            return null;
        return Py.FromSql(con.Rows("SELECT amount FROM wallet WHERE account_id = $a AND currency = ?", currency)[0][0]);
    }

    /// <summary>Add a slot-0 row with a full stack per item id and queue their update 10 (call inside a transaction).
    /// info(item_id) is the definition sent along; units as in every recorded row: null unless the definition has a
    /// stack size.</summary>
    public static void Deliver(Db con, IEnumerable<long> itemIds, Func<long, JsonObject?> info)
    {
        var rows = new JsonArray();
        var infos = new OrderedDictionary<long, JsonObject?>();
        foreach (var itemId in itemIds)
        {
            infos[itemId] = info(itemId);
            var units = infos[itemId]!["units"];
            units = units is null || Is(units, -2) ? null : units;
            var guid = NextId(con, "items", "guid");
            con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, 0, '{}', ?)", guid, itemId, units);
            rows.Add(new JsonObject
            {
                ["item_id"] = itemId, ["slot"] = 0, ["guid"] = guid.ToString(), ["metadata"] = new JsonObject(), ["units"] = C(units),
            });
        }
        con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject
        {
            ["items"] = new JsonObject { ["items"] = rows, ["infos"] = new JsonArray(infos.Values.Select(C).ToArray()) },
            ["update"] = 10,
        }));
    }

    /// <summary>skills rows from the connect's integer `skills` levels and a recorded Skill/list reply (numbers): the
    /// Skill/list XP, raised to the start of the connect's level when that connect is newer.</summary>
    public static List<(string Kind, long Key, double Xp)> SeedSkills(JsonObject levels, JsonObject numbers)
    {
        static (string, long, double) Row(string kind, long key, JsonNode? level, JsonNode? number)
        {
            var a = Py.Truthy(number) ? Skills.Xp(Py.D(number)) : Skills.FRESH_XP;
            var b = Skills.Threshold(Py.L(level));
            return (kind, key, b > a ? b : a);
        }
        var rows = new List<(string, long, double)>();
        foreach (var kind in new[] { "spotting", "tracking" })
        {
            var seen = Py.Truthy(numbers[kind]) ? numbers[kind]!.AsArray() : new JsonArray();
            var lv = G(levels, kind, new JsonArray())!.AsArray();
            for (var i = 0; i < lv.Count; i++)
                rows.Add(Row(kind, i, lv[i], i < seen.Count ? seen[i] : null));
        }
        var seenW = Py.Truthy(numbers["weapon"]) ? numbers["weapon"]!.AsObject() : new JsonObject();
        if (Py.Truthy(levels["weapon"]))
            foreach (var (w, level) in levels["weapon"]!.AsObject())
                rows.Add(Row("weapon", long.Parse(w), level, seenW[w]));
        return rows;
    }

    /// <summary>Store heartbeat events in one transaction; return this heartbeat's updates.
    ///
    /// Updates queued by earlier heartbeats come first. A harvest (event 3) gets its trophy score (update 6) in the
    /// *next* heartbeat and its gold (update 15, new balance) in this one; a photo request (event 4) gets its upload URL
    /// (update 11) right away. An item consumed (event 6) loses `quantity` units (a negative quantity
    /// refills); at 0 it moves to slot 0 like the client's record.
    /// uploadUrl(photo_id, part) builds the local upload URL. harvestPage(event3, spawn_event7, expedition_id,
    /// reserve_id, score, score_type) builds the /game/user/harvest/&lt;kill_id&gt; reply stored for the kill, in the
    /// latest expedition. trophy(event3, spawn_event7, event22, last_hit) returns (score, score_type, css).
    /// ammoCheck(hit, species) returns (toast update, notice message) for a non-permitted hit, else null: the toast comes
    /// with the next heartbeat, the message lands in the inbox, and a kill with any such hit is disqualified.
    /// skillUpdates(con, event, species) applies a spot's, clue's or hit's skill XP and returns its level-up toasts,
    /// which also come with the next heartbeat. An item metadata event (23) merges its keys into the item's (dog XP and
    /// stamina).</summary>
    public static JsonArray Heartbeat(Db con, double now, JsonArray events, Func<long, string, string> uploadUrl,
                                      Func<JsonObject, JsonObject, JsonNode?, JsonNode?, JsonNode?, string?, JsonObject> harvestPage,
                                      Func<JsonObject, JsonObject, JsonObject, JsonObject?, (JsonNode? Score, string? Type, double Css)> trophy,
                                      Func<JsonObject, JsonNode?, (JsonObject Toast, string Message)?> ammoCheck,
                                      Func<Db, JsonObject, JsonNode?, List<JsonObject>> skillUpdates)
    {
        con.Begin();
        // a late heartbeat reopens the hunt a stale close ended at its last heartbeat (every other close ends elsewhere);
        // its statistics are counted again when it closes
        con.Exec("UPDATE expeditions SET end_ts = NULL, stats = NULL WHERE account_id = $a AND end_ts = last_seen"
                 + " AND id = (SELECT MAX(id) FROM expeditions WHERE account_id = $a)");
        con.Exec("UPDATE expeditions SET last_seen = ? WHERE account_id = $a AND end_ts IS NULL", now);
        var updates = new JsonArray(con.Rows("SELECT id, body FROM pending WHERE account_id = $a ORDER BY id").Select(r => JsonNode.Parse((string)r[1])).ToArray());
        con.Exec("DELETE FROM pending WHERE account_id = $a");
        foreach (var node in events)
        {
            if (node is not JsonObject e) continue;
            JsonNode? species = null;
            con.Exec("INSERT INTO events (account_id, event, body) VALUES ($a, ?, ?)", e["event"], Py.Dumps(e));
            if (Is(e["event"], 1) || Is(e["event"], 11) || Is(e["event"], 12))
            {
                var row = con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 7 AND individual = ?"
                                   + " ORDER BY id DESC LIMIT 1", e["individual"]).FirstOrDefault();
                species = row is null ? null : Parse(row[0])["species"];
                foreach (var toast in skillUpdates(con, e, species))
                    con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(toast));
            }
            if (Is(e["event"], 1))
            {
                if (ammoCheck(e, species) is { } warning)
                {
                    con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(warning.Toast));
                    con.Exec("INSERT INTO messages (account_id, sender, content, ts) VALUES ($a, ?, ?, ?)", SYSTEM_SENDER, warning.Message, (long)Py.Now());
                }
            }
            else if (Is(e["event"], 6) && Py.IsDigit(Py.Str(G(e, "item", ""))) && Py.Truthy(G(e, "quantity", 0)))  // the first heartbeat lists every item with 0
                con.Exec("UPDATE items SET units = units - ?1, slot = CASE WHEN units - ?1 <= 0 THEN 0 ELSE slot END"
                         + " WHERE account_id = $a AND guid = ?2 AND units IS NOT NULL", G(e, "quantity", 0), long.Parse(Py.Str(e["item"])));
            else if (Is(e["event"], 23) && Py.IsDigit(Py.Str(G(e, "item", ""))) && e["metadata"] is JsonObject metadata)
                con.Exec("UPDATE items SET metadata = json_patch(metadata, ?) WHERE account_id = $a AND guid = ?",
                         Py.Dumps(metadata), long.Parse(Py.Str(e["item"])));
            else if (Is(e["event"], 3))
            {
                var harvest = Py.Dumps(e);
                var killId = Math.Max(NextId(con, "kills", "kill_id"), (long)con.Scalar("SELECT COALESCE(MAX(CAST(id AS INTEGER)), 0) + 1 FROM trophies WHERE account_id = $a AND source IN ('/game/user/trophies', '/game/user/harvest')")!);
                // a new kill id is above every restored harvest id. ponytail: kills that already exist, and kills made before a later restore, are not renumbered
                con.Exec("INSERT INTO kills (account_id, kill_id, individual, species, harvest) VALUES ($a, ?, ?, ?, ?)",
                         killId, e["individual"], e["extended_species"], harvest);
                var (e7, e22) = KillEvents(con, e["individual"], harvest);
                var hits = KillHits(con, e["individual"], harvest);
                var disqualified = hits.Any(h => ammoCheck(h, e7["species"]) is not null);
                var (score, scoreType, css) = disqualified ? (null, null, 0.0) : trophy(e, e7, e22, hits.Count > 0 ? hits[^1] : null);
                var expedition = con.Rows("SELECT id, reserve FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1").FirstOrDefault();
                con.Exec("INSERT INTO harvests (account_id, kill_id, body) VALUES ($a, ?, ?)", killId, Py.Dumps(harvestPage(
                    e, e7, Py.FromSql(expedition?[0]), Py.FromSql(expedition?[1]), score, scoreType)));
                // a harvest in a hunt that already closed (a heartbeat after its close): its statistics are counted again
                con.Exec("UPDATE expeditions SET stats = NULL WHERE account_id = $a AND id = ? AND end_ts IS NOT NULL", Py.FromSql(expedition?[0]));
                var update = new JsonObject
                {
                    ["individual"] = C(e["individual"]),
                    ["trophy_shot"] = disqualified ? 0 : 1,
                    ["score_text"] = disqualified ? "Disqualified " : Py.Truthy(score) ? $"Score: {Py.Fixed(Py.D(score), 4)}" : "No score",
                    ["update"] = 6,
                    ["score_type"] = scoreType,
                    ["score"] = disqualified ? (JsonNode)(-1) : Py.Truthy(score) ? C(score) : 0,
                    ["kill_id"] = killId,
                    ["css"] = css,
                };
                con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(update));
                if (Py.RoundInt(css) > 0)
                    updates.Add(new JsonObject { ["amount"] = AddCurrency(con, "gm", Py.RoundInt(css)), ["type"] = "gm", ["update"] = 15 });
            }
            else if (Is(e["event"], 4))
            {
                var photoId = con.Insert("INSERT INTO photos (account_id, hash, photo_type, shot_id, individual) VALUES ($a, ?, ?, ?, ?)",
                                         e["hash"], e["photo_type"], e["shot_id"], e["individual"]);
                var update = new JsonObject
                {
                    ["url"] = uploadUrl(photoId, ""), ["photo_type"] = C(e["photo_type"]), ["hash"] = C(e["hash"]), ["update"] = 11,
                };
                if (!Is(e["photo_type"], SHOT_EVIDENCE))
                    // ponytail: trophy-photo update 11 shape is a guess; thumb_url is a guess too
                    update["thumb_url"] = uploadUrl(photoId, "thumb");
                updates.Add(update);
            }
        }
        con.Commit();
        return updates;
    }

    /// <summary>Hit events (event 1) on the animal of a kill since its latest spawn (individual ids are reused across
    /// sessions). harvest is the kill's stored event 3 JSON, identical to its events row.</summary>
    public static List<JsonObject> KillHits(Db con, JsonNode? individual, string harvest) => con.Rows(
        "WITH k AS (SELECT MAX(id) AS id FROM events WHERE account_id = $a AND event = 3 AND body = ?2)"
        + " SELECT body FROM events, k WHERE account_id = $a AND event = 1 AND individual = ?1 AND events.id < k.id"
        + " AND events.id > COALESCE((SELECT MAX(id) FROM events WHERE account_id = $a AND event = 7"
        + " AND individual = ?1 AND id < k.id), 0) ORDER BY events.id", individual, harvest)
        .Select(r => Parse(r[0])).ToList();

    /// <summary>(event 7, event 22) of a kill: the latest of each for its individual before its event 3; {} when missing.</summary>
    public static (JsonObject E7, JsonObject E22) KillEvents(Db con, JsonNode? individual, string harvest)
    {
        JsonObject Latest(long ev)
        {
            var row = con.Rows(
                "WITH k AS (SELECT MAX(id) AS id FROM events WHERE account_id = $a AND event = 3 AND body = ?2)"
                + " SELECT body FROM events, k WHERE account_id = $a AND event = ?3 AND individual = ?1"
                + " AND events.id < k.id ORDER BY events.id DESC LIMIT 1", individual, harvest, ev).FirstOrDefault();
            return row is null ? new JsonObject() : Parse(row[0]);
        }
        return (Latest(7), Latest(22));
    }

    /// <summary>End the open expedition at its last heartbeat if none came for HEARTBEAT_TIMEOUT before <paramref name="now"/>
    /// (call inside a transaction); true when it closed one (credit the hunterscore after the commit).</summary>
    public static bool CloseStale(Db con, double now) =>
        con.Exec("UPDATE expeditions SET end_ts = last_seen WHERE account_id = $a AND end_ts IS NULL AND last_seen < ?", now - HEARTBEAT_TIMEOUT) > 0;

    /// <summary>Close and credit the stale hunts of every account (the timer's work: a quit through the game's menu sends no disconnect).</summary>
    public void CloseStaleHunts()
    {
        List<long> ids;
        using (var all = Store.Open())
            ids = all.Rows("SELECT id FROM accounts").Select(r => (long)r[0]).ToList();
        foreach (var id in ids)  // ponytail: one cheap UPDATE per account a tick; select only accounts with an open hunt if they reach the tens of thousands
        {
            using var con = Con(id);
            con.Begin();
            var stale = CloseStale(con, Clock());
            con.Commit();
            if (stale)
                UpdateHunterscore(con, id);
        }
    }

    public static long LastEvent(Db con) => (long)con.Scalar("SELECT COALESCE(MAX(id), 0) FROM events WHERE account_id = $a")!;

    /// <summary>Public_user/expedition `stats` (the Statistics/total shape the site's formatStats/formatAnimals read), as
    /// the site sends for a finished hunt: `data` holds `achievements` first, then `weapon` and
    /// `animal` only when not empty, and each counter only when not 0, in Statistics/total's key order.
    ///
    /// kills: [(species, hits, ethical)] of the expedition. Events after first_event up to untilEvent (the next expedition's
    /// first_event): 1 = hit, 21 = miss (matches the client's disconnect totals); 11 = spot and 12 = track clue, counted as
    /// distinct animals per species (Statistics/total `spots`/`tracks`; event 13, the identified track, does not count).</summary>
    public static JsonObject HuntStats(Db con, JsonNode? firstEvent, long untilEvent, List<(JsonNode? Sp, List<JsonObject> Hits, bool Ethical)> kills,
                                       long duration, JsonNode? distance)
    {
        var weapon = new JsonObject();
        var animal = new JsonObject();
        var seen = new HashSet<string>();

        JsonObject Ammo(JsonObject e) => SetDefault(SetDefault(weapon, Py.Str(e["weapon_id"]), () => new JsonObject()),
            Py.Str(e["ammunition_id"]), () => new JsonObject { ["hits"] = 0, ["misses"] = 0, ["kills"] = 0, ["ethical_kills"] = 0, ["distance"] = 0 });

        JsonObject Species(JsonNode? sp) =>
            SetDefault(animal, Py.Str(sp), () => new JsonObject { ["spots"] = 0, ["tracks"] = 0, ["kills"] = 0, ["ethical_kills"] = 0 });

        foreach (var r in con.Rows("SELECT id, event, body FROM events WHERE account_id = $a AND id > ? AND id <= ? AND event IN (1, 11, 12, 21)"
                                   + " ORDER BY id", firstEvent, untilEvent))
        {
            var (eventId, ev, e) = ((long)r[0], Py.FromSql(r[1]), Parse(r[2]));
            if (Is(ev, 1))
            {
                var s = Ammo(e);
                Inc(s, "hits");
                var d = Times10(G(e, "distance_cms", 0));
                if (Py.D(d) > Py.D(s["distance"])) s["distance"] = d;
            }
            else if (Is(ev, 21))
                Inc(Ammo(e), "misses");
            else if (seen.Add(Py.Str(ev) + "\n" + Py.Dumps(e["individual"])))
            {
                var row = con.Rows("SELECT json_extract(body, '$.species') FROM events WHERE account_id = $a AND event = 7"
                                   + " AND individual = ? AND id < ? ORDER BY id DESC LIMIT 1",
                                   e["individual"], eventId).FirstOrDefault();
                if (row is not null)
                    Inc(Species(Py.FromSql(row[0])), Is(ev, 11) ? "spots" : "tracks");
            }
        }
        foreach (var (sp, hits, ethical) in kills)
            foreach (var s in hits.Count > 0 ? new[] { Species(sp), Ammo(hits[^1]) } : [Species(sp)])
            {
                Inc(s, "kills");
                s["ethical_kills"] = Py.L(s["ethical_kills"]) + (ethical ? 1 : 0);
            }
        static JsonObject NonZero(JsonObject counters, params string[] order)
        {
            var o = new JsonObject();
            foreach (var k in order)
                if (Py.D(counters[k]) != 0)
                    o[k] = counters[k]!.DeepClone();
            return o;
        }
        var data = new JsonObject { ["achievements"] = new JsonArray() };
        var weaponOut = new JsonObject();
        foreach (var (w, ammo) in weapon)
        {
            var byAmmo = new JsonObject();
            foreach (var (a, c) in ammo!.AsObject())
                byAmmo[a] = NonZero(c!.AsObject(), "ethical_kills", "hits", "misses", "kills", "distance");
            weaponOut[w] = byAmmo;
        }
        if (weaponOut.Count > 0) data["weapon"] = weaponOut;
        var animalOut = new JsonObject();
        foreach (var (sp, c) in animal)
            animalOut[sp] = NonZero(c!.AsObject(), "ethical_kills", "tracks", "spots", "kills");
        if (animalOut.Count > 0) data["animal"] = animalOut;
        return new JsonObject { ["duration"] = duration, ["distance"] = distance, ["data"] = data };
    }

    /// <summary>Store an upload as &lt;photos_dir&gt;/&lt;id&gt;-&lt;photo_type&gt;[-&lt;part&gt;].jpg; return the file name,
    /// null if unknown id.</summary>
    public static string? SavePhoto(Db con, string photosDir, long photoId, byte[] data, string part = "")
    {
        con.Begin();
        var row = con.Rows("SELECT photo_type FROM photos WHERE account_id = $a AND id = ?", photoId).FirstOrDefault();
        if (row is null || row[0] is string)  // photo_type comes from the heartbeat: a string could carry a path
            return null;
        var name = $"{photoId}-{Py.Str(Py.FromSql(row[0]))}{(part != "" ? "-" + part : "")}.jpg";
        Directory.CreateDirectory(photosDir);
        File.WriteAllBytes(Path.Combine(photosDir, name), data);
        if (part == "")
            con.Exec("UPDATE photos SET file = ? WHERE account_id = $a AND id = ?", name, photoId);
        con.Commit();
        return name;
    }

    /// <summary>Apply an Inventory/move_multiple2 call in one transaction.
    ///
    /// cloned: [{"item_guid": "new_item_&lt;ts&gt;", "item_id": N}] creates items; the reply maps each placeholder to its
    /// new guid, or is null when nothing was cloned (the reply is `null`).
    /// mapping: [{"item_guid": guid or placeholder, "slot_id": N}] sets slots; unknown guids are ignored.</summary>
    public static JsonObject? MoveMultiple2(Db con, JsonArray mapping, JsonArray cloned, Dictionary<long, JsonNode?> defaultUnits)
    {
        var @new = new List<(JsonNode? Key, long Guid)>();  // Python dict: keys compared with ==
        con.Begin();
        var nextGuid = NextId(con, "items", "guid");
        foreach (var node in cloned)
        {
            var c = node!.AsObject();
            con.Exec("INSERT INTO items (account_id, guid, item_id, slot, metadata, units) VALUES ($a, ?, ?, 0, '{}', ?)",
                     nextGuid, c["item_id"], TryKey(c["item_id"], out var id) ? defaultUnits.GetValueOrDefault(id) : null);
            var i = @new.FindIndex(x => Py.Eq(x.Key, c["item_guid"]));
            if (i >= 0) @new[i] = (@new[i].Key, nextGuid);
            else @new.Add((c["item_guid"], nextGuid));
            nextGuid++;
        }
        foreach (var node in mapping)
        {
            var m = node!.AsObject();
            var i = @new.FindIndex(x => Py.Eq(x.Key, m["item_guid"]));
            var guid = i >= 0 ? @new[i].Guid.ToString() : Py.Str(m["item_guid"]);
            if (Py.IsDigit(guid))
                con.Exec("UPDATE items SET slot = ? WHERE account_id = $a AND guid = ?", m["slot_id"], long.Parse(guid));
        }
        con.Commit();
        if (@new.Count == 0)
            return null;
        var reply = new JsonObject();
        foreach (var (key, guid) in @new)
            reply[Py.IsStr(key) ? Py.Str(key) : Py.Dumps(key)] = guid;  // json.dumps turns non-str keys into their JSON text
        return reply;
    }

    readonly string photosDir, baseUrl, iconUrl, avatarBase;
    readonly JsonObject application;
    readonly JsonNode? skillList;
    readonly bool offline;
    /// <summary>A community server: every request needs a token, and the sign-in, start page and issued tokens are on.</summary>
    public bool Online => !offline;
    /// <summary>The server's database: accounts, tokens and every player's rows.</summary>
    public readonly Store Store;
    /// <summary>The account of the recorded connect: the one account of a local server, and the one a request that no
    /// token resolves gets while offline.</summary>
    public readonly long LocalAccount;
    readonly Dictionary<long, JsonObject> species = new(), items = new(), infos = new(), ballistics = new();
    readonly Dictionary<long, string?> reserves = new();
    readonly Dictionary<long, JsonNode?> locations = new(), defaultUnits = new();
    readonly JsonObject locale, connect, start;
    readonly Dictionary<long, string> textures;
    readonly Dictionary<int, JsonObject> reserveData = new();
    /// <summary>Admin spawner entries (spawn.json), added to the connect reply.</summary>
    public readonly Spawner Spawns;
    readonly JsonArray discounts, ethicalAmmo;
    readonly OrderedDictionary<long, long> family = new();
    readonly Facts facts;

    /// <summary>baseUrl: scheme+host this server answers on; upload and photo URLs point there.
    /// iconUrl: image shown in the toast queued on connect and in skill level-up toasts.
    /// application: the recorded Application/application reply (species, items, reserves, locations);
    /// locale: the recorded English locale strings ({key: text}); textures: {texture id: name} (ScorePage);
    /// discounts: the recorded Application/discounts reply; sampleInfos: item definitions of the
    /// json_inventory.json sample (`infos`); reserveConnects: {reserve id: connect body recorded after a Launch/start of
    /// that reserve}, the source of that reserve's RESERVE_KEYS; skillList: the player's recorded Skill/list reply, seeds
    /// the skills; itemDefs: the py_data item.json, whose `parent` chain groups weapon variants into the family
    /// that shares one weapon skill. offline: a request no token resolves is the local account's (a local server);
    /// false: it is refused (a community server). startBody: the neutral connect (Install.Connect) every new account
    /// starts from; null: connectBody is that start (no recorded connect). facts: the facts file (its rank table scores the hunterscore).</summary>
    public State(string dbPath, byte[] connectBody, string baseUrl, string iconUrl, JsonObject application, JsonObject locale,
                 Dictionary<long, string> textures, JsonArray? discounts = null, JsonArray? sampleInfos = null,
                 Dictionary<int, byte[]>? reserveConnects = null, JsonNode? skillList = null, JsonArray? itemDefs = null, bool offline = true,
                 byte[]? startBody = null, Facts? facts = null)
    {
        this.facts = facts ?? Facts.Default;
        this.skillList = skillList;
        this.offline = offline;
        Spawns = new Spawner(Path.Combine(Path.GetDirectoryName(dbPath) ?? "", "spawn.json"));
        photosDir = Path.Combine(Path.GetDirectoryName(dbPath) ?? "", "photos");
        this.baseUrl = baseUrl;
        this.iconUrl = iconUrl;
        this.application = application;
        foreach (var s in Arr(G(application, "species", null)))
            species[Py.L(s!["id"])] = s.AsObject();
        foreach (var i in Arr(G(application, "items", null)))
            items[Py.L(i!["id"])] = i.AsObject();
        foreach (var r in Arr(G(application, "reserves", null)))
            reserves[Py.L(r!["id"])] = (string?)r["name"];
        foreach (var loc in Arr(G(application, "locations", null)))
            locations[Py.L(loc!["id"])] = loc["pos"];
        this.locale = locale;
        this.textures = textures;
        connect = JsonNode.Parse(connectBody)!.AsObject();
        start = startBody is null ? connect : JsonNode.Parse(startBody)!.AsObject();
        foreach (var (r, body) in reserveConnects ?? [])
        {
            var data = new JsonObject();
            foreach (var (k, v) in JsonNode.Parse(body)!.AsObject())
                if (RESERVE_KEYS.Contains(k))
                    data[k] = C(v);
            reserveData[r] = data;
        }
        this.discounts = discounts ?? new JsonArray();
        var connectInfos = connect["inventory"]!["infos"]!.AsArray();
        foreach (var i in Arr(sampleInfos).Concat(connectInfos))
            infos[Py.L(i!["item_id"])] = i.AsObject();
        foreach (var i in connectInfos)
            defaultUnits[Py.L(i!["item_id"])] = i["units"];
        // permitted ammo per species id, all species (species_ammo is the reserve's subset)
        ethicalAmmo = G(connect, "ethical", null) as JsonArray ?? new JsonArray();
        var avatarUrl = Py.Str(G(AccountTemplate(), "avatar_url", ""));
        var (scheme, netloc) = SchemeNetloc(avatarUrl);
        // the account's picture is `<base>/256/<id>.png`; the notice sender's icons live beside it
        avatarBase = avatarUrl.IndexOf("/256/", StringComparison.Ordinal) is var cut and > 0 ? avatarUrl[..cut] : $"{scheme}://{netloc}";
        var byDefine = new Dictionary<string, JsonObject>();
        foreach (var d in Arr(itemDefs))
            byDefine[(string)d!["define"]!] = d.AsObject();

        long Root(JsonObject d)
        {
            while (d["parent"] is JsonNode p && Py.IsStr(p) && byDefine.TryGetValue((string)p!, out var parent))
                d = parent;
            return Py.L(d["id"]);
        }
        foreach (var d in Arr(itemDefs))
        {
            family[Py.L(d!["id"])] = Root(d.AsObject());
            if (d["extra"]?["ammo"] is JsonObject ammo)
                ballistics[Py.L(d["id"])] = ammo;
        }
        Store = new Store(dbPath);
        Store.PurgeExpired();
        var local = connect["account"] as JsonObject ?? new JsonObject();
        LocalAccount = Py.L(G(local, "user_id", 1));
        if (!Store.AccountExists(LocalAccount))
            Store.CreateAccount(LocalAccount, Py.Str(G(local, "handle", "")), (JsonObject)local.DeepClone(), seed: con => Seed(con, connect, skillList));
        // every closed hunt has its statistics, and the stored hunterscores follow the rank table and the counts of today, also
        // after a server update
        List<long> ids;
        using (var all = Store.Open())
            ids = all.Rows("SELECT id FROM accounts ORDER BY id").Select(r => (long)r[0]).ToList();
        foreach (var id in ids)
            using (var con = Store.Open(id))
                UpdateHunterscore(con, id);
    }

    /// <summary>The start's `account`: the template of every new account. Never the recorded one when there is a start.</summary>
    JsonObject AccountTemplate() => G(start, "account", null) as JsonObject ?? new JsonObject();

    /// <summary>The connect reply's `account` object of this account.</summary>
    JsonObject Account(long account) => Store.AccountJson(account);

    /// <summary>The connect `account` object of a new account: the start's shape (never the recorded one) with this user id and
    /// handle, and no other value of any real player (name of their page, pictures, score, ids).</summary>
    public JsonObject NewAccount(long id, string handle)
    {
        var account = (JsonObject)AccountTemplate().DeepClone();
        account["user_id"] = id;
        account["handle"] = handle;
        foreach (var (key, value) in new (string, JsonNode)[]
                 {
                     ("account_id", id), ("hostname", handle.ToLowerInvariant()), ("avatar_url", $"{avatarBase}/256/{DefaultAvatar}.png"),
                     ("avatar", DefaultAvatar), ("avatar_id", DefaultAvatar), ("face", DefaultAvatar), ("fullbody_avatar", 0),
                     ("gender", 0), ("membership", 0), ("hunterscore", 0),
                 })
            if (account.ContainsKey(key))
                account[key] = value;
        return account;
    }

    /// <summary>A new account with the server's starting data (the neutral start, never the recorded connect). id is the game's user id; throws when the
    /// id, the handle or the Steam id is taken.</summary>
    public void CreateAccount(long id, string handle, string? steamId64 = null) =>
        Store.CreateAccount(id, handle, NewAccount(id, handle), steamId64, con => Seed(con, start, ReferenceEquals(start, connect) ? skillList : null));

    /// <summary>The item catalogue a restore reads units by (the Application/application reply); null without one.</summary>
    public Restore.IItemCatalogue? Catalogue => items.Count == 0 ? null : new ItemCatalogue(items);

    sealed class ItemCatalogue(Dictionary<long, JsonObject> items) : Restore.IItemCatalogue
    {
        public bool HasUnits(int itemId) => items.TryGetValue(itemId, out var item) && item["units"] is JsonObject;
    }

    /// <summary>The account of a request: the first of its tokens that resolves. None does: the local account on a local
    /// server, null (refuse) on a community server.</summary>
    public long? Authenticate(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
            if (Store.ResolveToken(token) is { } account)
                return account;
        return offline ? LocalAccount : null;
    }

    string? SpeciesName(JsonNode? sp)
    {
        // ponytail: the antler-type suffix is dropped ("Mule Deer (Typical)" → "Mule Deer"); only checked for mule deer
        var name = Lookup(species, sp)?["name"];
        var s = (Py.Truthy(name) ? (string)name! : "").Split(" (")[0];
        return s == "" ? null : s;
    }

    /// <summary>(toast update, notice message) for a hit with ammo outside the species' connect `ethical` list; null if
    /// permitted or the species has no list.</summary>
    public (JsonObject Toast, string Message)? AmmoCheck(JsonObject hit, JsonNode? sp)
    {
        var allowed = sp is not null && Mod100(sp) < ethicalAmmo.Count ? ethicalAmmo[(int)Mod100(sp)] : null;
        if (!Py.Truthy(allowed))  // empty list (dogs, event creatures): no rule
            return null;
        if (allowed!.AsArray().Any(a => Py.Eq(a, hit["ammunition_id"])))
            return null;
        var name = SpeciesName(sp) ?? "None";
        var ammo = Py.Str(Lookup(items, hit["ammunition_id"])?["name"]);
        var toast = new JsonObject
        {
            ["text"] = $"Not permitted: {ammo} may not be used on {name}. See your inbox.",
            ["colour"] = COLOUR, ["destination"] = $"{avatarBase}/doc_64.jpg", ["update"] = 3, ["timer"] = -1, ["type"] = 100,
        };
        // ponytail: one message template for every species
        var names = string.Join(", ", allowed.AsArray().Select(i => Lookup(items, i) is { } item ? Py.Str(G(item, "name", "")) : ""));
        var message = $"Wrong ammunition for {name}. Permitted: {names}.";
        return (toast, message);
    }

    public bool Ethical(JsonNode? sp, List<JsonObject> hits) => !hits.Any(h => AmmoCheck(h, sp) is not null);

    /// <summary>Skill XP of a spot (event 11), a track clue (event 12) or a permitted hit (event 1), applied inside the
    /// heartbeat's transaction; the level-up toast if the species or the fired weapon gained a level (sent with the next
    /// heartbeat).</summary>
    public List<JsonObject> SkillUpdates(Db con, JsonObject e, JsonNode? sp)
    {
        string kind;
        List<object?> keys;
        double gain;
        string? name;
        if (Is(e["event"], 11) && sp is not null)
            (kind, keys, gain, name) = ("spotting", [sp], Skills.SPOT_XP, SpeciesName(sp));
        else if (Is(e["event"], 12) && sp is not null)
        {
            // the n-th clue on this animal since the latest hunt's first event (this one is already stored)
            var n = (long)con.Scalar("SELECT COUNT(*) FROM events WHERE account_id = $a AND event = 12 AND individual = ?"
                                     + " AND id > COALESCE((SELECT first_event FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1), 0)",
                                     e["individual"])!;
            (kind, keys, gain, name) = ("tracking", [sp], Skills.TrackXp(n), SpeciesName(sp));
        }
        else if (Is(e["event"], 1) && AmmoCheck(e, sp) is null)
        {
            var weapon = e["weapon_id"];
            var fam = TryKey(weapon, out var w) && family.TryGetValue(w, out var root) ? JsonValue.Create(root) : weapon;
            kind = "weapon";
            gain = Skills.HitXp(sp is null ? -1 : Py.L(sp), e["animal_state"]);
            name = Lookup(items, weapon)?["name"] is { } n ? Py.Str(n) : null;
            keys = [weapon, .. family.Where(kv => Py.Eq(JsonValue.Create(kv.Value), fam) && !Py.Eq(JsonValue.Create(kv.Key), weapon))
                                     .Select(kv => (object?)kv.Key)];
            con.Exec("INSERT OR IGNORE INTO skills (account_id, kind, key, xp) VALUES ($a, 'weapon', ?, ?)", weapon, Skills.FRESH_XP);
        }
        else
            return [];
        var row = con.Rows("SELECT xp FROM skills WHERE account_id = $a AND kind = ? AND key = ?", kind, keys[0]).FirstOrDefault();
        if (row is null)  // a species outside the 59 skill slots
            return [];
        foreach (var k in keys)
            con.Exec("UPDATE skills SET xp = ROUND(xp + ?, 1) WHERE account_id = $a AND kind = ? AND key = ?", gain, kind, k);
        var xp = Convert.ToDouble(row[0]);
        var level = Py.L(Skills.Value(xp + gain));
        if (level <= Py.L(Skills.Value(xp)))
            return [];
        return [new JsonObject
        {
            ["text"] = Skills.LevelUpText(kind, name, level), ["colour"] = COLOUR, ["destination"] = iconUrl,
            ["update"] = 3, ["timer"] = 1000, ["type"] = 100,
        }];
    }

    /// <summary>{weapon: {id: n}, tracking: [n], spotting: [n]} with n = number(xp): the shape of Skill/list
    /// (Skills.Value) and of the connect's `skills` (levels).</summary>
    JsonObject SkillNumbers(long account, Func<double, JsonNode> number)
    {
        List<object[]> rows;
        using (var con = Con(account))
            rows = con.Rows("SELECT kind, key, xp FROM skills WHERE account_id = $a ORDER BY kind, key");
        var weapon = new JsonObject();
        foreach (var r in rows.Where(r => (string)r[0] == "weapon"))
            weapon[Py.Str(Py.FromSql(r[1]))] = number(Convert.ToDouble(r[2]));
        JsonArray Of(string kind) => new(rows.Where(r => (string)r[0] == kind).Select(r => (JsonNode?)number(Convert.ToDouble(r[2]))).ToArray());
        return new JsonObject { ["weapon"] = weapon, ["tracking"] = Of("tracking"), ["spotting"] = Of("spotting") };
    }

    /// <summary>Skill/list for the player's user_id; null for anyone else (a local server replays their recorded reply, a
    /// community server sends the empty one).</summary>
    public byte[]? SkillListReply(long account, byte[] formBody)
    {
        var userId = Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id");
        if (userId != Py.Str(Account(account)["user_id"]))
            return null;
        return Py.DumpBytes(SkillNumbers(account, Skills.Value));
    }

    /// <summary>Statistics/total for the player's user_id: the restored statistics plus the closed server hunts; null for anyone
    /// else and, on a local server, for an account without a restored profile (its recorded reply is replayed). On a community
    /// server every account has them (a new account's base is empty).</summary>
    public byte[]? StatisticsReply(long account, byte[] formBody) => RestoredReply(account, formBody, RestoredReads.StatisticsTotal, Online);

    /// <summary>Achievement/stats for the player's user_id from the restored rows; null for anyone else and for an account
    /// without a restored profile (a local server replays their recorded reply, a community server sends the empty one).</summary>
    public byte[]? AchievementReply(long account, byte[] formBody) => RestoredReply(account, formBody, RestoredReads.AchievementStats);

    byte[]? RestoredReply(long account, byte[] formBody, Func<Db, JsonNode> read, bool everyAccount = false)
    {
        var userId = Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id");
        if (userId != Py.Str(Account(account)["user_id"]))
            return null;
        using var con = Con(account);
        return everyAccount || RestoredReads.Has(con) ? Py.DumpBytes(read(con)) : null;
    }

    /// <summary>Me/me of an account with a restored profile: its stored reply, with every key the recorded reply has and the
    /// profile lacks empty (a zip without its own Me/me); null for any other account. On a community server every account
    /// has one (a new account's is <see cref="MeOf"/>), and `recorded` is the endpoint's empty shape. On a community server
    /// `rank` is the account's rank by hunterscore (<see cref="Store.Rank"/>) among the players, not counting the recorded
    /// account (<see cref="LocalAccount"/>, which keeps the recorded player's hunterscore); a local server keeps the restored one.</summary>
    public JsonObject? MeReply(long account, JsonObject recorded)
    {
        using var con = Con(account);
        var me = Online ? MeOf(con, account) : RestoredReads.Me(con);
        if (me is null)
            return null;
        if (Online)
            me["rank"] = Store.Rank(account, LocalAccount);  // the rank among this server's players, never a backup's
        return RestoredReads.FillMissing(me, recorded);
    }

    JsonObject Sender() => new() { ["handle"] = "Server", ["user_id"] = SYSTEM_SENDER, ["avatar"] = $"{avatarBase}/256/10000000.png" };

    public long Unread(long account)
    {
        using var con = Con(account);
        return (long)con.Scalar("SELECT COUNT(*) FROM messages WHERE account_id = $a AND read = 0")!;
    }

    /// <summary>Chat/conversations: the notice sender's thread (the only sender this server has), newest message as preview.</summary>
    public byte[] ConversationsReply(long account)
    {
        object[]? newest;
        long unread;
        using (var con = Con(account))
        {
            newest = con.Rows("SELECT content, ts FROM messages WHERE account_id = $a ORDER BY id DESC LIMIT 1").FirstOrDefault();
            unread = (long)con.Scalar("SELECT COUNT(*) FROM messages WHERE account_id = $a AND read = 0")!;
        }
        // ponytail: empty inbox reply assumed to be []
        if (newest is null)
            return Py.DumpBytes(new JsonArray());
        var thread = Sender();
        thread["read"] = unread == 0 ? 1 : 0;
        thread["ts"] = Py.FromSql(newest[1]);
        thread["preview"] = Py.FromSql(newest[0]);
        return Py.DumpBytes(new JsonArray(thread));
    }

    /// <summary>Chat/conversation user_id=&lt;id&gt;: the thread, newest first; reading it marks it read.</summary>
    public byte[] ConversationReply(long account, byte[] formBody)
    {
        var userId = Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id");
        var sender = Py.IsDigit(userId) ? long.Parse(userId) : -1;
        List<object[]> rows;
        using (var con = Con(account))
        {
            con.Begin();
            rows = con.Rows("SELECT content, ts FROM messages WHERE account_id = $a AND sender = ? ORDER BY id DESC", sender);
            con.Exec("UPDATE messages SET read = 1 WHERE account_id = $a AND sender = ?", sender);
            con.Commit();
        }
        return Py.DumpBytes(new JsonObject
        {
            ["total"] = rows.Count,
            ["messages"] = new JsonArray(rows.Select(r =>
            {
                var m = Sender();
                m["content"] = Py.FromSql(r[0]);
                m["ts"] = Py.FromSql(r[1]);
                return (JsonNode?)m;
            }).ToArray()),
        });
    }

    Db Con(long account) => Store.Open(account);

    /// <summary>(x, y, z) of a start location: its Application/application `pos`, else the recorded connect's.</summary>
    (JsonNode? X, JsonNode? Y, JsonNode? Z) StartPosition(JsonNode? locationId)
    {
        var pos = Lookup(locations, locationId);
        var spawn = connect["expedition"]!.AsObject();
        return Py.Truthy(pos)
            ? (Py.D(pos!["x"]), Py.D(pos["y"]), Py.D(pos["z"]))
            : (C(G(spawn, "x", 0)), C(G(spawn, "y", 0)), C(G(spawn, "z", 0)));
    }

    /// <summary>Connect: the recorded reply with the current inventory and the open expedition (opened here if no
    /// Launch/start came first, e.g. a direct launch), starting at its location.</summary>
    public byte[] ConnectReply(long account)
    {
        var inventory = connect["inventory"]!.AsObject();
        JsonArray rows;
        JsonNode? expeditionId, locationId, reserve;
        using (var con = Con(account))
        {
            con.Begin();  // items read and their pending update 10 dropped atomically: a buy in between is not lost
            rows = Items(con);
            // Send one definition per distinct owned item id, in order of first appearance, as the game's server does; a list longer than
            // the items stops the game after connect, so never the whole catalogue
            var infosOut = new JsonArray(rows.Select(r => Py.L(r!["item_id"])).Distinct()
                .Select(ItemInfo).Where(i => Py.Truthy(i)).Select(i => C(i)).ToArray());
            var inv = (JsonObject)inventory.DeepClone();
            inv["items"] = rows;
            inv["infos"] = infosOut;
            rows = new JsonArray();  // moved into inv
            inventory = inv;
            var toast = Py.Dumps(new JsonObject
            {
                ["text"] = TOAST_TEXT, ["colour"] = COLOUR, ["destination"] = iconUrl, ["update"] = 3, ["timer"] = -1, ["type"] = 100,
            });
            // delete first: a login that connects twice still shows one toast
            con.Exec("DELETE FROM pending WHERE account_id = $a AND body = ?", toast);
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", toast);
            var closed = CloseStale(con, Clock());
            // bought or granted between hunts: this reply carries the items; the balance (update 15) still comes
            // with the first heartbeat
            con.Exec("DELETE FROM pending WHERE account_id = $a AND json_extract(body, '$.update') = 10");
            var row = con.Rows("SELECT id, location_id, reserve FROM expeditions WHERE account_id = $a AND end_ts IS NULL"
                               + " ORDER BY id DESC LIMIT 1").FirstOrDefault();
            if (row is not null)
                (expeditionId, locationId, reserve) = (Py.FromSql(row[0]), Py.FromSql(row[1]), Py.FromSql(row[2]));
            else
            {
                var lodge = connect["expedition"]!["lodge"];
                expeditionId = con.Insert("INSERT INTO expeditions (account_id, location_id, start_ts, first_event) VALUES ($a, ?, ?, ?)",
                                          lodge, Py.Now(), LastEvent(con));
                (locationId, reserve) = (C(lodge), null);
            }
            con.Commit();
            if (closed)
                UpdateHunterscore(con, account);
        }
        var (x, y, z) = StartPosition(locationId);
        var lodgeOut = TryKey(locationId, out var lid) && locations.ContainsKey(lid) ? C(locationId) : C(connect["expedition"]!["lodge"]);
        // ponytail: dir_x/dir_z stay the recorded lodge's; not in `locations`.
        // Non-lodge starts (Launch/start loctype other than LODGE) are untested: their id goes into `lodge` too
        var expedition = (JsonObject)connect["expedition"]!.DeepClone();
        expedition["expedition_id"] = expeditionId;
        expedition["x"] = x;
        expedition["y"] = y;
        expedition["z"] = z;
        expedition["lodge"] = lodgeOut;
        // direct launch (no Launch/start, so no reserve): the newest recorded connect's reserve data
        var reply = (JsonObject)connect.DeepClone();
        if (TryKey(reserve, out var r) && reserveData.TryGetValue((int)r, out var data))
            foreach (var (k, v) in data)
                reply[k] = C(v);
        reply["inventory"] = inventory;
        reply["expedition"] = expedition;
        reply["account"] = Store.AccountJson(account);
        reply["wallet"] = Wallet(account);
        reply["skills"] = SkillNumbers(account, xp => Py.L(Skills.Value(xp)));
        using (var con = Con(account))
        {
            // a restored account has its own stats and login data, not the recorded player's; on a community server so has every
            // other account (a new one has empty statistics), and none shows the recorded player's address
            var me = Online ? MeOf(con, account) : RestoredReads.Me(con);
            if (me is not null)
            {
                RestoredReads.Connect(con, reply, me, Store.SteamId64(account));
                if (Online && reply["network"] is JsonObject network)
                    network["ip"] = null;  // single player sends none
            }
        }
        // no seasonal event on this server, whatever the sample or a recording carries: the permitted ammo is the ethical list's, never an event's
        reply["ingame_event"] = null;
        if (reply["species_ammo"] is JsonArray ammo)
            foreach (var row in ammo.OfType<JsonObject>())
                if (long.TryParse(Py.Str(row["species_id"]), out var sid) && sid >= 0 && sid < ethicalAmmo.Count)
                    row["ammo"] = C(ethicalAmmo[(int)sid]);
        Spawns.Apply(reply, TryKey(reserve, out var launched) && reserve is not null ? launched : null);
        return Py.DumpBytes(reply);
    }

    /// <summary>update 10 definition: the recorded connect's, else the install sample's, else built from the
    /// Application/application record; null for an unknown item.</summary>
    public JsonObject? ItemInfo(long itemId)
    {
        if (infos.TryGetValue(itemId, out var info))
            return info;
        if (!items.TryGetValue(itemId, out var a) || a.Count == 0)
            return null;
        // ponytail: built definitions lack allowedCombinationSlots, usesCombinationSlots and arm_type; the game does not need them.
        // An ammo needs its ballistics (velocity_falloff, wounding_factor, ...) to get past connect: they come from the install's item definitions.
        // The other fields match the known definitions except a few fitItems lists
        var inv = a["inventory"]!;
        var fit = new List<JsonNode?>();
        foreach (var (_, f) in a["fit"]!.AsObject())
            foreach (var x in f!.AsArray())
                fit.Add(x!["id"]);
        var built = new JsonObject
        {
            ["item_id"] = itemId, ["name"] = C(a["name"]), ["description"] = C(a["description"]), ["define"] = C(a["define"]),
            ["type"] = C(a["type"]), ["group"] = C(a["group"]), ["cat"] = C(a["cat"]), ["client_type"] = C(a["client_type"]),
            ["weight"] = C(inv["weight"]), ["capacity"] = C(inv["capacity"]),
            ["allowedSlots"] = C(inv["slots"]), ["allowedCombinationSlots"] = new JsonArray(),
            ["fitItems"] = new JsonArray(fit.OrderBy(Py.D).Select(C).ToArray()), ["image"] = C(a["image"]),
            ["variationName"] = C(a["variationname"]), ["units"] = C(G(a["units"] as JsonObject ?? new JsonObject(), "count", -2)),
        };
        if (ballistics.TryGetValue(itemId, out var ammo))
            foreach (var (k, v) in ammo)
                built[k] = C(v);
        return built;
    }

    /// <summary>The store script's getPrice: the item's first row in the Application/discounts reply, if active, taken off
    /// and rounded half up (350 at 95 % off costs 18). A later active
    /// row (100 % ones listed after the 95 % ones) is never used.</summary>
    long Price(JsonObject item, string key)
    {
        var now = Py.Now();
        var d = discounts.FirstOrDefault(r => Py.Eq(r!["item_id"], item["id"]));
        var price = Py.D(item[key]);
        if (d is not null && Py.Truthy(d["percentage"]) && Py.D(d["start_ts"]) < now && now < Py.D(d["end_ts"]))
            price -= price * (Py.D(d["percentage"]) / 100);
        return (long)Math.Floor(price + 0.5);  // JS Math.round
    }

    /// <summary>The store as the website shows it (GET /catalog/store): categories by their address key (a top-level one by its default
    /// sub-category, as the store's links; one without items, the store's link to its bundles, is left out), the items that are not hidden with
    /// today's price (<see cref="Price"/>, em$ and silver alike; <c>was</c> the em$ price before the discount) and the bundles. A catalogue from the
    /// install has no categories or bundles: empty lists. No game data is made up: every name and price is the catalogue's, and no field the
    /// catalogue gives without a unit (the inventory weight) is passed on.</summary>
    public JsonObject StoreCatalogue()
    {
        var items = new JsonArray();
        var keys = new HashSet<string>();
        var names = new Dictionary<long, JsonNode?>();
        foreach (var i in Arr(G(application, "items", null)))
        {
            var item = i!.AsObject();
            names[Py.L(item["id"])] = C(item["name"]);  // a bundle may list an item the store does not show
            if (Py.Truthy(G(item, "hidden", null))) continue;
            var full = Whole(G(item, "price", null));
            var ems = full > 0 ? Price(item, "price") : 0;
            var key = item["subcat"] is JsonObject sub && sub["id"] is { } id ? Py.Str(id) : "";
            keys.Add(key);
            items.Add(new JsonObject
            {
                ["id"] = C(item["id"]), ["name"] = C(item["name"]), ["variant"] = C(G(item, "variationname", null)) ?? "",
                ["description"] = C(G(item, "description", null)) ?? "", ["category"] = key,
                ["ems"] = ems, ["was"] = ems < full ? full : null,
                ["gms"] = Py.IsNum(G(item, "silver_price", null)) ? Price(item, "silver_price") : 0,
                ["buyable"] = StoreSells(item),
            });
        }
        var categories = new JsonArray();
        foreach (var c in Arr(G(application, "categories", null)))
        {
            var children = Arr(c!["subcategories"]).Select(s => (JsonNode?)new JsonObject { ["key"] = Py.Str(s!["id"]), ["name"] = C(s["title"]) }).ToArray();
            var key = Py.Str(c["default"]);
            if (key != "all" && !children.Any(s => keys.Contains((string)s!["key"]!))) continue;
            categories.Add(new JsonObject { ["key"] = key, ["name"] = C(c["title"]), ["children"] = new JsonArray(children) });
        }
        var bundles = new JsonArray();
        foreach (var b in Arr(G(application, "bundles", null)))
            bundles.Add(new JsonObject
            {
                ["id"] = C(b!["id"]), ["name"] = C(b["name"]), ["description"] = C(G(b.AsObject(), "description", null)) ?? "",
                ["price"] = Whole(b["price"]), ["buyable"] = BundleSale(b) is not null,
                ["items"] = new JsonArray([.. Arr(b["items"]).Select(id => (JsonNode?)new JsonObject { ["id"] = C(id), ["name"] = names.TryGetValue(Py.L(id), out var n) ? C(n) : "" })]),
            });
        return new JsonObject { ["categories"] = categories, ["items"] = items, ["bundles"] = bundles };
    }

    // ponytail: services (connect active_services with an expiry) are not sold
    static bool Sellable(JsonObject item, string key) => item[key] is not null && !Py.Truthy(item["notbuyable"]) && !Is(item["type"], SERVICE);

    /// <summary>A catalogue price rounded half up to a whole number; 0 for none.</summary>
    static long Whole(JsonNode? price) => Py.IsNum(price) ? (long)Math.Floor(Py.D(price) + 0.5) : 0;

    /// <summary>What the website's store sells of the items: shown, with a price of at least 1, and sellable.</summary>
    static bool StoreSells(JsonObject item) => !Py.Truthy(G(item, "hidden", null)) && Whole(G(item, "price", null)) > 0 && Sellable(item, "price");

    /// <summary>The items a bundle gives, or null when the store does not sell it: a price of at least 1 and only items of the catalogue.</summary>
    long[]? BundleSale(JsonNode b)
    {
        long[] ids = [.. Arr(b["items"]).Select(x => Py.L(x))];
        return Whole(b["price"]) > 0 && ids.Length > 0 && ids.All(this.items.ContainsKey) ? ids : null;
    }

    /// <summary>Takes price from the currency and delivers the items in one transaction: the items (update 10) and the new balance
    /// (update 15) come with the next heartbeat, also mid-hunt. The new balance, or null (nothing changed) when it would drop below 0
    /// or the player would own more of an item than the catalogue allows (<see cref="OverOwnLimit"/>).</summary>
    JsonNode? Charge(long account, string currency, long price, IEnumerable<long> itemIds)
    {
        using var con = Con(account);
        con.Begin();
        if (itemIds.GroupBy(i => i).Any(g => OverOwnLimit(con, g.Key, g.Count())))
            return null;
        var balance = AddCurrency(con, currency, -price);
        if (balance is null)
            return null;
        Deliver(con, itemIds, ItemInfo);
        con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = currency, ["update"] = 15 }));
        con.Commit();
        return balance;
    }

    /// <summary>Whether n more copies of the item put the player over the catalogue's `ownLimit`, or over 1 when `ownMultiple` is false:
    /// the website's store refuses the same. A copy is one inventory row; a field the catalogue lacks sets no limit. An item with `units`
    /// (a one-use order or refill) is exempt from the `ownMultiple` rule only: the original store never showed one as owned.</summary>
    bool OverOwnLimit(Db con, long itemId, int n)
    {
        if (items.GetValueOrDefault(itemId) is not { } item)
            return false;
        var limit = Py.Truthy(item["ownLimit"]) ? Py.L(item["ownLimit"]) : long.MaxValue;
        if (item["ownMultiple"] is not null && !Py.Truthy(item["ownMultiple"]) && !Py.Truthy(item["units"]))
            limit = Math.Min(limit, 1);
        return limit != long.MaxValue && (long)con.Scalar("SELECT COUNT(*) FROM items WHERE account_id = $a AND item_id = ?", itemId)! + n > limit;
    }

    /// <summary>Inventory/buy item_id, amount, wallet: charge now, see <see cref="Charge"/>.</summary>
    public byte[] BuyReply(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        var (itemId, amount, wallet) = (Form1(form, "item_id"), Form1(form, "amount"), Form1(form, "wallet"));
        var item = Py.IsDigit(itemId) ? items.GetValueOrDefault(long.Parse(itemId)) : null;
        var (currency, key) = WALLETS.TryGetValue(wallet, out var w) ? w : (null!, null!);
        if (!(Py.Truthy(item) && currency is not null && Sellable(item!, key)
              && int.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxBuyAmount))
            return BUY_FAILED;
        var price = Price(item!, key) * n;
        return Charge(account, currency, price, Enumerable.Repeat(Py.L(item!["id"]), n)) is null
            ? BUY_FAILED
            : Py.DumpBytes(new JsonObject { ["price"] = (double)price, ["success"] = true });
    }

    /// <summary>The website's store (POST /account/buy): count of an item the store sells (<see cref="StoreSells"/>) at today's em$ price, or
    /// one bundle it sells (<see cref="BundleSale"/>) at its price with each of its items. The new em$ balance, or null (nothing changed) for
    /// something not for sale, a balance too low or an own limit reached.</summary>
    public JsonNode? WebBuy(long account, long id, bool bundle, int count)
    {
        if (bundle)
            return Arr(G(application, "bundles", null)).FirstOrDefault(x => Py.Eq(x!["id"], id)) is { } b && count == 1 && BundleSale(b) is { } ids
                ? Charge(account, "em", Whole(b["price"]), ids)
                : null;
        return items.GetValueOrDefault(id) is { } item && StoreSells(item) && count is >= 1 and <= MaxBuyAmount
            ? Charge(account, "em", Price(item, "price") * count, Enumerable.Repeat(id, count))
            : null;
    }

    /// <summary>Admin: add count items; a running hunt gets them with its next heartbeat (update 10), else the next
    /// connect carries them. False for an item without a definition.</summary>
    public bool GrantItems(long account, long itemId, long count = 1)
    {
        if (!Py.Truthy(ItemInfo(itemId)))
            return false;
        using var con = Con(account);
        con.Begin();
        Deliver(con, Enumerable.Repeat(itemId, (int)count), ItemInfo);
        con.Commit();
        return true;
    }

    /// <summary>Admin: add amount (negative: take) to a wallet currency, sent as update 15; the new balance, or null
    /// (unchanged) when the currency is missing or would drop below 0.</summary>
    public JsonNode? Give(long account, string currency, double amount)
    {
        using var con = Con(account);
        con.Begin();
        var balance = AddCurrency(con, currency, amount);
        if (balance is not null)
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = C(balance), ["type"] = currency, ["update"] = 15 }));
        con.Commit();
        return balance;
    }

    /// <summary>Heartbeat update 5: the running client activates a dormant population group (`group` = its 1-based
    /// record index).</summary>
    public void WakeGroup(long account, long populationId, int group)
    {
        using var con = Con(account);
        con.Begin();
        con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)",
                 Py.Dumps(new JsonObject { ["update"] = 5, ["population_id"] = populationId, ["group"] = group }));
        con.Commit();
    }

    /// <summary>Inventory rows in the connect reply's `inventory.items` shape.</summary>
    public JsonArray Inventory(long account)
    {
        using var con = Con(account);
        return Items(con);
    }

    public JsonObject Wallet(long account)
    {
        using var con = Con(account);
        var wallet = new JsonObject();
        foreach (var r in con.Rows("SELECT currency, amount FROM wallet WHERE account_id = $a"))
            wallet[(string)r[0]] = Py.FromSql(r[1]);
        return wallet;
    }

    /// <summary>Server `features` of a harvest (Scoring.Features); null when they can't be built.</summary>
    JsonNode? Features(JsonObject e3, JsonObject e7, JsonObject e22)
    {
        var sp = G(e3, "extended_species", -1);
        var list = Py.Truthy(connect["species"]) ? connect["species"]!.AsArray() : new JsonArray();
        var m = Mod100(sp);
        if (!(0 <= m && m < list.Count))
            return null;
        return Scoring.Features((int)Py.L(sp), e7, e22, list[(int)m]!["scale_factor"]);
    }

    /// <summary>(score, score_type, css) of a harvest; (null, null, 0.0) when its features can't be built.</summary>
    (JsonNode? Score, string? Type, double Css) Trophy(JsonObject e3, JsonObject e7, JsonObject e22, JsonObject? lastHit)
    {
        var f = Features(e3, e7, e22);
        if (f is null)
            return (null, null, 0.0);
        var sp = (int)Py.L(e3["extended_species"]);
        var (s, ty) = Scoring.Score(sp, f, G(e7, "weight_grams", 0), lastHit?["animal_state"]);
        return (s, ty, Scoring.Css(sp, s));
    }

    /// <summary>The client's disconnect call: close the open expedition; its body carries distance_travelled.</summary>
    public void Disconnect(long account, byte[] body)
    {
        JsonNode? distance;
        try
        {
            distance = (JsonNode.Parse(body) as JsonObject)?["distance_travelled"];
        }
        catch (JsonException)
        {
            distance = null;
        }
        using var con = Con(account);
        con.Begin();
        var closed = con.Exec("UPDATE expeditions SET end_ts = ?, distance = ? WHERE account_id = $a AND end_ts IS NULL", Py.Now(), distance) > 0;
        con.Commit();
        if (closed)
            UpdateHunterscore(con, account);
    }

    public byte[] MoveMultiple2Reply(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        var mapping = JsonNode.Parse(form.TryGetValue("mapping", out var m) ? m[0] : "[]")!.AsArray();
        var cloned = JsonNode.Parse(form.TryGetValue("clonedItems", out var c) ? c[0] : "[]")!.AsArray();
        using var con = Con(account);
        return Py.DumpBytes(MoveMultiple2(con, mapping, cloned, defaultUnits));
    }

    public byte[] HeartbeatReply(long account, byte[] body)
    {
        JsonArray events;
        try
        {
            var events_ = (JsonNode.Parse(body, documentOptions: new() { AllowDuplicateProperties = false }) as JsonObject ?? throw new JsonException("not a dict"))["events"]; // a duplicate key (at any depth) is a JsonException here, not an ArgumentException at the first read
            events = events_ as JsonArray ?? new JsonArray();
        }
        catch (JsonException)
        {
            events = new JsonArray();
        }
        using var con = Con(account);
        var updates = Heartbeat(con, Clock(), events, UploadUrl, HarvestPage, Trophy, AmmoCheck, SkillUpdates);
        if (con.Scalar("SELECT 1 FROM expeditions WHERE account_id = $a AND end_ts IS NOT NULL AND stats IS NULL LIMIT 1") is not null)
            UpdateHunterscore(con, account);
        return Py.DumpBytes(new JsonObject { ["updates"] = updates, ["err"] = 0 });
    }

    /// <summary>Launch/start: close any open expedition and open a new one at form `start` (location) in `reserve`.</summary>
    public void NoteLaunch(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        var (reserve, start) = (Form1(form, "reserve"), Form1(form, "start") is { Length: > 0 } s ? s : Form1(form, "lodge_id"));  // the current build calls the lodge `lodge_id`
        using var con = Con(account);
        con.Begin();
        var now = Py.Now();
        var closed = con.Exec("UPDATE expeditions SET end_ts = COALESCE(last_seen, ?) WHERE account_id = $a AND end_ts IS NULL", now) > 0;
        con.Exec("INSERT INTO expeditions (account_id, reserve, location_id, start_ts, first_event) VALUES ($a, ?, ?, ?, ?)",
                 Py.IsDigit(reserve) ? long.Parse(reserve) : null, Py.IsDigit(start) ? long.Parse(start) : null, now, LastEvent(con));
        con.Commit();
        if (closed)
            UpdateHunterscore(con, account);
    }

    JsonObject HarvestPage(JsonObject e3, JsonObject spawn, JsonNode? expeditionId, JsonNode? reserveId, JsonNode? score, string? scoreType) => new()
    {
        // ponytail: source of `holed` unknown
        ["antlers_score_percentage"] = C(e3["antlers_score_percentage"]), ["antlers_seed"] = C(e3["antlers_seed"]),
        ["base_texture"] = C(e3["base_texture"]), ["expedition_id"] = C(expeditionId),
        ["expedition_is_closed"] = false, ["fur_texture"] = C(e3["fur_texture"]), ["gender"] = C(spawn["gender"]),
        ["holed"] = 0, ["is_skull"] = false, ["reserve_id"] = C(reserveId), ["scale"] = C(e3["scale"]),
        ["score"] = Py.Truthy(score) ? C(score) : 0, ["score_type"] = string.IsNullOrEmpty(scoreType) ? "" : scoreType,
        ["species"] = C(e3["extended_species"]),
        ["species_define"] = C(Lookup(species, e3["extended_species"])?["define"]), ["taxidermized"] = false,
        ["texture_id"] = C(spawn["texture"]),
        ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        ["weight"] = C(spawn["weight_grams"]),
    };

    /// <summary>GET /game/user/harvest/&lt;kill_id&gt; body: this server's kill, else the harvest record a restore brought; null for an unknown kill.</summary>
    public byte[]? HarvestReply(long account, long killId)
    {
        using var con = Con(account);
        var row = con.Rows("SELECT body FROM harvests WHERE account_id = $a AND kill_id = ?", killId).FirstOrDefault();
        return row is not null ? Encoding.UTF8.GetBytes((string)row[0]) : Online ? RowReply(account, "trophies", "/game/user/harvest", killId.ToString()) : null;
    }

    /// <summary>Public_user/expedition: the latest server expedition and its kills; the recorded reply if none yet.</summary>
    public byte[] ExpeditionReply(long account, byte[] recorded)
    {
        object[] row;
        JsonArray kills;
        JsonNode? stats = null;
        using (var con = Con(account))
        {
            con.Begin();
            var stale = CloseStale(con, Clock());
            con.Commit();
            if (stale)
                UpdateHunterscore(con, account);
            var found = con.Rows("SELECT id, reserve, location_id, start_ts, end_ts, first_event, distance, stats FROM expeditions"
                                 + " WHERE account_id = $a ORDER BY id DESC LIMIT 1").FirstOrDefault();
            if (found is null)
                return recorded;
            row = found;
            var expeditionId = Py.FromSql(row[0]);
            var rows = con.Rows(
                "SELECT k.kill_id, k.individual, k.harvest, h.body FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                + " WHERE k.account_id = $a AND json_extract(h.body, '$.expedition_id') = ? ORDER BY k.kill_id DESC", expeditionId);
            var hits = new Dictionary<long, List<JsonObject>>();
            foreach (var r in rows)
                hits[(long)r[0]] = KillHits(con, Py.FromSql(r[1]), (string)r[2]);
            var userId = Account(account)["user_id"];
            kills = new JsonArray(rows.Select(r => (JsonNode?)KillEntry(userId, (long)r[0], Parse(r[2]), Parse(r[3]), hits[(long)r[0]], expeditionId)).ToArray());
            // stats are null mid-hunt; expeditions from before first_event have no scope; a closed one's are stored at its close
            if (row[4] is not DBNull && row[5] is not DBNull && row[7] is string s)
                stats = Parse(s);
        }
        var (x, y, z) = StartPosition(Py.FromSql(row[2]));
        // an open expedition's start_ts is fractional; a closed one's start_ts and end_ts are whole seconds
        var closed = row[4] is not DBNull;
        var expedition = new JsonObject
        {
            ["map"] = 0, ["z"] = Py.L(z), ["x"] = Py.L(x), ["y"] = Py.L(y),
            ["location_id"] = Py.FromSql(row[2]),
            ["end_ts"] = closed ? (long)Convert.ToDouble(row[4]) : null,
            ["start_ts"] = closed ? JsonValue.Create((long)Convert.ToDouble(row[3])) : Py.FromSql(row[3]),
            ["id"] = Py.FromSql(row[0]), ["reserve"] = Py.FromSql(row[1]),
        };
        return Py.DumpBytes(new JsonObject { ["stats"] = stats, ["antler_collectables"] = new JsonArray(), ["kills"] = kills, ["expedition"] = expedition });
    }

    /// <summary>GET &lt;web-host&gt;/user/&lt;handle&gt;/score/?id=&lt;kill_id&gt;: kill details HTML, or null for an unknown kill.</summary>
    public byte[]? ScorePageReply(long account, long killId)
    {
        object[] row;
        List<JsonObject> hits;
        JsonObject page, e7, e22;
        object[]? expedition;
        using (var con = Con(account))
        {
            var found = con.Rows("SELECT k.individual, k.harvest, h.body FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                                 + " WHERE k.account_id = $a AND k.kill_id = ?", killId).FirstOrDefault();
            if (found is null)
                return null;
            row = found;
            var individual = Py.FromSql(row[0]);
            var harvest = (string)row[1];
            (hits, page) = (KillHits(con, individual, harvest), Parse(row[2]));
            (e7, e22) = KillEvents(con, individual, harvest);
            expedition = con.Rows("SELECT id, reserve, location_id, start_ts, end_ts FROM expeditions WHERE account_id = $a AND id = ?",
                                  page["expedition_id"]).FirstOrDefault();
        }
        var e3 = Parse(row[1]);
        var sp = G(e3, "extended_species", -1);
        var f = Features(e3, e7, e22);
        var owner = Account(account);
        var entry = KillEntry(owner["user_id"], killId, e3, page, hits, page["expedition_id"]);
        var handle = Py.Str(G(owner, "handle", ""));
        var last = hits.Count > 0 ? hits[^1] : e3;
        var (x, y, z) = StartPosition(expedition is null ? null : Py.FromSql(expedition[2]));
        var killInfo = (JsonObject)entry["kill"]!.DeepClone();
        // ponytail: ts (shot time) = confirmTs; events carry no time
        killInfo["ts"] = C(killInfo["confirmTs"]);
        killInfo["pos"] = new JsonObject { ["y"] = C(last["player_y"]), ["x"] = C(last["player_x"]), ["z"] = C(last["player_z"]) };
        var expeditions = new JsonArray();
        if (expedition is not null)
            expeditions.Add(new JsonObject
            {
                ["map"] = 0, ["user_id"] = C(entry["userId"]), ["singleplayer"] = 1,
                ["end_ts"] = expedition[4] is DBNull ? 0 : (long)Convert.ToDouble(expedition[4]),
                ["start_ts"] = (long)Convert.ToDouble(expedition[3]), ["y"] = Py.L(y), ["x"] = Py.L(x),
                ["z"] = Py.L(z), ["location_id"] = Py.FromSql(expedition[2]), ["id"] = Py.FromSql(expedition[0]),
                ["reserve"] = Py.FromSql(expedition[1]),
            });
        var kill = new JsonObject
        {
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode?)new JsonObject
            {
                ["distance"] = Times10(G(h, "distance_cms", 0)), ["pose"] = C(h["pose"]), ["handle"] = handle,
                ["weapon_id"] = C(h["weapon_id"]), ["organ"] = C(G(h, "organ2", 0)),
                ["animal_effects"] = C(G(h, "animal_effects", new JsonArray())), ["player_z"] = C(h["player_z"]),
                ["hostname"] = handle, ["ammo_id"] = C(h["ammunition_id"]), ["bodypart"] = C(G(h, "organ1", 0)), ["platform"] = 0,
                ["player_y"] = C(h["player_y"]), ["scope_id"] = C(h["scope_id"]), ["animal_state"] = C(G(h, "animal_state", 0)),
                ["shot_id"] = C(h["shot_id"]), ["animal_y"] = C(h["y"]), ["animal_x"] = C(h["x"]),
                ["player_x"] = C(h["player_x"]), ["animal_z"] = C(h["z"]),
            }).ToArray()),
            ["kill"] = killInfo,
            ["features"] = f is not null ? C(f) : new JsonObject(),
            ["weight"] = C(entry["weight"]),
            ["expedition"] = expeditions,
            ["gender"] = C(entry["gender"]), ["variant"] = Mod100(sp), ["id"] = killId, ["texture"] = C(entry["texture"]),
            ["species"] = C(entry["species"]),
        };
        var define = Lookup(species, entry["species"]) is { } s ? Py.Str(G(s, "define", "")) : "";
        var speciesName = (string?)entry["speciesName"] ?? "";
        var sheet = f is not null
            ? Scoring.SheetHtml((int)Py.L(sp), define, speciesName, f, G(e7, "weight_grams", 0), entry["gender"])
            : null;
        var reserveName = expedition is not null && TryKey(Py.FromSql(expedition[1]), out var rid)
                          && reserves.TryGetValue(rid, out var rn) ? rn ?? "" : "";
        return Encoding.UTF8.GetBytes(ScorePage.Render(kill, handle, speciesName, define.ToLowerInvariant(), reserveName, items,
                                                       locale, textures, sheet));
    }

    /// <summary>Expedition/list: server expeditions (newest first) ahead of the recorded ones.
    ///
    /// Only the first page is merged; later pages replay the recording.</summary>
    public byte[] ExpeditionListReply(long account, byte[] recorded, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        var (offset, limit) = (Form1(form, "offset"), Form1(form, "limit"));
        if (offset is not ("" or "0"))
            return recorded;
        List<object[]> rows;
        using (var con = Con(account))
        {
            con.Begin();
            var stale = CloseStale(con, Clock());
            con.Commit();
            if (stale)
                UpdateHunterscore(con, account);
            rows = con.Rows(
                "SELECT e.id, e.reserve, e.location_id, e.start_ts, e.end_ts, (SELECT COUNT(*) FROM harvests"
                + " WHERE account_id = $a AND json_extract(body, '$.expedition_id') = e.id)"
                + " FROM expeditions e WHERE e.account_id = $a ORDER BY e.id DESC");
        }
        var reply = JsonNode.Parse(recorded)!.AsObject();
        var ours = rows.Select(r => (JsonNode?)new JsonObject
        {
            ["map"] = 0, ["kills"] = Py.FromSql(r[5]), ["singleplayer"] = 1,
            ["end_ts"] = r[4] is DBNull || Convert.ToDouble(r[4]) == 0 ? null : JsonValue.Create((long)Convert.ToDouble(r[4])),
            ["start_ts"] = (long)Convert.ToDouble(r[3]), ["collectables"] = 0, ["location_id"] = Py.FromSql(r[2]),
            ["id"] = Py.FromSql(r[0]), ["reserve"] = Py.FromSql(r[1]),
        }).ToList();
        var expeditions = new List<JsonNode?>(ours);
        if (Py.Truthy(reply["expeditions"]))
            expeditions.AddRange(reply["expeditions"]!.AsArray().Select(C));
        reply["expeditions"] = new JsonArray((Py.IsDigit(limit) ? expeditions.Take((int)Math.Min(long.Parse(limit), int.MaxValue)) : expeditions).ToArray());
        var total = G(reply, "total", 0);
        reply["total"] = Py.IsInt(total) ? JsonValue.Create(Py.L(total) + ours.Count) : JsonValue.Create(Py.D(total) + ours.Count);
        reply["has_more"] = Py.IsDigit(limit) && expeditions.Count > long.Parse(limit);  // the current build asks for this instead of `total`
        return Py.DumpBytes(reply);
    }

    /// <summary>One Public_user/expedition kill.</summary>
    JsonObject KillEntry(JsonNode? userId, long killId, JsonObject e3, JsonObject page, List<JsonObject> hits, JsonNode? expeditionId)
    {
        var confirm = DateTime.ParseExact((string)page["timestamp"]!, "yyyy-MM-dd'T'HH:mm:ss'Z'", null,
                                          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new JsonObject
        {
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode?)new JsonObject
            {
                ["distance"] = Times10(G(h, "distance_cms", 0)), ["weapon_id"] = C(h["weapon_id"]),
                ["organ"] = C(h["organ2"]), ["ammo_id"] = C(h["ammunition_id"]),
            }).ToArray()),
            ["expeditionId"] = C(expeditionId),
            ["speciesName"] = SpeciesName(page["species"]),
            ["weight"] = C(page["weight"]), ["gender"] = C(page["gender"]),
            ["kill"] = new JsonObject
            {
                ["ethical"] = Ethical(page["species"], hits),
                ["trophy_integrity"] = Py.D(G(e3, "trophy_integrity", 0)) / 1000,
                ["score"] = Py.Truthy(page["score"]) ? C(page["score"]) : null,
                ["wound_time"] = Py.D(G(e3, "wound_time", 0)) / 1000,
                ["confirmTs"] = new DateTimeOffset(confirm).ToUnixTimeSeconds(),
                ["photo"] = null, ["harvest_value"] = Py.D(G(e3, "harvest_value", 0)) / 1000,
                ["scoreType"] = Py.Truthy(page["score_type"]) ? C(page["score_type"]) : null,
            },
            ["userId"] = C(userId), ["species"] = C(page["species"]),
            ["texture"] = C(page["texture_id"]), ["id"] = killId,
        };
    }

    /// <summary>Mark a harvest taxidermized: this server's, else the record a restore brought; false for an unknown kill. No gold charged (taxidermy price unknown).</summary>
    public bool Taxidermize(long account, long killId)
    {
        using var con = Con(account);
        con.Begin();
        var n = con.Exec("UPDATE harvests SET body = json_set(body, '$.taxidermized', json('true')) WHERE account_id = $a AND kill_id = ?", killId);
        if (n == 0 && Online)
            n = con.Exec("UPDATE trophies SET node = json_set(node, '$.taxidermized', json('true')) WHERE account_id = $a AND source = '/game/user/harvest' AND id = ?", killId.ToString());
        con.Commit();
        return n > 0;
    }

    string UploadUrl(long photoId, string part) =>
        $"{baseUrl}{UPLOAD_PATH}?uploadType=resumable&upload_id={photoId}" + (part != "" ? $"&part={part}" : "");

    /// <summary>POST of a photo to UploadUrl(): store it, reply like GCS (storage#object), 404 if the id is unknown.</summary>
    public (int Status, byte[] Body) UploadReply(long account, string query, byte[] data)
    {
        var q = Py.ParseQs(query);
        var photoId = Form1(q, "upload_id");
        var part = q.TryGetValue("part", out var p) && p is ["thumb"] ? "thumb" : "";
        string? name;
        using (var con = Con(account))
            name = Py.IsDigit(photoId) ? SavePhoto(con, AccountPhotos(account), long.Parse(photoId), data, part) : null;
        if (string.IsNullOrEmpty(name))
            return (404, "{\"error\": {\"code\": 404, \"message\": \"No such upload\"}}"u8.ToArray());
        var md5 = Convert.ToBase64String(MD5.HashData(data));
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var path = $"{PHOTO_PREFIX}{account.ToString(CultureInfo.InvariantCulture)}/{name}";
        return (200, Py.DumpBytes(new JsonObject
        {
            ["kind"] = "storage#object", ["id"] = path, ["name"] = name, ["bucket"] = "photos",
            ["selfLink"] = $"{baseUrl}{path}", ["mediaLink"] = $"{baseUrl}{path}",
            ["generation"] = "1", ["metageneration"] = "1", ["contentType"] = "image/jpeg", ["storageClass"] = "STANDARD",
            ["size"] = data.Length.ToString(), ["md5Hash"] = md5, ["timeCreated"] = now, ["updated"] = now,
        }));
    }

    string AccountPhotos(long account) => Path.Combine(photosDir, account.ToString(CultureInfo.InvariantCulture));

    /// <summary>GET &lt;PHOTO_PREFIX&gt;&lt;account id&gt;/&lt;name&gt; → stored file path or null. The URL names the account: a photo
    /// is a public picture, shown by its link without a token.</summary>
    public string? PhotoFile(string path)
    {
        var rest = path.StartsWith(PHOTO_PREFIX, StringComparison.Ordinal) ? path[PHOTO_PREFIX.Length..] : path;
        var slash = rest.IndexOf('/');
        if (slash < 0 || !long.TryParse(rest[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var account))
            return null;
        var name = rest[(slash + 1)..];
        var f = Path.Combine(AccountPhotos(account), name);
        return name.IndexOfAny(['/', '\\', ':']) < 0 && File.Exists(f) ? f : null;  // ':' would make it drive-relative
    }

    // ---- Python dict/str semantics on JSON values

    /// <summary>dict.get(k, default): the default only for a missing key, not for a JSON null.</summary>
    static JsonNode? G(JsonObject o, string k, JsonNode? d) => o.TryGetPropertyValue(k, out var v) ? v : d;

    static JsonNode? C(JsonNode? n) => n?.DeepClone();

    static JsonArray Arr(JsonNode? n) => n as JsonArray ?? new JsonArray();

    static JsonObject Parse(object text) => JsonNode.Parse((string)text)!.AsObject();

    /// <summary>n == v for a JSON number (or bool, as in Python).</summary>
    static bool Is(JsonNode? n, double v) => n is JsonValue && !Py.IsStr(n) && Py.D(n) == v;

    /// <summary>n * 10, int stays int; a product beyond a double (a hit distance a player sent) is 0, so it never stores a
    /// number the database cannot read back.</summary>
    static JsonNode Times10(JsonNode? n) => Py.IsInt(n) ? JsonValue.Create(Py.L(n) * 10) : double.IsFinite(Py.D(n) * 10) ? JsonValue.Create(Py.D(n) * 10) : 0;

    /// <summary>Python n % 100 (floor modulo).</summary>
    static long Mod100(JsonNode? n) => ((Py.L(n) % 100) + 100) % 100;

    static void Inc(JsonObject o, string k) => o[k] = Py.L(o[k]) + 1;

    static JsonObject SetDefault(JsonObject o, string k, Func<JsonObject> make)
    {
        if (o[k] is JsonObject v)
            return v;
        var made = make();
        o[k] = made;
        return made;
    }

    /// <summary>An int-valued JSON number as a dict key (Python finds key 1 for 1 and 1.0); false outside the 64-bit range.</summary>
    static bool TryKey(JsonNode? n, out long k)
    {
        k = 0;
        if (!Py.IsNum(n) || Py.D(n) is var d && (d != Math.Floor(d) || d <= -9.223372036854775808E18 || d >= 9.223372036854775808E18))
            return false;
        k = Py.L(n);
        return true;
    }

    static T? Lookup<T>(Dictionary<long, T> d, JsonNode? k) where T : class? => TryKey(k, out var l) && d.TryGetValue(l, out var v) ? v : null;

    /// <summary>(form.get(k) or [""])[0]</summary>
    static string Form1(OrderedDictionary<string, List<string>> form, string k) =>
        form.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : "";

    /// <summary>urlsplit(url).scheme, .netloc</summary>
    static (string Scheme, string Netloc) SchemeNetloc(string url)
    {
        var scheme = "";
        var i = url.IndexOf(':');
        if (i > 0 && char.IsAsciiLetter(url[0]) && url[..i].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = url[..i].ToLowerInvariant();
            url = url[(i + 1)..];
        }
        if (!url.StartsWith("//", StringComparison.Ordinal))
            return (scheme, "");
        var end = url.IndexOfAny(['/', '?', '#'], 2);
        return (scheme, end < 0 ? url[2..] : url[2..end]);
    }
}
