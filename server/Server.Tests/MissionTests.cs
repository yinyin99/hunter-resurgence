using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Missions on a community server, with made-up definitions: the account's states, the replies, the progress the hunt's events make and the pay.</summary>
public class MissionTests : StateTestBase
{
    // 1 a harvest; 2 a track and a harvest (needs 1); 3 starts at a lodge; 4 a harvest (needs 2); 5 a single-hunt track and spot; 6 arrive; 7 ordered; 8 two clues of one animal;
    // 9 a female; 10 a harvest (needs 2); groups 10 holds 4 and 11 holds 10
    const string Definitions = """
        {"missions": [
          {"id": 1, "title": "Harvest a Test Deer", "dependencies": [], "rewards": [{"text": "100 gm$"}], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]},
          {"id": 2, "title": "Track and Harvest", "dependencies": [1], "rewards": [{"text": "250 gm$"}, {"text": "5 em$"}, {"text": "Test Scope"}],
           "objectives": [{"id": 0, "title": "Locate Test Deer tracks."}, {"id": 1, "title": "Harvest a Test Deer."}]},
          {"id": 3, "title": "Test Start", "dependencies": [], "rewards": [{"text": "40 gm$"}], "objectives": [{"id": 0, "title": "Start at Test Lodge."}]},
          {"id": 4, "title": "Next", "dependencies": [2], "rewards": [{"text": "10 gm$"}], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]},
          {"id": 5, "title": "One Hunt", "singleExpedition": true, "dependencies": [], "rewards": [{"text": "20 gm$"}],
           "objectives": [{"id": 0, "title": "Locate Test Deer tracks"}, {"id": 1, "title": "Spot a Test Deer"}]},
          {"id": 6, "title": "Pond", "dependencies": [], "rewards": [{"text": "30 gm$"}], "objectives": [{"id": 0, "title": "Visit Test Pond (x 5, y 6)"}]},
          {"id": 7, "title": "In Order", "dependencies": [], "rewards": [{"text": "60 gm$"}],
           "objectives": [{"id": 0, "title": "Harvest a Test Deer"}, {"id": 1, "title": "Then, spot a Test Deer"}]},
          {"id": 8, "title": "Clues", "dependencies": [], "rewards": [{"text": "70 gm$"}], "objectives": [{"id": 0, "title": "Locate 2 Test Deer tracks from the same animal"}]},
          {"id": 9, "title": "Doe", "dependencies": [], "rewards": [{"text": "80 gm$"}], "objectives": [{"id": 0, "title": "Harvest a female Test Deer"}]},
          {"id": 10, "title": "Other", "dependencies": [2], "rewards": [{"text": "15 gm$"}], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]}],
         "mission_groups": [{"id": 10, "missions": [4]}, {"id": 11, "missions": [10]}]}
        """;

    public MissionTests()
    {
        state = New("state.db");
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };  // pinned, so no test reads files
    }

    State New(string name, bool offline = false)
    {
        var app = APP;
        foreach (var (key, value) in O(Definitions))
            app[key] = value!.DeepClone();
        app["locations"]![0]!["name"] = "Test Lodge";
        return new State(Path.Combine(tmp, name), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: offline);
    }

    static byte[] Q(string form) => Encoding.ASCII.GetBytes(form);

    JsonNode Missions(long account = ME) => Reply(state.MissionsReply(account)!);

    static string Ids(JsonNode? list) => string.Join(",", list!.AsArray().Select(i => (long)i!));

    bool Call(string call, string form = "", long account = ME) => state.MissionCall(account, call, Q(form))!.Value;

    /// <summary>The updates of the next heartbeat (the queued ones).</summary>
    static string Kinds(JsonArray updates) => string.Join(",", updates.Select(u => (long)u!["update"]!));

    static JsonNode? Update(JsonArray updates, int kind) => updates.LastOrDefault(u => (long)u!["update"]! == kind);

    static JsonNode[] Kill(int individual, long species = 1, long gender = 0)
    {
        var events = AccountIsolationTests.Harvest(individual, 72270);
        events[0]["species"] = species;
        events[0]["gender"] = gender;
        events[^1]["extended_species"] = species;
        return events;
    }

    static JsonNode Spawn(int individual) => O($$"""{"event": 7, "individual": {{individual}}, "species": 1, "gender": 0, "weight_grams": 50000, "texture": 0}""");

    static JsonNode Track(int individual) => O($$"""{"event": 12, "individual": {{individual}}, "track_type": 3}""");

    static JsonNode SpotOf(int individual) => O($$"""{"event": 11, "individual": {{individual}}, "milliseconds_viewed": 3000, "percentage": 80}""");

    static JsonNode Enter(string place, string direction = "enter") =>
        O($$"""{"event": 101, "location_name": "{{place}}", "direction": "{{direction}}", "player_x": 1, "player_y": 2, "player_z": 3}""");

    double Gm(long account = ME) => state.Wallet(account)["gm"]!.GetValue<double>();

    string Objectives(int mission, long account = ME) => Ids(Missions(account)["states"]![mission.ToString()]!["objectives"]);

    [Fact]
    public void TestWithoutDefinitionsOrOnALocalServerThereAreNoMissions()
    {
        var none = new State(Path.Combine(tmp, "none.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        Assert.Null(none.MissionsReply(ME));
        Assert.Null(none.MissionCall(ME, "activate", Q("mission_id=1")));
        Assert.Null(New("local.db", offline: true).MissionsReply(ME));
    }

    [Fact]
    public void TestAvailableMissionsAreThoseWhoseDependenciesAreCompleted()
    {
        var reply = Missions();
        Assert.Equal("1,3,5,6,7,8,9", Ids(reply["available"]));
        Eq("[[], [], 0, 0, 0, []]", T(reply["active"], reply["completed"], reply["auto_mission_activate"], reply["activate_only_favourite"], reply["hide_completed_favourite"], reply["favourites"]));
        Eq("""{"state": 1}""", reply["states"]!["1"]);
        Assert.Null(reply["states"]!["2"]);  // needs 1
        Assert.Equal(7, reply["states"]!.AsObject().Count);  // an entry for every id listed and for no other
    }

    [Fact]
    public void TestOnlyAnAvailableMissionCanBeActivated()
    {
        Assert.False(Call("activate", "mission_id=2"));  // needs 1
        Assert.False(Call("activate", "mission_id=99"));  // no definition
        Assert.False(Call("activate", "mission_id=nope"));
        Assert.True(Call("activate", "mission_id=1"));
        Assert.False(Call("activate", "mission_id=1"));  // already active
        var reply = Missions();
        Assert.Equal(("1", "3,5,6,7,8,9"), (Ids(reply["active"]), Ids(reply["available"])));
        Eq("""{"state": 2, "objectives": []}""", reply["states"]!["1"]);
        Assert.True(Call("activate_all_available"));
        Assert.Equal("1,3,5,6,7,8,9", Ids(Missions()["active"]));
    }

    [Fact]
    public void TestAHarvestFinishesAMissionPaysItAndAnnouncesIt()
    {
        Call("activate", "mission_id=1");
        var before = Gm();
        var now = Hb(Kill(71));
        var paid = Update(now, 15)!;
        Assert.Single(now, u => (long)u!["update"]! == 15);  // the kill's css and the reward add into one
        Assert.Equal("gm", (string)paid["type"]!);
        Eq(state.Wallet(ME)["gm"], paid["amount"]);  // the new balance, the kill's own css reward included
        var row = state.WalletPage(ME, "gm", 0).Single(r => (string)r!["summary"]! == "gm$ reward")!;
        Eq("[\"Credit\", 100.0]", T(row["type"]!["name"], row["amount"]));
        Assert.True(Gm() >= before + 100);
        var next = Hb();  // the trophy score of the kill, then the announcement
        Assert.Equal("6,3", Kinds(next));
        Assert.Equal("Mission Harvest a Test Deer completed.", (string)Update(next, 3)!["text"]!);
        var reply = Missions();
        Assert.Equal(("", "1"), (Ids(reply["active"]), Ids(reply["completed"])));
        Eq("""{"state": 3}""", reply["states"]!["1"]);
        Assert.Contains("2", Ids(reply["available"]).Split(','));  // 2 needed 1
    }

    [Fact]
    public void TestATwoObjectiveMissionFinishesWithItsLastObjective()
    {
        Call("activate", "mission_id=1");
        Hb(Kill(71));
        Call("activate", "mission_id=2");
        Hb();
        Hb(Spawn(72), Track(72));
        Assert.Equal("0", Objectives(2));
        var next = Hb();  // the objective toast: a mission that is not finished pays nothing
        Assert.Equal("3", Kinds(next));
        Assert.Equal("Mission \"Track and Harvest\", objective \"Locate Test Deer tracks.\" completed.", (string)Update(next, 3)!["text"]!);
        var gm = Gm();
        var now = Hb(Kill(74));
        var em = Update(now, 15)!;
        Assert.Equal("em", (string)em["type"]!);
        Eq(187.0, em["amount"]);  // 182 and the reward's 5
        Assert.DoesNotContain(Kinds(now).Split(','), k => k == "10");  // an item reward is not delivered
        Assert.True(Gm() >= gm + 250);
        Assert.Equal(["em$ reward"], state.WalletPage(ME, "em", 0).Select(r => (string)r!["summary"]!));
        Assert.Equal("1,2", Ids(Missions()["completed"]));
    }

    [Fact]
    public void TestAnotherSpeciesOrGenderFinishesNothing()
    {
        Call("activate", "mission_id=1");
        Call("activate", "mission_id=9");
        Hb(Kill(71, species: 17));
        Hb(Kill(72));  // a male deer: mission 1 only
        Assert.Equal("1", Ids(Missions()["completed"]));
        Hb(Kill(73, gender: 1));
        Assert.Equal("1,9", Ids(Missions()["completed"]));
    }

    [Fact]
    public void TestAnOrderedObjectiveCountsOnlyAfterTheOnesBeforeIt()
    {
        Call("activate", "mission_id=7");
        Hb(Spawn(80), SpotOf(80));
        Assert.Equal("", Objectives(7));
        Hb(Kill(81));
        Assert.Equal("0", Objectives(7));
        Hb(Spawn(82), SpotOf(82));
        Assert.Equal("7", Ids(Missions()["completed"]));
    }

    [Fact]
    public void TestClueCountsAreOfOneAnimal()
    {
        Call("activate", "mission_id=8");
        Hb(Spawn(90), Track(90));
        Hb(Spawn(91), Track(91));  // two animals, one clue each
        Assert.Equal("", Ids(Missions()["completed"]));
        Hb(Track(90));
        Assert.Equal("8", Ids(Missions()["completed"]));
    }

    [Fact]
    public void TestEnteringAPlaceMeetsAnArriveObjective()
    {
        Call("activate", "mission_id=6");
        Hb(Enter("TEST_RESERVE_TEST_POND", "leave"), Enter("TEST_RESERVE_OTHER_PLACE"));
        Assert.Equal("", Ids(Missions()["completed"]));
        Hb(Enter("TEST_RESERVE_TEST_POND"));
        Assert.Equal("6", Ids(Missions()["completed"]));
    }

    [Fact]
    public void TestAHuntStartMeetsAStartObjectiveAndForgetsASingleHuntMissionsProgress()
    {
        Call("activate", "mission_id=3");
        Call("activate", "mission_id=5");
        Hb(Spawn(95), Track(95));
        Hb();
        Assert.Equal("0", Objectives(5));
        state.NoteLaunch(ME, Q("start=999&reserve=2"));  // another lodge
        Assert.Equal("", Objectives(5));
        Assert.Equal("", Ids(Missions()["completed"]));
        var before = Gm();
        state.NoteLaunch(ME, Q("start=101&reserve=2"));
        Assert.Equal("3", Ids(Missions()["completed"]));
        Assert.Equal(before + 40, Gm());
        Assert.Equal("15,3", Kinds(Hb()));
    }

    [Fact]
    public void TestSwitchesAndFavouriteGroupsAreStored()
    {
        Assert.True(Call("update_automatic_activation", "auto_mission_activate=true"));
        Assert.True(Call("update_hide_completed_favourite", "hide_completed_favourite=1"));
        Assert.True(Call("update_favourite", "group_id=10&is_favourite=true"));
        Assert.True(Call("update_favourite", "group_id=11&is_favourite=true"));
        Assert.True(Call("update_favourite", "group_id=10&is_favourite=false"));
        Assert.False(Call("update_favourite", "group_id=x&is_favourite=true"));
        var reply = Missions();
        Eq("[1, 0, 1, [11]]", T(reply["auto_mission_activate"], reply["activate_only_favourite"], reply["hide_completed_favourite"], reply["favourites"]));
        Assert.True(Call("update_automatic_activation", "auto_mission_activate=false"));
        Assert.True(Call("update_activate_only_favourite", "activate_only_favourite=true"));
        Eq("[0, 1]", T(Missions()["auto_mission_activate"], Missions()["activate_only_favourite"]));
    }

    const string Saved = """
        {"active": [2, 901], "completed": [1, 900], "available": [3],
         "states": {"1": {"state": 3}, "900": {"state": 3}, "2": {"state": 2, "objectives": [0]}, "901": {"state": 2, "objectives": [2, 3]}, "3": {"state": 1}},
         "auto_mission_activate": 1, "activate_only_favourite": 0, "hide_completed_favourite": true, "favourites": [500]}
        """;

    void Saved8(string saved)
    {
        state.CreateAccount(8, "second");
        using var con = state.Store.Open(8);
        con.Begin();
        PlayerRows.SetExtra(con, "Mission_missions.json", O(saved));
        con.Commit();
    }

    [Fact]
    public void TestARestoredMissionPageCarriesOverAndKeepsIdsWithoutADefinition()
    {
        Saved8(Saved);
        var reply = Missions(8);
        Assert.Equal(("2,901", "1,900", "3,5,6,7,8,9"), (Ids(reply["active"]), Ids(reply["completed"]), Ids(reply["available"])));
        Eq("""{"state": 2, "objectives": [0]}""", reply["states"]!["2"]);
        Eq("""{"state": 2, "objectives": [2, 3]}""", reply["states"]!["901"]);
        Eq("""{"state": 3}""", reply["states"]!["900"]);
        Eq("[1, 0, 1, [500]]", T(reply["auto_mission_activate"], reply["activate_only_favourite"], reply["hide_completed_favourite"], reply["favourites"]));
        Assert.Equal("", Ids(Missions()["completed"]));  // the other account has none of it
        // the saved progress counts: the harvest finishes mission 2, which unlocks 4 and 10, activated by the switch
        var before = Gm(8);
        state.HeartbeatReply(8, Events(Kill(71)));
        Assert.Equal(("1,2,900", "4,10,901"), (Ids(Missions(8)["completed"]), Ids(Missions(8)["active"])));
        Assert.True(Gm(8) >= before + 250);
    }

    [Fact]
    public void TestOnlyFavouriteGroupsAreActivatedWhenTheSwitchSaysSo()
    {
        Saved8(Saved.Replace("\"auto_mission_activate\": 1, \"activate_only_favourite\": 0", "\"auto_mission_activate\": 0, \"activate_only_favourite\": 1").Replace("[500]", "[10]"));
        state.HeartbeatReply(8, Events(Kill(71)));
        var reply = Missions(8);
        Assert.Equal("4,901", Ids(reply["active"]));  // 4 is in the favourite group 10, 10 is not
        Assert.Contains("10", Ids(reply["available"]).Split(','));
    }

    [Fact]
    public void TestARestoreCopiesTheMissionPageAndTheNextRestoreReplacesIt()
    {
        var saved = TestZip.Own().Reply("Mission_missions.json", "/v1/Mission/missions", Saved);
        var result = Restorer.Import(state, saved.Write(tmp, "a.zip"), null, tmp);
        using (var con = state.Store.Open(result.Account))
        {
            Eq("[[1, 3], [2, 2], [900, 3], [901, 2]]", new JsonArray(con.Rows("SELECT mission_id, state FROM missions WHERE account_id = $a ORDER BY mission_id")
                .Select(r => (JsonNode?)new JsonArray((long)r[0], (long)r[1])).ToArray()));
            Eq("[2, 3]", Reply(Encoding.UTF8.GetBytes((string)con.Scalar("SELECT objectives FROM missions WHERE account_id = $a AND mission_id = 901")!)));
        }
        Assert.Equal("1,900", Ids(Missions(result.Account)["completed"]));
        Restorer.Import(state, TestZip.Own().Write(tmp, "b.zip"), result.Account, tmp);  // a zip without a mission page
        Assert.Equal("", Ids(Missions(result.Account)["completed"]));
    }

    // the website's missions page: mission 1 names a reserve, a species and a weapon (and has markup in its text), mission 3 none
    async Task<(Loopback Server, (string, string)[] Headers)> WebServer()
    {
        var app = APP;
        foreach (var (key, value) in O(Definitions))
            app[key] = value!.DeepClone();
        app["locations"]![0]!["name"] = "Test Lodge";
        var first = app["missions"]![0]!.AsObject();
        first["desc"] = "Hello,<br/><br/>take <b>your</b> time.";
        first["reserves"] = JsonNode.Parse("[7, 2]");
        first["species"] = JsonNode.Parse("[1]");
        first["weapons"] = JsonNode.Parse("[5]");
        app["mission_groups"]![0]!["title"] = "Test Pack";
        state = new State(Path.Combine(tmp, "web.db"), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false);
        var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, state, tmp), tmp);
        return (server, [("Cookie", $"{SignIn.SessionCookie}={state.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}"), ("X-Requested-With", "XMLHttpRequest"), ("Content-Type", "application/x-www-form-urlencoded")]);
    }

    [Fact]
    public async Task TestTheWebsiteReadsAndActivatesTheOwnersMissions()
    {
        var (server, headers) = await WebServer();
        await using var _ = server;
        Assert.Equal(401, (await server.SendAsync("GET", "web.test", "/account/missions", [])).Status);
        Assert.Equal(401, (await server.SendAsync("POST", "web.test", "/account/missions/activate", headers[1..], Q("mission_id=1"))).Status);
        Assert.Equal(400, (await server.SendAsync("POST", "web.test", "/account/missions/activate", [headers[0]], Q("mission_id=1"))).Status);  // not from our page
        Assert.Equal(404, (await server.SendAsync("POST", "web.test", "/account/missions/nope", headers, Q(""))).Status);

        var page = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/missions", [headers[0]])).Body)!;
        var one = page["missions"]!.AsArray().Single(m => (long)m!["id"]! == 1)!;
        Eq("""["Hello, take your time.", "available", ["Harvest a Test Deer"], "100 gm$", "Test Reserve", "Test Deer", "Test Rifle"]""", T(one["desc"], one["state"], one["objectives"], one["reward"], one["reserve"], one["species"], one["weapon"]));
        Eq("""["locked", "available", ""]""", T(page["missions"]!.AsArray().Single(m => (long)m!["id"]! == 2)!["state"], page["missions"]!.AsArray().Single(m => (long)m!["id"]! == 3)!["state"],
            page["missions"]!.AsArray().Single(m => (long)m!["id"]! == 3)!["reserve"]));
        Eq("""[{"id": 10, "title": "Test Pack", "desc": "", "favourite": false, "missions": [4]}, {"id": 11, "title": "", "desc": "", "favourite": false, "missions": [10]}]""", page["groups"]);

        Assert.Equal(200, (await server.SendAsync("POST", "web.test", "/account/missions/activate", headers, Q("mission_id=1"))).Status);
        Assert.Equal(409, (await server.SendAsync("POST", "web.test", "/account/missions/activate", headers, Q("mission_id=2"))).Status);  // a dependency is open
        Assert.Equal(200, (await server.SendAsync("POST", "web.test", "/account/missions/update_favourite", headers, Q("group_id=10&is_favourite=true"))).Status);
        Assert.Equal(200, (await server.SendAsync("POST", "web.test", "/account/missions/update_automatic_activation", headers, Q("auto_mission_activate=true"))).Status);
        page = JsonNode.Parse((await server.SendAsync("GET", "web.test", "/account/missions", [headers[0]])).Body)!;
        Eq("""["active", true, true, false]""", T(page["missions"]!.AsArray().Single(m => (long)m!["id"]! == 1)!["state"], page["groups"]![0]!["favourite"], page["auto_mission_activate"], page["hide_completed_favourite"]));
        Assert.Equal("1", Ids(Missions()["active"]));  // the game sees the same rows
    }

    [Fact]
    public async Task TestWithoutMissionsTheWebsiteGetsNone()
    {
        var empty = new State(Path.Combine(tmp, "web-empty.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        await using var server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), WebCfg, empty, tmp), tmp);
        var cookie = ("Cookie", $"{SignIn.SessionCookie}={empty.Store.CreateWebSession(ME, TimeSpan.FromHours(1))}");
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", "/account/missions", [cookie])).Status);
        Assert.Equal(404, (await server.SendAsync("POST", "web.test", "/account/missions/activate_all_available", [cookie, ("X-Requested-With", "x")], Q(""))).Status);
    }

    static readonly JsonObject WebCfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
}

/// <summary>The Mission/* calls over HTTP on a community server.</summary>
public class MissionApiTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    Loopback server = null!;
    string token = "";

    public async ValueTask InitializeAsync()
    {
        var app = APP;
        app["missions"] = JsonNode.Parse("""[{"id": 1, "title": "Harvest a Test Deer", "dependencies": [], "rewards": [{"text": "100 gm$"}], "objectives": [{"id": 0, "title": "Harvest a Test Deer"}]}]""");
        state = new State(Path.Combine(tmp, "web.db"), Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, offline: false);
        token = state.Store.CreateToken(ME);
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    async Task<string> Post(string path, string body = "")
    {
        var reply = await server.SendAsync("POST", "api.test", path, [("Content-Type", "application/x-www-form-urlencoded")],
                                           Encoding.ASCII.GetBytes($"{body}&oauth_access_token={token}"));
        return Encoding.UTF8.GetString(reply.Body);
    }

    [Fact]
    public async Task TestTheCallsAnswerFromTheAccountsStates()
    {
        Eq("""{"state": 1}""", JsonNode.Parse(await Post(Replay.MISSIONS))!["states"]!["1"]);
        Assert.Equal("false", await Post(Replay.MISSION_ACTIVATE, "mission_id=7"));
        Assert.Equal("true", await Post(Replay.MISSION_ACTIVATE, "mission_id=1"));
        Assert.Equal("false", await Post(Replay.MISSION_ACTIVATE, "mission_id=1"));
        Assert.Equal("true", await Post(Replay.MISSION_AUTO, "auto_mission_activate=true"));
        Assert.Equal("true", await Post(Replay.MISSION_FAVOURITE, "group_id=3&is_favourite=true"));
        var reply = JsonNode.Parse(await Post(Replay.MISSIONS))!;
        Eq("[[1], [], 1, [3]]", T(reply["active"], reply["available"], reply["auto_mission_activate"], reply["favourites"]));
    }

    [Fact]
    public async Task TestWithoutAMissionTheEmptyReplyStands()
    {
        var empty = new State(Path.Combine(tmp, "empty.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        await using var other = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, empty, tmp), tmp);
        var t = empty.Store.CreateToken(ME);
        var reply = await other.SendAsync("POST", "api.test", Replay.MISSIONS, [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes("oauth_access_token=" + t));
        Eq("""{"active": [], "completed": [], "available": [], "states": {}}""", JsonNode.Parse(reply.Body));
    }
}
