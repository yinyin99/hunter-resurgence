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
    public static class Paths
    {
        public static readonly string Root = FindRoot();
        public static readonly string Work = System.IO.Path.Combine(Root, "data");
        public static readonly string Mirror = System.IO.Path.Combine(Root, "mirror");
        public static readonly string Recordings = System.IO.Path.Combine(Root, "recordings");

        // the folder that holds settings.json, searched upwards from the exe
        static string FindRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(System.IO.Path.Combine(d.FullName, "settings.json"))) return d.FullName;
            Console.Error.WriteLine("settings.json not found");
            Environment.Exit(1);
            return "";
        }
    }

    static readonly HashSet<string> DropHeaders = ["content-encoding", "content-length", "transfer-encoding", "connection", "keep-alive", "alt-svc", "date", "server"];
    static readonly HashSet<string> VolatileParams = ["oauth_access_token", "access_token", "callback", "_", "session_ticket"];
    static readonly Regex Jsonp = new(@"^\s*[\w$.]+\(", RegexOptions.ECMAScript);
    static readonly Regex Callback = new(@"\A[\w$.]+\z", RegexOptions.ECMAScript);
    static readonly FileExtensionContentTypeProvider Mime = new();

    public const string HEARTBEAT = ProtocolLiterals.RpcBase + "/heartbeat";
    public const string DISCONNECT = ProtocolLiterals.RpcBase + "/disconnect";
    public const string CONNECT = ProtocolLiterals.RpcBase + "/connect";
    public const string MOVE_MULTIPLE2 = "/v1/Inventory/move_multiple2";
    public const string BUY = "/v1/Inventory/buy";
    public const string DISCOUNTS = "/v1/Application/discounts";
    public const string SPLASH = "/v1/Splash/list_active";
    public const string LAUNCH_START = "/v1/Launch/start";
    public const string APPLICATION = "/v1/Application/application";
    public const string PUBLIC_EXPEDITION = "/v1/Public_user/expedition";
    public const string EXPEDITION_LIST = "/v1/Expedition/list";
    public const string ME = "/v1/Me/me";
    public const string POLL = "/v1/Me/poll";
    public const string CHAT_CONVERSATIONS = "/v1/Chat/conversations";
    public const string CHAT_CONVERSATION = "/v1/Chat/conversation";
    public const string SKILL_LIST = "/v1/Skill/list";
    public const string LOCALE_PREFIX = "/static/built/js/locale/";
    public const string WEB_SCRIPT_SUFFIX = "_compiled_web.min.js";

    // the hosts the server answers for: one settings key each
    public static readonly string[] HostKeys = ["gameserver_host", "api_host", "apiv2_host", "signup_host", "web_host", "static_host",
        "static_legacy_host", "map_host", "photo_host", "avatar_host", "news_host", "telemetry_host", "crash_host"];

    public record struct Key(string Method, string Host, string Path, string? Params);
    public record Hit(int Tier, string Started, int Status, List<(string, string)> Headers, byte[] Body);

    public sealed class HarTable
    {
        public readonly OrderedDictionary<Key, Hit> Entries = new();
        public readonly Dictionary<string, (string Started, byte[] Body)> Connects = new();
    }

    public static JsonObject LoadCfg() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(Paths.Root, "settings.json")))!;

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
            try { entries = (JsonArray)JsonNode.Parse(File.ReadAllText(p, new UTF8Encoding(false, true)))!["log"]!["entries"]!; }
            catch (Exception e) when (e is IOException or JsonException or NullReferenceException or InvalidCastException or DecoderFallbackException or UnauthorizedAccessException)
            {
                Console.WriteLine($"skip {System.IO.Path.GetFileName(p)}: {e.Message}");
                continue;
            }
            foreach (var e in entries)
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

    /// <summary>Player state on workDir/state.db built from the recorded replies; null without a recorded connect.</summary>
    public static State? LoadState(JsonObject cfg, HarTable table, string workDir)
    {
        var e = table.Entries;
        var gsHost = S(cfg, "gameserver_host").ToLowerInvariant();
        var apiHost = S(cfg, "api_host").ToLowerInvariant();
        var staticHost = S(cfg, "static_host").ToLowerInvariant();
        if (!e.TryGetValue(new("POST", gsHost, CONNECT, null), out var gsConnect)) return null;
        e.TryGetValue(new("POST", apiHost, APPLICATION, null), out var app);
        // the launcher's locale script: one call that wraps a JSON object
        byte[] Script(Func<string, bool> pick) =>
            e.FirstOrDefault(kv => kv.Key.Params is null && kv.Key.Method == "GET" && kv.Key.Host == staticHost && pick(kv.Key.Path)).Value?.Body ?? [];
        var script = Script(p => p.StartsWith(LOCALE_PREFIX, StringComparison.Ordinal));
        JsonObject locale = [];
        if (script.Length > 0)
        {
            var a = Array.IndexOf(script, (byte)'(') + 1;
            var b = Array.LastIndexOf(script, (byte)')');
            if (b < 0) b = script.Length - 1;
            locale = (JsonObject)JsonNode.Parse(script.AsSpan(a, Math.Max(b - a, 0)))!;
        }
        var web = Script(p => p.EndsWith(WEB_SCRIPT_SUFFIX, StringComparison.Ordinal));
        e.TryGetValue(new("GET", apiHost, DISCOUNTS, null), out var discounts);
        var sample = System.IO.Path.Combine(S(cfg, "install_dir"), "game", "json_inventory.json");  // item definitions update 10 sends
        var uid = ((JsonObject?)JsonNode.Parse(gsConnect.Body)!["account"])?["user_id"];
        e.TryGetValue(new("POST", apiHost, SKILL_LIST, ParamsKey([("user_id", [Py.Str(uid)])])), out var skillList);
        var item = ArcList.Find("item.json", ArcList.ArchiveIndex(S(cfg, "install_dir")));  // the item definitions the game keeps in its archives
        return new State(System.IO.Path.Combine(workDir, "state.db"), gsConnect.Body, $"https://{S(cfg, "gameserver_host")}",
            $"https://{S(cfg, "static_host")}{ProtocolLiterals.IconPath}",
            app != null ? (JsonObject)JsonNode.Parse(app.Body)! : [], locale,
            ScorePage.TextureNames(Encoding.UTF8.GetString(web)),
            discounts != null ? (JsonArray)JsonNode.Parse(discounts.Body)! : [],
            File.Exists(sample) ? (JsonArray)JsonNode.Parse(File.ReadAllText(sample, Encoding.UTF8))!["infos"]!.DeepClone() : [],
            table.Connects.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value.Body),
            skillList != null ? JsonNode.Parse(skillList.Body) : null,
            item != null ? (JsonArray)JsonNode.Parse(ArcList.ReadEntry(item))! : []);
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
        public readonly string Gs, Apiv2, Api, Web;
        public readonly HashSet<string> Sinks;

        public Game(HarTable table, JsonObject cfg, State? state, string mirrorDir)
        {
            (Table, Cfg, State, MirrorDir) = (table, cfg, state, mirrorDir);
            Gs = S(cfg, "gameserver_host").ToLowerInvariant();
            Apiv2 = S(cfg, "apiv2_host").ToLowerInvariant();
            Api = S(cfg, "api_host").ToLowerInvariant();
            Web = S(cfg, "web_host").ToLowerInvariant();
            Sinks = SinkHosts(cfg);
        }

        /// <summary>The recorded reply for this request: the one recorded with the same query/form params when it is
        /// usable (tier ≥ 1), else the newest one for method, host and path.</summary>
        public Hit? Recorded(Req r)
        {
            Table.Entries.TryGetValue(new(r.Method, r.Host, r.Path, ParamsKey(RequestParams(r.Query, r.Body, r.Header("Content-Type")))), out var hit);
            if (hit is null or { Tier: < 1 }) Table.Entries.TryGetValue(new(r.Method, r.Host, r.Path, null), out hit);
            return hit;
        }

        /// <summary>Everything the player state does not answer, in order: archive file pinned to the URL's directory,
        /// recorded reply (JSONP callback swapped), mirror, archive file found by basename, an empty 200 for a request
        /// only recorded as 304/0 ("weak"), else a miss.</summary>
        public Reply Fallback(Req r)
        {
            var get = r.Method is "GET" or "HEAD";
            var data = get ? ArchiveFile(Cfg, r.Host, r.Path, exact: true) : null;
            if (data != null) return new(200, [("Content-Type", ContentType(r.Path))], data, "archive");
            var hit = Recorded(r);
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
                var f = MirrorPath(MirrorDir, r.Host, r.Path);
                if (f != null) return new(200, [("Content-Type", ContentType(System.IO.Path.GetFileName(f)))], File.ReadAllBytes(f), "mirror");
                data = ArchiveFile(Cfg, r.Host, r.Path, exact: false);
                if (data != null) return new(200, [("Content-Type", ContentType(r.Path))], data, "archive");
            }
            if (hit != null) return new(200, hit.Headers, [], "weak");
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
