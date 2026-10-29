using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Restore;

/// <summary>Restore layer A: a backup zip of any backup tool version to a <see cref="PlayerModel"/> and a <see cref="Report"/>.
/// Everything is read from the raw replies; derived files are verified but not used. Mode is decided by the files, never by tool_version.</summary>
public static class Importer
{
    /// <summary>The options the model is written with (the CLI, the tests).</summary>
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Restored Read(string zipPath, IItemCatalogue? catalogue = null, Limits? limits = null)
    {
        using var s = File.OpenRead(zipPath);
        return Read(s, catalogue, limits);
    }

    public static Restored Read(Stream zip, IItemCatalogue? catalogue = null, Limits? limits = null) =>
        new Mapper(BackupReader.Read(zip, limits ?? Limits.Default), catalogue).Map();

    sealed class Mapper(BackupContent c, IItemCatalogue? catalogue)
    {
        readonly List<string> restored = [], warnings = [], missing = [.. c.Missing];
        readonly HashSet<string> used = new(StringComparer.Ordinal); // fully mapped: neither in Extras nor unsupported
        readonly HashSet<string> partly = new(StringComparer.Ordinal); // mapped in part: in Extras, not unsupported
        readonly SortedDictionary<string, JsonNode?> extras = new(StringComparer.Ordinal);
        bool own, inferred;

        IEnumerable<SavedFile> All(string endpoint) => c.Files.Where(f => f.Endpoint == endpoint);

        JsonNode? One(string endpoint, HashSet<string> mark, string? key = null, string? value = null)
        {
            var f = All(endpoint).FirstOrDefault(f => key is null || f.Params.GetValueOrDefault(key) == value);
            if (f is null) return null;
            mark.Add(f.Path);
            return f.Json;
        }

        void Missing(string part, bool ownOnly = false) => missing.Add(ownOnly && !own ? $"{part} (public zip)" : $"{part} (not in the zip)");

        public Restored Map()
        {
            var m = c.Manifest;
            var me = One("/v1/Me/me", partly) as JsonObject;
            own = me is not null;
            var byHandle = One("/v1/Public_user/getByHostname", partly) as JsonObject;
            var mode = own ? "my-account" : "public";
            if (BackupReader.Text(m["mode"]) is { } said && (said == "own") != own)
                warnings.Add($"the manifest says mode \"{said}\" but the files are {mode}: the files are used");

            var completed = Date(m["completed_utc"]);
            var identity = new Identity(c.Uid, BackupReader.Text(m["handle"]) ?? Str(me?["handle"]), own ? BackupReader.Text(m["steam_id64"]) : null, mode,
                c.Format, BackupReader.Text(m["tool_version"]), BackupReader.Text(m["claim_hash"])!, Date(m["started_utc"]), completed);

            var profile = Profile(me ?? byHandle, identity);
            var wallet = Wallet(me);
            var items = Items();
            var skills = Skills();
            var stats = Stats();
            var achievements = Achievements();
            var (trophies, lodges) = TrophiesAndLodges();
            var account = new AccountDefaults(FacebookEnabled: false, ShareOnFacebook: 0, FacebookId: null, Email: null, EmailVerified: 0, HasPassword: 0,
                AccountState: 0, FirstTime: 0, Face: 1000000, SteamId: identity.SteamId64, UseImperial: 0, UserGroup: 1);
            foreach (var (part, endpoint, ownOnly) in new[]
            {
                ("loadout", "/game/user/loadout", true), ("missions", "/v1/Mission/missions", true), ("messages", "/v1/Chat/conversations", true),
                ("friends", "/v1/User/friendslist", false), ("hunts", "/v1/Expedition/list", false),
            })
                if (!All(endpoint).Any()) Missing(part, ownOnly);
            // format 2: listed hunts whose reply was not saved. v0.4.0 also wrote "total" (a window count of the list, not the player's hunts) and could flag saved == listed: not a gap
            if (m["checks"]?["hunts"] is JsonObject h && Int(h["saved"]) is { } hs && Int(h["listed"]) is { } hl)
                if (h["list_failed"] is JsonValue lf && lf.TryGetValue<bool>(out var cut) && cut) missing.Add($"hunts: the hunt list did not load completely ({hs} of the {hl} listed were saved)");
                else if (hs < hl) missing.Add($"hunts: {hs} of the {hl} in the hunt list were saved");

            var unused = c.Files.Where(f => !used.Contains(f.Path)).ToList();
            foreach (var f in unused) extras[f.Path] = f.Json?.DeepClone();
            unused.RemoveAll(f => partly.Contains(f.Path));
            // ids in a path (/game/user/<uid>/ranks, one leaderboard per id) would give one line per file
            static string Pattern(string ep) => string.Join('/', ep.Split('/').Select(s => s.Length > 0 && s.All(char.IsAsciiDigit) ? "*" : s));
            var unsupported = unused.Where(f => f.Status != 404).GroupBy(f => Pattern(f.Endpoint)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{(g.Key.Length == 0 ? "unknown endpoint" : g.Key)}: {g.Count()} file(s), kept in Extras").ToList();
            if (unused.Count(f => f.Status == 404) is > 0 and var none) unsupported.Add($"\"no entry\" answers (status 404): {none} file(s), kept in Extras");
            if (c.OtherFiles > 0) unsupported.Add($"pictures and score pages: {c.OtherFiles} file(s), not read");

            var model = new PlayerModel(identity, profile, account, wallet, items, skills, stats, achievements, trophies, lodges, extras);
            var report = new Report(own ? "my-account" : byHandle is not null ? "public by handle" : "public by id", restored, unsupported, missing, warnings,
                c.Failed, c.Rejected, completed is not null ? "yes" : c.Format == 1 ? "unknown" : "no", catalogue is null ? "zero-only" : "catalogue", inferred);
            return new(model, report);
        }

        Profile Profile(JsonObject? src, Identity id)
        {
            var v2 = One($"/game/user/{c.Uid}/profile", partly) as JsonObject;
            if (src?["id"] is { } sid && BackupReader.Text(sid) != c.Uid)
                throw new RestoreException($"the identity reply's id {BackupReader.Text(sid)} is not the manifest uid {c.Uid}");
            var type = Str(src?["membershipType"]);
            int membership = 0;
            switch (type)
            {
                case "freeplay": break;
                case "staff": membership = 2; break;
                case "premium": membership = 1; inferred = true; break;
                case "freebeta": inferred = true; break;
                case null: missing.Add("membership type (not in the zip): 0 used"); break;
                default: warnings.Add($"unknown membership type \"{type}\": 0 used"); break;
            }
            if (src is null && v2 is null) Missing("profile");
            else restored.Add($"profile (from {(own ? "Me/me" : src is not null ? "Public_user/getByHostname" : "the apiv2 profile")})");
            return new(long.Parse(c.Uid, CultureInfo.InvariantCulture), Str(src?["handle"]) ?? id.Handle, Str(src?["hostname"]), Int(src?["gender"]) ?? 0,
                Int(src?["hunterscore"]) ?? Int(v2?["hunterscore"]) ?? 0, Str(src?["avatar"]), type, membership, Str(src?["uuid"]), Str(src?["locale"]),
                Str(src?["timezone"]), Str(src?["country"]), Long(src?["memberSince"]), UserGroup: 1, Int(src?["rank"]));
        }

        Wallet? Wallet(JsonObject? me)
        {
            if (Dbl(me?["ems"]) is { } em && Dbl(me?["gms"]) is { } gm)
            {
                restored.Add("wallet");
                return new(em, gm);
            }
            Missing("wallet", ownOnly: true);
            return null;
        }

        List<ItemRow> Items()
        {
            var dict = One("/v1/Inventory/dict", used);
            var list = One("/v1/Inventory/list", used);
            if (dict is null && list is null) { Missing("items"); return []; }
            var rows = new Dictionary<long, (int ItemId, JsonObject Row)>();
            var odd = new JsonArray();
            void Add(string? key, JsonNode? r, string from)
            {
                if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId) && r is JsonObject o && Long(o["id"]) is { } guid)
                {
                    if (from == "list" && rows.TryGetValue(guid, out var had)) // the dict can lag a change: the list wins
                    {
                        var diff = new[] { "metadata", "units", "dir" }.Where(k => Raw(k == "dir" ? had.Row["location"]?["dir"] : had.Row[k]) != Raw(k == "dir" ? o["location"]?["dir"] : o[k])).ToList();
                        if (diff.Count > 0) warnings.Add($"item {guid}: Inventory/list and Inventory/dict differ in {string.Join(", ", diff)}: the list is used");
                    }
                    rows[guid] = (itemId, o);
                }
                else odd.Add(new JsonObject { ["from"] = from, ["item_id"] = key, ["row"] = r?.DeepClone() });
            }
            if (dict is JsonObject d) // an empty map can arrive as []
                foreach (var (key, group) in d)
                    foreach (var r in group as JsonArray ?? []) Add(key, r, "dict");
            foreach (var g in list as JsonArray ?? [])
                foreach (var r in g?["own"] as JsonArray ?? []) Add(BackupReader.Text(g?["item"]?["id"]), r, "list");

            var items = new List<ItemRow>();
            var placedNoLocation = new JsonArray();
            foreach (var (guid, (itemId, row)) in rows.OrderBy(r => r.Value.ItemId).ThenBy(r => r.Key))
            {
                var slot = Int(row["slot"]) ?? 0;
                var loc = row["location"] as JsonObject;
                var pos = Vec(loc?["pos"]);
                if (loc?["pos"] is not null && pos is null) warnings.Add($"item {guid} (item {itemId}) has a position that is not a number within {MaxWebUnits} on each axis: read as no location");
                if (slot == 23 && pos is null)
                {
                    warnings.Add($"placed item {guid} (item {itemId}) has no location: not restored, kept in Extras");
                    placedNoLocation.Add(new JsonObject { ["item_id"] = itemId, ["row"] = row.DeepClone() });
                    continue;
                }
                var dir = pos is null ? null : Vec(loc?["dir"]);
                if (pos is not null && dir is null) warnings.Add($"item {guid} (item {itemId}) has a position but no direction: direction left empty");
                var units = Int(row["units"]);
                units = catalogue is null ? units == 0 ? null : units : catalogue.HasUnits(itemId) ? units : null;
                items.Add(new(guid, itemId, slot, units, row["metadata"] as JsonObject is { } md ? (JsonObject)md.DeepClone() : [], pos, dir, pos is null ? null : Int(loc?["reserve"])));
            }
            if (placedNoLocation.Count > 0) extras["placed-without-location"] = placedNoLocation;
            if (odd.Count > 0)
            {
                extras["inventory-rows-not-read"] = odd;
                warnings.Add($"{odd.Count} inventory row(s) without a numeric item id or row id: kept in Extras");
            }
            if (catalogue is null) warnings.Add("no item catalogue: units read as null only when 0 (an unlimited item saved with 1 keeps 1)");
            restored.Add($"items: {items.Count} (from {string.Join(" and ", new[] { list is null ? null : "Inventory/list", dict is null ? null : "Inventory/dict" }.OfType<string>())})");
            return items;
        }

        SkillSet? Skills()
        {
            if (One("/v1/Skill/list", used) is not JsonObject s) { Missing("skills"); return null; }
            var weapon = new Dictionary<int, double>();
            foreach (var (k, v) in (s["weapon"] as JsonObject ?? []).OrderBy(w => int.TryParse(w.Key, out var n) ? n : int.MaxValue))
                if (int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && Dbl(v) is { } n) weapon[id] = n;
                else warnings.Add($"skill weapon entry \"{k}\" is not an item id and a number: skipped");
            double[] Track(string key)
            {
                var a = (s[key] as JsonArray ?? []).Select(n => Dbl(n) ?? 0).ToArray();
                if (a.Length != 59) warnings.Add($"skills {key}: {a.Length} values, 59 expected");
                return a;
            }
            var set = new SkillSet(weapon, Track("tracking"), Track("spotting"));
            restored.Add($"skills: {weapon.Count} weapons, {set.Tracking.Length} tracking, {set.Spotting.Length} spotting");
            return set;
        }

        StatsBlock? Stats()
        {
            var st = One("/v1/Statistics/total", used) as JsonObject;
            int? Score(string game)
            {
                var r = One("/v1/Page_content/minigames_list", used, "game_id", game);
                return r is null ? null : Int(r["position"]?["score"]);
            }
            var (bust, lane) = (Score("0"), Score("1"));
            if (st is null) Missing("statistics");
            else restored.Add("statistics");
            if (st is null && bust is null && lane is null) return null;
            return new(st?["data"] as JsonObject is { } data ? (JsonObject)data.DeepClone() : [], Dbl(st?["distance"]) ?? 0, Dbl(st?["duration"]) ?? 0, bust, lane);
        }

        List<AchievementRow> Achievements()
        {
            if (One("/v1/Achievement/stats", used) is not JsonArray a) { Missing("achievements"); return []; }
            var rows = a.OfType<JsonObject>().Select(r => new AchievementRow(Int(r["id"]) ?? 0, Int(r["category_id"]) ?? 0, Int(r["group_id"]) ?? 0, r["value"]?.DeepClone(),
                r["triggers"] as JsonObject is { } t ? (JsonObject)t.DeepClone() : [])).OrderBy(r => r.Id).ToList();
            restored.Add($"achievements: {rows.Count}");
            return rows;
        }

        (List<RawRow>, List<RawRow>) TrophiesAndLodges()
        {
            var trophies = new List<RawRow>();
            static IEnumerable<RawRow> Rows(string source, JsonNode? arr) =>
                (arr as JsonArray ?? []).OfType<JsonObject>().Select(r => new RawRow(source, BackupReader.Text(r["id"]) ?? "", r.DeepClone()));
            var listed = One("/v1/Trophy/list", used);
            if (listed is null) Missing("trophies");
            trophies.AddRange(Rows("/v1/Trophy/list", listed?["trophies"]));
            var mine = One("/game/user/trophies", used);
            if (mine is null) Missing("own trophies and harvests", ownOnly: true);
            trophies.AddRange(Rows("/game/user/trophies", mine));
            int none = 0;
            foreach (var f in c.Files.Where(f => f.Endpoint.StartsWith("/game/user/harvest/", StringComparison.Ordinal)))
            {
                used.Add(f.Path);
                if (f.Status == 404) none++; // "no harvest" is an answer
                else trophies.Add(new("/game/user/harvest", f.Endpoint["/game/user/harvest/".Length..], f.Json?.DeepClone()));
            }

            var lodges = new List<RawRow>();
            var lodgeList = One($"/game/user/{c.Uid}/lodges", used);
            if (lodgeList is null) Missing("lodges");
            lodges.AddRange(Rows("/game/user/lodges", lodgeList));
            var each = $"/game/user/{c.Uid}/lodges/";
            foreach (var f in c.Files.Where(f => f.Endpoint.StartsWith(each, StringComparison.Ordinal)))
            {
                used.Add(f.Path);
                lodges.Add(new("/game/user/lodge", f.Endpoint[each.Length..], f.Json?.DeepClone()));
            }
            static List<RawRow> Sorted(List<RawRow> r) => [.. r.OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Id.Length).ThenBy(x => x.Id, StringComparer.Ordinal)];
            if (listed is not null || mine is not null) restored.Add($"trophies: {trophies.Count} rows ({none} without a harvest)");
            if (lodgeList is not null) restored.Add($"lodges: {lodges.Count} rows");
            return (Sorted(trophies), Sorted(lodges));
        }

        static DateTime? Date(JsonNode? n) => n is JsonValue v && v.TryGetValue<DateTime>(out var d) ? d.ToUniversalTime() : null;
    }

    // ---------- reply values: this API sends numbers as JSON numbers or as numeric strings ----------

    static string? Str(JsonNode? n) => BackupReader.Text(n);

    static long? Long(JsonNode? n) => long.TryParse(Str(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
        : decimal.TryParse(Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d) && d is >= long.MinValue and <= long.MaxValue ? (long)d : null;

    static int? Int(JsonNode? n) => Long(n) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    /// <summary>A finite number: "NaN", "Infinity" or 1e400 is no number (and could not be written as JSON).</summary>
    static double? Dbl(JsonNode? n) => double.TryParse(Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    /// <summary>The JSON text as the server wrote it, for comparing ("null" when absent).</summary>
    static string Raw(JsonNode? n) => n?.ToJsonString() ?? "null";

    /// <summary>The largest web coordinate read as a position; beyond it a value is not a place on a map (and would overflow).</summary>
    const decimal MaxWebUnits = 2_000_000;

    /// <summary>Web units to integer millimetres: the number's decimal text times 1000, rounded half away from zero; null beyond ±MaxWebUnits.
    /// Never through double: 3144.645 * 1000 is 3144644.9999999995 there.</summary>
    internal static int? Milli(JsonNode? n) =>
        decimal.TryParse(Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) <= MaxWebUnits ? (int)Math.Round(d * 1000m, MidpointRounding.AwayFromZero) : null;

    static Vec3I? Vec(JsonNode? n) => n is JsonObject o && Milli(o["x"]) is { } x && Milli(o["y"]) is { } y && Milli(o["z"]) is { } z ? new(x, y, z) : null;
}
