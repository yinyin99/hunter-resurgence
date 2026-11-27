using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A multiplayer game has one seasonal event: its host's (#996). A guest's hunt takes the host's decision, whatever his own setting, keeps it for its whole life, and the games list says what each game runs.
/// The players are invented; the shipped Halloween facts are used, on the game's reserve 1 (the whitetail 0 and the rabbit 17 have possessed versions, the rabbit's normal list is 241 and 14, the event's 962, 963 and 1142).</summary>
public class SeasonalEventGameTests : StateTestBase
{
    const long HOST = 11, GUEST = 12, THIRD = 13;
    const string Camp = "start=100&loctype=LODGE&reserve=2&time=540&loadout_id=0&name=Camp";
    const string Halloween = """{"name": "halloween_2026", "define": "HALLOWEEN_2026", "reserves": [0, 1, 2, 8, 9]}""";

    static readonly DateTimeOffset Opens = new(2031, 3, 10, 0, 0, 0, TimeSpan.Zero);

    // the event ends a minute after the tests start: a step of 90 s past it keeps every player of a session (120 s of silence end one)
    static readonly SeasonalEvent Event = new("halloween_2026", "HALLOWEEN_2026", [0, 1, 2, 8, 9], Opens, Opens.AddSeconds(3600 + 60));

    double at = Opens.ToUnixTimeSeconds() + 3600;
    string guestSession = "";

    State Server(string? db = null)
    {
        var connect = CONNECT;
        connect["populations"] = new JsonArray(O("""{"index": 100, "animal": "mule_deer", "reserve": 1}"""));
        connect["species_ammo"] = new JsonArray(O("""{"species_id": "17", "ammo": [999]}"""));
        var app = APP;
        app["locations"] = new JsonArray(O("""{"id": 100, "pos": {"y": 150, "x": -6420, "z": 3310}, "type": "LODGE", "reserve": 2}"""));
        var s = new State(db ?? Db, Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES, offline: false, facts: Facts.Default, seasonal: Event) { Clock = () => at };
        if (db is null)
            foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest"), (THIRD, "third") })
                s.CreateAccount(id, handle);
        return s;
    }

    /// <summary>HOST hosts a game and GUEST joins it, each with his own setting; with <paramref name="hostConnects"/> the host's game connected before the guest joined (as the game does: its port comes after its connect).
    /// Without it the host's hunt has no decision when the guest connects.</summary>
    JsonNode Game(State s, bool hostPlays, bool guestPlays, bool hostConnects = true)
    {
        s.Store.SetPlaysEvents(HOST, hostPlays);
        s.Store.SetPlaysEvents(GUEST, guestPlays);
        s.HostGame(HOST, "host-session", Encoding.UTF8.GetBytes(Camp));
        var hostReply = hostConnects ? Reply(s.ConnectReply(HOST, "host-session", "203.0.113.1")) : new JsonObject();
        s.HeartbeatReply(HOST, Events(O("""{"event": 29, "port": 32303, "guid": "9007199254740993", "color": "c73378"}""")));
        var id = (long)Reply(s.NetworkListReply())[0]!["id"]!;
        var (error, session) = s.JoinGame(GUEST, Encoding.UTF8.GetBytes($"start=100&loctype=LODGE&network_id={id}&loadout_id=0"));
        Assert.Null(error);
        guestSession = session!;
        return hostReply;
    }

    JsonNode GuestConnects(State s) => Reply(s.ConnectReply(GUEST, guestSession, "203.0.113.2"));

    static string Indexes(JsonNode reply) => string.Join(",", reply["populations"]!.AsArray().Select(p => (long)p!["index"]!));

    /// <summary>The event the account's latest hunt recorded at its first connect: its name, '' for none, null when it has no decision yet.</summary>
    static string? Recorded(State s, long account)
    {
        using var con = s.Store.Open(account);
        return con.Scalar("SELECT event FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1") as string;
    }

    /// <summary>A rabbit (17) without a coat, so only its hunt's event decides what it is, hit with each ammunition in turn and harvested: the updates of the heartbeat after it.</summary>
    static JsonArray Rabbit(State s, long who, int individual, params int[] ammo)
    {
        JsonNode[] events =
        [
            O($$"""{"event": 7, "individual": {{individual}}, "species": 17, "gender": 0, "weight_grams": 1100, "feature": 1100250}"""),
            .. ammo.Select(a => (JsonNode)O($$"""{"event": 1, "individual": {{individual}}, "ammunition_id": {{a}}, "weapon_id": 391}""")),
            O($$"""{"event": 3, "individual": {{individual}}, "extended_species": 17}"""),
        ];
        s.HeartbeatReply(who, Events(events));
        return Reply(s.HeartbeatReply(who, Events()))["updates"]!.AsArray();
    }

    static bool Disqualified(JsonArray updates) => (string)updates.Single(u => Py.Eq(u!["update"], 6))!["score_text"]! == "Disqualified ";

    /// <summary>The kills of an animal that the event decides, by the guest's hunt: true when a normal round is judged right and silver wrong (no event), the opposite with the event.</summary>
    void AssertJudgedByEvent(State s, bool event_, int firstIndividual)
    {
        Assert.Equal(event_, Disqualified(Rabbit(s, GUEST, firstIndividual, 14)));
        Assert.Equal(!event_, Disqualified(Rabbit(s, GUEST, firstIndividual + 1, 962)));
    }

    [Fact]
    public void TestAGuestWithTheEventOffGetsTheEventOfAHostWhoPlaysIt()
    {
        var s = Server();
        var host = Game(s, hostPlays: true, guestPlays: false);
        var reply = GuestConnects(s);
        Eq(Halloween, reply["ingame_event"]);
        Assert.Equal(host["ingame_event"]!.ToJsonString(), reply["ingame_event"]!.ToJsonString());
        Assert.NotEqual("100", Indexes(reply));  // the event's creatures
        Assert.Equal(Indexes(host), Indexes(reply));
        Assert.Equal(host["species_ammo"]!.ToJsonString(), reply["species_ammo"]!.ToJsonString());
        Eq("[962, 963, 1142]", reply["ethical"]![17]);
        Assert.Equal(host["ethical"]!.ToJsonString(), reply["ethical"]!.ToJsonString());
        Assert.Equal("halloween_2026", Recorded(s, GUEST));
        AssertJudgedByEvent(s, true, 401);  // a normal round is wrong on a possessed rabbit, silver right
    }

    [Fact]
    public void TestAGuestWithTheEventOnGetsNoEventFromAHostWhoDoesNotPlayIt()
    {
        var s = Server();
        var host = Game(s, hostPlays: false, guestPlays: true);
        Assert.Null(host["ingame_event"]);
        var reply = GuestConnects(s);
        Assert.Null(reply["ingame_event"]);
        Assert.Equal("100", Indexes(reply));
        Eq("[241, 14]", reply["ethical"]![17]);
        Eq("[241, 14]", reply["species_ammo"]![0]!["ammo"]);
        Assert.Equal("", Recorded(s, GUEST));
        AssertJudgedByEvent(s, false, 401);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void TestAGuestWhoConnectsBeforeTheHostGetsTheEventTheHostsSettingDecides(bool hostPlays, bool guestPlays, bool event_)
    {
        var s = Server();
        Game(s, hostPlays, guestPlays, hostConnects: false);
        Assert.Null(Recorded(s, HOST));  // the host's hunt has no decision yet
        var reply = GuestConnects(s);
        Assert.Equal(event_, reply["ingame_event"] is not null);
        Assert.Equal(event_ ? "halloween_2026" : "", Recorded(s, GUEST));
        Eq(event_ ? "[962, 963, 1142]" : "[241, 14]", reply["ethical"]![17]);
        AssertJudgedByEvent(s, event_, 401);
        // the host's own connect, later, decides the same by his setting
        Assert.Equal(event_, Reply(s.ConnectReply(HOST, "host-session", "203.0.113.1"))["ingame_event"] is not null);
        Assert.Equal(Recorded(s, GUEST), Recorded(s, HOST));
    }

    [Theory]
    [InlineData(true)]   // the guest connected before the event ended and connects again
    [InlineData(false)]  // the guest's first connect is after it ended, the host's hunt having decided before
    public void TestAGuestsHuntKeepsTheHostsDecisionWhenTheEventEndsAndTheSettingsChange(bool connectedBefore)
    {
        var s = Server();
        Game(s, hostPlays: true, guestPlays: false);
        if (connectedBefore)
            GuestConnects(s);
        at += 90;  // the event's window is over
        Assert.Null(s.EventStatus(GUEST)["event"]);
        s.Store.SetPlaysEvents(HOST, false);  // the host changes his setting, the guest his
        s.Store.SetPlaysEvents(GUEST, true);
        s.HeartbeatReply(HOST, Events());
        var reply = GuestConnects(s);  // his first connect, or the game started again inside the session
        Eq(Halloween, reply["ingame_event"]);
        Assert.NotEqual("100", Indexes(reply));
        Eq("[962, 963, 1142]", reply["ethical"]![17]);
        Assert.Equal("halloween_2026", Recorded(s, GUEST));
        AssertJudgedByEvent(s, true, 401);
    }

    [Fact]
    public void TestAGuestsHuntKeepsTheHostsDecisionAcrossAServerRestartAndWithoutAnEvent()
    {
        var s = Server();
        Game(s, hostPlays: true, guestPlays: false);
        GuestConnects(s);
        var restarted = Server(Db);  // the session is gone: the guest's game connects as a single-player one
        restarted.Store.SetPlaysEvents(GUEST, false);
        at += 90;
        Eq("[962, 963, 1142]", Reply(restarted.ConnectReply(GUEST))["ethical"]![17]);  // the hunt's recorded event
        Assert.Equal("halloween_2026", Recorded(restarted, GUEST));
        AssertJudgedByEvent(restarted, true, 401);

        // the other way round: a hunt that took none keeps none when the guest's setting is on and the event runs
        var off = Server(Path.Combine(tmp, "off.db"));
        foreach (var (id, handle) in new[] { (HOST, "host"), (GUEST, "guest") })
            off.CreateAccount(id, handle);
        at = Opens.ToUnixTimeSeconds() + 3600;
        Game(off, hostPlays: false, guestPlays: true);
        GuestConnects(off);
        var again = Server(Path.Combine(tmp, "off.db"));
        Eq("[241, 14]", Reply(again.ConnectReply(GUEST))["ethical"]![17]);
        Assert.Equal("", Recorded(again, GUEST));
        AssertJudgedByEvent(again, false, 401);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TestAGuestsNextSinglePlayerHuntFollowsHisOwnSettingAgain(bool hostPlays, bool guestPlays)
    {
        var s = Server();
        Game(s, hostPlays, guestPlays);
        GuestConnects(s);
        Assert.Equal(hostPlays ? "halloween_2026" : "", Recorded(s, GUEST));
        s.NoteLaunch(GUEST, "start=100&reserve=2"u8.ToArray());  // a hunt of his own ends his session and decides again
        var reply = Reply(s.ConnectReply(GUEST));
        Assert.Equal(guestPlays, reply["ingame_event"] is not null);
        Assert.Equal(guestPlays ? "halloween_2026" : "", Recorded(s, GUEST));
        Eq(guestPlays ? "[962, 963, 1142]" : "[241, 14]", reply["ethical"]![17]);
        AssertJudgedByEvent(s, guestPlays, 501);
    }

    [Theory]
    [InlineData(true, true, "halloween_2026", false)]   // a caller with the same setting as the host
    [InlineData(true, false, "halloween_2026", true)]   // another setting
    [InlineData(false, true, null, true)]
    [InlineData(false, false, null, false)]
    public void TestTheGamesListSaysWhatEventAGameRunsAndWhetherItDiffersFromTheCallersOwn(bool hostPlays, bool callerPlays, string? expected, bool differs)
    {
        var s = Server();
        Game(s, hostPlays, guestPlays: !hostPlays);
        s.Store.SetPlaysEvents(THIRD, callerPlays);
        var listed = Reply(s.NetworkListReply(THIRD, website: true))[0]!;
        Assert.Equal((expected, differs), ((string?)listed["event"], (bool)listed["event_differs"]!));
        var anonymous = Reply(s.NetworkListReply(website: true))[0]!;
        Assert.Equal((expected, false), ((string?)anonymous["event"], (bool)anonymous["event_differs"]!));  // no caller: nothing to differ from
    }

    [Fact]
    public void TestTheGamesListKeepsTheGamesEventAfterTheWindowAndBeforeTheHostConnects()
    {
        var s = Server();
        Game(s, hostPlays: true, guestPlays: true, hostConnects: false);
        s.Store.SetPlaysEvents(THIRD, true);
        var undecided = Reply(s.NetworkListReply(THIRD, website: true))[0]!;  // the host's hunt has no decision yet: the host's setting decides
        Assert.Equal(("halloween_2026", false), ((string?)undecided["event"], (bool)undecided["event_differs"]!));
        Reply(s.ConnectReply(HOST, "host-session", "203.0.113.1"));
        s.Store.SetPlaysEvents(HOST, false);
        at += 90;
        s.HeartbeatReply(HOST, Events());
        var listed = Reply(s.NetworkListReply(THIRD, website: true))[0]!;
        Assert.Equal(("halloween_2026", true), ((string?)listed["event"], (bool)listed["event_differs"]!));  // the host's hunt's decision stays; the caller's own event is over
    }
}
