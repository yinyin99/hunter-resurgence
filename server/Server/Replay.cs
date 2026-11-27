// Replay router: serves the operator's recorded HAR replies, the archives and the mirror; player state answers where it
// owns the reply. The branch order of Fallback is the routing contract.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace Resurgence.Server;

public static class Replay
{
    static readonly HashSet<string> DropHeaders = ["content-encoding", "content-length", "transfer-encoding", "connection", "keep-alive", "alt-svc", "date", "server"];
    static readonly HashSet<string> VolatileParams = ["oauth_access_token", "access_token", "callback", "_", "session_ticket"];
    static readonly Regex Jsonp = new(@"^\s*[\w$.]+\(", RegexOptions.ECMAScript);
    static readonly Regex Callback = new(@"\A[\w$.]+\z", RegexOptions.ECMAScript);
    static readonly FileExtensionContentTypeProvider Mime = new();

    public const string HEARTBEAT = ProtocolLiterals.RpcBase + "/heartbeat";
    public const string HEARTBEAT_SCENARIO = ProtocolLiterals.RpcBase + "/heartbeatscenario";  // lower case: NewBuild.Rewrite lower-cases the method
    public const string DISCONNECT = ProtocolLiterals.RpcBase + "/disconnect";
    public const string CONNECT = ProtocolLiterals.RpcBase + "/connect";
    public const string MOVE_MULTIPLE2 = "/v1/Inventory/move_multiple2";
    public const string BUY = "/v1/Inventory/buy";
    public const string REFILL_BARREL = "/v1/Inventory/refillBarrel";
    public const string DISCOUNTS = "/v1/Application/discounts";
    public const string AVATARS = "/v1/Avatar/getAvatars";
    public const string CHALLENGES = "/v1/Challenge/get_challenges";
    public const string USER_CHALLENGES = "/v1/Challenge/get_user_challenges";
    public const string SPLASH = "/v1/Splash/list_active";
    public const string LAUNCH_START = "/v1/Launch/start";
    public const string LAUNCH_START_MP = "/v1/Launch/start_mp";
    public const string LAUNCH_JOIN = "/v1/Launch/join";
    public const string NETWORK_LIST = "/v1/Network/list";
    public const string MAP_LOCATIONS = "/v1/Map/locations";
    public const string JWT = "/v1/User/jwt";
    public const string OAUTH_TOKEN = "/v1/Oauth/token";
    public const string APPLICATION = "/v1/Application/application";
    public const string PUBLIC_EXPEDITION = "/v1/Public_user/expedition";
    public const string PUBLIC_USER = "/v1/Public_user/getByHostname";
    public const string EXPEDITION_LIST = "/v1/Expedition/list";
    public const string ME = "/v1/Me/me";
    public const string POLL = "/v1/Me/poll";
    public const string ME_FRIENDS = "/v1/Me/friends";
    public const string FRIENDS_LIST = "/v1/User/friendslist";
    public const string BAN_PLAYER = "/v1/User/ban_player";
    public const string FRIEND_ADD = "/v1/Friend/add";
    public const string FRIEND_REMOVE = "/v1/Friend/remove";
    public const string CHAT_CONVERSATIONS = "/v1/Chat/conversations";
    public const string CHAT_CONVERSATION = "/v1/Chat/conversation";
    public const string SKILL_LIST = "/v1/Skill/list";
    public const string STATISTICS_TOTAL = "/v1/Statistics/total";
    public const string ACHIEVEMENT_STATS = "/v1/Achievement/stats";
    public const string INVENTORY_LIST = "/v1/Inventory/list";
    public const string INVENTORY_DICT = "/v1/Inventory/dict";
    public const string TROPHY_LIST = "/v1/Trophy/list";
    public const string LEADERBOARD_PERSONAL = "/v1/Leaderboard/personal";
    public const string CURRENT_PERSONAL = "/v1/Leaderboard/current_personal";
    public const string LEADERBOARDS_ALL = "/v1/Page_content/leaderboards_all";
    public const string LEADERBOARD_DETAILS = "/v1/Page_content/leaderboard_details";
    public const string HALL_OF_FAME = "/v1/Page_content/leaderboards_hall_of_fame";
    public const string GALLERY_LIST = "/v1/Gallery/list";
    public const string GALLERY_DELETE = "/v1/Gallery/delete";
    public const string WALLET_TRANSACTIONS = "/v1/Wallet/transactions";
    public const string WALLET_SILVER_TRANSACTIONS = "/v1/Wallet/silver_transactions";
    public const string WALLET_SPENT = "/v1/Wallet/spent";
    public const string MISSIONS = "/v1/Mission/missions";
    public const string MISSION_ACTIVATE = "/v1/Mission/activate";
    public const string MISSION_ACTIVATE_ALL = "/v1/Mission/activate_all_available";
    public const string MISSION_AUTO = "/v1/Mission/update_automatic_activation";
    public const string MISSION_ONLY_FAVOURITE = "/v1/Mission/update_activate_only_favourite";
    public const string MISSION_HIDE_COMPLETED = "/v1/Mission/update_hide_completed_favourite";
    public const string MISSION_FAVOURITE = "/v1/Mission/update_favourite";
    public const string DAILY_CALENDAR = "/v1/Daily_Mission/get_daily_missions_calendar";
    public const string DAILY_ACTIVATE = "/v1/Daily_Mission/activate_user_progress";
    // the current build's paths, lower case as it sends them (the old ones above are canonical in their own case; NewBuild.Canonical maps any case to them)
    public const string START_SINGLE_PLAYER = "/v1/session/start_single_player";
    public const string START_TUTORIAL = "/v1/session/start_tutorial";
    public const string CLIENT_APPLICATION = "/v1/application/client_application";
    public const string FEED_DATA = "/v1/splash/feed_data";
    public const string LOCALE_PREFIX = "/static/built/js/locale/";
    public const string WEB_SCRIPT_SUFFIX = "_compiled_web.min.js";

    // the hosts the server answers for: one settings key each
    public static readonly string[] HostKeys = ["gameserver_host", "api_host", "apiv2_host", "signup_host", "web_host", "game_web_host", "static_host",
        "static_legacy_host", "map_host", "photo_host", "avatar_host", "news_host", "telemetry_host", "crash_host"];

    public record struct Key(string Method, string Host, string Path, string? Params);
    public record Hit(int Tier, string Started, int Status, List<(string, string)> Headers, byte[] Body);

    public sealed class HarTable
    {
        public readonly OrderedDictionary<Key, Hit> Entries = new();
        public readonly Dictionary<string, (string Started, byte[] Body)> Connects = new();
    }

    static string S(JsonObject cfg, string key) => (string)cfg[key]!;

    public static List<string> RedirectedHosts(JsonObject cfg)
    {
        var hosts = HostKeys.Select(k => S(cfg, k)).Append(S(cfg, "web_host").RemovePrefix("www."));
        return hosts.Select(h => h.ToLowerInvariant()).Distinct().ToList();
    }

    static string RemovePrefix(this string s, string p) => s.StartsWith(p, StringComparison.Ordinal) ? s[p.Length..] : s;

    public static HashSet<string> SinkHosts(JsonObject cfg) => [S(cfg, "telemetry_host").ToLowerInvariant(), S(cfg, "crash_host").ToLowerInvariant()];

    public static byte[] ResponseBody(JsonNode? content)
    {
        var text = (string?)content?["text"] ?? "";
        return (string?)content?["encoding"] == "base64" ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text);
    }

    /// <summary>urlsplit(url): (hostname lowercased or "", path, query).</summary>
    public static (string Host, string Path, string Query) SplitUrl(string url)
    {
        var rest = url;
        var m = Regex.Match(url, @"\A[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.ECMAScript);
        if (m.Success) rest = url[m.Length..];
        var host = "";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var end = rest.IndexOfAny(['/', '?', '#'], 2);
            var netloc = end < 0 ? rest[2..] : rest[2..end];
            rest = end < 0 ? "" : rest[end..];
            netloc = netloc[(netloc.LastIndexOf('@') + 1)..];
            host = netloc.StartsWith('[') ? netloc[1..Math.Max(netloc.IndexOf(']'), 1)] : netloc.Split(':')[0];
        }
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        var q = rest.IndexOf('?');
        return (host.ToLowerInvariant(), q < 0 ? rest : rest[..q], q < 0 ? "" : rest[(q + 1)..]);
    }

    /// <summary>Query plus form-body parameters that select a recorded reply (tokens and cache busters dropped), sorted by name.</summary>
    public static List<(string Name, List<string> Values)> RequestParams(string query, string body, string? ctype)
    {
        var p = Py.ParseQs(query, keepBlank: true);
        if ((ctype ?? "").Contains("x-www-form-urlencoded"))
            foreach (var (k, v) in Py.ParseQs(body, keepBlank: true))
            {
                if (!p.TryGetValue(k, out var l)) p[k] = l = [];
                l.AddRange(v);
            }
        return p.Where(kv => !VolatileParams.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value))
            .OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
    }

    public static List<(string Name, List<string> Values)> RequestParams(string query, byte[] body, string? ctype) =>
        RequestParams(query, (ctype ?? "").Contains("x-www-form-urlencoded") ? Encoding.UTF8.GetString(body) : "", ctype);

    /// <summary>Canonical text of sorted params: equal params give equal keys, like Python's tuple equality.</summary>
    public static string ParamsKey(IEnumerable<(string Name, List<string> Values)> p) =>
        Py.Dumps(new JsonArray(p.Select(t => (JsonNode)new JsonArray(JsonValue.Create(t.Name), new JsonArray(t.Values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()))).ToArray()));

    /// <summary>The newest 200 wins per key (plain and with params); plus the connect reply of each launched reserve.</summary>
    public static HarTable BuildTable(IEnumerable<string> harPaths)
    {
        var table = new HarTable();
        foreach (var p in harPaths)
        {
            string? reserve = null;  // reserve of the last Launch/* in this HAR (tutorial and join carry none)
            JsonArray entries;
            try { entries = (JsonArray)JsonNode.Parse(File.ReadAllText(p, new UTF8Encoding(false, true)))!["log"]!["entries"]! ?? throw new InvalidCastException("no log entries"); }
            catch (Exception e) when (e is IOException or JsonException or NullReferenceException or InvalidCastException or InvalidOperationException or ArgumentException or DecoderFallbackException or UnauthorizedAccessException)
            {
                Console.WriteLine($"skip {System.IO.Path.GetFileName(p)}: {e.Message}");
                continue;
            }
            foreach (var e in entries)
            {
                try
                {
                    var req = e!["request"]!;
                    var resp = e["response"]!;
                    var (host, path, query) = SplitUrl((string)req["url"]!);
                    var status = (int)Py.L(resp["status"]);
                    var tier = status == 200 ? 2 : status is 0 or 304 ? 0 : 1;
                    var method = ((string)req["method"]!).ToUpperInvariant();
                    var started = (string?)e["startedDateTime"] ?? "";
                    var post = req["postData"] as JsonObject;
                    var mime = (string?)post?["mimeType"];
                    var prms = RequestParams(query, (mime ?? "").Contains("x-www-form-urlencoded") ? (string?)post?["text"] ?? "" : "", mime);  // other bodies may hold lone surrogates
                    if (path.StartsWith("/v1/Launch/", StringComparison.Ordinal))
                        reserve = prms.FirstOrDefault(t => t.Name == "reserve").Values?[0];
                    else if (path == CONNECT && status == 200 && !string.IsNullOrEmpty(reserve))
                    {
                        if (!table.Connects.TryGetValue(reserve, out var old) || string.CompareOrdinal(old.Started, started) < 0)
                            table.Connects[reserve] = (started, ResponseBody(resp["content"]));
                    }
                    Hit? value = null;
                    var keys = new List<Key> { new(method, host, path, null) };
                    if (prms.Count > 0) keys.Add(new(method, host, path, ParamsKey(prms)));
                    foreach (var k in keys)
                    {
                        if (table.Entries.TryGetValue(k, out var old)
                            && (old.Tier != tier ? old.Tier > tier : string.CompareOrdinal(old.Started, started) >= 0))
                            continue;
                        value ??= new Hit(tier, started, status,
                            (resp["headers"] as JsonArray ?? []).Select(h => (HeaderText(h!["name"]), HeaderText(h["value"])))
                                .Where(h => !DropHeaders.Contains(h.Item1.ToLowerInvariant())).ToList(),
                            ResponseBody(resp["content"]));
                        table.Entries[k] = value;
                    }
                }
                catch (Exception x) when (x is NullReferenceException or InvalidCastException or InvalidOperationException or FormatException or ArgumentException)
                {
                    Console.WriteLine($"skip an entry of {System.IO.Path.GetFileName(p)}: {x.Message}");
                }
            }
        }
        return table;
    }

    // a lone surrogate escape (\udcff) in a recorded header: Python keeps it, System.Text.Json refuses; keep the escaped text
    static string HeaderText(JsonNode? n)
    {
        try { return (string)n!; }
        catch (InvalidOperationException) { return ((JsonValue)n!).GetValue<JsonElement>().GetRawText().Trim('"'); }
    }

    public static string? MirrorPath(string mirrorDir, string host, string path)
    {
        var p = Uri.UnescapeDataString(path);
        if (p.EndsWith('/') || p.Length == 0) p += "index.html";
        else if (!p[(p.LastIndexOf('/') + 1)..].Contains('.')) p += "/index.html";  // a folder-like path means its index page
        try
        {
            var root = System.IO.Path.GetFullPath(mirrorDir);
            var f = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, host, p.TrimStart('/')));
            return f.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(f) ? f : null;
        }
        catch (ArgumentException) { return null; }
    }

    static readonly Regex PictureUrl = new(@"\A/content/(animal_organs/\d{1,4}/(?:male|female|\d{1,3})\.png|lodge/(?:animals|skulls|weapons|slots|styles|layouts|lodges)/[a-z0-9][\w.-]{0,79})\z", RegexOptions.ECMAScript);

    /// <summary>The in-game app's trophy lodge editor page: the lodge editor frame (`client#lodge_edit`) loads `/static/app/<version>/webgui/index.html`, and that page loads `/dist/main.js`. Both are the app pages of the install's archives (`webguiv2/`).</summary>
    static readonly Regex AppPage = new(@"\A/(?:static/app/[^/]+/webgui/(index\.html)|dist/main\.js)\z", RegexOptions.ECMAScript);

    /// <summary>Bytes the client would load from the install's archives for this URL, or null. The client resolves
    /// static image and map URLs through the archives. exact: only files found through the relative path (a basename
    /// that exists in several directories, flags 1), so in the URL's directory; else only the single archived file
    /// with that basename (flags 0), which can sit in another directory and be a different picture.</summary>
    public static byte[]? ArchiveFile(JsonObject cfg, string host, string path, bool exact)
    {
        var url = host + path;
        var mapHost = S(cfg, "map_host").ToLowerInvariant();
        string name;
        if (AppPage.Match(path) is { Success: true } page)
        {
            if (host != S(cfg, "game_web_host").ToLowerInvariant()) return null;
            name = page.Groups[1].Success ? "webguiv2/index.html" : "webguiv2/dist/main.js";
        }
        else if (PictureUrl.Match(path) is { Success: true } o)  // the kill screen's organ pictures (only the species' own folder, never another species' picture of the same name) and the lodge pictures (the layouts' names are in no other folder: the one archived file of that name is that layout)
        {
            if (host != S(cfg, "game_web_host").ToLowerInvariant() || (!exact && !o.Groups[1].Value.StartsWith("lodge/layouts/", StringComparison.Ordinal))) return null;
            name = "webgui/static/img/" + o.Groups[1].Value;
        }
        else if (url.Contains("static/img")) name = "webgui/" + url[url.IndexOf("static/img", StringComparison.Ordinal)..];
        else if (url.Contains(mapHost)) name = "webgui/static/map" + url[(url.IndexOf(mapHost, StringComparison.Ordinal) + mapHost.Length)..];
        else if (url.Contains("webguiv2/img")) name = url[url.IndexOf("webguiv2/img", StringComparison.Ordinal)..];
        else return null;
        var idx = ArcList.ArchiveIndex(S(cfg, "install_dir"));
        var baseRec = idx.GetValueOrDefault(ArcList.NameHash(name));
        var hit = baseRec != null && (baseRec.Flags == 1) == exact ? ArcList.Find(name, idx) : null;
        return hit != null ? ArcList.ReadEntry(hit) : null;
    }

    public const string NoticeSubject = "UNOFFICIAL SERVER";
    public const string NoticeText = "This is an unofficial server. Your progress is kept on this server only, not on the official servers.";

    /// <summary>Prepend the server's notice to a replayed Splash/list_active list.</summary>
    public static byte[] SplashWithNotice(byte[] body)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(body); }
        catch (JsonException) { return body; }
        if (parsed is not JsonArray splashes) return body;
        var notice = splashes.Count > 0 ? (JsonObject)splashes[0]!.DeepClone() : new JsonObject { ["link"] = "", ["image_url"] = "" };  // keeps a replayable image_url/link
        long priority = 0;
        foreach (var s in splashes)
            if (s?["priority"] is { } pr) priority = Math.Max(priority, Py.L(pr));  // ponytail: recorded priorities are ints
        notice["id"] = 0;
        notice["subject"] = NoticeSubject;
        notice["linktext"] = "";
        notice["description"] = NoticeText;
        notice["priority"] = priority + 1;
        notice["start_ts"] = 0;
        notice["end_ts"] = 4102444800L;
        var all = new JsonArray(notice);
        foreach (var s in splashes) all.Add(s?.DeepClone());
        return Py.DumpBytes(all);
    }

    public static List<(string, string)> Cors(Func<string, string?> header)
    {
        static string Or(string? v, string d) => string.IsNullOrEmpty(v) ? d : v;
        return [
            ("Access-Control-Allow-Origin", Or(header("Origin"), "*")),
            ("Access-Control-Allow-Credentials", "true"),
            ("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS"),
            ("Access-Control-Allow-Headers", Or(header("Access-Control-Request-Headers"), "*")),
        ];
    }

    /// <summary>Player state on workDir/state.db. Each input is the operator's recording when there is one, else what the
    /// install and the facts file give (Install, Facts): the connect reply, the application catalogue, the locale
    /// labels, the texture names and the populations of every reserve. The avatar list and the player's skill list come from
    /// recordings only (without them: fresh skills). The recorded Application/discounts is not read: the store's sales are the operator's (`store_sales`). Null when there is neither a recorded connect nor a
    /// connect sample in the install.</summary>
    public static State? LoadState(JsonObject cfg, HarTable table, string workDir)
    {
        NewBuild.Alias(table, cfg);
        var e = table.Entries;
        var gsHost = S(cfg, "gameserver_host").ToLowerInvariant();
        var apiHost = S(cfg, "api_host").ToLowerInvariant();
        var staticHost = S(cfg, "static_host").ToLowerInvariant();
        var facts = Facts.Default;
        var item = Install.ItemDefinitions(S(cfg, "install_dir"));  // the item definitions the game keeps in its archives: the `parent` chain groups weapon variants into the family that shares one weapon skill
        var recorded = e.TryGetValue(new("POST", gsHost, CONNECT, null), out var gsConnect);
        var neutral = Install.Connect(cfg, facts, item);  // what every new account starts from, recordings or not
        var offline = (bool?)cfg["offline_mode"] ?? true;
        var connect = recorded ? gsConnect!.Body : neutral;
        if (connect is null) return null;
        if (recorded && !offline)  // a community server: a recorded connect still names the original's punch-through server, players need ours
        {
            var rc = JsonNode.Parse(connect)!.AsObject();
            if (rc["config"] is JsonObject conf) { conf["nat"] = new JsonObject { ["pt_server"] = Install.PtServer(cfg) }; connect = Encoding.UTF8.GetBytes(rc.ToJsonString()); }
        }
        if (recorded && neutral is null && !offline)
            throw new InvalidOperationException("a community server needs the install's connect sample: new accounts never start from a recorded connect");
        e.TryGetValue(new("POST", apiHost, APPLICATION, null), out var app);
        // the operator's rules of single objectives, beside the recorded catalogue: read only with that recording, whose missions it names
        var missionRules = app is not null && cfg["recordings_dir"] is JsonValue dir ? System.IO.Path.Combine((string)dir!, State.MissionRulesFile) : null;
        // the launcher's locale script: one call that wraps a JSON object
        byte[] Script(Func<string, bool> pick) =>
            e.FirstOrDefault(kv => kv.Key.Params is null && kv.Key.Method == "GET" && kv.Key.Host == staticHost && pick(kv.Key.Path)).Value?.Body ?? [];
        var script = Script(p => p.StartsWith(LOCALE_PREFIX, StringComparison.Ordinal));
        var locale = facts.Labels;
        if (script.Length > 0)
        {
            var a = Array.IndexOf(script, (byte)'(') + 1;
            var b = Array.LastIndexOf(script, (byte)')');
            if (b < 0) b = script.Length - 1;
            locale = (JsonObject)JsonNode.Parse(script.AsSpan(a, Math.Max(b - a, 0)))!;
        }
        var web = Script(p => p.EndsWith(WEB_SCRIPT_SUFFIX, StringComparison.Ordinal));
        var avatars = e.FirstOrDefault(kv => kv.Key.Params is null && kv.Key.Host == apiHost && string.Equals(kv.Key.Path, AVATARS, StringComparison.OrdinalIgnoreCase)).Value;
        var challenges = e.FirstOrDefault(kv => kv.Key.Host == apiHost && string.Equals(kv.Key.Path, CHALLENGES, StringComparison.OrdinalIgnoreCase)).Value;
        var sample = System.IO.Path.Combine(S(cfg, "install_dir"), "game", "json_inventory.json");  // item definitions update 10 sends
        var uid = ((JsonObject?)JsonNode.Parse(connect)!["account"])?["user_id"];
        e.TryGetValue(new("POST", apiHost, SKILL_LIST, ParamsKey([("user_id", [Py.Str(uid)])])), out var skillList);
        // populations and permitted ammo per reserve: the facts file's, a recorded connect of the reserve wins
        var ethical = JsonNode.Parse(connect)!["ethical"] as JsonArray ?? [];
        var reserves = new Dictionary<int, byte[]>();
        foreach (var r in facts.Reserves)
            if (facts.ReserveConnect(r, ethical) is { } body) reserves[r] = body;
        foreach (var (r, rec) in table.Connects)
            reserves[int.Parse(r)] = rec.Body;
        var state = new State(System.IO.Path.Combine(workDir, "state.db"), connect, $"https://{S(cfg, "game_web_host")}",
            $"https://{S(cfg, "game_web_host")}{ProtocolLiterals.IconPath}",
            app != null ? (JsonObject)JsonNode.Parse(app.Body)! : Install.Application(S(cfg, "install_dir"), facts, item), locale,
            web.Length > 0 ? ScorePage.TextureNames(Encoding.UTF8.GetString(web)) : facts.Textures,
            File.Exists(sample) ? (JsonArray)JsonNode.Parse(File.ReadAllText(sample, Encoding.UTF8))!["infos"]!.DeepClone() : [],
            reserves,
            skillList != null ? JsonNode.Parse(skillList.Body) : null,
            item,
            offline: offline,  // a request no token resolves is the local account's; false on a community server
            startBody: recorded ? neutral : null,
            avatars: avatars is not null ? JsonNode.Parse(avatars.Body) as JsonArray : null,
            challenges: challenges is not null ? JsonNode.Parse(challenges.Body) as JsonArray : null,
            seasonal: SeasonalEvent.Parse(cfg["event"]), missionRules: File.Exists(missionRules) ? State.ReadMissionRules(missionRules) : null,
            spawnNamedAnimals: (bool?)cfg["spawn_named_animals"] ?? false)
        {
            CommunityDivisor = cfg["event_community_divisor"] is { } divisor ? (int)Py.L(divisor) : 20, AllLicenses = (bool?)cfg["all_licenses"] ?? false,
            UnlimitedAmmo = (bool?)cfg["unlimited_ammo"] ?? false, UnlimitedFuel = (bool?)cfg["unlimited_fuel"] ?? false, InventoryCapacity = cfg["default_inventory_capacity"] is { } capacity ? Py.L(capacity) : State.DefaultInventoryCapacity,
            Collectables = Install.CollectableDefinitions(S(cfg, "install_dir")), AvatarUploads = (string?)cfg["avatar_uploads"],
            PlaytimeSince = cfg["playtime_since"] is { } since ? Py.L(since) : State.DefaultPlaytimeSince
        };
        state.LoadHarvestGmBonuses(cfg["harvest_gm_bonus"]);
        foreach (var line in state.LoadStoreSales(cfg["store_sales"])) Console.WriteLine(line);
        state.MigratePhotoNames();  // every server: a photo stored under its old, guessable name gets its token name
        if (!offline) { state.MigrateAvatars(); state.MigrateAvatarGenders(); state.MigrateListedAvatarUrls(); }  // a community server: the pictures its accounts imported before the avatars were served
        return state;
    }

    public static List<string> BodyKeys(string ctype, byte[] body)
    {
        if (ctype.Contains("x-www-form-urlencoded"))
            return Py.ParseQs(Encoding.UTF8.GetString(body)).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        try { return (JsonNode.Parse(body) as JsonObject)?.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList() ?? []; }
        catch (JsonException) { return []; }
    }

    public static List<(string, string)> Json() => [("Content-Type", "application/json")];

    public static List<(string, string)> JsonAnyOrigin() => [("Content-Type", "application/json"), ("Access-Control-Allow-Origin", "*")];

    public static bool Has(byte[]? b) => b is { Length: > 0 };

    static string ContentType(string name) => Mime.TryGetContentType(name, out var ct) ? ct : "application/octet-stream";

    /// <summary>The in-game page's Teams tab reads the round timer as a number, but the game answers a record
    /// `{time, running}`; the page then recurses until it fails. Served here, the page's script accepts both forms
    /// (the record's `time`). It also removes what the old site's page sent to third-party services and loaded without need (issue #829): the page's tag manager and analytics ids are emptied, the script's error-reporting init never runs and its 2 s preload of 61 pictures is dropped. Each script rewrite is an exact text replacement; when a text is not found exactly once that rewrite is skipped and one warning is logged. The page's ids are matched by key instead (a page without them is served unchanged, with no warning). The same class adds the lodge page's hide label to a locale script that lacks it (<see cref="AddHide"/>). The script is also served with `Cache-Control: no-cache`, and the page of `/client`
    /// asks for it under a new `?t=` token, so a copy the game cached before is not used. The page also paints less (issue #829: on a weak PC the page is drawn on the CPU and stuttered on hover, click and scroll): the lodge's items (about 200, each with a large picture) are not painted while scrolled out of view (<see cref="PaintFix"/>).</summary>
    static class TimerFix
    {
        static readonly Regex Script = new(@"\A/static/app/[^/]+/client\.js\z", RegexOptions.ECMAScript);
        static readonly Regex Token = new(@"(/client\.js)(\?t=[\w.-]*)?", RegexOptions.ECMAScript);
        public const string Version = "timer5";
        static readonly Regex TrackerIds = new(@"(""google_(?:tag_manager|analytics)_id""\s*:\s*)""[^""]*""", RegexOptions.ECMAScript);
        // the script's 2 s picture preload and its error-reporting init (skipped on localhost only)
        public static readonly (string From, string To)[] Trackers =
            [("setTimeout(function(){a.load(\"/content/client-preload-images.json\")},2e3),", ""),
             ("\"localhost\"!==window.location.hostname&&e.init({dsn:", "false&&e.init({dsn:")];
        // the two places the script fills the Teams model's timer: the first read of the page and the refresh
        public static readonly (string From, string To)[] Reads =
            [("timer:e[4]", "timer:function(t){return t&&typeof t==\"object\"?t.time:t}(e[4])"),
             ("timer:e[2]", "timer:function(t){return t&&typeof t==\"object\"?t.time:t}(e[2])")];
        /// <summary>The in-game missions panel's writes (Activate, Activate All, the favourite toggle, the automatic-activation and filter switches) call the page's API helper with no token and no auth mode, so it sends none and the
        /// server answers token expired (the list call passes `oauth_access_token`, so it loaded). Each of the six calls (empty options, no token of its own) is given the helper's own `authMode:"required"` option: the helper then waits
        /// for the game's token and sends it as `Authorization: Bearer`, which the server already accepts.</summary>
        static readonly Regex MissionWrite = new(@"\br\.v1\(""POST"",""/v1/mission/(activate|activate_all_available|update_favourite|update_automatic_activation|update_activate_only_favourite|update_hide_completed_favourite)"",(\{(?:(?!oauth_access_token)[^{}])*\}),\{\}\)", RegexOptions.ECMAScript);
        /// <summary>The missions panel opens a mission pack (`toggleSet`), a mission (`toggleDetails`) and, when the route names a pack id, that pack (`l.find(".set-missions").toggle()`) by showing a hidden list, and changes nothing on the row, so the arrow in front of it (`icon-caret-right`) never turned. Each now also toggles the class `hr-open` on its row, which <see cref="ClientStyle"/> turns the arrow by. The Packs, Active and Available headlines already swap their own caret class.</summary>
        static readonly (string From, string To)[] Rows =
            [("toggleSet:function(){c(\".set-missions\",this.$el).toggle()}", "toggleSet:function(){c(\".set-missions\",this.$el).toggle(),c(\".set-row\",this.$el).first().toggleClass(\"hr-open\")}"),
             ("toggleDetails:function(){c(\".mission-details\",this.$el).toggle()}", "toggleDetails:function(){c(\".mission-details\",this.$el).toggle(),c(\".mission-row\",this.$el).first().toggleClass(\"hr-open\")}"),
             ("l.find(\".set-missions\").toggle()", "l.find(\".set-missions\").toggle(),l.find(\".set-row\").toggleClass(\"hr-open\")")];
        /// <summary>The confirm-kill page enables its Taxidermize button for an ethical score (`score >= 0`, the game's `ethical`). A harvest that counts for the member who shot the animal is not the harvester's to taxidermize (#1042),
        /// and nothing of what the game hands the page tells it apart, so the page asks the server (`GET /v1/harvest/screen` with the player's token, <see cref="State.HarvestScreenOwn"/>): the button stays disabled until the answer,
        /// and is enabled unless the answer is `taxidermy: false` (a failed call enables it, as before; the purchase itself is refused for a harvest that is not the player's). Matched by pattern (the script's minified names differ
        /// between versions); not found exactly once: skipped, one warning.</summary>
        static readonly Regex Taxidermy = new(@"(\w)\.ethical\?(\w)\(!0,\1\):\2\(!1\)", RegexOptions.ECMAScript);
        const string AskTaxidermy = "$1.ethical?($2(!1),Promise.resolve(" + ProtocolLiterals.PageBridge + ".getAccessToken()).then(function(t){return fetch(\"/v1/harvest/screen\",{headers:{Authorization:\"Bearer \"+t},cache:\"no-store\"})})"
            + ".then(function(r){return r.json()}).then(function(x){x&&x.taxidermy===false||$2(!0,$1)},function(){$2(!0,$1)})):$2(!1)";
        static int warnedRows, warnedTrackers, warned, warnedApp, warnedFrame, warnedPage, warnedTaxidermy;

        /// <summary>The trophy lodge editor builds its server addresses from the game's built-in domain (`api.`, `apiv2.` and `static.` names the player's PC does not send here); rewritten, each is this host's own origin, where `/v1/...` and `/static/...` are answered. Its frame page replaces the script tag by <see cref="FrameScript"/>, which loads the script under a new `?t=` token once the game's bridge is ready for it.</summary>
        static readonly Regex AppScript = new(@"\A/dist/main\.js\z", RegexOptions.ECMAScript), AppFrame = new(@"\A/static/app/[^/]+/webgui/index\.html\z", RegexOptions.ECMAScript);
        public static readonly (string From, string To)[] AppUrls =
            [("'https://api.' + getObj().getServer() + '/v1'", "window.location.origin + '/v1'"),
             ("'https://apiv2.' + getObj().getServer()", "window.location.origin + '/v1'"),
             ("'https://static.' + getObj().getServer()", "window.location.origin")];
        /// <summary>The picture addresses of the lodge editor and of the item pictures the script builds: the game answers `/static/img/...` from its archives alone, so they are asked for under `/content/` instead. The one on a fixed host (the gun racks' icons) has no address builder.</summary>
        static readonly Regex FixedPictureHost = new(@"'https://static\.[\w.-]+/static/img/(?=lodge/)", RegexOptions.ECMAScript), PictureDir = new(@"/static/img/(?=lodge/|items/)", RegexOptions.ECMAScript);
        /// <summary>The dragged picture (a `position: fixed` element) is placed at the mouse's position divided by 0.9, the zoom the editor's own style puts on its container; <see cref="FrameScript"/> sets that zoom to 1, so the picture would show 11% further right and down than the mouse. The division is dropped (the mouse's position as it is: the fixed element is not scaled by any zoom, the map's own being cancelled for it by the frame style). The drop is not affected: it reads the mouse and the targets' rectangles.</summary>
        static readonly Regex DragGhost = new(@"(offset\.[xy]) / 0\.9\b", RegexOptions.ECMAScript);
        // The page's reads of the lodge lists carry no token, but the routes here need the player's (an unlisted account's own lodges included), and the page calls the game's
        // functions (`window.hunter`) as if each answered at once, while in this build each answers with a promise (the locale then fails on `.split`, a label on `.replace`, and the page stays empty).
        // Before the script runs, the frame page therefore: wraps `fetch` so each request to this origin that has no token gets the game's own (asked at each call; a missing or failing bridge only skips it);
        // when `getLocale()` answers with a promise, waits for the answers the page reads at once (`getServer`, `getActiveLodgeId`, `getHostPlayerGuid`, `getLocalPlayerGuid`, `isStaff`, `isSinglePlayer`,
        // `getInventory`, `getLocale`; one that fails is not kept) and for the locale script of that language, then replaces `window.hunter` by a proxy of the game's object whose kept answers are returned at once,
        // whose `getLocalizedString` reads the locale script (the label key without its brackets, the key itself when unknown, as the game does) and whose other members pass on to the game (functions bound to
        // it, returning its promise; `getAuthToken` too: the page wraps it in a promise); only then, or after 8 seconds, is the script added. A build whose `getLocale()` answers at once gets the script as it was.
        // The game's page asks the frame to go to the editor's route (`setURL`) when the frame has loaded, which can be before the script is added: `setURL` is therefore defined at once, keeps the route, and the script's own is called with it when the script has loaded.
        // ponytail: the inventory is read once, when the page loads; the editor's later refreshes of it show that list.
        // The page's other calls (`getSelectedPlatformId`, the mounted and unmounted trophy deltas, `getWeaponPlatformTypeID`, `selectTrophyMount`, `selectWeaponMount`: the trophy and weapon menus' own routes;
        // `getLoggedInUserID`, `confirmBuy`: only when the page sits in the website) are not on the game's object and not made up here.
        // The editor's own style zooms its container to 0.9, and in this build the tooltips (`position: fixed`, placed from the icon's measured position) then land scaled on the icon they belong to: it loses the pointer and the tooltip closes at once. The zoom is cancelled for the three containers that have it (the editor's and the two menus'), under the names of both scripts the server may send (the mirror's and the install's); a name the script does not have changes nothing; the container is sized in screen height, so it still fits.
        // The page's body gets the dark background the game's other pages have (their colour and gradient, without the texture picture); the editor's page layout draws its own full-window container in `#000` over the body (one class name in the mirror's script, another in the install's), so that container's background is removed, under both names and with the body in the selector so it wins over the script's own style, which is added later.
        // The panel (the layout's box, 762 px wide in the script's own style, 962 px for one layout, with its own height) fills the window instead: its container keeps a margin of 2% of the smaller side, the panel is the window's width up to 80rem and at most its height (it scrolls inside when a very small window still does not hold it), the editor's container takes the height of its content, the left column floats and the right one (the map) takes the width that is left, and the lists' and the style picture's heights follow the window (`--l`, `--h`, set by the script below). The map is the lodge's picture with the slots placed on it in pixels, so it cannot be resized by its own style: the second script gives its container `zoom` so it fits the width of the right column and the height that is left under it (no bigger than 1.6 times), at load, on resize, whenever the editor draws something and whenever the panel or the column changes size (a font or a picture that arrives late); the tooltips and the drag picture, `position: fixed` inside that container, get the inverse zoom so they stay where the icon's measured position puts them. These selectors are the names of both scripts (the mirror's and the install's), as above.
        // The panel is shared by the other in-game pages (lodge selection, trophy and weapon menus, bait sites), whose own boxes keep their sizes, so only the panel that holds the editor's container gets the class `fit` (set by the second script) and with it the window-sized rules. ponytail: the script's 120 px floor for a list's height and its 4 px margin are fixed amounts; a rem-based floor if a window ever gets that small.
        // The in-game page (`/client`) holds the frame in `#main`, which is as high as the body, below the tab bar, so its lower end was cut off: <see cref="ClientStyle"/> makes the body a column of the tab bar and `#main`.
        const string FrameScript = """
<style>._2nkM,._2m_7,._1bAk,.XHcy,._3Y-A,._35mG{zoom:1}html,body{min-height:100%}body{background:#050505 linear-gradient(rgba(40,41,43,.65),rgba(7,5,12,.3))}body .OjlL,body ._15Yq{background:none;box-sizing:border-box;padding:2vmin}body .TfqS.fit,body ._1P0L.fit{box-sizing:border-box;width:100%;max-width:80rem;max-height:100%;overflow:auto}body .TfqS ._2nkM,body ._1P0L .XHcy{height:auto}body .TfqS ._3xhJ,body ._1P0L .Ati2{float:left}body .TfqS ._1fOv,body ._1P0L ._1K2e{display:block;overflow:hidden;width:auto;text-align:center}body .TfqS ._1nmh,body ._1P0L ._3JtD{height:auto;max-height:var(--l,60vh)}body ._3RH1,body ._2Lkx{width:auto;max-width:100%;max-height:var(--h,60vh);margin:0 auto}body ._2JVh,body ._3eN0{text-align:start}body ._2JVh ._2Ubp,body ._2JVh .w3T7,body ._3eN0 ._30FN,body ._3eN0 ._1UXP{zoom:calc(1/var(--z,1))}</style><script>(function(){var n=window.hunter,f=window.fetch,g=0,u,z=function(x){u=x;};window.setURL=z;
function go(){if(g)return;g=1;var s=document.createElement("script");s.src="/dist/main.js?t=urls2";s.onload=function(){if(u!==undefined&&window.setURL!==z)window.setURL(u);};document.body.appendChild(s);}
if(f)window.fetch=function(r,o){var s=this,u;try{u=typeof r==="string"?r:r.url;}catch(e){}
var d=function(){return f.call(s,r,o);};if(typeof u!=="string"||!(u.indexOf(location.origin+"/")===0||(u.charAt(0)==="/"&&u.charAt(1)!=="/")))return d();
return Promise.resolve().then(function(){return window.hunter.getAuthToken();}).then(function(t){try{if(t){if(typeof r==="string"){o=o||{};var h=new Headers(o.headers||{});if(!h.has("Authorization"))h.set("Authorization","Bearer "+t);o.headers=h;}else if(!r.headers.has("Authorization"))r.headers.set("Authorization","Bearer "+t);}}catch(e){}return d();},d);};
var v;try{v=n.getLocale();}catch(e){}if(!v||typeof v.then!=="function")return go();
var keep=["getServer","getActiveLodgeId","getHostPlayerGuid","getLocalPlayerGuid","isStaff","isSinglePlayer","getInventory"],c={},S={},H=Object.prototype.hasOwnProperty;
function w(){window.hunter=new Proxy(Object.create(n),{get:function(t,k){if(k==="getLocalizedString")return function(l){var s=String(l).slice(1,-1);return H.call(S,s)?S[s]:l;};if(H.call(c,k))return function(){return c[k];};var x=n[k];return typeof x==="function"?x.bind(n):x;}});}
setTimeout(function(){w();go();},8000);
Promise.all(keep.map(function(k){return Promise.resolve().then(function(){return n[k]();}).then(function(x){c[k]=x;},function(){});}).concat(v.then(function(l){c.getLocale=l;return new Promise(function(ok){window.hunterSetLocale=function(o){S=o;};var s=document.createElement("script");s.src=location.pathname.replace(/webgui\/[^\/]*$/,"locale/")+encodeURIComponent(l)+".js";s.onload=s.onerror=ok;document.head.appendChild(s);});}).then(null,function(){}))).then(function(){
w();}).then(go,go);})();</script><script>(function(){var q=0,cs=getComputedStyle,n=function(e,k){return parseFloat(cs(e)[k])||0},f=function(){q=0;var p=document.querySelector(".TfqS,._1P0L");if(!p||!p.querySelector("._2nkM,.XHcy"))return;p.classList.add("fit");var b=n(p,"paddingBottom")+n(p.parentNode,"paddingBottom")+4,a=function(e){return Math.max(120,innerHeight-e.getBoundingClientRect().top-p.scrollTop-b)},r=p.querySelector("._1fOv,._1K2e"),l=p.querySelector("._1nmh,._3JtD"),i=p.querySelector('img[src*="lodge/layouts/"]'),m=i&&i.parentNode;if(l)p.style.setProperty("--l",a(l)+"px");if(!r)return;if(ro){ro.observe(p);ro.observe(r);}p.style.setProperty("--h",a(r)+"px");if(!m)return;m.style.zoom=1;var nh=m.offsetHeight,mw=m.offsetWidth+n(m,"marginLeft");if(!nh||!mw)return;var s=Math.max(.3,Math.min(m.parentNode.clientWidth/mw,(a(r)-r.offsetHeight+nh)/nh,1.6));m.style.zoom=s;m.style.setProperty("--z",s);},g=function(){q||(q=requestAnimationFrame(f));},ro=window.ResizeObserver&&new ResizeObserver(g);addEventListener("resize",g);addEventListener("load",g,true);new MutationObserver(g).observe(document,{childList:true,subtree:true});g();})();</script>
""";
        const string AppScriptTag = "<script src=\"/dist/main.js\"></script>";
        static readonly (string From, string To)[] AppFrameSrc = [(AppScriptTag, FrameScript)];
        /// <summary>The in-game page's missions and lodge editor pages, whose boxes are fixed in the page's own style (the missions page's 940 px wide, 800 px high with its own scrollbar and an empty rest under it; the editor's frame cut off below, see <see cref="FrameScript"/>): the body is a column of the tab bar and `#main`, which takes the rest of the height, and the missions page's box takes the width up to 75rem and all the height, its list scrolling inside it. The filters' labels (half the width of their row in the page's own style, so a control only wrapped under its label while the box was narrow) are always above their control, except the checkbox's. The rules name the body's id (set by the page's script) and are as specific as the page's own, so they win over it, and no other page is touched. The arrow before a mission pack or a mission (`.set-row`, `.mission-row`) turns 90° while its row has the class `hr-open`, which the rewrite of the page's script (<see cref="Rows"/>) sets as the row opens.</summary>
        const string ClientStyle = "<style>html body#lodge_edit,html body#client_missions_page{display:flex;flex-direction:column}html body#lodge_edit .tab-bar,html body#client_missions_page .tab-bar{flex:none}html body#lodge_edit #main,html body#client_missions_page #main{flex:1 1 0;min-height:0;height:auto}html body#client_missions_page #main #page_container{box-sizing:border-box;width:100%;height:100%}html body#client_missions_page #main #page_body{box-sizing:border-box;width:100%;max-width:75rem;height:100%;margin:0 auto;display:flex;flex-direction:column}html body#client_missions_page #main #page_body #ajax_content{flex:1 1 0;min-height:0;max-height:none}html body#client_missions_page .mission-filters-form fieldset label:not([for=\"hide-completed\"]){display:block;width:auto}html body#client_missions_page .set-row.hr-open .state-icon-container i,html body#client_missions_page .mission-row.hr-open .state-icon-container i{display:inline-block;transform:rotate(90deg)}</style></head>";
        /// <summary>After capturing a key the game calls `game.updateKeybinding(...)` or `game.overrideKeybinding(...)` on the in-game page, which only defines them on `window.ewcallback` (set later by the page's own script), so
        /// the options page never saw the key and never saved. The script defines both on `window.game` (an existing `game` and any member already set is kept) and forwards every argument to `window.ewcallback` when the call is made.</summary>
        const string KeybindingScript = "<script>(function(){var g=window.game||(window.game={});['updateKeybinding','overrideKeybinding'].forEach(function(n){if(g[n]===undefined)g[n]=function(){var c=window.ewcallback;if(c&&typeof c[n]==='function')return c[n].apply(c,arguments)}})})()</script>";
        /// <summary>The body with each text replaced; null (and one warning) when a text is not found exactly once.</summary>
        static string? ReplaceOnce(string text, (string From, string To)[] edits, ref int warnedFlag, string what)
        {
            foreach (var (from, to) in edits)
            {
                if (text.IndexOf(from, StringComparison.Ordinal) is var at && (at < 0 || text.IndexOf(from, at + 1, StringComparison.Ordinal) >= 0))
                {
                    if (Interlocked.Exchange(ref warnedFlag, 1) == 0)
                        Console.Error.WriteLine($"warning: {what} was not found; that rewrite is skipped");
                    return null;
                }
                text = text.Replace(from, to, StringComparison.Ordinal);
            }
            return text;
        }

        /// <summary>Less to paint on the in-game page (issue #829): `content-visibility:auto` on the lodge's items (`#lodge-main-view .inventory-item`) skips painting the ones scrolled out of view. The rule is on the item itself, not on its tile: the page empties and refills the view's container, so the tiles end up under another element and are smaller than their content, but the item has its own size, and the hover info and the drag picture are clones appended to `body`, so the rule's clipping does not reach them.</summary>
        const string PaintFix = "<style>#lodge-main-view .inventory-item{content-visibility:auto}</style>";
        static readonly (string From, string To)[] ClientPageSrc = [("</head>", KeybindingScript + PaintFix + ClientStyle)];
        public static Reply Apply(Req r, Reply reply)
        {
            if (reply.Status != 200 || r.Method != "GET") return reply;
            if (r.Path == "/client")
            {
                var was = Encoding.Latin1.GetString(reply.Body);
                var page = TrackerIds.Replace(Token.Replace(was, "$1?t=" + Version), "$1\"\"");
                if (ReplaceOnce(page, ClientPageSrc, ref warnedPage, "the in-game page's head") is { } styled) page = styled;
                return page == was ? reply : NoCache(reply, Encoding.Latin1.GetBytes(page));
            }
            if (Locale.IsMatch(r.Path)) return AddHide(reply);  // no-cache too, so a copy cached before is not used
            // Latin1 keeps every byte of the rest of a file as it was
            if (AppScript.IsMatch(r.Path) && ReplaceOnce(Encoding.Latin1.GetString(reply.Body), AppUrls, ref warnedApp, "the lodge editor's server addresses") is { } app)
                return NoCache(reply, Encoding.Latin1.GetBytes(DragGhost.Replace(PictureDir.Replace(FixedPictureHost.Replace(app, "'/content/"), "/content/"), "$1")));
            if (AppFrame.IsMatch(r.Path) && ReplaceOnce(Encoding.Latin1.GetString(reply.Body), AppFrameSrc, ref warnedFrame, "the lodge editor's script tag") is { } frame)
                return NoCache(reply, Encoding.Latin1.GetBytes(frame));
            if (!Script.IsMatch(r.Path)) return reply;
            var src = Encoding.Latin1.GetString(reply.Body);
            var t = ReplaceOnce(src, Trackers, ref warnedTrackers, "the in-game page's picture preload or error reporting") ?? src;
            var rows = ReplaceOnce(t, Rows, ref warnedRows, "the in-game missions panel's pack and mission rows") ?? t;
            var script = MissionWrite.Replace(ReplaceOnce(rows, Reads, ref warned, "the in-game page's round timer read") ?? rows, "r.v1(\"POST\",\"/v1/mission/$1\",$2,{authMode:\"required\"})");
            if (Taxidermy.Matches(script).Count == 1)
                script = Taxidermy.Replace(script, AskTaxidermy);
            else if (Interlocked.Exchange(ref warnedTaxidermy, 1) == 0)
                Console.Error.WriteLine("warning: the confirm-kill page's Taxidermize button was not found; that rewrite is skipped");
            return script == src ? reply : NoCache(reply, Encoding.Latin1.GetBytes(script));
        }

        static readonly Regex Locale = new(@"\A/static/app/[^/]+/locale/[a-z_]+\.js\z", RegexOptions.ECMAScript);
        const string HideKey = "\"hun_web_gui_hide\"", Opening = "hunterSetLocale({";

        /// <summary>The lodge selection page labels its hide button with a key some locale scripts lack (it then shows the key itself): add it, in English, to a script that wraps its labels the usual way and does not have it; else serve the script unchanged.</summary>
        static Reply AddHide(Reply reply)
        {
            var text = Encoding.Latin1.GetString(reply.Body);
            return text.StartsWith(Opening, StringComparison.Ordinal) && !text.Contains(HideKey, StringComparison.Ordinal)
                ? NoCache(reply, Encoding.Latin1.GetBytes(Opening + "\n" + HideKey + ": \"Hide\"," + text[Opening.Length..])) : reply;
        }

        static Reply NoCache(Reply reply, byte[] body) =>
            new(reply.Status, [.. reply.Headers.Where(h => !h.Name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase)), ("Cache-Control", "no-cache")], body, "patch");
    }

    /// <summary>The game hosts, the recorded replies, the player state and the replay every game request falls back
    /// to. GameApi's endpoints answer what the player state owns and hand everything else to Fallback.</summary>
    public sealed class Game
    {
        public readonly HarTable Table;
        public readonly JsonObject Cfg;
        public readonly State? State;
        public readonly string MirrorDir;
        /// <summary>Web: our website's domain (sign-in, account pages). GameWeb: the game's own web host, where all of the current build's traffic arrives.</summary>
        public readonly string Gs, Apiv2, Api, Web, GameWeb, Signup;
        public readonly HashSet<string> Sinks;
        /// <summary>The crash host (one of the sinks): its reports are kept per account (<see cref="CrashReports"/>), not in telemetry.jsonl.</summary>
        public readonly string CrashHost;
        /// <summary>The game's credentials; null without player state.</summary>
        public readonly GameTokens? Tokens;

        public Game(HarTable table, JsonObject cfg, State? state, string mirrorDir)
        {
            (Table, Cfg, State, MirrorDir) = (table, cfg, state, mirrorDir);
            Gs = S(cfg, "gameserver_host").ToLowerInvariant();
            Apiv2 = S(cfg, "apiv2_host").ToLowerInvariant();
            Api = S(cfg, "api_host").ToLowerInvariant();
            Web = S(cfg, "web_host").ToLowerInvariant();
            GameWeb = S(cfg, "game_web_host").ToLowerInvariant();
            Sinks = SinkHosts(cfg);
            CrashHost = S(cfg, "crash_host").ToLowerInvariant();
            Signup = S(cfg, "signup_host").ToLowerInvariant();
            Tokens = state is null ? null : new GameTokens(state.Store, S(cfg, "game_web_host"));
        }

        /// <summary>The API paths of the recordings by lower-case name, as recorded (the recordings are keyed by the raw path).</summary>
        public Dictionary<string, string> RecordedApiPaths => recordedApiPaths ??= Table.Entries.Keys.Where(k => k.Host == Api && k.Path.StartsWith("/v1/", StringComparison.Ordinal))
            .Select(k => k.Path).Distinct().GroupBy(p => p, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string>? recordedApiPaths;

        /// <summary>The recorded reply for this request: the one recorded with the same query/form params when it is
        /// usable (tier ≥ 1), else the newest one for method, host and path.</summary>
        public Hit? Recorded(Req r)
        {
            Table.Entries.TryGetValue(new(r.Method, r.Host, r.Path, ParamsKey(RequestParams(r.Query, r.Body, r.Header("Content-Type")))), out var hit);
            if (hit is null or { Tier: < 1 }) Table.Entries.TryGetValue(new(r.Method, r.Host, r.Path, null), out hit);
            return hit;
        }

        /// <summary>The reply a state handler builds on: the recorded one on a local server; on a community server the empty
        /// reply of the endpoint's shape (<see cref="ReplayScope"/>), never a recording. Null: nothing usable.</summary>
        public Hit? Base(Req r)
        {
            if (State is not { Online: true })
                return Recorded(r) is { Tier: >= 1 } hit ? hit : null;
            return ReplayScope.Find(this, r) is { Global: false } rule ? new Hit(2, "", rule.Status, JsonAnyOrigin(), Encoding.UTF8.GetBytes(rule.Empty!)) : null;
        }

        /// <summary>The kill screen's organ pictures and the lodge pictures (the lodge editor's own too), when the archive lacks one: the static host's folder of the mirror.</summary>
        string? PictureMirror(Req r) => r.Host == GameWeb && PictureUrl.Match(r.Path) is { Success: true } m
            ? MirrorPath(MirrorDir, S(Cfg, "static_host").ToLowerInvariant(), "/static/img/" + m.Groups[1].Value) : null;

        static readonly Regex TileUrl = new(@"\A/content/map/(v7/tiles/([1-5])/(\d{1,4})/(\d{1,4})\.jpg)\z", RegexOptions.ECMAScript);

        /// <summary>The game web host asks for map tiles under /content/map/; its own folder holds only some, so a tile inside the zoom's grid (2^z per side) is read from the map host's folder.</summary>
        string? MapTile(Req r)
        {
            if (r.Host != GameWeb || TileUrl.Match(r.Path) is not { Success: true } m) return null;
            var side = 1 << int.Parse(m.Groups[2].Value);
            return int.Parse(m.Groups[3].Value) < side && int.Parse(m.Groups[4].Value) < side
                ? MirrorPath(MirrorDir, S(Cfg, "map_host").ToLowerInvariant(), "/" + m.Groups[1].Value) : null;
        }

        static readonly Regex WebAvatar = new(@"\A/media/avatar/256/([1-9]\d{0,17})\.png\z", RegexOptions.ECMAScript);
        static readonly Regex HostAvatar = new(@"\A/256/([1-9]\d{0,17})\.png\z", RegexOptions.ECMAScript);

        /// <summary>The picture of one of the game's avatars, `/media/avatar/256/&lt;id&gt;.png` on the game web host and `/256/&lt;id&gt;.png` on the avatar host (the avatar list's own address):
        /// the operator's `avatar-&lt;id&gt;.png` in `images_dir`, else the stock copy `avatars/256/&lt;id&gt;.png` there, else the default avatar's. Only an id of the list (or the notice sender's, <see cref="State.SenderAvatar"/>) is read, so the path never leaves the folder; null for anything else, or no picture at all.</summary>
        string? AvatarPicture(Req r)
        {
            if (State is null || Cfg["images_dir"]?.GetValue<string>() is not { } dir) return null;
            var m = r.Host == GameWeb ? WebAvatar.Match(r.Path) : r.Host == S(Cfg, "avatar_host").ToLowerInvariant() ? HostAvatar.Match(r.Path) : null;
            if (m is not { Success: true }) return null;
            var num = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (num != State.SenderAvatar && !State.AvatarIds.Contains(num)) return null;
            string Own(string id) => System.IO.Path.Combine(dir, $"avatar-{id}.png");
            string Stock(string id) => System.IO.Path.Combine(dir, "avatars", "256", $"{id}.png");
            var fallback = State.DefaultAvatar.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new[] { Own(m.Groups[1].Value), Stock(m.Groups[1].Value), Own(fallback), Stock(fallback) }.FirstOrDefault(File.Exists);
        }

        /// <summary>Everything the player state does not answer, in order: archive file pinned to the URL's directory,
        /// recorded reply (JSONP callback swapped), mirror, archive file found by basename, an empty 200 for a request
        /// only recorded as 304/0 ("weak"), else a miss. A community server replays a recording only for a global endpoint
        /// (<see cref="ReplayScope"/>); a per-player one gets its empty reply, an unlisted one the miss.</summary>
        public Reply Fallback(Req r) => TimerFix.Apply(r, NoStoreMiss(Served(r)));

        /// <summary>A miss may be answered later (a picture the server learns to serve), so the client must not keep it in its disk cache.</summary>
        static Reply NoStoreMiss(Reply reply) =>
            reply.Source == "miss" ? reply with { Headers = [.. reply.Headers, ("Cache-Control", "no-store")] } : reply;

        Reply Served(Req r)
        {
            var get = r.Method is "GET" or "HEAD";
            var data = get ? ArchiveFile(Cfg, r.Host, r.Path, exact: true) : null;
            if (data != null) return new(200, [("Content-Type", ContentType(r.Path))], data, "archive");
            var denied = ReplayScope.Denied(this, r);
            var hit = denied ? null : Recorded(r);
            if (hit is { Tier: >= 1 })
            {
                var bodyOut = hit.Body;
                var cb = Py.ParseQs(r.Query).TryGetValue("callback", out var cbs) ? cbs[0] : "";
                if (cb.Length > 0 && Callback.IsMatch(cb))
                    bodyOut = Encoding.Latin1.GetBytes(Jsonp.Replace(Encoding.Latin1.GetString(bodyOut), _ => cb + "(", 1));
                return new(hit.Status, hit.Headers, bodyOut, "har");
            }
            if (get)
            {
                if (AvatarPicture(r) is { } avatar) return new(200, [("Content-Type", "image/png")], File.ReadAllBytes(avatar), "image");
                var f = MirrorPath(MirrorDir, r.Host, r.Path) ?? MapTile(r) ?? PictureMirror(r);
                if (f != null) return new(200, [("Content-Type", ContentType(System.IO.Path.GetFileName(f)))], File.ReadAllBytes(f), "mirror");
                data = ArchiveFile(Cfg, r.Host, r.Path, exact: false);
                if (data != null) return new(200, [("Content-Type", ContentType(r.Path))], data, "archive");
            }
            if (hit != null) return new(200, hit.Headers, [], "weak");
            if (denied && ReplayScope.Find(this, r) is { Global: false } rule)
            {
                List<(string, string)> headers = [("Content-Type", rule.ContentType), .. rule.Host == "apiv2" ? Cors(r.Header) : rule.Host == "api" ? [("Access-Control-Allow-Origin", "*")] : []];
                return new(rule.Status, headers, Encoding.UTF8.GetBytes(rule.Empty!), "empty");
            }
            if (r.Method == "OPTIONS") return new(204, Cors(r.Header), [], "miss");
            if (r.Path.StartsWith("/v1/", StringComparison.Ordinal)) return new(200, Json(), "null"u8.ToArray(), "miss");
            if (r.Host == Gs) return new(200, Json(), "{\"err\": 0}"u8.ToArray(), "miss");
            if (r.Host == Apiv2)
                return new(404, [("Content-Type", "application/vnd.goa.error")],
                    "{\"id\":\"server\",\"code\":\"not_found\",\"status\":404,\"detail\":\"not recorded\"}"u8.ToArray(), "miss");
            return new(404, [], [], "miss");
        }
    }
}
