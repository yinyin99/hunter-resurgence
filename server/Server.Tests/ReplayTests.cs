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
    public async Task TestMissCarriesNoStoreButAMirrorHitDoesNot()
    {
        var f = Path.Combine(mirror, "static.test", "img", "n.png");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, Png);
        var hit = await R("GET", "static.test", "/img/n.png");
        Assert.DoesNotContain(hit.Headers, h => h.Name == "Cache-Control");
        foreach (var miss in new[] { await R("GET", "static.test", "/img/none.png"), await R("OPTIONS", "api.test", "/v1/x"), await R("POST", "api.test", "/v1/nothing") })
        {
            Assert.Equal("miss", miss.Source);
            Assert.Contains(("Cache-Control", "no-store"), miss.Headers);
        }
    }

    [Fact]
    public async Task TestTeamsTimerReadAcceptsTheRecordAndStaysFailSafe()
    {
        void Put(string path, string text)
        {
            var f = Path.Combine(mirror, "game_web.test", path);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, text);
        }
        Put("static/app/9.9.9/client.js", "a({timer:e[4],x:1});b({timer:e[2]});");
        Put("static/app/9.9.8/client.js", "unrelated script");
        Put("client/index.html", "<script src=\"/static/app/9.9.9/client.js?t=v1\"></script>");
        var fixedScript = await R("GET", "game_web.test", "/static/app/9.9.9/client.js");
        var text = Encoding.UTF8.GetString(fixedScript.Body);
        Assert.Equal("patch", fixedScript.Source);
        Assert.DoesNotContain("timer:e[", text);
        Assert.Contains("(e[4])", text);
        Assert.Contains("(e[2])", text);
        Assert.Contains(("Cache-Control", "no-cache"), fixedScript.Headers);
        Put("static/app/9.9.7/client.js", "a({timer:e[4]});b({timer:e[4]});b({timer:e[2]});");
        Same(("a({timer:e[4]});b({timer:e[4]});b({timer:e[2]});"u8.ToArray(), "mirror"), await R("GET", "game_web.test", "/static/app/9.9.7/client.js"));
        Same(("unrelated script"u8.ToArray(), "mirror"), await R("GET", "game_web.test", "/static/app/9.9.8/client.js"));
        var page = await R("GET", "game_web.test", "/client");
        Assert.Contains("client.js?t=timer5", Encoding.UTF8.GetString(page.Body));
    }

    [Fact]
    public async Task TestTheInGamePageLoadsNoTrackersOrPreload()
    {
        void Put(string path, string text)
        {
            var f = Path.Combine(mirror, "game_web.test", path);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, text);
        }
        const string preload = "setTimeout(function(){a.load(\"/content/client-preload-images.json\")},2e3),", sentry = "\"localhost\"!==window.location.hostname&&e.init({dsn:";
        Put("static/app/9.9.9/client.js", "a({timer:e[4]});b({timer:e[2]});" + preload + sentry + "x});");
        Put("static/app/9.9.8/client.js", "b();" + preload + sentry + "x});");
        var both = Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.9/client.js")).Body);
        Assert.DoesNotContain("timer:e[", both);
        Assert.DoesNotContain("client-preload-images", both);
        Assert.Contains("false&&e.init({dsn:", both);
        var only = Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.8/client.js")).Body);
        Assert.DoesNotContain("client-preload-images", only);
        Assert.Contains("false&&e.init({dsn:", only);
        Put("client/index.html", "<script>require({\"google_tag_manager_id\": \"GTM-AB12\", \"google_analytics_id\": \"G-AB12\"})</script><script src=\"/static/app/9.9.9/client.js?t=v1\"></script>");
        var page = Encoding.UTF8.GetString((await R("GET", "game_web.test", "/client")).Body);
        Assert.Contains("\"google_tag_manager_id\": \"\"", page);
        Assert.Contains("\"google_analytics_id\": \"\"", page);
        Assert.Contains("client.js?t=timer5", page);
    }

    [Fact]
    public async Task TestTheMissionPanelWritesCarryThePlayersToken()
    {
        const string was = "x=r.v1(\"POST\",\"/v1/mission/missions\",{oauth_access_token:a,lang:i});y=r.v1(\"POST\",\"/v1/mission/activate\",{mission_id:t},{});z=r.v1(\"POST\",\"/v1/mission/activate_all_available\",{},{});"
            + "w=r.v1(\"POST\",\"/v1/mission/update_favourite\",{group_id:t,is_favourite:a},{});own=r.v1(\"POST\",\"/v1/mission/activate\",{mission_id:t,oauth_access_token:k},{});"
            + "other=r.v1(\"POST\",\"/v1/mission/update_other\",{a:1},{});";
        var f = Path.Combine(mirror, "game_web.test", "static/app/9.9.8/client.js");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, was);
        var text = Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.8/client.js")).Body);
        const string required = ",{authMode:\"required\"});";
        Assert.Contains("x=r.v1(\"POST\",\"/v1/mission/missions\",{oauth_access_token:a,lang:i});", text);  // the list already has its own
        Assert.Contains("y=r.v1(\"POST\",\"/v1/mission/activate\",{mission_id:t}" + required, text);
        Assert.Contains("z=r.v1(\"POST\",\"/v1/mission/activate_all_available\",{}" + required, text);
        Assert.Contains("w=r.v1(\"POST\",\"/v1/mission/update_favourite\",{group_id:t,is_favourite:a}" + required, text);
        Assert.Contains("own=r.v1(\"POST\",\"/v1/mission/activate\",{mission_id:t,oauth_access_token:k},{});", text);  // a write with its own token and an unrelated update_ call stay as they are
        Assert.Contains("other=r.v1(\"POST\",\"/v1/mission/update_other\",{a:1},{});", text);
    }

    [Fact]
    public async Task TestTheMissionRowsTakeAClassWhenTheyOpen()
    {
        var f = Path.Combine(mirror, "game_web.test", "static/app/9.9.8/client.js");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        const string toggleSet = "toggleSet:function(){c(\".set-missions\",this.$el).toggle()}", toggleDetails = "toggleDetails:function(){c(\".mission-details\",this.$el).toggle()}", route = "l.length&&(l.find(\".set-missions\").toggle(),x())";
        File.WriteAllText(f, toggleSet + "," + toggleDetails + "," + route);
        var text = Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.8/client.js")).Body);
        Assert.Contains("toggleSet:function(){c(\".set-missions\",this.$el).toggle(),c(\".set-row\",this.$el).first().toggleClass(\"hr-open\")}", text);
        Assert.Contains("toggleDetails:function(){c(\".mission-details\",this.$el).toggle(),c(\".mission-row\",this.$el).first().toggleClass(\"hr-open\")}", text);
        Assert.Contains("l.length&&(l.find(\".set-missions\").toggle(),l.find(\".set-row\").toggleClass(\"hr-open\"),x())", text);  // a pack the route opens by its id too
        // a text that is missing or doubled: the rewrite is skipped altogether (the first such script logs one warning, which a later one does not repeat)
        foreach (var script in new[] { toggleSet + "," + route, toggleSet + "," + toggleSet + "," + toggleDetails + "," + route })
        {
            File.WriteAllText(f, script);
            Assert.DoesNotContain("hr-open", Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.8/client.js")).Body));
        }
    }

    [Fact]
    public async Task TestTheTaxidermizeButtonStaysDisabledForAHarvestThatCountsForItsShooter()
    {
        var f = Path.Combine(mirror, "game_web.test", "static/app/9.9.8/client.js");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "function u(e){h&&(clearTimeout(h),h=null),e.ethical?g(!0,e):g(!1),s(\"#confirm-kill-score\")}");
        var text = Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.8/client.js")).Body);
        Assert.Contains("e.ethical&&String(e.score).indexOf(\"(shooter's kill)\")<0?g(!0,e):g(!1),", text);
        File.WriteAllText(f, "a.ethical?f(!0,a):f(!1);b.ethical?f(!0,b):f(!1)");  // found twice: left as it is
        Assert.DoesNotContain("shooter", Encoding.UTF8.GetString((await R("GET", "game_web.test", "/static/app/9.9.8/client.js")).Body));
    }

    [Fact]
    public async Task TestMissionsAndEditorPagesFillTheWindowInsteadOfAFixedBox()
    {
        var f = Path.Combine(mirror, "game_web.test", "client/index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "<html><head><link href=\"/static/app/9.9.9/client.css\"></head><body></body></html>");
        var reply = await R("GET", "game_web.test", "/client");
        var page = Encoding.UTF8.GetString(reply.Body);
        Assert.EndsWith("</style></head><body></body></html>", page);  // after the page's own style sheets, in the head
        Assert.Contains("body#client_missions_page #main #page_body{box-sizing:border-box;width:100%;max-width:75rem;height:100%;margin:0 auto;display:flex;flex-direction:column}", page);
        Assert.Contains("body#client_missions_page #main #page_body #ajax_content{flex:1 1 0;min-height:0;max-height:none}", page);  // the list takes the height left in the box, its scrollbar stays inside
        Assert.Contains("html body#lodge_edit #main,html body#client_missions_page #main{flex:1 1 0;min-height:0;height:auto}", page);  // the frame is no longer cut off under the tab bar
        Assert.Contains("fieldset label:not([for=\"hide-completed\"]){display:block;width:auto}", page);  // the filters' labels stay above their controls (Search too) in a wide box
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "window\\.game=\\{\\}"));  // the key binding forwarder: once, defined only where the page has none, forwarding to ewcallback at call time
        Assert.Contains("if(g[n]===undefined)g[n]=function(){var c=window.ewcallback;if(c&&typeof c[n]==='function')return c[n].apply(c,arguments)}", page);
        Assert.Contains("['updateKeybinding','overrideKeybinding']", page);
        Assert.Contains("body#client_missions_page .set-row.hr-open .state-icon-container i,html body#client_missions_page .mission-row.hr-open .state-icon-container i{display:inline-block;transform:rotate(90deg)}", page);  // the arrow of an open pack or mission turns
        Assert.Contains("#lodge-main-view .inventory-item{content-visibility:auto}", page);  // the lodge's items out of view are not painted
        var style = page[page.IndexOf("<style>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("px", style);  // nothing in it is a fixed size
        Assert.Contains(("Cache-Control", "no-cache"), reply.Headers);
        File.WriteAllText(f, "<html><body></body></html>");  // a page without a head is served as it is
        Same(("<html><body></body></html>"u8.ToArray(), "mirror"), await R("GET", "game_web.test", "/client"));
    }

    [Fact]
    public async Task TestThePageCopiesAnAmmunitionStackAsItIs()
    {
        var f = Path.Combine(mirror, "game_web.test", "static/app/9.9.6/client.js");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        const string Clonable = "isItemClonable:function(e){return 3===e.get(\"cat\")&&1==this.unlimited_ammo&&0==e.get(\"equippedSlot\")}";
        File.WriteAllText(f, "x," + Clonable + ",y");
        Same((Encoding.UTF8.GetBytes("x," + Clonable + ",y"), "mirror"), await R("GET", "game_web.test", "/static/app/9.9.6/client.js"));
    }

    [Fact]
    public async Task TestLodgeEditorTalksToThisHostAndStaysFailSafe()
    {
        void Put(string path, string text)
        {
            var f = Path.Combine(mirror, "game_web.test", path);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, text);
        }
        var cfg = (JsonObject)Cfg.DeepClone();
        cfg["install_dir"] = tmp;
        await using var own = await Loopback.StartAsync(new Game(table, cfg, null, mirror));
        Put("dist/main.js", "function a(){return 'https://api.' + getObj().getServer() + '/v1';}function b(){return 'https://apiv2.' + getObj().getServer();}function c(){return 'https://static.' + getObj().getServer();}var i=s + '/static/img/lodge/slots/' + n,j='https://static.example.test/static/img/lodge/slots/' + m,k=s + '/static/img/items/256/' + n,l=s + '/static/img/map/' + n;var g={top: this.props.offset.y / 0.9, left: this.props.offset.x / 0.9};");
        Put("static/app/9.9.9/webgui/index.html", "<script src=\"/dist/main.js\"></script>");
        var script = await own.SendAsync("GET", "game_web.test", "/dist/main.js");
        Assert.Equal("patch", script.Source);
        Assert.Equal("function a(){return window.location.origin + '/v1';}function b(){return window.location.origin + '/v1';}function c(){return window.location.origin;}var i=s + '/content/lodge/slots/' + n,j='/content/lodge/slots/' + m,k=s + '/content/items/256/' + n,l=s + '/static/img/map/' + n;var g={top: this.props.offset.y, left: this.props.offset.x};", Encoding.UTF8.GetString(script.Body));  // the lodge and item pictures are asked for under /content, which this server answers; the dragged picture is at the mouse's position, not divided by the zoom the page no longer has
        Assert.Contains(("Cache-Control", "no-cache"), script.Headers);
        var frame = await own.SendAsync("GET", "game_web.test", "/static/app/9.9.9/webgui/index.html");
        var page = Encoding.UTF8.GetString(frame.Body);
        Assert.StartsWith("<style>._2nkM,._2m_7,._1bAk,.XHcy,._3Y-A,._35mG{zoom:1}html,body{min-height:100%}body{background:#050505 linear-gradient(rgba(40,41,43,.65),rgba(7,5,12,.3))}body .OjlL,body ._15Yq{background:none;box-sizing:border-box;padding:2vmin}body .TfqS.fit,body ._1P0L.fit{box-sizing:border-box;width:100%;max-width:80rem;max-height:100%;overflow:auto}", page);  // the editor's panel is the window's size, not a fixed box; the other pages' panels are not touched
        Assert.Contains("</style><script>(function(){var n=window.hunter,f=window.fetch,g=0,u,z=function(x){u=x;};window.setURL=z;", page);  // before the script runs: requests that carry no token get the player's, and the game's functions that answer with a promise are made to answer at once
        Assert.DoesNotContain("px", page[..page.IndexOf("</style>", StringComparison.Ordinal)]);  // no fixed size in the style
        Assert.Contains("body .TfqS ._2nkM,body ._1P0L .XHcy{height:auto}", page);  // both scripts' names
        Assert.Contains("body ._2JVh ._2Ubp,body ._2JVh .w3T7,body ._3eN0 ._30FN,body ._3eN0 ._1UXP{zoom:calc(1/var(--z,1))}", page);  // the tooltips stay where the icon's measured position puts them inside the zoomed map
        Assert.Contains("img[src*=\"lodge/layouts/\"]", page);  // the map is scaled to the room that is left
        Assert.EndsWith("g();})();</script>", page);
        Assert.DoesNotContain("<script src=\"/dist/main.js\">", page);  // the page's own tag is replaced: the script is added once the answers are in, under the new token
        Assert.Contains("s.src=\"/dist/main.js?t=urls2\"", page);
        Assert.Contains("window.hunter.getAuthToken()", page);
        Assert.Contains("hunterSetLocale", page);
        Assert.Contains("window.setURL(u)", page);  // the game asks the frame for the editor's route as soon as it has loaded, before the script: a stand-in keeps it and hands it to the script's own
        Assert.Contains(("Cache-Control", "no-cache"), frame.Headers);
        Put("static/app/9.9.8/webgui/index.html", "<script src=\"/other.js\"></script>");
        Same(("<script src=\"/other.js\"></script>"u8.ToArray(), "mirror"), await own.SendAsync("GET", "game_web.test", "/static/app/9.9.8/webgui/index.html"));  // a page without the tag is served as it is
        Put("dist/main.js", "function b(){return 'https://apiv2.' + getObj().getServer();}");  // a script without all three texts is served as it is
        Same(("function b(){return 'https://apiv2.' + getObj().getServer();}"u8.ToArray(), "mirror"), await own.SendAsync("GET", "game_web.test", "/dist/main.js"));
    }

    [Fact]
    public async Task TestLocaleScriptGetsTheHideLabelOnlyWhenItLacksIt()
    {
        void Put(string name, string text)
        {
            var f = Path.Combine(mirror, "game_web.test", "static/app/9.9.9/locale", name);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, text);
        }
        Put("en_us.js", "hunterSetLocale({\n\"a\": \"A\"\n});\n");
        Put("ru_ru.js", "hunterSetLocale({\n\"hun_web_gui_hide\": \"X\",\n\"a\": \"A\"\n});\n");
        Put("xx_xx.js", "something else");
        var en = await R("GET", "game_web.test", "/static/app/9.9.9/locale/en_us.js");
        Assert.Equal("patch", en.Source);
        Assert.Equal("hunterSetLocale({\n\"hun_web_gui_hide\": \"Hide\",\n\"a\": \"A\"\n});\n", Encoding.UTF8.GetString(en.Body));
        Assert.Equal("mirror", (await R("GET", "game_web.test", "/static/app/9.9.9/locale/ru_ru.js")).Source);
        Assert.Equal("mirror", (await R("GET", "game_web.test", "/static/app/9.9.9/locale/xx_xx.js")).Source);
    }

    [Fact]
    public async Task TestGameWebMapTilesFallBackToTheMapHostFolder()
    {
        foreach (var (host, path, bytes) in new[] { ("map.test", "v7/tiles/2/3/3.jpg", "map"u8.ToArray()), ("map.test", "v7/tiles/2/4/0.jpg", "out"u8.ToArray()), ("map.test", "v7/tiles/2/0/4.jpg", "out"u8.ToArray()), ("map.test", "v7/tiles/6/0/0.jpg", "out"u8.ToArray()), ("game_web.test", "content/map/v7/tiles/1/0/0.jpg", "own"u8.ToArray()), ("map.test", "v7/tiles/1/0/0.jpg", "map"u8.ToArray()) })
        {
            var f = Path.Combine(mirror, host, path);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, bytes);
        }
        Same(("map"u8.ToArray(), "mirror"), await R("GET", "game_web.test", "/content/map/v7/tiles/2/3/3.jpg"));
        Same(("own"u8.ToArray(), "mirror"), await R("GET", "game_web.test", "/content/map/v7/tiles/1/0/0.jpg"));
        Assert.Equal("miss", (await R("GET", "game_web.test", "/content/map/v7/tiles/2/4/0.jpg")).Source);
        Assert.Equal("miss", (await R("GET", "game_web.test", "/content/map/v7/tiles/2/0/4.jpg")).Source);
        Assert.Equal("miss", (await R("GET", "game_web.test", "/content/map/v7/tiles/6/0/0.jpg")).Source);
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
            (ArcList.NameHash("0.png"), "other 0"u8.ToArray(), 0),  // an unrelated archived 0.png (a heart hit asks for <species>/0.png)
            (ArcList.NameHash("5.png"), Array.Empty<byte>(), 1),  // an organ overlay name shared by species folders
            (ArcList.HashLittle(Encoding.ASCII.GetBytes(@"webgui\static\img\animal_organs\24\5.png")), "organ"u8.ToArray(), 0),
            (ArcList.NameHash("classy.png"), Array.Empty<byte>(), 1),  // a picture name shared by folders (the lodge's layout has one too)
            (ArcList.NameHash("only.svg"), "layout"u8.ToArray(), 0),  // a layout picture only the archive has: its name is in no other folder
            (ArcList.HashLittle(Encoding.ASCII.GetBytes(@"webgui\static\img\lodge\lodges\classy.png")), "lodge"u8.ToArray(), 0),
            (ArcList.NameHash("index.html"), Array.Empty<byte>(), 1),  // two index.html pages: the classic page and the app's
            (ArcList.HashLittle(Encoding.ASCII.GetBytes(@"webgui\index.html")), "classic page"u8.ToArray(), 0),
            (ArcList.HashLittle(Encoding.ASCII.GetBytes(@"webguiv2\index.html")), "app page"u8.ToArray(), 0),
            (ArcList.NameHash("main.js"), "bundle"u8.ToArray(), 0),
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
        foreach (var (host, path) in new[] { ("static.test", "/static/img/items/256/x.png"), ("map.test", "/v7/tiles/1/0/0.jpg"), ("map.test", "/v7/tiles/2/0/0.jpg"), ("static.test", "/static/img/animal_organs/26/female.png"), ("static.test", "/static/img/lodge/lodges/rustic.png"), ("static.test", "/static/img/lodge/skulls/skull_x.png"), ("static.test", "/static/img/lodge/other/z.png") })
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
        Same(("organ"u8.ToArray(), "archive"), await Get("game_web.test", "/content/animal_organs/24/5.png"));
        Same(("mirror"u8.ToArray(), "mirror"), await Get("game_web.test", "/content/animal_organs/26/female.png"));
        Assert.Equal("miss", (await Get("game_web.test", "/content/animal_organs/25/5.png")).Source);  // another species' overlay of that name is never served
        Assert.Equal("miss", (await Get("game_web.test", "/content/animal_organs/24/0.png")).Source);  // an unrelated archived 0.png is never the overlay
        Same(("lodge"u8.ToArray(), "archive"), await Get("game_web.test", "/content/lodge/lodges/classy.png"));
        Same(("layout"u8.ToArray(), "archive"), await Get("game_web.test", "/content/lodge/layouts/only.svg"));
        Assert.Equal("miss", (await Get("game_web.test", "/content/lodge/slots/x.png")).Source);  // only a layout takes the archive's one file of its name: x.png there is the item picture
        Same(("mirror"u8.ToArray(), "mirror"), await Get("game_web.test", "/content/lodge/lodges/rustic.png"));
        Assert.Equal("miss", (await Get("game_web.test", "/content/lodge/lodges/unknown.png")).Source);
        Assert.Equal("miss", (await Get("game_web.test", "/content/lodge/lodges/..%2Fx.png")).Source);  // a name outside the pattern
        Same(("mirror"u8.ToArray(), "mirror"), await Get("game_web.test", "/content/lodge/skulls/skull_x.png"));  // the editor's own pictures: the static host's folder
        Assert.Equal("miss", (await Get("game_web.test", "/content/lodge/other/z.png")).Source);
        Same(("app page"u8.ToArray(), "archive"), await Get("game_web.test", "/static/app/3.0.81/webgui/index.html"));  // the lodge editor frame: the app's page, not the other index.html
        Same(("bundle"u8.ToArray(), "archive"), await Get("game_web.test", "/dist/main.js"));
        Assert.Equal("miss", (await Get("static.test", "/static/app/3.0.81/webgui/index.html")).Source);  // only the game web host's page
        Assert.Equal("miss", (await Get("game_web.test", "/static/app/3.0.81/webgui/other.html")).Source);
    }

    [Fact]
    public async Task TestStateAnswersOnlyItsExactMethodAndPath()
    {
        var state = new State(Path.Combine(tmp, "state.db"), Py.DumpBytes(Fixtures.CONNECT), Fixtures.BASE, Fixtures.ICON,
            Fixtures.APP, Fixtures.LOCALE, Fixtures.TEXTURES);
        var u11 = JsonNode.Parse(state.HeartbeatReply(Fixtures.ME, Encoding.ASCII.GetBytes(
            "{\"events\": [{\"event\": 4, \"hash\": 1, \"photo_type\": 2}, {\"event\": 3, \"individual\": 9, \"extended_species\": 17}]}")))!["updates"]![0]!;
        var media = new Uri((string)JsonNode.Parse(state.UploadReply(Fixtures.ME, new Uri((string)u11["url"]!).Query.TrimStart('?'), "jpeg"u8.ToArray()).Body)!["mediaLink"]!).AbsolutePath;
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
        Assert.Equal("state", await Source("HEAD", "gameserver.test", media));
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
            Write("array", "[]"u8.ToArray()),
            Write("logarray", "{\"log\":[]}"u8.ToArray()),
            Write("dupkey", "{\"log\":{\"entries\":[]},\"log\":{\"entries\":[]}}"u8.ToArray()),
            Write("dupentry", "{\"log\":{\"entries\":[{\"request\":{\"method\":\"GET\",\"url\":\"https://api.test/a\"},\"request\":{}}]}}"u8.ToArray()),
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
