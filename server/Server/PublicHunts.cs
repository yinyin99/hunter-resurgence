// What a profile page shows of an account's hunts, in the game (Expedition/list and Public_user/expedition of any user id) and on the website
// (GET /hunters/<handle>/hunts, /hunts/<id> and /score/<kill>): the server hunts and the restored ones, and the score sheet of a server kill.
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    public const int HuntsPageSize = 40;

    /// <summary>The account a hunts read is of: the requester's own (no `user_id`, or its own), else the account of the form's `user_id` when that is a player here
    /// (<see cref="PublicAccount(long)"/>); null for anyone else.</summary>
    public long? HuntsOf(long account, byte[] formBody)
    {
        if (IsOwn(account, formBody))
            return account;
        return long.TryParse(Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id"), out var id) ? PublicAccount(id) : null;
    }

    static long Integer(JsonNode? n) => Py.IsNum(n) && double.IsFinite(Py.D(n)) ? Py.L(n) : 0;  // a restored hunt is the player's: any member can be of another type
    static double Real(JsonNode? n) => Py.IsNum(n) && double.IsFinite(Py.D(n)) ? Py.D(n) : 0;
    static int Count(JsonNode? n) => (int)Math.Clamp(Integer(n), 0, int.MaxValue);

    /// <summary>The name of a reserve; "#id" where the catalogue has none.</summary>
    string ReserveName(JsonNode? id) => TryKey(id, out var r) && reserves.TryGetValue(r, out var name) && name is not null ? name : Py.Truthy(id) ? "#" + Py.Str(id) : "";

    string ItemName(JsonNode? id) => Lookup(items, id)?["name"] is { } n && Py.IsStr(n) ? (string)n! : "";

    /// <summary>What a profile page's hunt history shows of an account, `GET /hunters/&lt;handle&gt;/hunts?page=n`: `{page, pages, total, rows: [{id, start, seconds, reserve, lodge, collectables, kills}]}`,
    /// <see cref="HuntsPageSize"/> a page, newest first: the hunts made here, then the restored ones (as Expedition/list); `start` is Unix seconds, `seconds` 0 for an open hunt, `lodge` "#id" (the
    /// catalogue has no lodge names), "" without one.</summary>
    public JsonObject PublicHunts(long account, int page)
    {
        var all = JsonNode.Parse(ExpeditionListReply(account, "{}"u8.ToArray(), []))!["expeditions"]!.AsArray();
        var pages = Math.Max(1, (all.Count + HuntsPageSize - 1) / HuntsPageSize);
        page = Math.Clamp(page, 1, pages);
        JsonNode? Row(JsonNode? e)
        {
            var o = e as JsonObject ?? [];
            long start = Integer(o["start_ts"]), end = Integer(o["end_ts"]);
            return new JsonObject
            {
                ["id"] = Integer(o["id"]), ["start"] = start, ["seconds"] = end > start ? end - start : 0, ["reserve"] = ReserveName(o["reserve"]),
                ["lodge"] = Py.Truthy(o["location_id"]) ? "#" + Py.Str(o["location_id"]) : "", ["collectables"] = Count(o["collectables"]), ["kills"] = Count(o["kills"]),
            };
        }
        return new JsonObject
        {
            ["page"] = page, ["pages"] = pages, ["total"] = all.Count,
            ["rows"] = new JsonArray([.. all.Skip((page - 1) * HuntsPageSize).Take(HuntsPageSize).Select(Row)]),
        };
    }

    /// <summary>What a profile page's hunt page shows, `GET /hunters/&lt;handle&gt;/hunts/&lt;id&gt;` (`latest` for the newest): `{id, reserve, lodge, start, end, weapons, animals, collectables, achievements, distanceKm, seconds, antlers, kills}`
    /// of Public_user/expedition (a hunt made here, else a restored one). `weapons`, `animals` and `distanceKm` are the statistics tab's rows of the hunt's stats (none while the hunt is open);
    /// `end` is null for an open hunt; `collectables`, `achievements` and `antlers` are empty (the server has no names for them). `kills` are
    /// `{id, species, weapon, ammo, distanceM, score, weightKg, when, sheet}` of the last hit; `sheet` says the kill has a score sheet (`/score/&lt;id&gt;`): a kill made here, or a restored one whose backup saved its page. Null for an unknown hunt.</summary>
    public JsonObject? PublicHunt(long account, long? id)
    {
        if (JsonNode.Parse(ExpeditionReply(account, "null"u8.ToArray(), id is { } i ? Encoding.UTF8.GetBytes($"expedition_id={i}") : null)) is not JsonObject reply || reply["expedition"] is not JsonObject e)
            return null;
        HashSet<long> sheets;
        using (var con = Con(account))
            sheets = [.. con.Rows("SELECT kill_id FROM kills WHERE account_id = $a").Select(r => (long)r[0])];
        long start = Integer(e["start_ts"]), end = Integer(e["end_ts"]);
        var view = reply["stats"] is JsonObject stats && stats["data"] is JsonObject ? StatisticsView(stats) : null;
        string ItemOf(JsonObject? hit, string key) => ItemName(hit?[key]);
        JsonNode? Kill(JsonNode? node)
        {
            var k = node as JsonObject ?? [];
            var hit = (k["hits"] as JsonArray)?.LastOrDefault() as JsonObject;
            var info = k["kill"] as JsonObject;
            var killId = Integer(k["id"]);
            return new JsonObject
            {
                ["id"] = killId, ["species"] = Py.IsStr(k["speciesName"]) ? Py.Str(k["speciesName"]) : "#" + Py.Str(k["species"]), ["weapon"] = ItemOf(hit, "weapon_id"), ["ammo"] = ItemOf(hit, "ammo_id"),
                ["distanceM"] = Real(hit?["distance"]) / 1000, ["score"] = Real(info?["score"]), ["weightKg"] = Real(k["weight"]) / 1000, ["when"] = Integer(info?["confirmTs"]), ["sheet"] = sheets.Contains(killId) || SavedPage(account, killId) is not null,
            };
        }
        return new JsonObject
        {
            ["id"] = Integer(e["id"]), ["reserve"] = ReserveName(e["reserve"]), ["lodge"] = Py.Truthy(e["location_id"]) ? "#" + Py.Str(e["location_id"]) : "", ["start"] = start,
            ["end"] = Py.IsNum(e["end_ts"]) ? end : null,
            ["weapons"] = C(view?["weapons"]) ?? new JsonArray(), ["animals"] = C(view?["animals"]) ?? new JsonArray(), ["collectables"] = new JsonArray(), ["achievements"] = new JsonArray(),
            ["distanceKm"] = Real(view?["distanceKm"]), ["seconds"] = Py.IsNum(e["end_ts"]) && end > start ? end - start : 0, ["antlers"] = new JsonArray(),
            ["kills"] = new JsonArray([.. (reply["kills"] as JsonArray ?? []).Select(Kill)]),
        };
    }

    /// <summary>What a profile page's score sheet shows, `GET /hunters/&lt;handle&gt;/score/&lt;kill&gt;`: the worked sheet of <see cref="Scoring.SheetData"/> (`kind`, `facts`, `rows`, `abnormal`, `sums`) and the facts
    /// the score page prints (species, the last hit's weapon, scope, ammo, distance in metres, animal state, body part and stance, the platform, weight in kilograms, type, wound time, trophy integrity,
    /// the shot location, the number of shots, the harvest time in Unix seconds, the expedition, the score and the harvest value). A restored kill whose backup saved its page has the facts of the saved hunt and no
    /// worked sheet (`kind` "antler" without rows: the saved page, which the game shows, holds it as markup); null for any other kill that was not made here.</summary>
    public JsonObject? PublicSheet(long account, long killId)
    {
        JsonObject sheet, kill;
        string species, reserve;
        if (LoadKill(account, killId) is { } k)
        {
            sheet = Scoring.SheetData(k.Features is null ? -1 : (int)Py.L(k.Sp), k.Features);
            (kill, species, reserve) = (k.Kill, k.SpeciesName, k.ReserveName);
        }
        else if (SavedPage(account, killId) is not null && RestoredKill(account, killId) is { } saved)
        {
            sheet = Scoring.SheetData(-1, null);
            (kill, species) = (saved, Py.IsStr(saved["speciesName"]) ? (string)saved["speciesName"]! : "");
            reserve = ReserveName(saved["expedition"] is JsonArray { Count: > 0 } e && e[0] is JsonObject first ? first["reserve"] : null);
        }
        else
            return null;
        var hits = kill["hits"] as JsonArray ?? [];
        var shot = hits.Count > 0 ? hits[^1] as JsonObject : null;
        var info = kill["kill"] as JsonObject ?? [];
        long L(JsonNode? n) => Integer(n);
        string At(string axis) => info["pos"] is JsonObject pos && pos[axis] is JsonValue ? Py.Str(pos[axis]) : "";
        var texture = ScorePage.Texture(kill["texture"], textures);
        sheet["species"] = species;
        sheet["weapon"] = ItemName(shot?["weapon_id"]);
        sheet["scope"] = ItemName(shot?["scope_id"]) != "";
        sheet["ammo"] = ItemName(shot?["ammo_id"]);
        sheet["distanceM"] = Real(shot?["distance"]) / 1000;
        sheet["state"] = shot is null ? "" : ScorePage.AnimalState(locale, L(shot["animal_state"]));
        sheet["bodyPart"] = shot is null ? "" : ScorePage.BodyPart(locale, L(shot["bodypart"]), L(shot["organ"])).Trim();
        sheet["stance"] = shot is null ? "" : ScorePage.Pose(locale, L(shot["pose"]));
        sheet["platform"] = "None";
        sheet["weightKg"] = Real(kill["weight"]) / 1000;
        sheet["type"] = $"{ScorePage.Loc(locale, "animal_texture_" + texture, texture)} ({(Real(kill["gender"]) == 0 ? "M" : "F")})";
        sheet["woundTime"] = Real(info["wound_time"]);
        sheet["integrity"] = (int)Math.Round(Real(info["trophy_integrity"]));
        sheet["shotLocation"] = $"{reserve} ({At("x")}, {At("z")})";
        sheet["shots"] = hits.Count;
        sheet["when"] = L(info["confirmTs"]);
        sheet["expeditionId"] = kill["expedition"] is JsonArray { Count: > 0 } expedition && expedition[0] is JsonObject x ? L(x["id"]) : 0;
        sheet["score"] = Real(info["score"]);
        sheet["harvestValue"] = Real(info["harvest_value"]);
        return sheet;
    }
}
