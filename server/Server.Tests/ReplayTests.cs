using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;

namespace Resurgence.Server.Tests;

/// <summary>Routing through the real HTTP pipeline (Loopback): endpoints, fall-through to the replay, misses.</summary>
public class ReplayTests : IAsyncLifetime
{
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    readonly string tmp = Directory.CreateTempSubdirectory("replay").FullName;
    readonly string mirror;
    readonly HarTable table;
    Loopback server = null!;

    static JsonObject Entry(string method, string url, int status, string started, string text = "", string? encoding = null,
        (string, string)[]? headers = null, string? post = null)
    {
        var content = new JsonObject { ["text"] = text };
        if (encoding != null) content["encoding"] = encoding;
        var req = new JsonObject { ["method"] = method, ["url"] = url };
        if (post != null) req["postData"] = new JsonObject { ["mimeType"] = "application/x-www-form-urlencoded", ["text"] = post };
        return new JsonObject
        {
            ["startedDateTime"] = started,
            ["request"] = req,
            ["response"] = new JsonObject
            {
                ["status"] = status,
                ["headers"] = new JsonArray((headers ?? []).Select(h => (JsonNode)new JsonObject { ["name"] = h.Item1, ["value"] = h.Item2 }).ToArray()),
                ["content"] = content,
            },
        };
    }

    static string Har(params JsonObject[] entries) =>
        Py.Dumps(new JsonObject { ["log"] = new JsonObject { ["entries"] = new JsonArray(entries) } });

    public ReplayTests()
    {
        mirror = Path.Combine(tmp, "mirror");
        JsonObject[] first =
        [
            Entry("POST", "https://api.test/v1/Me/me?x=1", 200, "2030-10-02T00:00:00", "{\"new\": 1}"),
            Entry("GET", "https://web.test/login", 302, "2030-10-01T00:00:00", headers: [("Location", "/home")]),
            Entry("GET", "https://static.test/a.png", 304, "2030-10-01T00:00:00"),
            Entry("GET", "https://static.test/b.png", 200, "2030-10-01T00:00:00", Convert.ToBase64String(Png),
                "base64", [("Content-Encoding", "gzip"), ("Content-Length", "99"), ("Content-Type", "image/png")]),
            Entry("POST", $"https://gameserver.test{HEARTBEAT}", 200, "2030-10-03T00:00:00", "{\"updates\": [{\"update\": 6}], \"err\": 0}"),
            Entry("POST", "https://api.test/v1/Splash/list_active", 200, "2030-10-01T00:00:00",
                "[{\"id\": 7, \"priority\": 8, \"subject\": \"S\", \"image_url\": \"i\", \"link\": \"l\"}]"),
            Entry("POST", "https://api.test/v1/Page_content/competitions_new", 200, "2030-10-01T00:00:00", "{\"c\": 1}",
                post: "id=1&lang=en_US&oauth_access_token=a"),
            Entry("POST", "https://api.test/v1/Page_content/competitions_new", 200, "2030-10-02T00:00:00", "{\"c\": 2}",
                post: "id=2&lang=en_US&oauth_access_token=b"),
            Entry("GET", "https://news.test/api/get_recent_posts/?callback=jQ1&_=5", 200, "2030-10-01T00:00:00", "jQ1({\"a\": 1})"),
        ];
        JsonObject[] second =
        [
            Entry("POST", "https://api.test/v1/Me/me", 200, "2030-10-01T00:00:00", "{\"old\": 1}"),
            Entry("GET", "https://web.test/login", 304, "2030-10-05T00:00:00"),
        ];
        var paths = new List<string>();
        foreach (var (i, entries) in new[] { first, second }.Index())
        {
            var p = Path.Combine(tmp, $"{i}{Recordings.Ext}");
            File.WriteAllText(p, Har(entries));
            paths.Add(p);
        }
        table = BuildTable(paths);
    }

    public async ValueTask InitializeAsync() => server = await Loopback.StartAsync(new Game(table, Cfg, null, mirror));

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        Fixtures.DeleteDir(tmp);
    }

    static void Same((byte[] Body, string Source) expected, Reply actual)
    {
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Source, actual.Source);
    }

    Task<Reply> R(string method, string host, string path, params (string, string)[] headers) => server.SendAsync(method, host, path, headers);

    static JsonObject WithPopulations(string[] pops)
    {
        var c = (JsonObject)Fixtures.CONNECT.DeepClone();
        c["populations"] = new JsonArray(pops.Select(p => (JsonNode)p).ToArray());
        c["species_ammo"] = new JsonArray(pops.Select(p => (JsonNode)p).ToArray());
        return c;
    }

    [Fact]
    public async Task TestLatest200WinsRegardlessOfFileOrder() =>
        Assert.Equal("{\"new\": 1}"u8.ToArray(), (await R("POST", "api.test", "/v1/Me/me")).Body);

    [Fact]
    public async Task TestMeReportsStateWallet()
    {
        var state = new State(Path.Combine(tmp, "state.db"), Py.DumpBytes(Fixtures.CONNECT), Fixtures.BASE, Fixtures.ICON,
            Fixtures.APP, Fixtures.LOCALE, Fixtures.TEXTURES);
        await using var withState = await Loopback.StartAsync(new Game(table, Cfg, state, mirror));
        var r = await withState.SendAsync("POST", "api.test", "/v1/Me/me");
        var body = JsonNode.Parse(r.Body)!;
        Assert.Equal(33.0, (double)body["gms"]!);
        Assert.Equal(1, (int)body["new"]!);
        Assert.Equal("state", r.Source);
    }

    [Fact]
    public async Task TestRedirectBeatsLater304()
    {
        var r = await R("GET", "web.test", "/login");
        Assert.Equal((302, "har"), (r.Status, r.Source));
        Assert.Contains(("Location", "/home"), r.Headers);
    }

    [Fact]
    public async Task TestReplyFollowsFormParams()
    {
        async Task<byte[]> Competitions(string form) => (await server.SendAsync("POST", "api.test", "/v1/Page_content/competitions_new",
            [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes(form))).Body;
        Assert.Equal("{\"c\": 1}"u8.ToArray(), await Competitions("id=1&lang=en_US&oauth_access_token=z"));
        Assert.Equal("{\"c\": 2}"u8.ToArray(), await Competitions("id=2&lang=en_US&oauth_access_token=z"));
        Assert.Equal("{\"c\": 2}"u8.ToArray(), await Competitions("id=3&lang=en_US&oauth_access_token=z"));
    }

    [Fact]
    public async Task TestJsonpUsesRequestedCallback()
    {
        async Task<byte[]> News(string query) => (await R("GET", "news.test", "/api/get_recent_posts/?" + query)).Body;
        Assert.Equal("jQ2({\"a\": 1})"u8.ToArray(), await News("callback=jQ2&_=9"));
        Assert.Equal("jQ1({\"a\": 1})"u8.ToArray(), await News("callback=x);alert(1"));
    }

    [Fact]
    public async Task Test304OnlyIsWeakEmpty200()
    {
        var r = await R("GET", "static.test", "/a.png");
        Assert.Equal((200, 0, "weak"), (r.Status, r.Body.Length, r.Source));
    }

    [Fact]
    public async Task TestBase64DecodedAndEncodingHeadersDropped()
    {
        var r = await R("GET", "static.test", "/b.png");
        Assert.Equal(Png, r.Body);
        Assert.Equal(["Content-Type"], r.Headers.Select(h => h.Name));
    }

    [Fact]
    public async Task TestHeartbeatIsIdleStub() =>
        Same(("{\"updates\": [], \"err\": 0}"u8.ToArray(), "stub"), await R("POST", "gameserver.test", HEARTBEAT));

    [Fact]
    public async Task TestSplashGetsServerNoticeFirst()
    {
        var r = await R("POST", "api.test", "/v1/Splash/list_active");
        var list = (JsonArray)JsonNode.Parse(r.Body)!;
        Assert.Equal("patch", r.Source);
        Assert.Equal(Replay.NoticeSubject, (string)list[0]!["subject"]!);
        Assert.Equal(Replay.NoticeText, (string)list[0]!["description"]!);
        Assert.Equal(9, (int)list[0]!["priority"]!);
        Assert.Equal("i", (string)list[0]!["image_url"]!);
        Assert.Equal(7, (int)list[1]!["id"]!);
    }

    [Fact]
    public async Task TestMirrorFallbackAndNoEscape()
    {
        var f = Path.Combine(mirror, "static.test", "img", "c.png");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, "img"u8.ToArray());
        Same(("img"u8.ToArray(), "mirror"), await R("GET", "static.test", "/img/c.png"));
        Assert.Equal("miss", (await R("GET", "static.test", $"/../../0{Recordings.Ext}")).Source);
    }

    [Fact]
    public async Task TestArchiveExactHitsBeforeMirrorBasenameHitsAfter()
    {
        var d = Path.Combine(tmp, "game", "archives");
        Directory.CreateDirectory(d);
        (uint Hash, byte[] Data, uint Flags)[] records =
        [
            (ArcList.NameHash("x.png"), "item"u8.ToArray(), 0),  // only x.png in the archives, any directory
            (ArcList.NameHash("0.jpg"), "other dir"u8.ToArray(), 1),  // basename in several dirs
            (ArcList.HashLittle(Encoding.ASCII.GetBytes(@"webgui\static\map\v7\tiles\1\0\0.jpg")), "tile"u8.ToArray(), 0),
        ];
        var arc = new MemoryStream();
        using (var tab = new BinaryWriter(File.Create(Path.Combine(d, "pc0.tab"))))
        {
            tab.Write(0x800u);
            foreach (var (h, data, flags) in records)
            {
                tab.Write(h);
                tab.Write((uint)arc.Length);
                tab.Write((uint)data.Length);
                tab.Write(flags);
                arc.Write(data);
            }
        }
        File.WriteAllBytes(Path.Combine(d, "pc0.arc"), arc.ToArray());
        foreach (var (host, path) in new[] { ("static.test", "/static/img/items/256/x.png"), ("map.test", "/v7/tiles/1/0/0.jpg"), ("map.test", "/v7/tiles/2/0/0.jpg") })
        {
            var f = Path.Combine(mirror, host, path.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, "mirror"u8.ToArray());
        }
        var cfg = (JsonObject)Cfg.DeepClone();
        cfg["install_dir"] = tmp;
        await using var withArchives = await Loopback.StartAsync(new Game(table, cfg, null, mirror));
        Task<Reply> Get(string host, string path) => withArchives.SendAsync("GET", host, path);
        Same(("tile"u8.ToArray(), "archive"), await Get("map.test", "/v7/tiles/1/0/0.jpg"));
        Same(("mirror"u8.ToArray(), "mirror"), await Get("static.test", "/static/img/items/256/x.png"));
        Same(("item"u8.ToArray(), "archive"), await Get("static.test", "/static/img/items/1024/x.png"));
        Same(("mirror"u8.ToArray(), "mirror"), await Get("map.test", "/v7/tiles/2/0/0.jpg"));
        Assert.Equal("miss", (await Get("map.test", "/v7/tiles/3/0/0.jpg")).Source);
    }

    [Fact]
    public async Task TestStateAnswersOnlyItsExactMethodAndPath()
    {
        var state = new State(Path.Combine(tmp, "state.db"), Py.DumpBytes(Fixtures.CONNECT), Fixtures.BASE, Fixtures.ICON,
            Fixtures.APP, Fixtures.LOCALE, Fixtures.TEXTURES);
        var u11 = JsonNode.Parse(state.HeartbeatReply(Fixtures.ME, Encoding.ASCII.GetBytes(
            "{\"events\": [{\"event\": 4, \"hash\": 1, \"photo_type\": 2}, {\"event\": 3, \"individual\": 9, \"extended_species\": 17}]}")))!["updates"]![0]!;
        state.UploadReply(Fixtures.ME, new Uri((string)u11["url"]!).Query.TrimStart('?'), "jpeg"u8.ToArray());
        await using var withState = await Loopback.StartAsync(new Game(table, Cfg, state, mirror));
        async Task<string> Source(string method, string host, string path, params (string, string)[] headers) =>
            (await withState.SendAsync(method, host, path, headers)).Source;
        // routing ignores case and a trailing slash; the handlers don't
        Assert.Equal("state", await Source("POST", "gameserver.test", HEARTBEAT));
        Assert.Equal("miss", await Source("POST", "gameserver.test", HEARTBEAT.ToUpperInvariant()));
        Assert.Equal("miss", await Source("POST", "gameserver.test", HEARTBEAT + "/"));
        Assert.Equal("state", await Source("GET", "apiv2.test", "/game/user/harvest/1"));
        // a CORS preflight for a state endpoint is the replay's, a HEAD for a stored photo the state's
        Assert.Equal("miss", await Source("OPTIONS", "api.test", BUY, ("Origin", "https://web.test"), ("Access-Control-Request-Method", "POST")));
        Assert.Equal("state", await Source("OPTIONS", "apiv2.test", "/game/user/harvest/1", ("Origin", "https://web.test"), ("Access-Control-Request-Method", "GET")));
        Assert.Equal("state", await Source("HEAD", "gameserver.test", "/photos/7/1-2.jpg"));
    }

    [Fact]
    public async Task TestLocalhostCorsOnlyForSwaggerOrigin()
    {
        async Task<string?> AllowOrigin(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://gameserver.test" + HEARTBEAT) { Content = new ByteArrayContent([]) };
            request.Headers.Add("Origin", origin);
            using var response = await server.Http.SendAsync(request);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;
        }
        Assert.Equal("http://localhost", await AllowOrigin("http://localhost"));
        Assert.Null(await AllowOrigin("http://localhost:3000"));
        Assert.Null(await AllowOrigin("https://evil.test"));
    }

    [Fact]
    public async Task TestMissDefaults()
    {
        var r = await R("POST", "api.test", "/v1/Nope/nope");
        Assert.Equal(200, r.Status);
        Assert.Equal("null"u8.ToArray(), r.Body);
        Assert.Equal("{\"err\": 0}"u8.ToArray(), (await R("POST", "gameserver.test", "/json/x")).Body);
        r = await R("GET", "apiv2.test", "/game/user/1/x");
        Assert.Equal((404, "not_found"), (r.Status, (string)JsonNode.Parse(r.Body)!["code"]!));
        r = await R("OPTIONS", "apiv2.test", "/game/user/harvest/9", ("Origin", "https://web.test"));
        Assert.Equal("miss", r.Source);
        Assert.Contains(("Access-Control-Allow-Origin", "https://web.test"), r.Headers);
        r = await R("POST", "telemetry.test", "/collect");
        Assert.Equal((204, "sink"), (r.Status, r.Source));
    }

    [Fact]
    public async Task TestConnectCarriesLaunchedReservesPopulations()
    {
        var gs = $"https://gameserver.test{CONNECT}";
        string Con(params string[] pops) => Py.Dumps(WithPopulations(pops));
        JsonObject[] hunts =  // only the launched reserve's populations and species_ammo come in connect
        [
            Entry("POST", "https://api.test/v1/Launch/start", 200, "2030-10-01T00:00:00", "{}", post: "start=200&loctype=LODGE&reserve=3&oauth_access_token=a"),
            Entry("POST", gs, 200, "2030-10-01T00:00:01", Con("r3")),
            Entry("POST", "https://api.test/v1/Launch/tutorial", 200, "2030-10-01T00:00:02", "{}", post: "tutorial_id=1"),
            Entry("POST", gs, 200, "2030-10-01T00:00:03", Con("tutorial")),
        ];
        var har = Path.Combine(tmp, $"hunts{Recordings.Ext}");
        File.WriteAllText(har, Har(hunts));
        var cfg = (JsonObject)Cfg.DeepClone();
        cfg["install_dir"] = tmp;
        var huntTable = BuildTable([har]);
        await using var withState = await Loopback.StartAsync(new Game(huntTable, cfg, LoadState(cfg, huntTable, tmp)!, mirror));
        async Task Launch(string form) =>
            Assert.Equal("{}"u8.ToArray(), (await withState.SendAsync("POST", "api.test", LAUNCH_START, [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes(form))).Body);
        async Task Check(string pop)
        {
            var reply = JsonNode.Parse((await withState.SendAsync("POST", "gameserver.test", CONNECT)).Body)!;
            Assert.Equal($"[\"{pop}\"]", Py.Dumps(reply["populations"]));
            Assert.Equal($"[\"{pop}\"]", Py.Dumps(reply["species_ammo"]));
        }
        await Launch("start=200&reserve=3");  // the reply is replayed; the launch is noted
        await Check("r3");
        await Launch("start=400&reserve=99");  // neither recorded nor in the facts file: the latest connect's
        await Check("tutorial");
    }

    [Fact]
    public void TestAnUnreadableHarOrEntryIsSkippedNotFatal()
    {
        string Write(string name, byte[] bytes) { var p = Path.Combine(tmp, name + Recordings.Ext); File.WriteAllBytes(p, bytes); return p; }
        var good = Entry("POST", "https://api.test/v1/Good/one", 200, "2030-10-01T00:00:00", "{\"g\": 1}");
        var mixed = new JsonObject { ["log"] = new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["response"] = new JsonObject() }, null, Entry("POST", "https://api.test/v1/Good/two", 200, "2030-10-01T00:00:00", "{}")) } };
        var files = new[]
        {
            Write("garbage", "not json"u8.ToArray()),
            Write("badutf8", [0xC3, 0x28]),
            Write("nolog", "{\"log\":{}}"u8.ToArray()),
            Write("mixed", Encoding.UTF8.GetBytes(Py.Dumps(mixed))),
            Write("good", Encoding.UTF8.GetBytes(Har(good))),
        };
        var t = BuildTable(files);
        Assert.Contains(new Key("POST", "api.test", "/v1/Good/one", null), t.Entries.Keys);
        Assert.Contains(new Key("POST", "api.test", "/v1/Good/two", null), t.Entries.Keys);
    }

    [Fact]
    public void TestALoneSurrogateInARecordedHeaderKeepsItsEscapedText()
    {
        var p = Path.Combine(tmp, "sur" + Recordings.Ext);
        File.WriteAllText(p, """{"log":{"entries":[{"request":{"method":"GET","url":"https://web.test/s"},"response":{"status":200,"headers":[{"name":"X-A","value":"\udcff"}],"content":{"text":""}}}]}}""");
        var hit = Assert.Single(BuildTable([p]).Entries).Value;
        Assert.Contains(("X-A", "\\udcff"), hit.Headers);
    }

    [Theory]
    [InlineData("https://U:p@API.test:8443/v1/x?a=1#f", "api.test", "/v1/x", "a=1")]
    [InlineData("//h/p", "h", "/p", "")]
    [InlineData("/v1/x?y", "", "/v1/x", "y")]
    [InlineData("http://[::1]:80/p", "::1", "/p", "")]
    public void TestSplitUrl(string url, string host, string path, string query) => Assert.Equal((host, path, query), SplitUrl(url));

    [Fact]
    public async Task TestLengthsHeadAndLatin1HeadersOnTheWire()
    {
        var f = Path.Combine(mirror, "static.test", "img", "h.png");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, "image bytes"u8.ToArray());
        table.Entries[new("GET", "web.test", "/accent", null)] = new Hit(2, "", 200, [("X-Accent", "\u00e9")], "x"u8.ToArray());
        using var head = await server.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "http://static.test/img/h.png"), TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode.OK, 11L, 0), (head.StatusCode, head.Content.Headers.ContentLength, (await head.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length));
        using var sink = await server.Http.PostAsync("http://telemetry.test/collect", new ByteArrayContent([]), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, sink.StatusCode);
        Assert.False(sink.Content.Headers.Contains("Content-Length"));
        using var accent = await server.Http.GetAsync("http://web.test/accent", TestContext.Current.CancellationToken);
        Assert.Equal("\u00e9", Assert.Single(accent.Headers.GetValues("X-Accent")));
    }
}
