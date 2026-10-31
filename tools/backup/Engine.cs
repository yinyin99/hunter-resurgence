using System.Collections.Concurrent;
using System.IO.Compression;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Backup;

/// <summary>Derived = made by the tool from saved replies (map-setups.json), not a server reply. Date = the reply's HTTP Date header (the server's clock), when it sent one.</summary>
sealed record Entry(string Path, string Endpoint, Dictionary<string, string> Params, int? Status, string? Sha256, DateTime TimeUtc, string? Error, bool? Derived = null, DateTime? Date = null);

/// <summary>Format 2 adds Format, Mode (own or public), Identity (me, handle or id) and Checks (hunts: saved fewer than listed, omitted when complete); a manifest without format is format 1.</summary>
sealed record Manifest(string ToolVersion, List<string> Groups, string Uid, string? Handle, string ClaimHash, DateTime StartedUtc, List<Entry> Files, [property: JsonPropertyName("steam_id64")] string? SteamId64, DateTime? CompletedUtc = null,
    int? Format = null, string? Mode = null, string? Identity = null, JsonObject? Checks = null);

/// <summary>One request. P = form fields (no token: Auth adds it), V2 = apiv2 GET, Jwt = apiv2 Bearer, Abs = absolute GET (pictures, pages),
/// Opt = a 404 is an answer ("no entry"), saved as an empty result, Xhr = the website's ajax header, Fresh = ask the server even when resume finds the file,
/// Keep = the first saved answer stays: a saved file is never asked again, even when resume is off.</summary>
sealed record R(string Path, (string K, string V)[]? P = null, bool V2 = false, bool Auth = false, bool Jwt = false, string? Abs = null, bool Opt = false, bool Xhr = false, bool Fresh = false, bool Keep = false);

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
        ("history", false, Session.None),
        ("messages", false, Session.All),
    ];

    /// <summary>The items of the "What isn't saved?" dialog: only what is not in a backup.</summary>
    public static string[] NotCovered => [.. Enumerable.Range(1, 6).Select(i => Loc.S("NotCovered_" + i))];

    /// <summary>The release version (the git tag, set by the build); "0.0.0-dev" for a local build. The +commit suffix the SDK adds is cut.</summary>
    public static string Version { get; } = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];
    const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Minimum gap between two request starts on one host, and the first retry wait (doubles, 4 retries).</summary>
    public int PaceMs = 1000, BackoffMs = 5000;
    /// <summary>A host lane that gave up on this many requests in a row is spent: its other requests are not sent and count as failed, the other hosts run on; the longest wait a Retry-After header can ask for.</summary>
    public int GiveUpLimit = 20, RetryAfterCapMs = 120_000;
    /// <summary>The Steam account id of the signed-in Steam user, 0 = nobody. Read-only registry; a field so the tests do not depend on this PC.</summary>
    public Func<uint> SteamAccount = ReadSteamAccount;
    /// <summary>The API refused the token (errorCode 5 or 11) or it is missing: the UI says "start a hunt to refresh".</summary>
    public volatile bool TokenStale;
    /// <summary>Counters for the result line: files written, files already there (resume), requests that gave no file.</summary>
    public int Saved => saved;
    public int Skipped => skipped;
    public int Failed => failed;
    /// <summary>Pictures and pages the server no longer has (404): listed in the manifest, not a failure, nothing to run again for.</summary>
    public int Dead => dead;
    /// <summary>The run ended whole: no manifest entry is left to retry (this run's and the earlier runs' it continued). Only then the result bar shows success.</summary>
    public bool Whole => completed != null;
    /// <summary>Plain words for the result bar, in the UI language: which groups lost requests and why. Empty when nothing failed.</summary>
    public string FailureSummary
    {
        get
        {
            lock (problems)
                return string.Join(" ", problems.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Loc.F(p.Value == ServerError ? "Fail_Server" : p.Value == NotData ? "Fail_NotData" : "Fail_Refused", Loc.S("Group_" + p.Key))));
        }
    }
    /// <summary>The hosts that stopped answering for good in this run (their budget of give-ups in a row was spent) and how many give-ups that took; the requests after it failed unsent.</summary>
    public (string Host, int GiveUps)[] SilentHosts => [.. lanes.Where(l => l.Value.Spent).OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => (l.Key, l.Value.GiveUps))];
    /// <summary>Requests actually sent, retries included (the UI's time estimate).</summary>
    public int Sent => sent;
    /// <summary>Key of the group of the request that last finished, for the progress line.</summary>
    public volatile string Group = "";
    /// <summary>False while a group is still listing pages, so the total can still grow.</summary>
    /// <summary>The folder of this backup (after Run has found the user id).</summary>
    public string Folder => dir;
    public bool TotalKnown { get { lock (unsized) return unsized.Count == 0; } }

    /// <summary>One request at a time per host, at most one start per PaceMs. Hosts run side by side.</summary>
    /// <summary>Dead = a request gave up after its retries: the rest of the run gets one attempt per request, no back-off, until one answers. Spent = GiveUpLimit give-ups in a row: nothing more is sent to this host in this run.</summary>
    sealed class Lane { public readonly SemaphoreSlim Gate = new(1, 1); public DateTime Last; public bool Dead, Spent; public int GiveUps; }

    string? token, jwt, handle, claimHash;
    Func<string?>? tokenOf; // asked before every request that needs the session: a hunt started during a long run brings a new token
    readonly object renewing = new();
    JsonNode? me; // the identity reply (Me/me or Public_user/getByHostname): avatar
    string uid = "", dir = "", identity = ""; // identity: how the account was named, me (own), handle or id (public)
    bool own, resume;
    DateTime jwtAt, started = DateTime.UtcNow;
    DateTime? completed;
    int done, total, sinceWrite, saved, skipped, failed, sent, dead;
    const string ServerError = "server error", NotData = "not data", DeadError = "dead on the server (404)";
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
    public async Task<string> Run(string outRoot, string idText, Func<string?>? tokenOf, ISet<string> groups, bool resume, bool zip, CancellationToken ct)
    {
        (this.tokenOf, this.resume, this.ct, own) = (tokenOf, resume, ct, idText.Length == 0);
        token = tokenOf?.Invoke();
        http.Timeout = TimeSpan.FromSeconds(120); // player queries are slow
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
        if (own && token == null) throw new InvalidOperationException(Loc.S("Err_NoToken"));
        var idr = own ? Q("Me/me", true) : idText.All(char.IsAsciiDigit) ? null : Q("Public_user/getByHostname", false, ("hostname", idText));
        identity = own ? "me" : idr == null ? "id" : "handle";
        (int Status, byte[] Body, DateTimeOffset? Date)? idRes = null;
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
        lock (unsized) foreach (var g in chosen) if (g.Key is "profile" or "trophies" or "hunts" or "kills" or "account" or "photos" or "history") unsized.Add(g.Key);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.ct = stop.Token;
        try
        {
            if (idr != null)
            {
                if (Leaks(idr, idRes!.Value.Body)) Fail(idr, FileName(idr), idRes.Value.Status, "reply contains a token, not saved"); // restore reads mode and wallet from this file: say why it is missing
                else Save(idr, FileName(idr), idRes.Value.Status, idRes.Value.Body, idRes.Value.Date);
            }
            // groups run side by side; each host's lane keeps its own requests one at a time
            try { await Task.WhenAll(chosen.Select(g => RunGroup(g.Key, stop))); }
            catch when (fault != null) { }
            if (fault != null) ExceptionDispatchInfo.Capture(fault).Throw();
            if (groups.Contains("setups")) await RunGroup("setups", stop); // the tail: it reads the setup last, whatever resume skipped before
            lock (files) if (failed == 0 && !files.Values.Any(e => e.Error != null && e.Error != DeadError)) completed = DateTime.UtcNow; // whole = nothing failed in this run and no entry left to retry, including failed ones an earlier run left that this one did not repeat: the next run is a new snapshot
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
                "account" => Account(), "friends" => Friends(), "photos" => Photos(), "history" => History(), _ => Messages(),
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
        Need(s.ConsumerKey == null ? 11 : 12);
        foreach (var ep in new[] { "Statistics/total", "Skill/list", "Achievement/stats", "Challenge/get_user_challenges" })
            await Get(Q(ep, false, ("user_id", uid)));
        foreach (var ep in new[] { "Campaign/get_active_campaign", "Community_Campaign/get_active_community_campaign" }) // null while no campaign runs: valid data
            await Get(Q(ep, false, ("user_id", uid), ("lang", "en")));
        await Get(Q("Leaderboard/personal", false, ("user_id", uid), ("season_no", "0")), ready: Done);
        await Get(Q("Leaderboard/current_personal", false, ("user_id", uid)));
        foreach (var g in new[] { "0", "1" }) // the player's position on the two minigame boards
            await Get(Q("Page_content/minigames_list", false, ("offset", "0"), ("limit", "1"), ("game_id", g), ("user_id", uid)));
        await Get(Q("Membership_Bundle/get_active")); // the membership bundles on sale: public, and a token makes it answer handlerError
        int seasons = 0;
        if (s.ConsumerKey is { } key) // the catalogue makes item, species and achievement ids readable later; its season is the newest board
            seasons = (int)(Num(Json(await Get(Q("Application/application", false, ("oauth_consumer_key", key), ("lang", "en"))))?["season"]) ?? 0);
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

    /// <summary>Set when a hunt in the list has no saved Public_user/expedition reply (HuntsSaved &lt; HuntsListed) or a list page gave no file (HuntsListFailed: the list is cut short,
    /// so hunts can be missing that nothing counts); both null when neither happened. Expedition/list's own "total" is not used: it counts the rows left from the page's offset, capped at limit + 1, not the player's hunts.</summary>
    public int? HuntsListed { get; private set; }
    public int? HuntsSaved { get; private set; }
    public bool HuntsListFailed { get; private set; }

    /// <summary>Hunt list pages, then every hunt. With kills: the score sheet page and the picture of every scored kill too.</summary>
    async Task Hunts(bool kills)
    {
        (HuntsListed, HuntsSaved, HuntsListFailed) = (null, null, false); // a resumed run counts again
        var listed = new List<(string Id, long Start)>();
        for (int off = 0; ; off += 50) // every page first, so the total is known before the per-hunt requests
        {
            Need(1);
            var body = await Get(Q("Expedition/list", false, ("user_id", uid), ("limit", "50"), ("offset", off.ToString())));
            if (body == null) { HuntsListFailed = true; break; } // no file: the pages after it are unknown
            var ex = Json(body)?["expeditions"] as JsonArray;
            if (ex is not { Count: > 0 }) break;
            listed.AddRange(ex.Where(e => e?["id"] != null).Select(e => (e!["id"]!.ToString(), Num(e["start_ts"]) ?? 0)));
            if (ex.Count < 50) break;
        }
        var ids = listed.OrderByDescending(h => h.Start).Select(h => h.Id).ToList(); // newest first (the list comes that way): a run that is cancelled or too long keeps the latest hunts
        Need(ids.Count);
        if (!kills) Sized("hunts");
        var found = new List<(string? Sheet, string? Photo)>(); // a score sheet only for a scored kill, a picture for every kill that has one
        int got = 0;
        foreach (var id in ids)
        {
            var body = await Get(Q("Public_user/expedition", false, ("user_id", uid), ("expedition_id", id)));
            if (body != null) got++; // null only when no reply was saved (a reply of JSON null is saved)
            var hunt = Json(body);
            foreach (var k in (hunt?["kills"] as JsonArray) ?? [])
            {
                if (!kills) break;
                var sheet = k?["kill"]?["score"] != null && k["id"]?.ToString() is { Length: > 0 } sid && sid.All(char.IsAsciiDigit) ? sid : null; // digits only: the id becomes a file name
                var photo = k?["kill"]?["photo"]?.ToString() is { Length: > 0 } ph ? ph : null;
                if (sheet != null || photo != null) found.Add((sheet, photo));
            }
        }
        if (got < ids.Count || HuntsListFailed)
        {
            (HuntsSaved, HuntsListed) = (got, ids.Count);
            log(HuntsListFailed ? $"! hunts: a list page failed, {got} of the {ids.Count} listed saved" : $"! hunts: {got} of {ids.Count} listed saved");
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

    /// <summary>A reply number: a JSON number or a numeric string (this API sends both), "250.0" included; a value that is not a whole number, or not a number, is null.</summary>
    static long? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        double d = 0;
        if (!(v.TryGetValue<double>(out d) || (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)))) return null;
        return double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : null;
    }

    /// <summary>Unread conversations opened (fetched) in this run: the server probably shows them as read afterwards.</summary>
    public int MessagesOpened { get; private set; }

    async Task Messages()
    {
        Need(1);
        // opening a thread can mark it read: the list is saved first as the original state
        var list = Json(await Get(Q("Chat/conversations", true) with { Keep = true })) as JsonArray;
        var entries = (list ?? []).OfType<JsonObject>().Where(c => c["user_id"] != null).ToList();
        Need(entries.Count);
        var rows = new JsonArray();
        bool Unread(JsonObject c) => c["read"]?.ToString() is "0" or "false";
        foreach (var c in entries.Where(c => !Unread(c)).Concat(entries.Where(Unread)))
        {
            var id = c["user_id"]!.ToString();
            var r = Q("Chat/conversation", true, ("user_id", id));
            var name = FileName(r);
            var had = resume && File.Exists(Path.Combine(dir, name));
            if (Json(await Get(r)) is not JsonObject th) continue;
            if (Unread(c) && !had) MessagesOpened++;
            var count = (th["messages"] as JsonArray)?.Count ?? 0;
            var total = Num(th["total"]);
            log($"messages {id}: total {total}, {count} in reply");
            rows.Add(new JsonObject { ["user_id"] = c["user_id"]!.DeepClone(), ["read"] = c["read"]?.DeepClone(), ["file"] = name, ["total"] = total, ["messages"] = count });
        }
        if (list == null) return;
        var capped = new JsonArray([.. rows.Cast<JsonObject>().Where(x => Num(x["total"]) != (int)x["messages"]!).Select(x => x["user_id"]!.DeepClone())]);
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["format"] = 1, ["tool_version"] = Version, ["uid"] = uid, ["threads"] = rows, ["checks"] = new JsonObject { ["possibly_capped"] = capped } }.ToJsonString(J));
        WriteAtomic(Path.Combine(dir, "messages-index.json"), bytes);
        lock (files) files["messages-index.json"] = new("messages-index.json", "derived", [], null, Hex(SHA256.HashData(bytes)), DateTime.UtcNow, null, true);
        Interlocked.Increment(ref saved);
        if (capped.Count > 0) log($"! messages: {capped.Count} thread(s) with a total different from the messages returned (possibly capped)");
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

    internal static string FileName(R r) => Clean(r.Path.TrimStart('/')[(r.Path.StartsWith("/v1/") ? 3 : 0)..] + string.Concat(Pub(r).Select(p => $"__{p.K}={p.V}"))) + ".json";

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
        Entry? gone = null;
        if (resume && !r.Fresh && r.Abs != null) lock (files) files.TryGetValue(name, out gone);
        if (gone?.Error == DeadError)
        {
            Interlocked.Increment(ref skipped); // known to be gone: not asked again by the run that goes on
            Interlocked.Increment(ref dead);
            return null;
        }
        Entry? known = null;
        if ((resume || r.Keep) && !r.Fresh) lock (files) files.TryGetValue(name, out known);
        // an optional reply with no status on record can be a 404 that a killed run saved before its manifest was written: ask again so the answer keeps its status
        if ((resume || r.Keep) && !r.Fresh && File.Exists(full) && !(r.Opt && known?.Status is null))
        {
            byte[] b;
            if (r.Abs != null && known?.Sha256 != null) b = []; // a picture: nothing parses it, do not read it back
            else
            {
                b = File.ReadAllBytes(full);
                if (known?.Sha256 == null) lock (files) files[name] = new(name, r.Path, Params(r), null, Hex(SHA256.HashData(b)), File.GetLastWriteTimeUtc(full), null, Date: known?.Date);
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
        if ((r.Auth || r.Jwt) && own) Renew();
        if ((r.Auth || r.Jwt) && (token == null || TokenStale)) return Fail(r, name, null, "needs the game session (missing or refused)");
        if (r.Jwt && !await EnsureJwt()) return Fail(r, name, null, "no JWT", ServerError);
        log($"{(r.V2 || r.Abs != null ? "GET" : "POST")} {r.Path} {string.Join('&', Pub(r).Select(p => $"{p.K}={p.V}"))}".TrimEnd());
        var jwtRetried = false;
        for (int poll = 0; ; poll++)
        {
            string? why = null;
            if (await Send(r, e => why = e) is not { } x) return Fail(r, name, null, why ?? "network failure", ServerError);
            if (r.Jwt && x.Status == 401 && !jwtRetried)
            {
                // expired (or made for another address): one new JWT, and this request once more
                (jwtRetried, jwt) = (true, null);
                if (await EnsureJwt()) { poll--; continue; }
            }
            if (r.Abs != null && x.Status == 404) // a picture or sheet that is gone for good: say so, it is not a failure to run again
            {
                log($"gone from the server: {r.Path}");
                Interlocked.Increment(ref dead);
                lock (files) files[name] = new(name, r.Path, Params(r), 404, null, DateTime.UtcNow, DeadError);
                return null;
            }
            if (r.Opt && x.Status == 404) { Save(r, name, 404, x.Body, x.Date); return x.Body; } // "no entry" is an answer, not a failure
            if (!Ok(r, x.Status, x.Body)) return Fail(r, name, x.Status, TokenStale && r.Abs == null ? "token refused" : "error reply");
            if (Leaks(r, x.Body)) return Fail(r, name, x.Status, "reply contains a token, not saved");
            if (!IsData(r, x.Body)) return Fail(r, name, x.Status, "reply is not data, not saved", NotData);
            if (ready != null && !ready(x.Body))
            {
                if (poll == 19) return Fail(r, name, x.Status, "still building", ServerError);
                await Task.Delay(PaceMs == 0 ? 0 : 3000, ct);
                continue;
            }
            Save(r, name, x.Status, x.Body, x.Date);
            return x.Body;
        }
    }

    /// <summary>A newer token from the game replaces the old one, clears "refused" and drops the JWT, which belongs to the token it was made with.</summary>
    void Renew()
    {
        if (tokenOf?.Invoke() is not { } now) return;
        lock (renewing)
        {
            if (now == token) return;
            (token, jwt, jwtAt, TokenStale) = (now, null, default, false);
            log("new game session found");
        }
    }

    static Dictionary<string, string> Params(R r) => Pub(r).ToDictionary(p => p.K, p => p.V);

    byte[]? Fail(R r, string name, int? status, string err, string kind = "error reply")
    {
        lock (problems)
        {
            var g = group.Value ?? "lookup";
            if (kind == ServerError || !problems.TryGetValue(g, out var had) || (kind == NotData && had != ServerError)) problems[g] = kind; // a server error is the one worth retrying later, then a reply that held no data
        }
        log($"! {r.Path}: {err}");
        Interlocked.Increment(ref failed);
        lock (files) if (!files.TryGetValue(name, out var e) || e.Sha256 == null) files[name] = new(name, r.Path, Params(r), status, null, DateTime.UtcNow, err);
        return null;
    }

    void Save(R r, string name, int status, byte[] body, DateTimeOffset? date)
    {
        var full = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WriteAtomic(full, body);
        lock (files) files[name] = new(name.Replace('\\', '/'), r.Path, Params(r), status, Hex(SHA256.HashData(body)), DateTime.UtcNow, null, Date: date?.UtcDateTime);
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

    /// <summary>What a reply must be before it is saved: JSON for the API (null is allowed, an empty or cut-off body is not), an image for a picture, a page with a table for a score sheet. A proxy's or login page is none of them.</summary>
    static bool IsData(R r, byte[] b)
    {
        if (r.Abs == null)
        {
            try { using var _ = JsonDocument.Parse(b); return true; } catch (JsonException) { return false; }
        }
        if (r.Xhr) return Encoding.UTF8.GetString(b).Contains("<table", StringComparison.OrdinalIgnoreCase);
        var s = b.AsSpan();
        return s.StartsWith<byte>([255, 216, 255]) // JPEG
            || s.StartsWith<byte>([137, 80, 78, 71]) // PNG
            || s.StartsWith("GIF8"u8)
            || (s.Length >= 12 && s.StartsWith("RIFF"u8) && s[8..12].SequenceEqual("WEBP"u8));
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

    async Task<(int Status, byte[] Body, DateTimeOffset? Date)?> Send(R r, Action<string>? gaveUp = null)
    {
        var lane = lanes.GetOrAdd(Host(r), _ => new());
        await lane.Gate.WaitAsync(ct); // held through retries: a host that answers badly is not hit by a second request
        try
        {
            if (lane.Spent)
            {
                gaveUp?.Invoke("host not answering, not tried");
                return null;
            }
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
                    // a 403 on a picture or a score sheet is the answer for that file, not an outage
                    if (!(st is 407 or 429 or >= 500 || (st == 403 && r.Abs == null))) { (lane.Dead, lane.GiveUps) = (false, 0); return (st, body, resp.Headers.Date); }
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
                    if (++lane.GiveUps >= GiveUpLimit)
                    {
                        lane.Spent = true;
                        log($"! {Host(r)} stopped answering: its other requests are not tried");
                    }
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
            // the folder holds own-account files (an old manifest has no mode: its Me/me reply tells): a public run continuing it keeps it own
            if (m?.Mode == "own" || (m != null && m.Mode == null && (m.Files ?? []).Any(e => e.Path == "Me_me.json"))) identity = "me";
        }
        catch (JsonException) { } // damaged manifest: files on disk are re-registered by resume
    }

    void WriteManifest()
    {
        Entry[] list;
        lock (files) list = [.. files.Values.OrderBy(e => e.Path, StringComparer.Ordinal)];
        var checks = HuntsListed is { } hl ? new JsonObject { ["hunts"] = new JsonObject { ["listed"] = hl, ["saved"] = HuntsSaved } } : null;
        if (HuntsListFailed && checks?["hunts"] is JsonObject hc) hc["list_failed"] = true; // only when true
        var m = new Manifest(Version, [.. groupsSeen], uid, handle, claimHash!, started, [.. list], steamId64, completed, Format: 2, Mode: identity == "me" ? "own" : "public", Identity: identity, Checks: checks);
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
