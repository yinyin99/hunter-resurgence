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
    public const string DISCOUNTS = "/v1/Application/discounts";
    public const string AVATARS = "/v1/Avatar/getAvatars";
    public const string CHALLENGES = "/v1/Challenge/get_challenges";
    public const string USER_CHALLENGES = "/v1/Challenge/get_user_challenges";
    public const string SPLASH = "/v1/Splash/list_active";
    public const string LAUNCH_START = "/v1/Launch/start";
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

    /// <summary>Bytes the client would load from the install's archives for this URL, or null. The client resolves
    /// static image and map URLs through the archives. exact: only files found through the relative path (a basename
    /// that exists in several directories, flags 1), so in the URL's directory; else only the single archived file
    /// with that basename (flags 0), which can sit in another directory and be a different picture.</summary>
    public static byte[]? ArchiveFile(JsonObject cfg, string host, string path, bool exact)
    {
        var url = host + path;
        var mapHost = S(cfg, "map_host").ToLowerInvariant();
        string name;
        if (url.Contains("static/img")) name = "webgui/" + url[url.IndexOf("static/img", StringComparison.Ordinal)..];
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
    /// labels, the texture names and the populations of every reserve. Discounts, the avatar list and the player's skill list come from
    /// recordings only (without them: no discounts, fresh skills). Null when there is neither a recorded connect nor a
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
        if (recorded && neutral is null && !offline)
            throw new InvalidOperationException("a community server needs the install's connect sample: new accounts never start from a recorded connect");
        e.TryGetValue(new("POST", apiHost, APPLICATION, null), out var app);
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
        e.TryGetValue(new("GET", apiHost, DISCOUNTS, null), out var discounts);
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
            discounts != null ? (JsonArray)JsonNode.Parse(discounts.Body)! : [],
            File.Exists(sample) ? (JsonArray)JsonNode.Parse(File.ReadAllText(sample, Encoding.UTF8))!["infos"]!.DeepClone() : [],
            reserves,
            skillList != null ? JsonNode.Parse(skillList.Body) : null,
            item,
            offline: offline,  // a request no token resolves is the local account's; false on a community server
            startBody: recorded ? neutral : null,
            avatars: avatars is not null ? JsonNode.Parse(avatars.Body) as JsonArray : null,
            challenges: challenges is not null ? JsonNode.Parse(challenges.Body) as JsonArray : null,
            seasonal: SeasonalEvent.Parse(cfg["event"])) { CommunityDivisor = cfg["event_community_divisor"] is { } divisor ? (int)Py.L(divisor) : 20, AllLicenses = (bool?)cfg["all_licenses"] ?? false,
                Collectables = Install.CollectableDefinitions(S(cfg, "install_dir")) };
        if (!offline) state.MigrateAvatars();  // a community server: the pictures its accounts imported before the avatars were served
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

        static readonly Regex TileUrl = new(@"^/content/map/(v7/tiles/([1-5])/(\d{1,4})/(\d{1,4})\.jpg)$", RegexOptions.CultureInvariant);

        /// <summary>The game web host asks for map tiles under /content/map/; its own folder holds only some, so a tile inside the zoom's grid (2^z per side) is read from the map host's folder.</summary>
        string? MapTile(Req r) =>
            r.Host == GameWeb && TileUrl.Match(r.Path) is { Success: true } m && int.Parse(m.Groups[3].Value) < 1 << int.Parse(m.Groups[2].Value) && int.Parse(m.Groups[4].Value) < 1 << int.Parse(m.Groups[2].Value)
                ? MirrorPath(MirrorDir, S(Cfg, "map_host").ToLowerInvariant(), "/" + m.Groups[1].Value) : null;

        /// <summary>Everything the player state does not answer, in order: archive file pinned to the URL's directory,
        /// recorded reply (JSONP callback swapped), mirror, archive file found by basename, an empty 200 for a request
        /// only recorded as 304/0 ("weak"), else a miss. A community server replays a recording only for a global endpoint
        /// (<see cref="ReplayScope"/>); a per-player one gets its empty reply, an unlisted one the miss.</summary>
        public Reply Fallback(Req r)
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
                var f = MirrorPath(MirrorDir, r.Host, r.Path) ?? MapTile(r);
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
