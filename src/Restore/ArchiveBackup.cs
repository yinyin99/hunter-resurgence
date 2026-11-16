using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Restore;

/// <summary>A third community tool's account export (`*_PRIVATE.zip`): `ARCHIVE_MANIFEST.json` at the root (`schema` ending `-account-file-manifest-v1`, `files` with a `path` and `sha256` each), `EXPORT_REPORT.json`,
/// the account's replies under `authenticated/`, the profile's under `profile/resume/` (sections, pages of lists, one file per hunt and per harvest) and its gallery pictures under `profile/media/gallery/`
/// (`&lt;photo id&gt;_full.jpg`, also copied under `profile/resume/media/gallery/`; one copy is used). Every file read is checked against the manifest's SHA-256 like our own manifest's; a mismatch or an unlisted file is rejected.
/// The player's id is `authenticated/account.json`'s (the Me/me reply) and must agree with the game's connect reply `account/connect.json`. The zip holds no Steam id and no claim key, so the identity has neither and an operator
/// reviews it (the format decides only the parser). Each file becomes the saved reply of the endpoint our own backups save it as, so the mapper, the rules and the pictures read it like one of ours; a paged list is merged into
/// the one reply our backup holds; a file with no such endpoint is kept as it is. The connect reply is read only for what no other file holds (skills, items, statistics), and only when its shape matches. `launcher/`, `settings/`,
/// `game_connect/`, `capture.log`, flags, `.mime` and `.jsonl` files are private or not restorable and are never read.</summary>
static partial class ArchiveBackup
{
    const string SchemaEnd = "-account-file-manifest-v1";
    const string ManifestName = "ARCHIVE_MANIFEST.json", ReportName = "EXPORT_REPORT.json", ConnectName = "account/connect.json";

    /// <summary>The JSON files that are read: the account's and profile's replies, the connect reply, the report. Everything else in the zip is not read (the flat `conversation_&lt;id&gt;.json` repeats the paged one).</summary>
    [GeneratedRegex(@"^(?:authenticated/(?!conversation_[^/]*\.json$)|profile/resume/(?:sections|pages|expeditions|harvests|meta)/).*\.json$|^(?:EXPORT_REPORT|account/connect|account/deployed_items)\.json$")]
    private static partial Regex Readable();

    [GeneratedRegex(@"^profile/(?:resume/)?media/gallery/([0-9]{1,18})_full\.jpg$")]
    private static partial Regex Picture();

    [GeneratedRegex(@"^authenticated/pages/conversation_[^/]+/response\.json$")]
    private static partial Regex Conversation();

    static JsonObject? Manifest(ZipArchive zip, Limits lim, ref long total, out byte[] bytes)
    {
        bytes = [];
        if (zip.GetEntry(ManifestName) is not { } e) return null;
        var read = BackupReader.Bytes(e, lim.ManifestBytes, lim, ref total, null) ?? throw new RestoreException($"{ManifestName} is over {lim.ManifestBytes} bytes");
        bytes = read;
        return BackupReader.TryParse(read, lim, out var node) && node is JsonObject o && BackupReader.Text(o["schema"]) is { } s && s.EndsWith(SchemaEnd, StringComparison.Ordinal) ? o : null;
    }

    public static bool IsOne(ZipArchive zip) { long total = 0; return Manifest(zip, Limits.Default, ref total, out _) is not null; }

    public static BackupContent Read(ZipArchive zip, Limits lim)
    {
        long total = 0;
        var m = Manifest(zip, lim, ref total, out var mBytes)!;
        long jsonTotal = mBytes.Length;
        if (jsonTotal > lim.JsonTotalBytes) throw new RestoreException($"JSON content over {lim.JsonTotalBytes} bytes");
        var listed = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in (m["files"] as JsonArray ?? []).OfType<JsonObject>())
            if (BackupReader.Text(f["path"]) is { Length: > 0 } p) listed.TryAdd(p, BackupReader.Text(f["sha256"]));

        var json = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var pictures = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal); // photo id -> its verified entry
        var rejected = new List<string>();
        var cap = Math.Min(lim.EntryBytes, lim.JsonEntryBytes);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')).DistinctBy(e => e.FullName).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var name = e.FullName;
            present.Add(name);
            var photo = Picture().Match(name);
            var isJson = Readable().IsMatch(name);
            if (!isJson && !photo.Success) continue;
            if (photo.Success && pictures.ContainsKey(photo.Groups[1].Value)) continue; // the copy under profile/resume/media
            if (!listed.TryGetValue(name, out var sha)) { rejected.Add($"{name}: not listed in {ManifestName}"); continue; }
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var limit = isJson ? cap : lim.EntryBytes;
            var body = BackupReader.Bytes(e, limit, lim, ref total, h) ?? throw new RestoreException($"{(isJson ? "JSON entry" : "entry")} over {limit} bytes: {name}");
            if (isJson && (jsonTotal += body.Length) > lim.JsonTotalBytes) throw new RestoreException($"JSON content over {lim.JsonTotalBytes} bytes");
            if (sha is null || !Convert.ToHexStringLower(h.GetHashAndReset()).Equals(sha, StringComparison.OrdinalIgnoreCase)) { rejected.Add($"{name}: sha256 does not match the manifest"); continue; }
            if (photo.Success) pictures[photo.Groups[1].Value] = e;
            else if (!BackupReader.TryParse(body, lim, out var node)) rejected.Add(BackupReader.NotJson(name, lim));
            else json[name] = node;
        }
        var missing = listed.Keys.Where(p => (Readable().IsMatch(p) || Picture().IsMatch(p)) && !present.Contains(p)).Order(StringComparer.Ordinal)
            .Select(p => $"{p} (listed in {ManifestName}, not in the zip)").ToList();

        var report = json.Remove(ReportName, out var r) ? r as JsonObject : null;
        foreach (var x in (report?["missing"] as JsonArray ?? []).Select(BackupReader.Text).OfType<string>()) missing.Add($"{x} ({ReportName} says it was not saved)");
        var connect = json.Remove(ConnectName, out var c) ? c as JsonObject : null;

        var me = json.GetValueOrDefault("authenticated/account.json") as JsonObject;
        var uid = BackupReader.CheckUid(BackupReader.Text(me?["id"]) ?? BackupReader.Text((connect?["account"] as JsonObject)?["user_id"]), "authenticated/account.json and account/connect.json: the id of");
        if (connect?["account"] is JsonObject acct && BackupReader.Text(acct["user_id"]) is { } connectUid && connectUid != uid)
            throw new RestoreException($"the connect reply's user id {connectUid} is not the account's id {uid}");

        var files = new List<SavedFile>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        bool Has(string endpoint) => files.Any(f => f.Endpoint == endpoint.Replace("@", uid));
        void Add(string endpoint, string[] ps, JsonNode? node) => files.Add(SectionBackup.Reply(endpoint, ps, uid, node));
        void One(string path, string endpoint, params string[] ps)
        {
            if (json.TryGetValue(path, out var n)) { Add(endpoint, ps, n); done.Add(path); }
        }
        /// <summary>The pages of a list under <paramref name="dir"/> (`&lt;offset&gt;.json`), in offset order, as the one reply: the arrays of `key` joined (or the arrays themselves, when key is null), with the first page's `total`.</summary>
        JsonNode? Merged(string dir, string? key)
        {
            var pages = json.Keys.Where(k => k.StartsWith(dir + "/", StringComparison.Ordinal) && Regex.IsMatch(k[(dir.Length + 1)..], @"^[0-9]{1,18}\.json$"))
                .OrderBy(k => long.Parse(k[(dir.Length + 1)..^".json".Length], CultureInfo.InvariantCulture)).ToList();
            if (pages.Count == 0) return null;
            var rows = new JsonArray();
            foreach (var p in pages)
            {
                done.Add(p);
                foreach (var row in ((key is null ? json[p] : (json[p] as JsonObject)?[key]) as JsonArray ?? [])) rows.Add(row?.DeepClone());
            }
            return key is null ? rows : new JsonObject { [key] = rows, ["total"] = (json[pages[0]] as JsonObject)?["total"]?.DeepClone() };
        }

        One("authenticated/account.json", "/v1/Me/me");
        One("authenticated/inventory.json", "/v1/Inventory/list", "user_id=@");
        One("profile/resume/sections/skills.json", "/v1/Skill/list", "user_id=@");
        One("profile/resume/sections/statistics.json", "/v1/Statistics/total", "user_id=@");
        One("profile/resume/sections/achievements.json", "/v1/Achievement/stats", "user_id=@");
        One("profile/resume/sections/owned_trophies.json", "/game/user/trophies");
        One("profile/resume/sections/loadouts.json", "/game/user/loadout");
        One("profile/resume/sections/lodges.json", "/game/user/@/lodges");
        One("profile/resume/sections/ranks.json", "/game/user/@/ranks");
        One("profile/resume/sections/profile_v2.json", "/game/user/@/profile");
        One("authenticated/missions.json", "/v1/Mission/missions");
        One("authenticated/daily_missions.json", "/v1/Daily_Mission/get_daily_missions_calendar");
        One("authenticated/user_challenges.json", "/v1/Challenge/get_user_challenges", "user_id=@");
        One("authenticated/personal_leaderboards.json", "/v1/Leaderboard/personal", "user_id=@", "season_no=0");
        One("authenticated/conversations.json", "/v1/Chat/conversations");
        One("authenticated/wallet_spent.json", "/v1/Wallet/spent");
        // a whole history and its pages hold the same transactions: the whole one is used when the zip has it
        foreach (var (stem, endpoint, ps) in new[] { ("em", "/v1/Wallet/transactions", new[] { "lang=en", "offset=0" }), ("gm", "/v1/Wallet/silver_transactions", new[] { "lang=en", "offset=0" }), ("bank", "/v1/Wallet/bank_transactions", Array.Empty<string>()) })
        {
            var paged = Merged($"authenticated/pages/wallet_{stem}_history", null);
            var whole = $"authenticated/wallet_{stem}_history.json";
            if (json.TryGetValue(whole, out var all)) done.Add(whole);
            if ((all ?? paged) is { } wallet) Add(endpoint, ps, wallet);
        }
        if (Merged("profile/resume/pages/trophies", "trophies") is { } trophies) Add("/v1/Trophy/list", ["user_id=@", "offset=0", "limit=100000"], trophies);
        if (Merged("profile/resume/pages/expedition_list", "expeditions") is { } hunts) Add("/v1/Expedition/list", ["user_id=@", "limit=50", "offset=0"], hunts);
        if (Merged("profile/resume/pages/friends", "friends") is { } friends) Add("/v1/User/friendslist", ["user_id=@", "offset=0", "limit=0"], friends);

        var media = new List<MediaFile>();
        if (Merged("profile/resume/pages/gallery", "photos") is JsonObject gallery)
        {
            Add("/v1/Gallery/list", ["user_id=@", "offset=0", "limit=24"], gallery);
            // the pictures are in the zip: matched by photo id, stored under the path our backups give a link's picture, so no link is fetched
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in (gallery["photos"] as JsonArray ?? []).OfType<JsonObject>())
                if (BackupReader.Text(p["id"]) is { } id && pictures.TryGetValue(id, out var entry) && Importer.MediaPath("photos", BackupReader.Text(p["url"])) is { } path && seen.Add(path))
                    media.Add(new(path, entry.FullName));
        }
        if (json.GetValueOrDefault("profile/resume/sections/lodge_details.json") is JsonObject details)
        {
            done.Add("profile/resume/sections/lodge_details.json");
            foreach (var (key, lodge) in details)
                if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && key == id.ToString(CultureInfo.InvariantCulture)) Add($"/game/user/@/lodges/{key}", [], lodge);
                else rejected.Add("profile/resume/sections/lodge_details.json: a key that is not a lodge id");
        }
        foreach (var (path, node) in json)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (path.StartsWith("profile/resume/expeditions/", StringComparison.Ordinal) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _) && path.Count(ch => ch == '/') == 3)
                Add("/v1/Public_user/expedition", ["user_id=@", $"expedition_id={id}"], node);
            else if (path.StartsWith("profile/resume/harvests/", StringComparison.Ordinal) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _) && path.Count(ch => ch == '/') == 3)
                Add($"/game/user/harvest/{id}", [], node);
            else if (Conversation().IsMatch(path) && Partner(node, uid) is { } partner) Add("/v1/Chat/conversation", [$"user_id={partner}"], node);
            else continue;
            done.Add(path);
        }
        if (connect is not null) FromConnect(connect, Has, Add, files);
        files.AddRange(json.Where(j => !done.Contains(j.Key)).Select(j => new SavedFile(j.Key, "", new Dictionary<string, string>(), null, j.Value)));

        var manifest = new JsonObject { ["uid"] = uid };
        if ((BackupReader.Text(me?["handle"]) ?? BackupReader.Text((connect?["account"] as JsonObject)?["handle"])) is { } handle) manifest["handle"] = handle;
        if (BackupReader.Text(report?["captured_at"]) is { } at) manifest["started_utc"] = at;
        return new(manifest, uid, 0, [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], media, [], rejected, missing);
    }

    /// <summary>The other person of a conversation: the one user id among the messages' that is not the player's; null when it is not one id.</summary>
    static string? Partner(JsonNode? reply, string uid)
    {
        var others = ((reply as JsonObject)?["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(x => BackupReader.Text(x["user_id"])).Where(u => u is not null && u != uid).Distinct().ToList();
        return others is [{ } one] && one.All(char.IsAsciiDigit) && long.TryParse(one, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? one : null;
    }

    /// <summary>What the other files lack, from the connect reply: `skills` (the Skill/list reply as it is), `inventory.items` (flat rows, to the Inventory/dict shape) and `user_stats` (to Statistics/total).
    /// A part whose shape does not match is kept as it is under `account/connect.json#&lt;part&gt;` (the report then lists what it would have given as missing).</summary>
    static void FromConnect(JsonObject connect, Func<string, bool> has, Action<string, string[], JsonNode?> add, List<SavedFile> files)
    {
        void Keep(string part) { if (connect[part] is { } n) files.Add(new($"{ConnectName}#{part}", "", new Dictionary<string, string>(), null, n.DeepClone())); }
        if (!has("/v1/Skill/list"))
            if (connect["skills"] is JsonObject s && s["weapon"] is JsonObject && s["tracking"] is JsonArray && s["spotting"] is JsonArray) add("/v1/Skill/list", ["user_id=@"], s.DeepClone());
            else Keep("skills");
        if (!has("/v1/Inventory/list") && !has("/v1/Inventory/dict"))
        {
            var dict = new JsonObject();
            var ok = (connect["inventory"] as JsonObject)?["items"] is JsonArray rows && rows.All(r => r is JsonObject o && BackupReader.Text(o["guid"]) is { } g && long.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out _) && BackupReader.Text(o["item_id"]) is { Length: > 0 } i && i.All(char.IsAsciiDigit));
            if (ok)
            {
                foreach (var o in ((JsonArray)connect["inventory"]!["items"]!).OfType<JsonObject>())
                {
                    var key = BackupReader.Text(o["item_id"])!;
                    var group = dict[key] as JsonArray ?? (JsonArray)(dict[key] = new JsonArray());
                    group.Add(new JsonObject { ["id"] = BackupReader.Text(o["guid"]), ["slot"] = o["slot"]?.DeepClone(), ["units"] = o["units"]?.DeepClone(), ["metadata"] = o["metadata"]?.DeepClone() });
                }
                add("/v1/Inventory/dict", ["user_id=@"], dict);
            }
            else Keep("inventory");
        }
        if (!has("/v1/Statistics/total"))
            if (connect["user_stats"] is JsonObject { } us && us["user_stats"] is JsonObject st && new[] { "animal", "collectables", "daily_missions", "weapon" }.All(k => st[k] is JsonObject) && st["distance"] is JsonValue && st["duration"] is JsonValue)
                add("/v1/Statistics/total", ["user_id=@"], new JsonObject
                {
                    ["data"] = new JsonObject { ["collectables"] = st["collectables"]!.DeepClone(), ["weapon"] = st["weapon"]!.DeepClone(), ["animal"] = st["animal"]!.DeepClone(), ["daily_missions"] = st["daily_missions"]!.DeepClone() },
                    ["distance"] = st["distance"]!.DeepClone(), ["duration"] = st["duration"]!.DeepClone(),
                });
            else Keep("user_stats");
    }
}
