using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Backup;

/// <summary>Derived = made by the tool from saved replies (map-setups.json), not a server reply.</summary>
sealed record Entry(string Path, string Endpoint, Dictionary<string, string> Params, int? Status, string? Sha256, DateTime TimeUtc, string? Error, bool? Derived = null);

sealed record Manifest(string ToolVersion, List<string> Groups, string Uid, string? Handle, string ClaimHash, DateTime StartedUtc, List<Entry> Files, [property: JsonPropertyName("steam_id64")] string? SteamId64, DateTime? CompletedUtc = null);

/// <summary>One request. P = form fields (no token: Auth adds it), V2 = apiv2 GET, Jwt = apiv2 Bearer, Abs = absolute GET (pictures, pages),
/// Opt = a 404 is an answer ("no entry"), saved as an empty result, Xhr = the website's ajax header, Fresh = ask the server even when resume finds the file.</summary>
sealed record R(string Path, (string K, string V)[]? P = null, bool V2 = false, bool Auth = false, bool Jwt = false, string? Abs = null, bool Opt = false, bool Xhr = false, bool Fresh = false);

/// <summary>Read-only backup: raw replies to files, manifest.json, optional zip. One instance per run.</summary>
sealed partial class Engine(Settings s, HttpClient http, Action<string> log, Action<int, int> progress, string keyPath)
{
    /// <summary>How much of a group needs the game session: None = public data only, Part = a few requests need it, All = all of it.</summary>
    public enum Session { None, Part, All }

    /// <summary>The groups the UI offers; their texts are the resources Group_&lt;key&gt; and GroupDesc_&lt;key&gt;.</summary>
    public static readonly (string Key, bool On, Session Needs)[] Groups =
    [
        ("profile", true, Session.None),
        ("inventory", true, Session.Part),
        ("setups", true, Session.Part),
        ("trophies", true, Session.Part),
        ("hunts", true, Session.None),
        ("kills", false, Session.None),
        ("account", true, Session.All),
        ("friends", true, Session.Part),
        ("photos", false, Session.None),
        ("messages", false, Session.All),
    ];

    public static string NotCovered => string.Join("\r\n", Loc.S("NotCovered_Title"), "- " + Loc.S("NotCovered_1"), "- " + Loc.S("NotCovered_2"), "- " + Loc.S("NotCovered_3"), "- " + Loc.S("NotCovered_4"));

    /// <summary>The release version (the git tag, set by the build); "0.0.0-dev" for a local build. The +commit suffix the SDK adds is cut.</summary>
    public static string Version { get; } = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];
    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Minimum gap between two request starts on one host, and the first retry wait (doubles, 4 retries).</summary>
    public int PaceMs = 1000, BackoffMs = 5000;
    /// <summary>A host lane that gave up on this many requests in a row ends the run (what was saved is kept); the longest wait a Retry-After header can ask for.</summary>
    public int GiveUpLimit = 20, RetryAfterCapMs = 120_000;
    /// <summary>The Steam account id of the signed-in Steam user, 0 = nobody. Read-only registry; a field so the tests do not depend on this PC.</summary>
    public Func<uint> SteamAccount = ReadSteamAccount;
    /// <summary>The API refused the token (errorCode 5 or 11) or it is missing: the UI says "start a hunt to refresh".</summary>
    public volatile bool TokenStale;
    /// <summary>Counters for the result line: files written, files already there (resume), requests that gave no file.</summary>
    public int Saved => saved;
    public int Skipped => skipped;
    public int Failed => failed;
    /// <summary>Plain words for the result bar, in the UI language: which groups lost requests and why. Empty when nothing failed.</summary>
    public string FailureSummary
    {
        get
        {
            lock (problems)
                return string.Join(" ", problems.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Loc.F(p.Value == ServerError ? "Fail_Server" : "Fail_Refused", Loc.S("Group_" + p.Key))));
        }
    }
    /// <summary>Requests actually sent, retries included (the UI's time estimate).</summary>
    public int Sent => sent;
    /// <summary>Key of the group of the request that last finished, for the progress line.</summary>
    public volatile string Group = "";
    /// <summary>False while a group is still listing pages, so the total can still grow.</summary>
    /// <summary>The folder of this backup (after Run has found the user id).</summary>
    public string Folder => dir;
    public bool TotalKnown { get { lock (unsized) return unsized.Count == 0; } }

    /// <summary>One request at a time per host, at most one start per PaceMs. Hosts run side by side.</summary>
    /// <summary>Dead = a request gave up after its retries: the rest of the run gets one attempt per request, no back-off, until one answers.</summary>
    sealed class Lane { public readonly SemaphoreSlim Gate = new(1, 1); public DateTime Last; public bool Dead; public int GiveUps; }

    string? token, jwt, handle, claimHash;
    JsonNode? me; // the identity reply (Me/me or Public_user/getByHostname): avatar
    string uid = "", dir = "";
    bool own, resume;
    DateTime jwtAt, started = DateTime.UtcNow;
    DateTime? completed;
    int done, total, sinceWrite, saved, skipped, failed, sent;
    const string ServerError = "server error";
    readonly Dictionary<string, string> problems = [];
    CancellationToken ct;
    Exception? fault;
    readonly object manifestLock = new();
    readonly SemaphoreSlim jwtGate = new(1, 1);
    readonly ConcurrentDictionary<string, Lane> lanes = [];
    readonly AsyncLocal<string> group = new();
    string? steamId64; // own account only; a public profile never has one
    readonly HashSet<string> unsized = [];
    readonly Dictionary<string, Entry> files = [];
    readonly SortedSet<string> groupsSeen = [];

    /// <summary>idText empty = the account of <paramref name="token"/>; a handle or id = public data only. Returns the zip, or the folder.
    /// Cancelling throws OperationCanceledException after manifest.json was written for what was saved, so a resumed run continues.</summary>
    public async Task<string> Run(string outRoot, string idText, string? token, ISet<string> groups, bool resume, bool zip, CancellationToken ct)
    {
        (this.token, this.resume, this.ct, own) = (token, resume, ct, idText.Length == 0);
        http.Timeout = TimeSpan.FromSeconds(120); // player queries are slow
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
        if (own && token == null) throw new InvalidOperationException(Loc.S("Err_NoToken"));
        var idr = own ? Q("Me/me", true) : idText.All(char.IsAsciiDigit) ? null : Q("Public_user/getByHostname", false, ("hostname", idText));
        (int Status, byte[] Body)? idRes = null;
        if (idr == null) uid = idText;
        else
        {
            log($"looking up {(own ? "your account" : idText)}");
            idRes = await Send(idr);
            if (idRes is not { } x || !Ok(idr, x.Status, x.Body)) throw new InvalidOperationException(Loc.S(TokenStale ? "Err_Refused" : "Err_Lookup"));
            var j = Json(x.Body);
            uid = j?["id"]?.ToString() ?? "";
            handle = j?["handle"]?.ToString();
            me = j;
            if (uid.Length == 0 || !uid.All(char.IsAsciiDigit)) throw new InvalidOperationException(Loc.S("Err_NoUid"));
        }
        dir = FolderFor(outRoot, uid);
        Directory.CreateDirectory(dir);
        foreach (var p in Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories)) File.Delete(p); // a crash mid-write
        LoadManifest();
        groupsSeen.UnionWith(groups);
        var key = ClaimKey();
        claimHash = Hex(SHA256.HashData(key));
        if (own)
        {
            var acc = SteamAccount();
            if (acc == 0) log("Steam account not found");
            else steamId64 = (76561197960265728UL + acc).ToString();
        }
        File.WriteAllBytes(Path.Combine(dir, "claim.key"), key);
        var chosen = Groups.Where(g => groups.Contains(g.Key) && g.Key != "setups" && !(g.Key == "hunts" && groups.Contains("kills"))).ToList(); // kills reads the hunts itself; setups runs last
        if (groups.Contains("setups")) Need(2); // its two reads are counted from the start
        lock (unsized) foreach (var g in chosen) if (g.Key is "profile" or "trophies" or "hunts" or "kills" or "account" or "photos") unsized.Add(g.Key);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.ct = stop.Token;
        try
        {
            if (idr != null && !Leaks(idr, idRes!.Value.Body)) Save(idr, FileName(idr), idRes.Value.Status, idRes.Value.Body);
            // groups run side by side; each host's lane keeps its own requests one at a time
            try { await Task.WhenAll(chosen.Select(g => RunGroup(g.Key, stop))); }
            catch when (fault != null) { }
            if (fault != null) ExceptionDispatchInfo.Capture(fault).Throw();
            if (groups.Contains("setups")) await RunGroup("setups", stop); // the tail: it reads the setup last, whatever resume skipped before
            if (failed == 0) completed = DateTime.UtcNow; // a run that ended whole (not cancelled, nothing left to retry) is final: the next run is a new snapshot
        }
        finally { WriteManifest(); }
        log($"{saved} files saved, {skipped} already there, {failed} not saved (see manifest.json; run again with 'skip files already saved' to retry)");
        if (!zip) return dir;
        var path = Path.Combine(outRoot, $"backup-{uid}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ZipFile.CreateFromDirectory(dir, path);
        return path;
    }

    async Task RunGroup(string key, CancellationTokenSource stop)
    {
        group.Value = key;
        try
        {
            log($"== {key}");
            await (key switch
            {
                "profile" => Profile(), "inventory" => Inventory(), "setups" => Setups(), "trophies" => Trophies(), "hunts" => Hunts(false), "kills" => Hunts(true),
                "account" => Account(), "friends" => Friends(), "photos" => Photos(), _ => Messages(),
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Interlocked.CompareExchange(ref fault, e, null); // the first real failure ends the other groups
            stop.Cancel();
            throw;
        }
        finally { Sized(key); }
    }

    void Sized(string key)
    {
        lock (unsized) unsized.Remove(key);
        progress(Volatile.Read(ref done), Volatile.Read(ref total));
    }

    // ---------- groups ----------

    static Func<byte[], bool> Done => b => Json(b)?["status"]?.ToString() == "done"; // the server builds a board on first request: queued/building until done

    async Task Profile()
    {
        int open = 2;
        void Listed() { if (Interlocked.Decrement(ref open) == 0) Sized("profile"); }
        await Task.WhenAll(ProfileApi(Listed), ProfileV2(Listed)); // two hosts, two lanes
    }

    async Task ProfileApi(Action listed)
    {
        Need(s.ConsumerKey == null ? 9 : 10);
        foreach (var ep in new[] { "Statistics/total", "Skill/list", "Achievement/stats", "Challenge/get_user_challenges" })
            await Get(Q(ep, false, ("user_id", uid)));
        await Get(Q("Leaderboard/personal", false, ("user_id", uid), ("season_no", "0")), ready: Done);
        await Get(Q("Leaderboard/current_personal", false, ("user_id", uid)));
        foreach (var g in new[] { "0", "1" }) // the player's position on the two minigame boards
            await Get(Q("Page_content/minigames_list", false, ("offset", "0"), ("limit", "1"), ("game_id", g), ("user_id", uid)));
        await Get(Q("Membership_Bundle/get_active")); // the membership bundles on sale: public, and a token makes it answer handlerError
        int seasons = 0;
        if (s.ConsumerKey is { } key) // the catalogue makes item, species and achievement ids readable later; its season is the newest board
            seasons = (int?)Json(await Get(Q("Application/application", false, ("oauth_consumer_key", key), ("lang", "en"))))?["season"] ?? 0;
        else log("! Application/application skipped: no oauth_consumer_key in settings.json");
        Need(seasons + 1);
        listed();
        for (int n = 1; n <= seasons; n++)
            await Get(Q("Leaderboard/personal", false, ("user_id", uid), ("season_no", n.ToString())), ready: Done);
        await Picture(me?["avatar"]?.ToString(), "images/");
    }

    async Task ProfileV2(Action listed)
    {
        Need(2);
        await Get(new R($"/game/user/{uid}/profile", V2: true));
        await Get(new R($"/game/user/{uid}/ranks", V2: true));
        var boards = new List<(string Type, string Id)>();
        foreach (var type in new[] { "single", "double" }) // antler boards, then the player's own entry on each (404 = no entry)
        {
            Need(1);
            var all = Json(await Get(new R($"/game/user/leaderboards/antlers/{type}/all", V2: true))) as JsonArray;
            boards.AddRange((all ?? []).Select(b => b?["id"]?.ToString()).OfType<string>().Select(id => (type, id)));
        }
        Need(boards.Count);
        listed();
        foreach (var (type, id) in boards)
            await Get(new R($"/game/user/leaderboards/antlers/{type}/{id}/personal/{uid}", V2: true, Opt: true));
    }

    async Task Inventory()
    {
        Need(2);
        await Get(Q("Inventory/dict", false, ("user_id", uid)));
        await Get(new R("/game/user/loadout", V2: true, Jwt: true));
    }

    async Task Trophies()
    {
        Need(4);
        await Get(Q("Trophy/list", false, ("user_id", uid), ("offset", "0"), ("limit", "100000")));
        var lodges = Json(await Get(new R($"/game/user/{uid}/lodges", V2: true))) as JsonArray;
        var mine = Json(await Get(new R("/game/user/trophies", V2: true, Jwt: true))) as JsonArray; // own trophies; each has a harvest record
        Need((lodges?.Count ?? 0) + (mine?.Count ?? 0));
        Sized("trophies");
        foreach (var l in lodges ?? [])
            if (l?["id"] is { } id) await Get(new R($"/game/user/{uid}/lodges/{id}", V2: true));
        foreach (var m in mine ?? [])
            if (m?["id"] is { } id) await Get(new R($"/game/user/harvest/{id}", V2: true, Jwt: true, Opt: true));
    }

    /// <summary>Hunt list pages, then every hunt. With kills: the score sheet page and the picture of every scored kill too.</summary>
    async Task Hunts(bool kills)
    {
        var listed = new List<(string Id, long Start)>();
        for (int off = 0; ; off += 50) // every page first, so the total is known before the per-hunt requests
        {
            Need(1);
            var ex = Json(await Get(Q("Expedition/list", false, ("user_id", uid), ("limit", "50"), ("offset", off.ToString()))))?["expeditions"] as JsonArray;
            if (ex is not { Count: > 0 }) break;
            listed.AddRange(ex.Where(e => e?["id"] != null).Select(e => (e!["id"]!.ToString(), (long?)e["start_ts"] ?? 0)));
            if (ex.Count < 50) break;
        }
        var ids = listed.OrderByDescending(h => h.Start).Select(h => h.Id).ToList(); // newest first (the list comes that way): a run that is cancelled or too long keeps the latest hunts
        Need(ids.Count);
        if (!kills) Sized("hunts");
        var found = new List<(string? Sheet, string? Photo)>(); // a score sheet only for a scored kill, a picture for every kill that has one
        foreach (var id in ids)
        {
            var hunt = Json(await Get(Q("Public_user/expedition", false, ("user_id", uid), ("expedition_id", id))));
            foreach (var k in (hunt?["kills"] as JsonArray) ?? [])
            {
                if (!kills) break;
                var sheet = k?["kill"]?["score"] != null ? k["id"]?.ToString() : null;
                var photo = k?["kill"]?["photo"]?.ToString() is { Length: > 0 } ph ? ph : null;
                if (sheet != null || photo != null) found.Add((sheet, photo));
            }
        }
        if (!kills) return;
        Need(found.Count(k => k.Sheet != null) + found.Count(k => k.Photo != null));
        Sized("kills");
        foreach (var (kid, photo) in found)
        {
            if (kid != null)
            {
                var page = $"https://{s.WebHost}/user/x/score/?id={kid}&tab=true"; // the page does not check the handle
                await Get(new R(new Uri(page).PathAndQuery, Abs: page, Xhr: true), $"kills/score_{kid}.html");
            }
            if (photo != null) await Picture(photo, "kills/");
        }
    }

    async Task Account()
    {
        Need(9);
        await Get(Q("Mission/missions", true));
        await Get(Q("Daily_Mission/get_daily_missions_calendar", true));
        await Get(Q("Wallet/bank_transactions", true));
        foreach (var ep in new[] { "Wallet/spent", "Page_content/competition_states", "Page_content/competition_community_league_states", "Chat/get_message_settings", "Recruit/get_recruits" })
            await Get(Q(ep, true));
        var backgrounds = Json(await Get(new R("/game/user/backgrounds", V2: true, Jwt: true))) as JsonArray;
        foreach (var b in backgrounds ?? []) // the picture of the active profile background ("none" and "most_recent" are not pictures)
            if (b?["active"]?.GetValue<bool>() == true && b["url"]?.ToString() is { } u && u.Contains('/'))
            {
                Need(1);
                await Picture(u, "images/");
            }
        foreach (var ep in new[] { "Wallet/transactions", "Wallet/silver_transactions" })
            for (int off = 0; ; off += 50)
            {
                Need(1);
                if (Json(await Get(Q(ep, true, ("lang", "en"), ("offset", off.ToString())))) is not JsonArray { Count: 50 }) break;
            }
    }

    async Task Friends()
    {
        Need(2);
        await Get(Q("User/friendslist", false, ("user_id", uid), ("offset", "0"), ("limit", "0"))); // limit 0 = all
        await Get(Q("Me/friends", true));
    }

    async Task Messages()
    {
        Need(1);
        await Get(Q("Chat/conversations", true)); // ponytail: previews only; Chat/conversation may mark threads read, unverified
    }

    async Task Photos()
    {
        var urls = new List<string?>();
        for (int off = 0; ; off += 24)
        {
            Need(1);
            var ph = Json(await Get(Q("Gallery/list", false, ("user_id", uid), ("offset", off.ToString()), ("limit", "24"))))?["photos"] as JsonArray;
            if (ph is not { Count: > 0 }) break;
            urls.AddRange(ph.Select(p => p?["url"]?.ToString()));
            if (ph.Count < 24) break;
        }
        Need(urls.Count);
        Sized("photos");
        foreach (var u in urls) await Picture(u, "photos/", true); // the photo host has its own lane
    }

    /// <summary>A picture by URL: only https on a configured host. Otherwise skipped; strict (gallery) records it as a failed request in the manifest, the others only log it.</summary>
    async Task Picture(string? url, string dirName, bool strict = false)
    {
        if (url != null && url.StartsWith("//")) url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !s.Hosts.Contains(u.Host))
        {
            const string why = "not an https URL on a configured host";
            if (strict) Fail(new R(url ?? ""), dirName + "skipped_" + Clean(url ?? ""), null, url is { Length: > 0 } ? why : "no picture URL");
            else if (url is { Length: > 0 }) log($"! picture skipped ({why}): {url}");
            progress(Interlocked.Increment(ref done), Volatile.Read(ref total));
            return;
        }
        await Get(new R(u.AbsolutePath, Abs: u.ToString()), dirName + Clean(u.Host + u.AbsolutePath));
    }

    // ---------- requests ----------

    static R Q(string ep, bool auth = false, params (string, string)[] p) => new("/v1/" + ep, p, Auth: auth);

    static string Clean(string x) => NotName().Replace(x, "_");

    [GeneratedRegex(@"[^A-Za-z0-9._=-]")]
    private static partial Regex NotName();

    static IEnumerable<(string K, string V)> Pub(R r) => (r.P ?? []).Where(p => p.K != "oauth_consumer_key"); // never in a file name, the log or the manifest

    static string FileName(R r) => Clean(r.Path.TrimStart('/')[(r.Path.StartsWith("/v1/") ? 3 : 0)..] + string.Concat(Pub(r).Select(p => $"__{p.K}={p.V}"))) + ".json";

    static JsonNode? Json(byte[]? b)
    {
        try { return b == null ? null : JsonNode.Parse(b); } catch (JsonException) { return null; }
    }

    static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    /// <summary>Seconds still to go at the pace between two progress samples (all hosts counted); null while there is too little to tell (under 5 seconds, no progress, or done).</summary>
    public static double? SecondsLeft(double fromSecs, int fromDone, double nowSecs, int done, int total) =>
        nowSecs - fromSecs >= 5 && done > fromDone && total > done ? (total - done) / ((done - fromDone) / (nowSecs - fromSecs)) : null;

    void Need(int n) { progress(Volatile.Read(ref done), Interlocked.Add(ref total, n)); }

    async Task<byte[]?> Get(R r, string? name = null, Func<byte[], bool>? ready = null)
    {
        try { return await Fetch(r, name ?? FileName(r), ready); }
        finally
        {
            if (group.Value is { } g) Group = g;
            progress(Interlocked.Increment(ref done), Volatile.Read(ref total));
        }
    }

    async Task<byte[]?> Fetch(R r, string name, Func<byte[], bool>? ready)
    {
        var full = Path.Combine(dir, name);
        if (resume && !r.Fresh && File.Exists(full))
        {
            Entry? known;
            lock (files) files.TryGetValue(name, out known);
            byte[] b;
            if (r.Abs != null && known?.Sha256 != null) b = []; // a picture: nothing parses it, do not read it back
            else
            {
                b = File.ReadAllBytes(full);
                if (known?.Sha256 == null) lock (files) files[name] = new(name, r.Path, Params(r), null, Hex(SHA256.HashData(b)), File.GetLastWriteTimeUtc(full), null);
            }
            Interlocked.Increment(ref skipped);
            if (r.Abs == null) log($"have {name}");
            return b;
        }
        if ((r.Auth || r.Jwt) && !own)
        {
            log($"skipped, needs the game: {r.Path}"); // public profile: never asked, so not a failure and not in the manifest
            return null;
        }
        if ((r.Auth || r.Jwt) && (token == null || TokenStale)) return Fail(r, name, null, "needs the game session (missing or refused)");
        if (r.Jwt && !await EnsureJwt()) return Fail(r, name, null, "no JWT", ServerError);
        log($"{(r.V2 || r.Abs != null ? "GET" : "POST")} {r.Path} {string.Join('&', Pub(r).Select(p => $"{p.K}={p.V}"))}".TrimEnd());
        for (int poll = 0; ; poll++)
        {
            string? why = null;
            if (await Send(r, e => why = e) is not { } x) return Fail(r, name, null, why ?? "network failure", ServerError);
            if (r.Opt && x.Status == 404) { Save(r, name, 404, x.Body); return x.Body; } // "no entry" is an answer, not a failure
            if (!Ok(r, x.Status, x.Body)) return Fail(r, name, x.Status, TokenStale ? "token refused" : "error reply");
            if (Leaks(r, x.Body)) return Fail(r, name, x.Status, "reply contains a token, not saved");
            if (ready != null && !ready(x.Body))
            {
                if (poll == 19) return Fail(r, name, x.Status, "still building", ServerError);
                await Task.Delay(PaceMs == 0 ? 0 : 3000, ct);
                continue;
            }
            Save(r, name, x.Status, x.Body);
            return x.Body;
        }
    }

    static Dictionary<string, string> Params(R r) => Pub(r).ToDictionary(p => p.K, p => p.V);

    byte[]? Fail(R r, string name, int? status, string err, string kind = "error reply")
    {
        lock (problems)
        {
            var g = group.Value ?? "lookup";
            if (kind == ServerError || !problems.ContainsKey(g)) problems[g] = kind; // a server error is the one worth retrying later
        }
        log($"! {r.Path}: {err}");
        Interlocked.Increment(ref failed);
        lock (files) if (!files.TryGetValue(name, out var e) || e.Sha256 == null) files[name] = new(name, r.Path, Params(r), status, null, DateTime.UtcNow, err);
        return null;
    }

    void Save(R r, string name, int status, byte[] body)
    {
        var full = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WriteAtomic(full, body);
        lock (files) files[name] = new(name.Replace('\\', '/'), r.Path, Params(r), status, Hex(SHA256.HashData(body)), DateTime.UtcNow, null);
        Interlocked.Increment(ref saved);
        if (Interlocked.Increment(ref sinceWrite) % 25 == 0) WriteManifest();
    }

    /// <summary>False = do not save: not 2xx, or a /v1 error reply (HTTP 200 with errorCode or handlerError). Errors 5 and 11 on a token call mark the token stale.</summary>
    bool Ok(R r, int status, byte[] body)
    {
        if (r.Jwt && status == 401) jwt = null; // expired: fetched again on the next call
        if (status is < 200 or >= 300) return false;
        if (r.V2 || r.Abs != null || body.Length >= 512 || Json(body) is not JsonObject o) return true; // error replies are small
        if (r.Auth && o["errorCode"]?.ToString() is "5" or "11")
        {
            TokenStale = true;
            log("token expired or refused: start a hunt to refresh");
        }
        return !(o.ContainsKey("errorCode") || (o.ContainsKey("error") && o.ContainsKey("code")));
    }

    async Task<bool> EnsureJwt()
    {
        await jwtGate.WaitAsync(ct); // one User/jwt call however many groups wait for it
        try
        {
            if (jwt != null && DateTime.UtcNow - jwtAt < TimeSpan.FromMinutes(20)) return true; // lifetime 30 min
            var r = Q("User/jwt", true);
            if (await Send(r) is { } x && Ok(r, x.Status, x.Body) && Json(x.Body)?["token"] is JsonValue v && v.TryGetValue<string>(out var t))
            {
                (jwt, jwtAt) = (t, DateTime.UtcNow); // never saved
                return true;
            }
            return false;
        }
        finally { jwtGate.Release(); }
    }

    bool Leaks(R r, byte[] b)
    {
        if (r.Abs != null) return false;
        var sp = b.AsSpan();
        return sp.IndexOf("refresh_token"u8) >= 0
            || (token != null && sp.IndexOf(Encoding.ASCII.GetBytes(token)) >= 0)
            || (s.ConsumerKey != null && sp.IndexOf(Encoding.ASCII.GetBytes(s.ConsumerKey)) >= 0)
            || (jwt != null && sp.IndexOf(Encoding.ASCII.GetBytes(jwt)) >= 0);
    }

    string Host(R r) => r.Abs != null ? new Uri(r.Abs).Host : r.V2 ? s.Apiv2Host : s.ApiHost;

    async Task<(int Status, byte[] Body)?> Send(R r, Action<string>? gaveUp = null)
    {
        var lane = lanes.GetOrAdd(Host(r), _ => new());
        await lane.Gate.WaitAsync(ct); // held through retries: a host that answers badly is not hit by a second request
        try
        {
            for (int n = 0; ; n++)
            {
                var wait = lane.Last.AddMilliseconds(PaceMs) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                ct.ThrowIfCancellationRequested(); // the last check before a request leaves
                lane.Last = DateTime.UtcNow;
                string err;
                var retryAfter = 0.0;
                try
                {
                    using var req = Build(r);
                    Interlocked.Increment(ref sent);
                    using var resp = await http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsByteArrayAsync(ct);
                    var st = (int)resp.StatusCode;
                    if (st is not (403 or 407 or 429 or >= 500)) { (lane.Dead, lane.GiveUps) = (false, 0); return (st, body); }
                    err = $"HTTP {st}";
                    if (st is 429 or 503 && resp.Headers.RetryAfter is { } ra) // the server says how long to wait
                        retryAfter = Math.Min(ra.Delta?.TotalMilliseconds ?? Math.Max(0, ((ra.Date ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalMilliseconds), RetryAfterCapMs);
                }
                catch (Exception e) when ((e is HttpRequestException or TaskCanceledException or IOException) && !ct.IsCancellationRequested)
                {
                    err = $"{e.GetType().Name}: {e.Message}";
                }
                if (n == (lane.Dead ? 0 : 4))
                {
                    log(lane.Dead ? $"! host not answering, try again later: {r.Path} ({err})" : $"! giving up on {r.Path}: {err}");
                    lane.Dead = true;
                    gaveUp?.Invoke(err);
                    if (++lane.GiveUps >= GiveUpLimit) throw new HostSilentException(Host(r), lane.GiveUps);
                    return null;
                }
                var d = retryAfter > 0 ? (int)retryAfter : BackoffMs << n;
                log($"{r.Path}: {err}, retry in {d / 1000} s");
                await Task.Delay(d, ct);
            }
        }
        finally { lane.Gate.Release(); }
    }

    HttpRequestMessage Build(R r)
    {
        if (r.Abs != null)
        {
            var a = new HttpRequestMessage(HttpMethod.Get, r.Abs);
            if (r.Xhr) a.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return a;
        }
        var uri = $"https://{(r.V2 ? s.Apiv2Host : s.ApiHost)}{r.Path}";
        if (r.V2)
        {
            var g = new HttpRequestMessage(HttpMethod.Get, uri);
            g.Headers.Accept.ParseAdd("application/json");
            if (r.Jwt) g.Headers.Authorization = new("Bearer", jwt);
            return g;
        }
        var form = new List<KeyValuePair<string, string>>();
        if (r.Auth) form.Add(new("oauth_access_token", token!));
        foreach (var (k, v) in r.P ?? []) form.Add(new(k, v));
        var m = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        m.Headers.Add("Origin", $"https://{s.WebHost}");
        m.Headers.Referrer = new($"https://{s.WebHost}/");
        return m;
    }

    // ---------- output ----------

    /// <summary>The newest backup-&lt;uid&gt; folder when its run did not end (resume continues it), else a new one named with the time: a finished backup is never filled up with later data.</summary>
    static string FolderFor(string root, string uid)
    {
        var name = "backup-" + uid;
        var last = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Where(d => Regex.IsMatch(d, $@"^{name}(-\d{{8}}-\d{{6}})?$")).Order(StringComparer.Ordinal).LastOrDefault()
            : null;
        if (last == null) return Path.Combine(root, name);
        var manifest = Path.Combine(root, last, "manifest.json");
        if (!File.Exists(manifest) || Json(File.ReadAllBytes(manifest))?["completed_utc"] == null) return Path.Combine(root, last);
        var at = DateTime.Now;
        while (Directory.Exists(Path.Combine(root, $"{name}-{at:yyyyMMdd-HHmmss}"))) at = at.AddSeconds(1);
        return Path.Combine(root, $"{name}-{at:yyyyMMdd-HHmmss}");
    }

    void LoadManifest()
    {
        var p = Path.Combine(dir, "manifest.json");
        if (!File.Exists(p)) return;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(p), J);
            foreach (var e in m?.Files ?? []) files[e.Path] = e;
            groupsSeen.UnionWith(m?.Groups ?? []);
        }
        catch (JsonException) { } // damaged manifest: files on disk are re-registered by resume
    }

    void WriteManifest()
    {
        Entry[] list;
        lock (files) list = [.. files.Values.OrderBy(e => e.Path, StringComparer.Ordinal)];
        var m = new Manifest(Version, [.. groupsSeen], uid, handle, claimHash!, started, [.. list], steamId64, completed);
        lock (manifestLock) WriteAtomic(Path.Combine(dir, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(m, J));
    }

    /// <summary>Write beside, then move over: a crash or cancel never leaves a half file that resume would trust.</summary>
    static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".part";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }

    static uint ReadSteamAccount() => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int v ? (uint)v : 0;

    /// <summary>32 random bytes made once per machine and kept next to the settings; the manifest records only their SHA-256.</summary>
    byte[] ClaimKey()
    {
        if (File.Exists(keyPath) && new FileInfo(keyPath).Length == 32) return File.ReadAllBytes(keyPath);
        var k = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, k);
        return k;
    }
}

/// <summary>A host lane gave up on <see cref="Engine.GiveUpLimit"/> requests in a row: trying the rest would only wait.</summary>
sealed class HostSilentException(string host, int count) : InvalidOperationException(Loc.F("Err_HostSilent", host, count));
