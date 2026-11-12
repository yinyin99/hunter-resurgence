using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The seasonal event: the connect reply's `ingame_event` and the event's content by window, reserve and player, and the player's setting.</summary>
public class SeasonalEventTests : StateTestBase
{
    // CONNECT starts at lodge 100: the game's reserve 1. The event adds a deer population and silver-like ammo there, and ammo only on reserve 3
    static readonly Facts EventFacts = Facts.Parse("""
        {"format": 1,
         "events": {"season": {"reserves": {
           "1": {"ammo": [962, 963], "populations": [
             {"index": 150, "animal": "deer", "density": "mule_deer", "endangered": false, "license": 1,
              "groups": [{"x": 0, "z": 0, "radius": 0, "males": 2, "females": 3}]}]},
           "3": {"ammo": [962], "populations": []}}}}}
        """);

    static readonly DateTimeOffset Start = new(2031, 3, 10, 0, 0, 0, TimeSpan.Zero), End = Start.AddDays(10);

    static SeasonalEvent Season(params int[] reserves) => new("season", "SEASON", reserves, Start, End);

    static readonly JsonObject WebCfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    State New(SeasonalEvent? season, bool oldEventRow = false)
    {
        var connect = CONNECT;
        connect["populations"] = new JsonArray(O("""{"index": 100, "animal": "mule_deer", "reserve": 1}"""));
        if (oldEventRow)  // what a recorded connect of the event itself carries
            connect["populations"]!.AsArray().Add(O("""{"index": 150, "animal": "deer", "reserve": 2}"""));
        connect["species_ammo"] = new JsonArray(O("""{"species_id": "17", "ammo": [999]}"""));
        var s = new State(Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".db"), Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES,
                          offline: false, facts: EventFacts, seasonal: season);
        s.Clock = () => Start.ToUnixTimeSeconds() + 3600;
        return s;
    }

    static JsonNode Connect(State s) => Reply(s.ConnectReply(ME));

    static string Indexes(JsonNode reply) => string.Join(",", reply["populations"]!.AsArray().Select(p => (long)p!["index"]!));

    [Fact]
    public void TestWithNoEventTheConnectSendsNoneAndNoContent()
    {
        var reply = Connect(New(null, oldEventRow: true));
        Assert.Null(reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));  // a recorded event creature goes too
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestAnEventThatRunsIsAnnouncedAndItsReservesGetItsContent()
    {
        var reply = Connect(New(Season(1, 3), oldEventRow: true));
        Eq("""{"name": "season", "define": "SEASON", "reserves": [1, 3]}""", reply["ingame_event"]);
        Assert.Equal("100,150", Indexes(reply));  // the recorded one is replaced, not doubled
        var row = reply["populations"]![1]!;
        Eq("""["deer", "mule_deer", 2, "2031-03-10T00:00:00", "2031-03-20T00:00:00"]""",
           T(row["animal"], row["density_map"], row["reserve"], row["active_start"], row["active_end"]));
        Eq("[962, 963]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestAnEventForOtherReservesIsAnnouncedButAddsNothingHere()
    {
        var reply = Connect(New(Season(3)));
        Eq("""{"name": "season", "define": "SEASON", "reserves": [3]}""", reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
    }

    [Fact]
    public void TestNoReservesMeansEveryReserve()
    {
        var reply = Connect(New(Season()));
        Eq("""{"name": "season", "define": "SEASON", "reserves": []}""", reply["ingame_event"]);
        Assert.Equal("100,150", Indexes(reply));
    }

    [Fact]
    public void TestTheEventRunsFromItsStartUntilItsEnd()
    {
        var s = New(Season(1));
        bool Runs(double at)
        {
            s.Clock = () => at;
            return Connect(s)["ingame_event"] is not null;
        }
        Assert.False(Runs(Start.ToUnixTimeSeconds() - 1));
        Assert.True(Runs(Start.ToUnixTimeSeconds()));
        Assert.True(Runs(End.ToUnixTimeSeconds() - 1));
        Assert.False(Runs(End.ToUnixTimeSeconds()));
        s.Clock = () => End.ToUnixTimeSeconds();
        Assert.Null(s.EventStatus(ME)["event"]);
    }

    [Fact]
    public void TestAPlayerWhoOptedOutGetsNoEventAndNoContent()
    {
        var s = New(Season(1), oldEventRow: true);
        Assert.True(s.Store.PlaysEvents(ME));  // on by default
        s.Store.SetPlaysEvents(ME, false);
        var reply = Connect(s);
        Assert.Null(reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
        s.Store.SetPlaysEvents(ME, true);
        Assert.NotNull(Connect(s)["ingame_event"]);
    }

    [Fact]
    public void TestATutorialHasNoEvent()
    {
        var s = New(Season(1));
        s.NoteTutorial(ME, "tutorial-session", 1);
        Assert.Null(Reply(s.ConnectReply(ME, "tutorial-session"))["ingame_event"]);
        Assert.NotNull(Connect(s)["ingame_event"]);  // a hunt without that session
    }

    [Fact]
    public async Task TestThePlayerReadsAndChangesTheSettingOnTheWebsite()
    {
        var s = New(Season(1));
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        var form = ("Content-Type", "application/x-www-form-urlencoded");
        var own = ("X-Requested-With", "XMLHttpRequest");
        Task<Reply> Post(string body, params (string, string)[] headers) =>
            server.SendAsync("POST", "web.test", "/account/events", [form, .. headers], Encoding.ASCII.GetBytes(body));

        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/events", [])).Status);
        var read = await server.SendAsync("GET", "web.test", "/account/events", [cookie]);
        Assert.Equal(200, read.Status);
        Eq("""{"play": true, "event": {"name": "season", "define": "SEASON", "reserves": [1]}}""", JsonNode.Parse(read.Body));

        Assert.Equal(401, (await Post("play=0", own)).Status);
        Assert.Equal(400, (await Post("play=0", cookie)).Status);  // not from our page
        Assert.Equal(400, (await Post("play=maybe", cookie, own)).Status);
        Assert.Equal(400, (await Post("", cookie, own)).Status);
        Assert.True(s.Store.PlaysEvents(ME));

        var off = await Post("play=0", cookie, own);
        Assert.Equal(200, off.Status);
        Eq("""{"play": false, "event": {"name": "season", "define": "SEASON", "reserves": [1]}}""", JsonNode.Parse(off.Body));
        Assert.Null(Connect(s)["ingame_event"]);
        Assert.Equal(200, (await Post("play=1", cookie, own)).Status);
        Assert.NotNull(Connect(s)["ingame_event"]);
    }

    [Fact]
    public async Task TestThePanelIsReadOnlyByAPlayerWhoPlaysWhileTheEventRuns()
    {
        var s = New(Season(1));
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Task<Reply> Get(params (string, string)[] headers) => server.SendAsync("GET", "web.test", "/account/events/panel", headers);

        Assert.Equal(401, (await Get()).Status);
        var panel = JsonNode.Parse((await Get(cookie)).Body)!;
        Eq("""["season", "season", [], [], [], []]""", T(panel["name"], panel["title"], panel["community"], panel["contributors"], panel["missions"], panel["items"]));
        s.Store.SetPlaysEvents(ME, false);
        Eq("""{"event": null}""", JsonNode.Parse((await Get(cookie)).Body));
    }

    [Fact]
    public void TestTheShippedHalloweenEventHasItsMissionAndItsStoreItems()
    {
        var content = Facts.Default.EventOf("halloween_2026")!;
        Assert.Equal("Halloween 2026", (string)content["title"]!);
        Eq("[1201]", content["missions"]);
        Eq("[960, 967, 1143, 962, 963, 1142]", content["items"]);
        Eq("[]", content["community"]);  // no number of its community missions is known
    }

    [Fact]
    public async Task TestWithNoEventTheSettingIsStillReadable()
    {
        var s = New(null);
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, s, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={s.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Eq("""{"play": true, "event": null}""", JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/events", [cookie])).Body));
    }

    [Fact]
    public void TestTheSchemaOfVersion16GetsTheSettingOnForEveryAccount()
    {
        var file = Path.Combine(tmp, "v16.db");
        var store = new Store(file);
        store.CreateAccount(5, "old", new JsonObject());
        using (var con = store.Open())
        {
            con.Exec("ALTER TABLE accounts DROP COLUMN seasonal_events");
            con.Exec("DROP TABLE claim_lookups");
            con.Exec("DROP TABLE claims");
            con.Exec("PRAGMA user_version = 16");
        }
        var migrated = new Store(file);
        Assert.True(migrated.PlaysEvents(5));
        migrated.SetPlaysEvents(5, false);
        Assert.False(migrated.PlaysEvents(5));
        using var check = migrated.Open();
        Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TestTheShippedFactsHoldTheHalloweenEventOfWhiterimeRidge()
    {
        var content = Facts.Default.Event("halloween_2026", 9)!.Value;
        Assert.Equal([962L, 963L, 1142L], content.Ammo);
        Assert.Equal(["werewolf", "wendigo", "feral_hog"], content.Populations.Select(p => (string)p["animal"]!));
        Assert.All(content.Populations, p => Assert.Equal(10, (int)p["reserve"]!));  // the web's number of the game's reserve 9
        Assert.Equal([908L, 909L, 914L], Facts.Default.EventIndexes(9));
        Assert.Null(Facts.Default.Event("halloween_2026", 0));
    }

    [Theory]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [0, 9], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00+01:00"}""", true)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", true)]
    [InlineData("""{"define": "SEASON", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "Season", "define": "SEASON", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "season", "reserves": [], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [-1], "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "start": "2031-03-10T00:00:00Z", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [], "start": "soon", "end": "2031-03-20T00:00:00Z"}""", false)]
    [InlineData("""{"name": "season", "define": "SEASON", "reserves": [], "start": "2031-03-20T00:00:00Z", "end": "2031-03-10T00:00:00Z"}""", false)]
    [InlineData("\"season\"", false)]
    public void TestTheSettingIsCheckedWhenTheServerStarts(string json, bool valid)
    {
        if (valid) Assert.NotNull(SeasonalEvent.Parse(JsonNode.Parse(json)));
        else Assert.Throws<InvalidDataException>(() => SeasonalEvent.Parse(JsonNode.Parse(json)));
    }

    [Fact]
    public void TestAnEventWithoutASettingIsNone() => Assert.Null(SeasonalEvent.Parse(null));
}
