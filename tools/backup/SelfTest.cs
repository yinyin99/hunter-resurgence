using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backup;

/// <summary>`backup.exe --selftest`: no live host is contacted (fake HTTP handler, harmless fake process).</summary>
static class SelfTest
{
    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("FAIL " + what);
        Console.WriteLine("ok   " + what);
    }

    sealed class Fake : HttpMessageHandler
    {
        public readonly List<string> Seen = [];
        public readonly string Token;
        public int DelayMs;
        /// <summary>This host always answers 502.</summary>
        public string? DeadHost;
        /// <summary>Called under the lock when request number n (1-based) starts; true = cancel the run right there.</summary>
        public Func<int, bool>? CancelAt;
        public CancellationTokenSource? Cts;
        public int StartedAtCancel = -1;
        public readonly Dictionary<string, int> Active = [], MaxActive = [];
        public readonly Dictionary<string, List<DateTime>> Starts = [];
        public bool Overlap; // two different hosts had a request in flight at the same time
        readonly object gate = new();
        public Fake(string token) => Token = token;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var host = req.RequestUri.Host;
            var form = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
            lock (gate)
            {
                ct.ThrowIfCancellationRequested(); // a real handler refuses a cancelled request too
                Seen.Add(path);
                Starts.TryAdd(host, []);
                Starts[host].Add(DateTime.UtcNow);
                Active[host] = Active.GetValueOrDefault(host) + 1;
                MaxActive[host] = Math.Max(MaxActive.GetValueOrDefault(host), Active[host]);
                if (Active.Count(a => a.Value > 0) > 1) Overlap = true;
                if (CancelAt?.Invoke(Seen.Count) == true) { Cts!.Cancel(); StartedAtCancel = Seen.Count; }
            }
            try { if (DelayMs > 0) await Task.Delay(DelayMs, CancellationToken.None); }
            finally { lock (gate) Active[host]--; }
            if (path.Contains("/personal/") || path == "/game/user/harvest/102") return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("null") };
            if (host == DeadHost) return new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = new StringContent("bad gateway") };
            var body = path switch
            {
                "/v1/Me/me" => form.Contains(Token) ? """{"id":7,"handle":"hh","worlditems":[]}""" : """{"errorCode":11,"message":"access denied"}""",
                "/v1/Application/application" => """{"season":3}""",
                "/game/user/leaderboards/antlers/single/all" => """[{"id":5},{"id":6}]""",
                "/game/user/leaderboards/antlers/double/all" => """[{"id":9}]""",
                "/game/user/trophies" => """[{"id":101},{"id":102}]""",
                "/v1/Public_user/expedition" => """{"kills":[{"id":900,"kill":{"score":1.5,"photo":"https://photo.test/k.jpg"}},{"id":901,"kill":{"score":null}}]}""",
                "/v1/Public_user/getByHostname" => """{"id":7,"handle":"hh"}""",
                "/v1/User/jwt" => """{"token":"fake.jwt.value"}""",
                "/v1/Leaderboard/personal" => """{"status":"done","data":[]}""",
                "/game/user/7/lodges" => """[{"id":3}]""",
                "/v1/Expedition/list" => """{"total":1,"expeditions":[{"id":42}]}""",
                "/v1/Gallery/list" => """{"photos":[{"url":"https://photo.test/g1.jpg"},{"url":"https://other.test/g2.jpg"}]}""",
                "/v1/Mission/missions" => $$"""{"leak":"{{Token}}"}""", // a reply carrying the token must never be saved
                "/v1/Wallet/transactions" or "/v1/Wallet/silver_transactions" => "[]",
                _ => """{"ok":1}""",
            };
            return new HttpResponseMessage { Content = new StringContent(body) };
        }
    }

    static Engine Make(Fake f, string keyPath) => new(
        new("api.test", "apiv2.test", "web.test", "fakeclient.exe", ["photo.test"], "ckey-secret-9"), new HttpClient(f), _ => { }, (_, _) => { }, keyPath) { PaceMs = 0, BackoffMs = 0, SteamAccount = () => 100 };

    public static int Run()
    {
        try
        {
            Parser();
            Lanes(Manifest().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            Watch();
            Console.WriteLine("selftest ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            return 1;
        }
    }

    static void Parser()
    {
        string h = new('1', 40), a = new('a', 40), r = new('b', 40);
        foreach (var scheme in new[] { "hunt", "huntbeta", "huntdev" })
            Check(Watcher.ParseToken($"C:\\g\\client.exe /connect:{scheme}://gs.test/{h}/lang/en_US/access_token/{a}/refresh_token/{r} /param:169 /steam") == a, "parse " + scheme);
        Check(Watcher.ParseToken($"\"C:\\g\\client.exe\" \"/connect:hunt://gs.test/{h}/lang/en_US/access_token/{a}/refresh_token/{r}\"") == a, "parse quoted argument");
        Check(Watcher.ParseToken($"x /connect:hunt://gs.test/{h}/lang/en_US/access_token/{a[..39]}/refresh_token/{r}") == null, "short token rejected");
        Check(Watcher.ParseToken($"x /connect:other://gs.test/{h}/lang/en_US/access_token/{a}/refresh_token/{r}") == null, "other scheme rejected");
        Check(Watcher.ParseToken("C:\\g\\launcher.exe") == null, "launcher command line has no token");
    }

    /// <summary>Returns the files and request count of one complete run, the baseline of the cancel test.</summary>
    static async Task<(HashSet<string> Files, int Requests)> Manifest()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "backup-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var key = Path.Combine(tmp, "claim.key");
        string tok = new('a', 40);
        var groups = new HashSet<string> { "profile", "inventory", "trophies", "hunts", "account", "friends", "messages" };
        try
        {
            var f1 = new Fake(tok);
            var res = await Make(f1, key).Run(tmp, "", tok, groups, true, true, default);
            var dir = Path.Combine(tmp, "backup-7");
            Check(res.EndsWith(".zip") && File.Exists(res), "zip written");
            var m = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;
            Check(m["uid"]!.ToString() == "7" && m["handle"]!.ToString() == "hh" && m["tool_version"] != null, "manifest uid, handle, version");
            var files = m["files"]!.AsArray();
            var good = files.Where(e => e!["error"] == null).ToList();
            Check(good.Count > 10 && good.All(e => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, e!["path"]!.ToString())))) == e["sha256"]!.ToString()), "manifest sha256 matches the files");
            Check(files.All(e => !e!["params"]!.AsObject().ContainsKey("oauth_access_token")), "no token in manifest params");
            Check(!File.Exists(Path.Combine(dir, "Mission_missions.json")) && files.Any(e => e!["path"]!.ToString() == "Mission_missions.json" && e["error"] != null), "reply with the token not saved");
            Check(Directory.GetFiles(dir).Where(p => !p.EndsWith("claim.key")).All(p => !File.ReadAllText(p).Contains(tok) && !File.ReadAllText(p).Contains("fake.jwt.value")), "no token or JWT in any file");
            var k = File.ReadAllBytes(Path.Combine(dir, "claim.key"));
            Check(k.Length == 32 && m["claim_hash"]!.ToString() == Convert.ToHexStringLower(SHA256.HashData(k)), "claim hash matches claim.key");
            Check(m["steam_id64"]?.ToString() == "76561197960265828", "own account: Steam id 100 -> steam_id64 76561197960265828");
            Check(good.Any(e => e!["path"]!.ToString() == "Public_user_expedition__user_id=7__expedition_id=42.json") && f1.Seen.Count(p => p == "/v1/User/jwt") == 1, "expedition detail fetched, one JWT");

            var f2 = new Fake(tok);
            await Make(f2, key).Run(tmp, "", tok, groups, true, false, default);
            Check(f2.Seen.Count == 2 && f2.Seen.Contains("/v1/Me/me") && f2.Seen.Contains("/v1/Mission/missions"), $"resume asks only identity and the unsaved reply ({f2.Seen.Count} requests)");
            var fz = new Fake(tok);
            var ez = Make(fz, key);
            ez.SteamAccount = () => 0;
            await ez.Run(tmp, "", tok, groups, true, false, default);
            Check(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject().ContainsKey("steam_id64") == false, "Steam not signed in (0): no steam_id64 in the manifest");

            File.Delete(Path.Combine(dir, "manifest.json")); // crash before the manifest was written
            var f3 = new Fake(tok);
            await Make(f3, key).Run(tmp, "", tok, groups, true, false, default);
            var m3 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
            Check(f3.Seen.Count == 2 && m3.Count(e => e!["error"] == null) == good.Count, "resume re-registers files without a manifest");

            var f4 = new Fake("z");
            var e4 = await Assert.Throws(() => Make(f4, key).Run(tmp, "", tok, groups, true, false, default));
            Check(e4.Message.Contains("start a hunt"), "errorCode 11 -> start a hunt to refresh");
            return (good.Select(e => e!["path"]!.ToString()).ToHashSet(), f1.Seen.Count);
        }
        finally { Directory.Delete(tmp, true); }
    }

    static async Task Lanes((HashSet<string> Files, int Requests) full)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "backup-selftest-lanes-" + Guid.NewGuid().ToString("N")[..8]);
        var key = Path.Combine(tmp, "claim.key");
        string tok = new('a', 40);
        var groups = new HashSet<string> { "profile", "inventory", "trophies", "hunts", "account", "friends", "messages" };
        var dir = Path.Combine(tmp, "backup-7");
        try
        {
            var f = new Fake(tok) { DelayMs = 15 };
            await Make(f, key).Run(tmp, "", tok, groups, false, false, default);
            Check(f.MaxActive["api.test"] == 1, $"api host: never two requests at once ({f.Starts["api.test"].Count} requests)");
            Check(f.MaxActive["apiv2.test"] == 1 && f.Overlap, $"apiv2 lane runs beside the api lane ({f.Starts["apiv2.test"].Count} requests)");

            Directory.Delete(dir, true);
            var fp = new Fake(tok);
            var ep = Make(fp, key);
            ep.PaceMs = 120;
            await ep.Run(tmp, "", tok, new HashSet<string> { "profile", "friends" }, false, false, default);
            var gaps = fp.Starts.Where(h => h.Value.Count > 1).Select(h => h.Value.Zip(h.Value.Skip(1), (a, b) => (b - a).TotalMilliseconds).Min()).ToList();
            Check(gaps.Count == 2 && gaps.All(g => g >= 100), $"each host is paced (smallest gaps {string.Join(" and ", gaps.Select(g => (int)g + " ms"))} for 120 ms)");

            Directory.Delete(dir, true);
            using var cts = new CancellationTokenSource();
            var fc = new Fake(tok) { DelayMs = 10, Cts = cts, CancelAt = n => n == 8 };
            var ex = await Assert.Throws(() => Make(fc, key).Run(tmp, "", tok, groups, true, false, cts.Token));
            Check(ex is OperationCanceledException, "cancel ends the run with OperationCanceledException");
            Check(fc.StartedAtCancel == 8 && fc.Seen.Count == 8, $"no request starts after cancel ({fc.Seen.Count} started)");
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).Select(e => e!["path"]!.ToString()).ToList();
            Check(saved.Count > 0 && saved.All(p => File.Exists(Path.Combine(dir, p))) && !Directory.EnumerateFiles(dir, "*.part").Any(), $"cancelled run left a manifest of {saved.Count} saved files");

            var fr = new Fake(tok);
            await Make(fr, key).Run(tmp, "", tok, groups, true, false, default);
            var after = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray().Where(e => e!["error"] == null).Select(e => e!["path"]!.ToString()).ToHashSet();
            Check(after.SetEquals(full.Files), "resumed run completes the cancelled one");
            Check(fr.Seen.Count < full.Requests, $"resume asked {fr.Seen.Count} requests instead of {full.Requests}");

            Directory.Delete(dir, true);
            var fg = new Fake(tok);
            var eg = Make(fg, key);
            await eg.Run(tmp, "", tok, new HashSet<string> { "profile", "trophies", "kills", "account" }, false, false, default);
            var mg = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
            JsonNode? Entry(string path) => mg.FirstOrDefault(e => e!["path"]!.ToString() == path);
            Check(fg.Seen.Count(p => p == "/v1/Leaderboard/personal") == 4 && Entry("Leaderboard_personal__user_id=7__season_no=3.json") is { } && Entry("Leaderboard_personal__user_id=7__season_no=4.json") == null,
                "season loop: Leaderboard/personal for season 0 to the catalogue's season 3");
            Check(Entry("Application_application__lang=en.json") is { } && !File.ReadAllText(Path.Combine(dir, "manifest.json")).Contains("ckey-secret-9") && !mg.Any(e => e!["path"]!.ToString().Contains("ckey")),
                "catalogue saved, consumer key in no file name, param or manifest");
            var personal = mg.Where(e => e!["endpoint"]!.ToString().Contains("/personal/7")).ToList();
            Check(personal.Count == 3 && personal.All(e => e!["error"] == null && (int)e["status"]! == 404), "antler boards: one personal request per board, 404 = saved as empty, not failed");
            Check(Entry("game_user_harvest_101.json") is { } && (int)Entry("game_user_harvest_102.json")!["status"]! == 404 && eg.Failed == 1, "harvest record per own trophy, 404 saved as empty (only the token-leak reply failed)");
            Check(fg.Seen.Count(p => p == "/user/x/score/") == 1 && fg.Seen.Count(p => p == "/k.jpg") == 1 && Entry("kills/score_900.html") is { }, "kills: score page and photo only for the scored kill");

            Directory.Delete(dir, true);
            var fd = new Fake(tok) { DeadHost = "apiv2.test" };
            var ed = Make(fd, key);
            ed.BackoffMs = 20;
            await ed.Run(tmp, "hh", null, new HashSet<string> { "profile", "trophies" }, false, false, default);
            Check(ed.Failed > 2 && fd.Starts["apiv2.test"].Count == 5 + (ed.Failed - 1), $"dead host: one back-off cycle, then one attempt per request ({fd.Starts["apiv2.test"].Count} attempts for {ed.Failed} requests)");
            Check(ed.FailureSummary == "Some profile pages did not answer (server error). Some trophies and lodges pages did not answer (server error).", "result bar names the groups that did not answer");

            Directory.Delete(dir, true);
            var fu = new Fake(tok);
            var eu = Make(fu, key);
            await eu.Run(tmp, "hh", null, groups, false, false, default);
            string[] gameOnly = ["/v1/Me/me", "/v1/Me/friends", "/v1/User/jwt", "/game/user/loadout", "/game/user/trophies", "/v1/Chat/conversations", "/v1/Wallet/spent",
                "/v1/Page_content/competition_states", "/v1/Page_content/competition_community_league_states", "/v1/Chat/get_message_settings", "/v1/Recruit/get_recruits", "/v1/Mission/missions",
                "/v1/Daily_Mission/get_daily_missions_calendar", "/v1/Wallet/bank_transactions", "/v1/Wallet/transactions", "/v1/Wallet/silver_transactions", "/game/user/backgrounds"];
            var mu = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
            Check(!fu.Seen.Any(p => p.StartsWith("/game/user/harvest/")) && eu.Failed == 0 && mu.All(e => e!["error"] == null) && !fu.Seen.Intersect(gameOnly).Any(), $"public profile: no game-only request, 0 failed ({fu.Seen.Count} requests)");
            Check(!JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!.AsObject().ContainsKey("steam_id64"), "public profile: never a steam_id64 even when Steam is signed in");

            Directory.Delete(dir, true);
            var fq = new Fake(tok);
            var eq = Make(fq, key);
            await eq.Run(tmp, "hh", null, new HashSet<string> { "photos" }, false, false, default);
            var mq = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!.AsArray();
            var skipped = mq.Where(e => e!["error"]?.ToString() == "not an https URL on a configured host").ToList();
            Check(eq.Failed == 1 && skipped.Count == 1 && fq.Seen.Count(p => p == "/g1.jpg") == 1 && !fq.Seen.Contains("/g2.jpg"), "gallery photo on an unconfigured host: one failed, one manifest entry with the reason, not fetched");
        }
        finally { Directory.Delete(tmp, true); }
    }

    static class Assert
    {
        public static async Task<Exception> Throws(Func<Task> f)
        {
            try { await f(); } catch (Exception e) { return e; }
            throw new Exception("FAIL expected an exception");
        }
    }

    static void Watch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backup-selftest-game-" + Guid.NewGuid().ToString("N")[..8]);
        var procs = new List<Process>();
        try
        {
            // a renamed copy of cmd.exe (the 32-bit one, like the real client, when present) that sleeps with the fake argument after `rem`
            var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "cmd.exe");
            var src = File.Exists(wow) ? wow : Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process Spawn(string sub, string tok)
            {
                Directory.CreateDirectory(Path.Combine(dir, sub));
                var exe = Path.Combine(dir, sub, "fakeclient.exe");
                File.Copy(src, exe);
                var p = Process.Start(new ProcessStartInfo(exe, $"/c ping -n 40 127.0.0.1 >nul & rem /connect:hunt://fake.invalid/{new string('1', 40)}/lang/en_US/access_token/{tok}/refresh_token/{new string('d', 40)} /param:1 /steam") { UseShellExecute = false, CreateNoWindow = true })!;
                procs.Add(p);
                return p;
            }
            var w = new Watcher("fakeclient.exe");
            Check(!w.Scan() && w.Token == null, "no token without the game");
            string t1 = new('c', 40), t2 = new('e', 40);
            Spawn("one", t1);
            Check(Poll(w, t1), $"watcher reads the token from a fake process ({Path.GetFileName(src)} in {Path.GetFileName(Path.GetDirectoryName(src))})");
            w.Stale = true;
            Spawn("two", t2);
            Check(Poll(w, t2) && !w.Stale, "a new process replaces the token and clears stale");
            foreach (var p in procs) { p.Kill(true); p.WaitForExit(); p.Dispose(); }
            procs.Clear();
            for (int i = 0; i < 40 && (w.Scan() || w.GameSeen); i++) Thread.Sleep(250); // a killed process can stay listed for a moment on a busy machine
            Check(w.Token == t2 && !w.GameSeen, "token is kept after the game exits");
        }
        finally
        {
            foreach (var p in procs.Where(p => !p.HasExited)) p.Kill(true);
            foreach (var p in procs) p.WaitForExit();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static bool Poll(Watcher w, string tok)
    {
        for (int i = 0; i < 40; i++)
        {
            w.Scan();
            if (w.Token == tok) return true;
            Thread.Sleep(250);
        }
        return false;
    }
}
