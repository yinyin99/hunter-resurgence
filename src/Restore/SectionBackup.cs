using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Restore;

/// <summary>Another community tool's backup: a folder (at the root of the zip or one level down) with `index.json` (`user_id`, `handle`, `captured_at`, `files`) and one `json_&lt;section&gt;.json` per API section,
/// each the original server's reply body as it came. The zip holds no manifest, no claim key and no checksums, so nothing in it proves whose it is: the Steam id and the claim hash are never read (the importer's
/// identity has neither), and the zip goes to the operator like any other. A section becomes the saved reply of the endpoint our own backups save (file name and params as the backup tool gives them), so
/// the mapper, the restore rules and the pictures read it like one of ours; a section with no endpoint is kept as it is. Entries outside the folder (a converted copy for another server) are ignored.</summary>
static partial class SectionBackup
{
    /// <summary>The identity's format in the report: 0, a zip with no manifest.</summary>
    const int Format = 0;

    /// <summary>"@" in an endpoint or a param is the user id.</summary>
    const string U = "@";

    static readonly Dictionary<string, (string Endpoint, string[] Params)> Known = new(StringComparer.Ordinal)
    {
        ["me"] = ("/v1/Me/me", []),
        ["inventory"] = ("/v1/Inventory/list", ["user_id=@"]),
        ["inventory_dict"] = ("/v1/Inventory/dict", ["user_id=@"]),
        ["skills"] = ("/v1/Skill/list", ["user_id=@"]),
        ["statistics"] = ("/v1/Statistics/total", ["user_id=@"]),
        ["achievements"] = ("/v1/Achievement/stats", ["user_id=@"]),
        ["trophies"] = ("/v1/Trophy/list", ["user_id=@", "offset=0", "limit=100000"]),
        ["gallery"] = ("/v1/Gallery/list", ["user_id=@", "offset=0", "limit=24"]),
        ["gallery_all"] = ("/v1/Gallery/list_all", []),
        ["expeditions"] = ("/v1/Expedition/list", ["user_id=@", "limit=50", "offset=0"]),
        ["missions"] = ("/v1/Mission/missions", []),
        ["challenges"] = ("/v1/Challenge/get_user_challenges", ["user_id=@"]),
        ["daily_missions_calendar"] = ("/v1/Daily_Mission/get_daily_missions_calendar", []),
        ["friends"] = ("/v1/Me/friends", []),
        ["friendslist"] = ("/v1/User/friendslist", ["user_id=@", "offset=0", "limit=0"]),
        ["chat_conversations"] = ("/v1/Chat/conversations", []),
        ["leaderboard"] = ("/v1/Leaderboard/personal", ["user_id=@", "season_no=0"]),
        ["wallet_transactions"] = ("/v1/Wallet/transactions", ["lang=en", "offset=0"]),
        ["wallet_silver"] = ("/v1/Wallet/silver_transactions", ["lang=en", "offset=0"]),
        ["wallet_bank"] = ("/v1/Wallet/bank_transactions", []),
        ["wallet_spent"] = ("/v1/Wallet/spent", []),
        ["v2_profile"] = ("/game/user/@/profile", []),
        ["v2_ranks"] = ("/game/user/@/ranks", []),
        ["v2_lodges"] = ("/game/user/@/lodges", []),
        ["v2_backgrounds"] = ("/game/user/backgrounds", []),
        ["v2_trophies"] = ("/game/user/trophies", []),
        ["v2_loadout"] = ("/game/user/loadout", []),
        // the same layout from another tool, under its own section names
        ["trophy_list"] = ("/v1/Trophy/list", ["user_id=@", "offset=0", "limit=100000"]),
        ["user_challenges"] = ("/v1/Challenge/get_user_challenges", ["user_id=@"]),
        ["leaderboard_personal"] = ("/v1/Leaderboard/personal", ["user_id=@", "season_no=0"]),
        ["loadouts"] = ("/game/user/loadout", []),
        ["lodges"] = ("/game/user/@/lodges", []),
        ["ranks"] = ("/game/user/@/ranks", []),
    };

    [GeneratedRegex(@"^json_([A-Za-z0-9_]+)\.json$")]
    private static partial Regex SectionFile();

    /// <summary>`lodge_&lt;id&gt;`: the reply of one lodge (`/game/user/&lt;uid&gt;/lodges/&lt;id&gt;`).</summary>
    [GeneratedRegex(@"^lodge_([0-9]{1,18})$")]
    private static partial Regex LodgeFile();

    /// <summary>The section a file of the folder <paramref name="prefix"/> holds (null: no section file, or in a deeper folder).</summary>
    static string? Section(ZipArchiveEntry e, string prefix) =>
        e.FullName.StartsWith(prefix, StringComparison.Ordinal) && SectionFile().Match(e.FullName[prefix.Length..]) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The folders (the root or one level down) with an index.json next to at least one section file, as their prefixes in the zip. One pass over the entries.</summary>
    static List<(ZipArchiveEntry Index, string Prefix)> Folders(ZipArchive zip)
    {
        var indexes = new List<(ZipArchiveEntry Index, string Prefix)>();
        var withSection = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in zip.Entries.Where(e => e.FullName.AsSpan().Count('/') <= 1))
        {
            var cut = e.FullName.LastIndexOf('/') + 1;
            if (e.FullName[cut..] == "index.json") indexes.Add((e, e.FullName[..cut]));
            else if (SectionFile().IsMatch(e.FullName.AsSpan(cut))) withSection.Add(e.FullName[..cut]);
        }
        return [.. indexes.Where(i => withSection.Contains(i.Prefix))];
    }

    public static bool IsOne(ZipArchive zip) => Folders(zip).Count > 0;

    public static BackupContent Read(ZipArchive zip, Limits lim)
    {
        if (Folders(zip) is not [{ Index: var indexEntry, Prefix: var prefix }])
            throw new RestoreException("more than one folder with an index.json: a backup holds one");
        long total = 0;
        var bytes = BackupReader.Bytes(indexEntry, lim.ManifestBytes, lim, ref total, null) ?? throw new RestoreException($"index.json is over {lim.ManifestBytes} bytes");
        long jsonTotal = bytes.Length;
        if (jsonTotal > lim.JsonTotalBytes) throw new RestoreException($"JSON content over {lim.JsonTotalBytes} bytes");
        if (!BackupReader.TryParse(bytes, lim, out var parsed) || parsed is not JsonObject index)
            throw new RestoreException("index.json is not a JSON object (or nested too deep, or has a duplicate key)");
        var uid = BackupReader.CheckUid(BackupReader.Text(index["user_id"]), "index.json");

        var listed = (index["files"] as JsonArray ?? []).Select(BackupReader.Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var files = new List<SavedFile>();
        var rejected = new List<string>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var cap = Math.Min(lim.EntryBytes, lim.JsonEntryBytes);
        foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/') && e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName[prefix.Length..].Contains('/') && e != indexEntry)
                     .DistinctBy(e => e.FullName).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var name = e.FullName[prefix.Length..];
            present.Add(name);
            if (Section(e, prefix) is not { } section) { rejected.Add($"{name}: not a section file"); continue; }
            if (!listed.Contains(name)) { rejected.Add($"{name}: not listed in index.json"); continue; }
            var body = BackupReader.Bytes(e, cap, lim, ref total, null) ?? throw new RestoreException($"JSON entry over {cap} bytes: {name}");
            if ((jsonTotal += body.Length) > lim.JsonTotalBytes) throw new RestoreException($"JSON content over {lim.JsonTotalBytes} bytes");
            if (!BackupReader.TryParse(body, lim, out var json)) { rejected.Add(BackupReader.NotJson(name, lim)); continue; }
            if (section == "hunt_details") Hunts(uid, name, json, files, rejected);
            // `trophies` is Trophy/list ({total, trophies}) in one tool and the own trophies and harvests (an array) in the other
            else if (section == "trophies" && json is JsonArray) files.Add(Reply("/game/user/trophies", [], uid, json));
            // `profile` is the public profile (`hostname`: Public_user/getByHostname) in one tool and the API v2 profile in the other; a `v2_profile` section, when there is one, wins
            else if (section == "profile" && json is JsonObject { } profile && BackupReader.Text(profile["hostname"]) is { } hostname) files.Add(Reply("/v1/Public_user/getByHostname", [$"hostname={hostname}"], uid, json));
            else if (section == "profile" && json is JsonObject { } v2 && (v2.ContainsKey("background") || v2.ContainsKey("ribbon") || v2.ContainsKey("earned_achievements")))
            {
                if (!zip.Entries.Any(x => x.FullName == prefix + "json_v2_profile.json")) files.Add(Reply("/game/user/@/profile", [], uid, json));
            }
            else if (LodgeFile().Match(section) is { Success: true } lodge) files.Add(Reply($"/game/user/@/lodges/{lodge.Groups[1].Value}", [], uid, json));
            else if (Known.TryGetValue(section, out var k)) files.Add(Reply(k.Endpoint, k.Params, uid, json));
            else files.Add(new(name, "", new Dictionary<string, string>(), null, json));
        }
        var missing = listed.Where(n => !present.Contains(n)).Order(StringComparer.Ordinal).Select(n => $"{n} (listed in index.json, not in the zip)").ToList();

        var manifest = new JsonObject { ["uid"] = uid };
        if (BackupReader.Text(index["handle"]) is { } handle) manifest["handle"] = handle;
        if (BackupReader.Text(index["captured_at"]) is { } at) manifest["started_utc"] = at;
        return new(manifest, uid, Format, [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], [], [], rejected, missing);
    }

    /// <summary>`hunt_details` is one object keyed by hunt id: each value is the reply of `Public_user/expedition` for that hunt, saved as its own file.</summary>
    static void Hunts(string uid, string name, JsonNode? json, List<SavedFile> files, List<string> rejected)
    {
        if (json is not JsonObject hunts)
        {
            if (json is not (null or JsonArray { Count: 0 })) rejected.Add($"{name}: not an object keyed by hunt id");
            return;
        }
        foreach (var (key, hunt) in hunts)
            if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && key == id.ToString(CultureInfo.InvariantCulture))
                files.Add(Reply("/v1/Public_user/expedition", ["user_id=@", $"expedition_id={key}"], uid, hunt));
            else rejected.Add($"{name}: a key that is not a hunt id");
    }

    /// <summary>The saved reply of an endpoint, named the way the backup tool names it: the path without its `/v1` and `/`, then `__key=value` per param, as <see cref="Importer.CleanName"/>.</summary>
    static SavedFile Reply(string endpoint, string[] ps, string uid, JsonNode? json)
    {
        endpoint = endpoint.Replace(U, uid);
        var pairs = ps.Select(p => p.Replace(U, uid).Split('=', 2)).ToList();
        var name = (endpoint.StartsWith("/v1/", StringComparison.Ordinal) ? endpoint[4..] : endpoint[1..]) + string.Concat(pairs.Select(p => $"__{p[0]}={p[1]}"));
        return new(Importer.CleanName(name) + ".json", endpoint, pairs.ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal), null, json);
    }
}
