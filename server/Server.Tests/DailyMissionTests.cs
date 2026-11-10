using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Daily missions on a community server, with made-up templates and dates (`State.Clock`): the rotation, the calendar, the streak, the pay and the statistics.</summary>
public class DailyMissionTests : StateTestBase
{
    // easy: 11 spots a male deer, 12 tracks a wolf; medium: 13 harvests a female deer; hard: 14 two tracks of a deer, then spots a deer
    internal static readonly Facts DailyFacts = Facts.Parse("""
        {"format": 1,
         "reserves": [{"id": 2, "name": "Test Reserve", "species": [1]}, {"id": 1, "name": "Alpha Reserve", "species": [1, 56]}],
         "daily_missions": {
          "cycle": [1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 3],
          "difficulties": [
           {"id": 1, "title": "Easy", "alias": "EASY", "cooldown": 30, "color": "green", "reward_id": 130, "reward": 100},
           {"id": 2, "title": "Medium", "alias": "MEDIUM", "cooldown": 20, "color": "blue", "reward_id": 131, "reward": 500},
           {"id": 3, "title": "Hard", "alias": "HARD", "cooldown": 10, "color": "red", "reward_id": 132, "reward": 1000}],
          "templates": [
           {"id": 11, "difficulty": 1, "title": "Spot a male Test Deer", "giver": {"id": 15, "alias": "TEST_GIVER"}, "objectives": ["Spot a male Test Deer"]},
           {"id": 12, "difficulty": 1, "title": "ID tracks from a Test Wolf", "giver": {"id": 16, "alias": "OTHER_GIVER"}, "objectives": ["ID tracks from a Test Wolf"]},
           {"id": 13, "difficulty": 2, "title": "Harvest a female Test Deer", "giver": {"id": 16, "alias": "OTHER_GIVER"}, "objectives": ["Harvest a female Test Deer"]},
           {"id": 14, "difficulty": 3, "title": "Hunt a Test Deer", "giver": {"id": 15, "alias": "TEST_GIVER"}, "objectives": ["ID 2 tracks from a Test Deer", "Then spot a Test Deer"]}]}}
        """);

    static readonly DateOnly D0 = new(2031, 3, 10);

    int individual = 100;

    public DailyMissionTests()
    {
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        Scoring.Variants = new() { [1] = (30, 260, 1.1), [17] = (600, 1800, 0.9) };  // pinned, so no test reads files
        state = New("state.db");
        On(D0);
    }

    State New(string name, bool offline = false, JsonObject? connect = null) =>
        new(Path.Combine(tmp, name), Py.DumpBytes(connect ?? CONNECT), BASE, ICON, Application(), LOCALE, TEXTURES, offline: offline, facts: DailyFacts);

    static JsonObject Application()
    {
        var app = APP;
        app["species"]!.AsArray().Add(O("""{"id": 2, "name": "Test Goose", "define": "TEST_GOOSE"}"""));
        return app;
    }

    /// <summary>Set the clock at noon (UTC) of the date.</summary>
    void On(DateOnly date, State? s = null) => (s ?? state).Clock = () => (date.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber) * 86400.0 + 43200;

    JsonNode Calendar(long account = ME, State? s = null) => Reply((s ?? state).DailyCalendarReply(account)!);

    static JsonNode TodayOf(JsonNode calendar) => calendar["calendar"]!.AsArray().Single(e => (bool)e!["today"]!)!;

    static long MissionOf(JsonNode calendar) => (long)TodayOf(calendar)["mission"]!["id"]!;

    JsonNode Spawn(long species, long gender) => O($$"""{"event": 7, "individual": {{++individual}}, "species": {{species}}, "gender": {{gender}}, "weight_grams": 50000, "texture": 0}""");

    JsonNode SpotOf() => O($$"""{"event": 11, "individual": {{individual}}, "milliseconds_viewed": 3000, "percentage": 80}""");

    JsonNode Track() => O($$"""{"event": 12, "individual": {{individual}}, "track_type": 3}""");

    /// <summary>The events that finish today's easy mission, whichever it is: a male deer spotted and a wolf's tracks.</summary>
    JsonNode[] Easy() => [Spawn(1, 0), SpotOf(), Spawn(56, 0), Track()];

    JsonNode[] FemaleKill()
    {
        var events = AccountIsolationTests.Harvest(++individual, 72270);
        events[0]["species"] = 1;
        events[0]["gender"] = 1;
        events[^1]["extended_species"] = 1;
        return events;
    }

    bool[] Completed() => [.. JsonNode.Parse((string)TodayOf(Calendar())["objectives"]!)!.AsArray().Select(o => o!["objective_data"]!["completed"] is not null)];

    static JsonNode? Update(JsonArray updates, int kind) => updates.LastOrDefault(u => (long)u!["update"]! == kind);

    double Gm(long account = ME) => state.Wallet(account)["gm"]!.GetValue<double>();

    long Days(string difficulty, long account = ME) =>
        Reply(state.StatisticsReply(account, Encoding.UTF8.GetBytes($"user_id={account}"))!)["data"]!["daily_missions"]![difficulty] is { } d ? (long)d["days"]! : 0;

    [Fact]
    public void TestWithoutTemplatesOrOnALocalServerThereAreNoDailyMissions()
    {
        var none = new State(Path.Combine(tmp, "none.db"), Py.DumpBytes(CONNECT), BASE, ICON, Application(), LOCALE, TEXTURES, offline: false, facts: Facts.Parse("""{"format": 1}"""));
        Assert.Null(none.DailyCalendarReply(ME));
        Assert.Null(none.DailyActivate(ME));
        Assert.Null(New("local.db", offline: true).DailyCalendarReply(ME));
    }

    [Fact]
    public void TestANewAccountsCalendarIsFrozenAndHoldsTodaysMissionAmongSevenDays()
    {
        var calendar = Calendar();
        Eq("[1, 0, false]", T(calendar["is_frozen"], calendar["broken_streak_info"]!["streak_larger"], calendar["broken_streak_info"]!["has_broken_streak"]));
        Assert.Equal((D0.DayNumber - new DateOnly(1970, 1, 1).DayNumber + 1) * 86400L - 1, (long)calendar["mission_end"]!);
        var days = calendar["calendar"]!.AsArray();
        Eq("[1, 2, 3, 4, 5, 6, 7]", new JsonArray(days.Select(d => d!["day_number"]!.DeepClone()).ToArray()));
        Eq("""["EASY", "EASY", "EASY", "MEDIUM", "EASY", "EASY", "EASY"]""", new JsonArray(days.Select(d => d!["difficulty"]!["alias"]!.DeepClone()).ToArray()));
        Eq("[100, 100, 100, 500, 100, 100, 100]", new JsonArray(days.Select(d => d!["rewards"]![0]!["amount"]!.DeepClone()).ToArray()));
        Assert.Equal(1, days.Count(d => (bool)d!["today"]!));
        Assert.Equal(1, days.Count(d => d!["mission"] is not null));  // only today's has one
        var today = TodayOf(calendar);
        Eq("""[1, "2031-03-10", "Easy", 30, "green", 130, "SILVER"]""", T(today["day_number"], today["date"], today["difficulty"]!["title"], today["difficulty"]!["mission_cooldown"],
                                                                      today["difficulty"]!["color"], today["rewards"]![0]!["id"], today["rewards"]![0]!["reward_type"]));
        Eq("""[{"text": "100 gm$"}]""", today["rewards_translations"]);
        Assert.Equal(today["objectives"]!.ToString(), today["mission"]!["objectives"]!.ToString());
    }

    [Fact]
    public void TestTheMissionIsTheDatesForEveryoneAndFollowsTheDateInTurn()
    {
        state.CreateAccount(8, "second");
        state.DailyActivate(ME);
        state.DailyActivate(8);
        var first = MissionOf(Calendar());
        Assert.Equal(first, MissionOf(Calendar(8)));
        Assert.Contains(first, new[] { 11L, 12 });
        Hb(Easy());  // a completed day is followed by the next date's mission
        state.HeartbeatReply(8, Events(Easy()));
        On(D0.AddDays(1));
        var second = MissionOf(Calendar());
        Assert.NotEqual(first, second);  // the two easy templates in turn
        Assert.Equal(second, MissionOf(Calendar(8)));
        Hb(Easy());
        On(D0.AddDays(2));
        Assert.Equal(first, MissionOf(Calendar()));
    }

    [Fact]
    public void TestTheMissionIsDescribedAsThePageReadsIt()
    {
        var first = TodayOf(Calendar());  // a frozen account keeps the date it was made on
        state.CreateAccount(8, "second");
        On(D0.AddDays(1));
        var second = TodayOf(Calendar(8));
        var (spot, tracks) = (long)first["mission"]!["id"]! == 11 ? (first, second) : (second, first);
        var mission = spot["mission"]!;
        Eq("""[11, "Spot a male Test Deer", 0, "MULE_DEER", "Alpha Reserve, Test Reserve", "MULE_DEER"]""",
           T(mission["id"], mission["title"], mission["gender"], mission["species"], mission["reserves"], spot["species"]));
        Eq("""{"id": 15, "name": "Test Giver", "alias": "TEST_GIVER", "description": "Test Giver", "image": "test_giver.jpg"}""", spot["character"]);
        Eq("""[{"objective_data": {"id": 1, "title": "Spot a male Test Deer", "update_message": "", "conditions": "a.species() == c.species.MULE_DEER and a.gender() == c.gender.MALE"}, "event_type": "SPOT"}]""",
           JsonNode.Parse((string)spot["objectives"]!));
        var clue = JsonNode.Parse((string)tracks["objectives"]!)![0]!;
        Eq("""["CLUE", "a.species() == c.species.WOLF and e.trackType() == c.clue.FOOTSTEPS"]""", T(clue["event_type"], clue["objective_data"]!["conditions"]));
    }

    [Fact]
    public void TestActivatingStartsTheFirstDayAndAnActiveStreakIsKept()
    {
        Assert.True(state.DailyActivate(ME));
        Assert.Equal(0, (long)Calendar()["is_frozen"]!);
        Hb(Easy());
        Assert.Equal(1, Days("1"));
        On(D0.AddDays(1));
        Assert.Equal(2, (long)TodayOf(Calendar())["day_number"]!);
        state.DailyActivate(ME);  // not frozen: nothing starts again
        Assert.Equal(2, (long)TodayOf(Calendar())["day_number"]!);
    }

    [Fact]
    public void TestAFrozenAccountsEventsCountForNothing()
    {
        Hb(Easy());
        Assert.Equal(0, Days("1"));
        var objectives = JsonNode.Parse((string)TodayOf(Calendar())["objectives"]!)!;
        Assert.DoesNotContain(objectives.AsArray(), o => o!["objective_data"]!["completed"] is not null);
    }

    [Fact]
    public void TestCompletingTheDayPaysTellsTheGameAndCountsTheDay()
    {
        state.DailyActivate(ME);
        var before = Gm();
        var updates = Hb(Easy());
        var paid = Update(updates, 15)!;
        Assert.Equal("gm", (string)paid["type"]!);
        Eq(state.Wallet(ME)["gm"], paid["amount"]);
        Assert.Equal(before + 100, Gm());
        var row = state.WalletPage(ME, "gm", 0).Single(r => (string)r!["summary"]! == "Reward for Daily Mission")!;
        Eq("""["Credit", 100.0, ""]""", T(row["type"]!["name"], row["amount"], row["mission"]));
        var done = Update(updates, 17)!;
        Assert.Single(updates, u => (long)u!["update"]! == 17);
        Assert.Equal(1, (long)done["difficulty_id"]!);
        Assert.Equal(MissionOf(Calendar()), (long)done["mission_id"]!);
        Assert.Equal(1, Days("1"));
        var objectives = JsonNode.Parse((string)TodayOf(Calendar())["objectives"]!)!;
        Assert.All(objectives.AsArray(), o => Assert.True((bool)o!["objective_data"]!["completed"]!));
        Hb(Easy());  // the day is done: no second payment
        Assert.Equal(before + 100, Gm());
        Assert.Equal(1, Days("1"));
        Assert.Equal(1, (long)TodayOf(Calendar())["day_number"]!);  // the entry stays until the day ends
    }

    [Fact]
    public void TestTheStreakMovesThroughTheCycleAndEachDifficultyIsCounted()
    {
        state.DailyActivate(ME);
        for (var day = 0; day < 3; day++)
        {
            On(D0.AddDays(day));
            Hb(Easy());
        }
        On(D0.AddDays(3));
        var calendar = Calendar();
        Eq("""[4, "MEDIUM", 13, 500]""", T(TodayOf(calendar)["day_number"], TodayOf(calendar)["difficulty"]!["alias"], TodayOf(calendar)["mission"]!["id"], TodayOf(calendar)["rewards"]![0]!["amount"]));
        var before = Gm();
        Hb(Easy());  // not the medium mission's objective
        Assert.Equal(before, Gm());
        var updates = Hb(FemaleKill());
        Assert.True(Gm() >= before + 500);
        Assert.Equal(2, (long)Update(updates, 17)!["difficulty_id"]!);
        Assert.Equal(13, (long)Update(updates, 17)!["mission_id"]!);
        Eq("[3, 1, 0]", T(Days("1"), Days("2"), Days("3")));
        On(D0.AddDays(4));
        Assert.Equal(5, (long)TodayOf(Calendar())["day_number"]!);
    }

    /// <summary>Put the account on the day of its streak (0 is the first) of the date, not frozen and not done.</summary>
    void SetDay(long day)
    {
        using var con = state.Store.Open(ME);
        con.Begin();
        con.Exec("INSERT INTO daily_missions (account_id, body) VALUES ($a, ?1) ON CONFLICT (account_id) DO UPDATE SET body = ?1",
                 """{"frozen": false, "day": DAY, "date": "2031-03-10", "done": false, "objectives": [], "has_broken": false, "larger": 0, "days": {}}""".Replace("DAY", day.ToString()));
        con.Commit();
    }

    [Fact]
    public void TestTheHardDayFinishesWithItsLastObjectiveInOrder()
    {
        SetDay(15);
        var today = TodayOf(Calendar());
        Eq("""[2, "HARD", 14, 1000]""", T(today["day_number"], today["difficulty"]!["alias"], today["mission"]!["id"], today["rewards"]![0]!["amount"]));
        var before = Gm();
        Hb(Spawn(1, 0), SpotOf());  // ordered: not before the tracks
        Assert.Equal(before, Gm());
        Hb(Track());
        Hb(Track());
        Assert.Equal(before, Gm());  // the second objective, not yet
        Assert.Equal([true, false], Completed());
        var updates = Hb(Spawn(1, 0), SpotOf());
        Assert.Equal(before + 1000, Gm());
        Assert.Equal(3, (long)Update(updates, 17)!["difficulty_id"]!);
        Assert.Equal(1, Days("3"));
    }

    [Fact]
    public void TestEachCompletedCycleAddsATenthToTheRewardsUpToDouble()
    {
        SetDay(31);  // the 32nd day: the 16th of the second cycle, hard, 10 % more; its block is days 29 to 35
        var calendar = Calendar();
        Eq("""["EASY", "EASY", "EASY", "HARD", "EASY", "EASY", "EASY"]""", new JsonArray(calendar["calendar"]!.AsArray().Select(d => d!["difficulty"]!["alias"]!.DeepClone()).ToArray()));
        Eq("[110, 110, 110, 1100, 120, 120, 120]", new JsonArray(calendar["calendar"]!.AsArray().Select(d => d!["rewards"]![0]!["amount"]!.DeepClone()).ToArray()));
        Assert.Equal(4, (long)TodayOf(calendar)["day_number"]!);
        SetDay(16 * 12);  // 12 cycles done: the bonus stops at 10
        Eq("[200, 200, 2000, 200, 200, 200, 1000]", new JsonArray(Calendar()["calendar"]!.AsArray().Select(d => d!["rewards"]![0]!["amount"]!.DeepClone()).ToArray()));
    }

    [Fact]
    public void TestASkippedDayBreaksTheStreakAndFreezesTheCalendarUntilItIsActivated()
    {
        state.DailyActivate(ME);
        Hb(Easy());
        On(D0.AddDays(1));
        Hb(Easy());  // day 2 done
        On(D0.AddDays(2));
        var kept = MissionOf(Calendar());
        Assert.Equal(3, (long)TodayOf(Calendar())["day_number"]!);
        On(D0.AddDays(5));  // the third day was missed
        var calendar = Calendar();
        Eq("[1, true, 2, 1]", T(calendar["is_frozen"], calendar["broken_streak_info"]!["has_broken_streak"], calendar["broken_streak_info"]!["streak_larger"], TodayOf(calendar)["day_number"]));
        Assert.Equal("2031-03-12", (string)TodayOf(calendar)["date"]!);  // the day it was left on
        Assert.Equal(kept, MissionOf(calendar));  // the same mission, however long it stays frozen
        On(D0.AddDays(9));
        Assert.Equal(kept, MissionOf(Calendar()));
        Hb(Easy());
        Assert.Equal(2, Days("1"));  // frozen: nothing counted
        state.DailyActivate(ME);
        calendar = Calendar();
        Eq("[0, false, 0, 1, \"2031-03-19\"]", T(calendar["is_frozen"], calendar["broken_streak_info"]!["has_broken_streak"], calendar["broken_streak_info"]!["streak_larger"],
                                                  TodayOf(calendar)["day_number"], TodayOf(calendar)["date"]));
    }

    [Fact]
    public void TestAnEmptyStreakThatLapsesFreezesWithoutBreakingAnything()
    {
        state.DailyActivate(ME);
        On(D0.AddDays(1));  // day 1 was never done
        var calendar = Calendar();
        Eq("[1, false, 0]", T(calendar["is_frozen"], calendar["broken_streak_info"]!["has_broken_streak"], calendar["broken_streak_info"]!["streak_larger"]));
    }

    [Fact]
    public void TestTheSchemaOfVersion13GetsTheDailyMissionsTable()
    {
        var file = Path.Combine(tmp, "v13.db");
        var store = new Store(file);
        using (var con = store.Open())
        {
            con.Exec("DROP TABLE daily_missions");
            con.Exec("PRAGMA user_version = 13");
        }
        using var migrated = new Store(file).Open();
        Assert.Equal(1L, migrated.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'daily_missions'"));
        Assert.Equal((long)Store.Version, migrated.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void TestTheDaysCountedHereAddToTheRestoredBaseAndAwardTheAchievements()
    {
        var connect = CONNECT;
        connect["achievements"] = JsonNode.Parse("""[{"id": 169, "category": 4, "group": 127, "group_title": "Easy", "value": "daily_missions.EASY.days", "triggers": [1, 12, 25]}]""");
        state = New("achievements.db", connect: connect);
        On(D0);
        state.CreateAccount(8, "second");
        using (var con = state.Store.Open(8))
        {
            PlayerRows.SetStatistics(con, O("""{"daily_missions": {"1": {"days": 11}, "3": {"days": 2}}}"""), 0, 0, null, null);
        }
        state.DailyActivate(8);
        state.HeartbeatReply(8, Events(Easy()));
        Eq("""{"1": {"days": 12}, "3": {"days": 2}}""", Reply(state.StatisticsReply(8, "user_id=8"u8.ToArray())!)["data"]!["daily_missions"]);
        using var db = state.Store.Open(8);
        state.UpdateHunterscore(db, 8);
        var row = RestoredReads.AchievementStats(db).AsArray().Single(a => (long)a!["id"]! == 169)!;
        Eq("[169, 12, [\"1\", \"12\"]]", T(row["id"], row["value"], new JsonArray(row["triggers"]!.AsObject().Select(t => (JsonNode?)t.Key).ToArray())));
    }

    [Fact]
    public void TestARestoredCalendarCarriesOverAndTheNextRestoreReplacesIt()
    {
        var saved = TestZip.Own().Reply("Daily_Mission_get_daily_missions_calendar.json", "/v1/Daily_Mission/get_daily_missions_calendar", """
            {"is_frozen": 1, "mission_end": 1948000000.5, "broken_streak_info": {"has_broken_streak": true, "streak_larger": 6},
             "calendar": [{"day_number": 1, "today": true, "date": "2031-02-01", "mission": {"id": 99, "title": "Another pool's"}}]}
            """);
        var result = Restorer.Import(state, saved.Write(tmp, "a.zip"), null, tmp);
        var calendar = Calendar(result.Account);
        Eq("""[1, true, 6, 1, "2031-02-01"]""", T(calendar["is_frozen"], calendar["broken_streak_info"]!["has_broken_streak"], calendar["broken_streak_info"]!["streak_larger"],
                                                  TodayOf(calendar)["day_number"], TodayOf(calendar)["date"]));
        Assert.Contains(MissionOf(calendar), new[] { 11L, 12 });  // ours, for the date it showed
        Restorer.Import(state, TestZip.Own().Write(tmp, "b.zip"), result.Account, tmp);  // a zip without a calendar
        Eq("[1, false, 0]", T(Calendar(result.Account)["is_frozen"], Calendar(result.Account)["broken_streak_info"]!["has_broken_streak"], Calendar(result.Account)["broken_streak_info"]!["streak_larger"]));
        Assert.Equal("2031-03-10", (string)TodayOf(Calendar(result.Account))["date"]!);
    }

    [Fact]
    public void TestARestoredCalendarThatIsNotFrozenStartsToday()
    {
        var saved = TestZip.Own().Reply("Daily_Mission_get_daily_missions_calendar.json", "/v1/Daily_Mission/get_daily_missions_calendar", """
            {"is_frozen": 0, "broken_streak_info": {"has_broken_streak": false, "streak_larger": 0}, "calendar": [{"day_number": 1, "today": true, "date": "2031-02-01"}]}
            """);
        var result = Restorer.Import(state, saved.Write(tmp, "a.zip"), null, tmp);
        var calendar = Calendar(result.Account);
        Eq("[0, 1, \"2031-03-10\"]", T(calendar["is_frozen"], TodayOf(calendar)["day_number"], TodayOf(calendar)["date"]));
        var before = Gm(result.Account);
        state.HeartbeatReply(result.Account, Events(Easy()));
        Assert.Equal(before + 100, Gm(result.Account));
    }
}

/// <summary>The Daily_Mission/* calls over HTTP on a community server.</summary>
public class DailyMissionApiTests : StateTestBase, IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    Loopback server = null!;
    string token = "";

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: DailyMissionTests.DailyFacts);
        token = state.Store.CreateToken(ME);
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    async Task<JsonNode> Post(string path, string? oauth = null)
    {
        var reply = await server.SendAsync("POST", "api.test", path, [("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes("oauth_access_token=" + (oauth ?? token)));
        return JsonNode.Parse(reply.Body)!;
    }

    [Fact]
    public async Task TestTheCallsAnswerFromTheAccountsStreak()
    {
        var calendar = await Post(Replay.DAILY_CALENDAR);
        Assert.Equal(1, (long)calendar["is_frozen"]!);
        Assert.Equal(7, calendar["calendar"]!.AsArray().Count);
        Eq("""{"success": true}""", await Post(Replay.DAILY_ACTIVATE));
        Assert.Equal(0, (long)(await Post(Replay.DAILY_CALENDAR))["is_frozen"]!);
    }
}
