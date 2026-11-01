// Starting data from the operator's own game install (read only): the connect template of a new account and the
// catalogue the player state needs, with the facts file for what the install does not carry.
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class Install
{
    public const string ConnectSample = "json_connect_results.json";  // under <install_dir>/game/

    /// <summary>The user id and handle of the account a server without recordings starts with.</summary>
    public const long LocalUserId = 1;
    public const string LocalHandle = "hunter";

    static JsonArray? ArchiveJson(string installDir, string name)
    {
        var rec = ArcList.Find(name, ArcList.ArchiveIndex(installDir));
        return rec is null ? null : JsonNode.Parse(ArcList.ReadEntry(rec)) as JsonArray;
    }

    /// <summary>The item definitions the game keeps in its archives (item.json); empty when the install has none.</summary>
    public static JsonArray ItemDefinitions(string installDir) => ArchiveJson(installDir, "item.json") ?? [];

    /// <summary>`species`, `items`, `reserves` and `locations` of the application catalogue (what State reads of
    /// Application/application): species and items from the install's archives, reserves and start positions from the
    /// facts file.</summary>
    public static JsonObject Application(string installDir, Facts facts, JsonArray itemDefinitions) => new()
    {
        ["species"] = new JsonArray((ArchiveJson(installDir, "species.json") ?? []).OfType<JsonObject>().Select(s => (JsonNode?)new JsonObject
        {
            ["id"] = s["id"]?.DeepClone(), ["name"] = s["name"]?.DeepClone(), ["define"] = s["define"]?.DeepClone(), ["short"] = s["short"]?.DeepClone(),
        }).ToArray()),
        ["items"] = facts.ApplicationItems(itemDefinitions),
        ["reserves"] = facts.ApplicationReserves(),
        ["locations"] = facts.ApplicationLocations(),
    };

    /// <summary>The connect reply a new account starts from, or null when the install has no connect sample. The
    /// sample is a reply of a test account: everything of that account is replaced (identity, e-mail, address, stats,
    /// inventory, wallet, skills) by the neutral start of the facts file; the game data of the reply (species, items
    /// text, locations, store, ethical ammo, ...) stays. cfg's hosts replace the sample's domains.</summary>
    public static byte[]? Connect(JsonObject cfg, Facts facts, JsonArray itemDefinitions)
    {
        var file = Path.Combine((string)cfg["install_dir"]!, "game", ConnectSample);
        if (!File.Exists(file)) return null;
        var c = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? throw new InvalidDataException($"{file}: not a JSON object");
        string host(string key) => (string)cfg[key]!;
        var web = host("game_web_host");

        var account = (c["account"] as JsonObject ?? []).DeepClone().AsObject();
        account["account_id"] = LocalUserId;
        account["user_id"] = LocalUserId;
        account["handle"] = LocalHandle;
        account["hostname"] = LocalHandle;
        account["hunterscore"] = 0;
        account["membership"] = 0;
        account["fullbody_avatar"] = 0;
        account["avatar_url"] = $"https://{web}/media/avatar/256/1.png";
        c["account"] = account;
        c["account_info"] = new JsonObject
        {
            ["account_state"] = 0, ["country"] = "", ["email"] = "", ["email_verified"] = 0, ["facebook_id"] = null, ["has_password"] = 0,
            ["id"] = LocalUserId, ["locale"] = "en_US", ["share_on_facebook"] = 0, ["steam_id"] = "", ["timezone"] = "UTC",
            ["user_group"] = 1, ["user_id"] = LocalUserId, ["uuid"] = "",
        };
        c["network"] = new JsonObject
        {
            ["clients"] = new JsonArray(), ["handle"] = LocalHandle, ["host"] = LocalUserId, ["ip"] = null, ["max_players"] = 8,
            ["singleplayer"] = true, ["user_id"] = LocalUserId,
        };
        // one host serves everything; the punch-through server is not used in single player
        c["config"] = new JsonObject
        {
            ["domains"] = new JsonObject
            {
                ["main"] = $"https://{web}",
                ["subdomains"] = new JsonObject { ["avatar"] = $"https://{web}/media/avatar/", ["static"] = $"https://{web}/" },
            },
            ["enable_logging"] = false,
            ["nat"] = new JsonObject { ["pt_server"] = new JsonObject { ["ip"] = "127.0.0.1", ["port"] = 10000 } },
        };
        if (c["expedition"] is JsonObject e) e["expedition_id"] = 0;

        // user_stats: the structure of a new player's, counters at 0
        if (c["user_stats"] is JsonObject stats)
        {
            foreach (var (k, v) in stats.ToList())
                if (k == "achievement_stats") stats[k] = Zeroed(v);
                else if (k == "user_stats") stats[k] = new JsonObject
                {
                    ["animal"] = new JsonObject(), ["collectables"] = new JsonObject(), ["daily_missions"] = new JsonObject(),
                    ["distance"] = 0, ["duration"] = 0, ["weapon"] = new JsonObject(),
                };
                else if (Py.IsNum(v)) stats[k] = 0;
        }
        foreach (var b in (c["boost"] as JsonObject)?["boost"] as JsonArray ?? [])
            if (b is JsonObject o && o.ContainsKey("own")) o["own"] = false;
        c["spawn"] = new JsonArray();
        c["competitions"] = new JsonArray();

        // inventory, wallet and skills of a new account: the facts file's start kit, a wallet, every skill at level 1
        var (kit, wallet) = facts.Start();
        var byId = itemDefinitions.OfType<JsonObject>().Where(d => d["id"] is not null).ToDictionary(d => Py.L(d["id"]), d => d);
        var items = new JsonArray();
        foreach (var id in kit)
        {
            var units = byId.TryGetValue(id, out var d) && d["units"] is { } u && Py.IsNum(u) && Py.D(u) > 0 ? u.DeepClone() : null;
            items.Add(new JsonObject
            {
                ["item_id"] = id, ["slot"] = 0, ["guid"] = (items.Count + 1).ToString(), ["metadata"] = new JsonObject(), ["units"] = units,
            });
        }
        var inventory = c["inventory"] as JsonObject ?? [];
        inventory["items"] = items;
        c["inventory"] = inventory;
        c["wallet"] = wallet;
        var skills = c["skills"] as JsonObject ?? [];
        foreach (var kind in new[] { "spotting", "tracking" })
            skills[kind] = new JsonArray((skills[kind] as JsonArray ?? []).Select(_ => (JsonNode?)1).ToArray());
        skills["weapon"] = new JsonObject();
        c["skills"] = skills;
        return Encoding.UTF8.GetBytes(c.ToJsonString());
    }

    // every number in the value becomes 0, every string stays: counters of a new player
    static JsonNode? Zeroed(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Key is "id" or "category_id" or "group_id" ? kv.Value?.DeepClone() : Zeroed(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Zeroed).ToArray()),
        JsonValue v when Py.IsNum(v) => 0,
        _ => n?.DeepClone(),
    };
}
