using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The operator's report of objectives that players attempt and nobody completes: what is listed, what is not, and the conditions it names, on invented missions and events.</summary>
public class MissionStuckTests : MissionTestBase
{
    const string Carbine = "Harvest a Test Deer with the Test Carbine";
    double now = 1_800_000_000;

    State Stucked(string file, string catalogue)
    {
        var s = New(file, catalogue);
        s.Clock = () => now;
        return s;
    }

    List<string> Report(double days = 14, bool discord = false) => state.StuckMissions(days, discord: discord).ToList();

    /// <summary>`count` harvests of a deer with the .243 rifle: the right animal, not the weapon the mission asks for.</summary>
    void WrongWeapon(int count, long account = ME, int first = 1)
    {
        for (var i = first; i < first + count; i++)
            state.HeartbeatReply(account, Events(Shot(i, weapon: 60)));
    }

    string Me => state.Store.HandleOf(ME)!;

    [Fact]
    public void TestAnObjectiveWith5HarvestsOfTheRightAnimalWithAnotherWeaponIsListedWithTheWeaponsName()
    {
        state = Stucked("weapon.db", Catalogue([Carbine]));
        ActivateAll();
        WrongWeapon(4);
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 1 with candidates among 1 open on 1 accounts", Assert.Single(Report()));  // 4 are too few
        WrongWeapon(1, first: 5);
        var report = Report();
        Assert.Equal("missions stuck, the last 14 days: 1 objectives listed of 1 with candidates among 1 open on 1 accounts", report[0]);
        Assert.Equal($"mission 1 {Carbine} | {Carbine} | 5 candidates, 0 completed | open for 1 accounts: {Me} | Weapon 5/5 | Weapon: .243 Test Rifle (5)", Assert.Single(report[1..]));
    }

    [Fact]
    public void TestAnObjectiveAnAccountCompletedHereIsNotListedUnlessOneConditionRefusesNearlyEveryCandidate()
    {
        state = Stucked("completed.db", Catalogue([Carbine]));
        ActivateAll();
        WrongWeapon(5);
        state.CreateAccount(9, "other");
        state.MissionCall(9, "activate_all_available", []);
        state.HeartbeatReply(9, Events(Shot(1, weapon: 50)));  // the weapon the title asks for finishes it
        Assert.Equal("1", Completed(9));
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 1 with candidates among 1 open on 1 accounts", Assert.Single(Report()));
        WrongWeapon(5, first: 6);  // 10 candidates, the weapon refuses all of them
        Assert.EndsWith("| 10 candidates, 1 completed | open for 1 accounts: " + Me + " | Weapon 10/10 | Weapon: .243 Test Rifle (10)", Report()[1]);
        state.HeartbeatReply(ME, Events(Shot(20, weapon: 50)));  // one more that meets it: the mission is completed, nothing is open
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 0 with candidates among 0 open on 0 accounts", Assert.Single(Report()));
    }

    [Fact]
    public void TestAnObjectiveCompletedOnlyByARestoreIsStillListed()
    {
        state = Stucked("restored.db", Catalogue([Carbine]));
        ActivateAll();
        WrongWeapon(5);
        state.CreateAccount(8, "restored");
        using (var con = state.Store.Open(8))
        {
            con.Begin();
            PlayerRows.SetExtra(con, "Mission_missions.json", O("""{"active": [], "completed": [1], "states": {"1": {"state": 3}}}"""));
            con.Commit();
        }
        Assert.Equal("1", Completed(8));
        Assert.Contains("| 5 candidates, 0 completed |", Assert.Single(Report()[1..]));
    }

    [Fact]
    public void TestAnObjectiveTheServerDoesNotUnderstandIsNotListed()
    {
        state = Stucked("unknown.db", Catalogue(["Harvest a Test Deer 5 minutes after another."]));
        ActivateAll();
        WrongWeapon(6);
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 0 with candidates among 0 open on 0 accounts", Assert.Single(Report()));
    }

    [Fact]
    public void TestAnOrderedObjectiveCountsOnlyOnceTheOnesBeforeItAreFinished()
    {
        state = Stucked("ordered.db", """
            {"missions": [{"id": 1, "title": "Two", "dependencies": [], "rewards": [], "objectives": [
              {"id": 0, "title": "Harvest a Test Deer with the Test Carbine"}, {"id": 1, "title": "Then harvest a Test Deer with the Test Shotgun"}]}]}
            """);
        ActivateAll();
        WrongWeapon(5);
        var report = Report();
        Assert.Equal("missions stuck, the last 14 days: 1 objectives listed of 1 with candidates among 1 open on 1 accounts", report[0]);
        Assert.StartsWith("mission 1 Two | Harvest a Test Deer with the Test Carbine | 5 candidates", Assert.Single(report[1..]));
    }

    [Fact]
    public void TestATitleNamingSeveralSpeciesCountsOnlyTheEventsOfThoseSpeciesAsCandidates()
    {
        state = Stucked("species.db", Catalogue(["Harvest an Elk with the Test Carbine"]));
        ActivateAll();
        WrongWeapon(5);  // deer: no candidates
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 0 with candidates among 1 open on 1 accounts", Assert.Single(Report()));
        for (var i = 11; i <= 15; i++)
            state.HeartbeatReply(ME, Events(Shot(i, weapon: 60, species: 3)));  // a Plains Elk
        Assert.Contains("| 5 candidates, 0 completed | open for 1 accounts: " + Me + " | Weapon 5/5 |", Report()[1]);
    }

    [Fact]
    public void TestEqualObjectivesOfOneMissionAreListedOnce()
    {
        state = Stucked("equal.db", """
            {"missions": [{"id": 1, "title": "Twice", "dependencies": [], "rewards": [], "objectives": [
              {"id": 0, "title": "Harvest a Test Deer with the Test Carbine"}, {"id": 1, "title": "Harvest a Test Deer with the Test Carbine"}]}]}
            """);
        ActivateAll();
        WrongWeapon(5);
        Assert.Equal(2, Report().Count);
    }

    [Fact]
    public void TestAnOrderedObjectiveCountsOnlyTheEventsAfterTheOneBeforeItFinished()
    {
        state = Stucked("floor.db", """
            {"missions": [{"id": 1, "title": "Two", "dependencies": [], "rewards": [], "objectives": [
              {"id": 0, "title": "Harvest a Test Deer with the Test Carbine"}, {"id": 1, "title": "Then harvest a Test Deer with the Test Shotgun"}]}]}
            """);
        ActivateAll();
        for (var i = 1; i <= 5; i++)
            state.HeartbeatReply(ME, Events(Shot(i, weapon: 51)));  // the shotgun: the ordered objective is not open yet, nothing took these
        state.HeartbeatReply(ME, Events(Shot(6, weapon: 50)));  // the carbine finishes the first
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 0 with candidates among 1 open on 1 accounts", Assert.Single(Report()));
        for (var i = 11; i <= 15; i++)
            state.HeartbeatReply(ME, Events(Shot(i, weapon: 51, value: 100000)));  // the next one meets it and finishes it: the mission is completed
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnOrderedObjectiveAfterASpotIsNotListedBecauseTheReportCannotTellWhereToBegin()
    {
        state = Stucked("afterspot.db", """
            {"missions": [{"id": 1, "title": "Two", "dependencies": [], "rewards": [], "objectives": [
              {"id": 0, "title": "Spot a Test Deer from a Tree Stand"}, {"id": 1, "title": "Then harvest a Test Deer with the Test Carbine"}]}]}
            """);
        ActivateAll();
        WrongWeapon(5);  // before the spot: the ordered objective is not open
        state.HeartbeatReply(ME, Events(Spot(20, location: 2)));  // finishes the first
        WrongWeapon(5, first: 21);
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 0 with candidates among 0 open on 0 accounts", Assert.Single(Report()));
    }

    [Fact]
    public void TestASingleHuntMissionCountsAnEarlierHuntsEventOnlyWhenAConditionRefusedIt()
    {
        state = Stucked("single.db", """
            {"missions": [{"id": 1, "title": "Two", "dependencies": [], "singleExpedition": true, "rewards": [], "objectives": [
              {"id": 0, "title": "Harvest a Test Deer with the Test Carbine"}, {"id": 1, "title": "Harvest a Test Deer with the Test Shotgun"}]}]}
            """);
        ActivateAll();
        for (var i = 1; i <= 5; i++)
        {
            Launch();
            state.HeartbeatReply(ME, Events(Shot(i, weapon: 51)));  // the shotgun finishes the second objective, which the next hunt forgets
        }
        Launch();
        var report = Report();
        Assert.Equal("missions stuck, the last 14 days: 1 objectives listed of 1 with candidates among 2 open on 1 accounts", report[0]);
        Assert.Contains("| Harvest a Test Deer with the Test Carbine | 5 candidates, 0 completed | open for 1 accounts: " + Me + " | Weapon 5/5 |", Assert.Single(report[1..]));
    }

    [Fact]
    public void TestASpotAndAClueAreListedWithTheConditionThatRefusedThem()
    {
        state = Stucked("kinds.db", Catalogue(["Spot a Test Deer from a Tree Stand", "ID droppings from a Test Deer"]));
        ActivateAll();
        for (var i = 1; i <= 5; i++)
            state.HeartbeatReply(ME, Events(Spot(i, location: 3)));  // from a ground blind
        for (var i = 11; i <= 15; i++)
            state.HeartbeatReply(ME, Events(Clue(i, 2)));  // footprints
        var report = Report();
        Assert.Equal("missions stuck, the last 14 days: 2 objectives listed of 2 with candidates among 2 open on 1 accounts", report[0]);
        Assert.Contains("| 5 candidates, 0 completed | open for 1 accounts: " + Me + " | Stand 5/5 | Stand: 3 (5)", report.Single(l => l.StartsWith("mission 1 ")));
        Assert.Contains("| Clue 5/5 | Clue: Footprints (5)", report.Single(l => l.StartsWith("mission 2 ")));
    }

    [Fact]
    public void TestACountOfCluesNamesHowManyTheRefusedOnesHad()
    {
        state = Stucked("count.db", Catalogue(["Locate 3 Test Deer tracks from the same animal"]));
        ActivateAll();
        for (var i = 1; i <= 5; i++)
            state.HeartbeatReply(ME, Events(Clue(i, 2)));  // one clue each of five animals
        Assert.EndsWith("| Clue 5/5 | Clue: 1 of 3 clues (5)", Report()[1]);
    }

    [Fact]
    public void TestOnlyTheEventsOfTheLastDaysCountAndTheReportWritesNothing()
    {
        state = Stucked("window.db", Catalogue([Carbine]));
        ActivateAll();
        WrongWeapon(5);
        long Rows(Db con) => (long)con.Scalar("SELECT (SELECT COUNT(*) FROM missions WHERE account_id = $a) + (SELECT COUNT(*) FROM pending WHERE account_id = $a) + (SELECT COUNT(*) FROM events WHERE account_id = $a)")!;
        using (var writer = state.Store.Open(ME))  // a write in progress: the report takes no lock of its own
        {
            writer.Begin();
            var before = Rows(writer);
            Assert.Equal(2, Report().Count);
            Assert.Equal(before, Rows(writer));
        }
        now += 15 * 86400;
        Assert.Equal("missions stuck, the last 14 days: 0 objectives listed of 0 with candidates among 0 open on 0 accounts", Assert.Single(Report()));
    }

    [Fact]
    public void TestTheDiscordReportIsMarkdownInMessagesOf2000CharactersAtMost()
    {
        var missions = string.Join(",", Enumerable.Range(1, 40).Select(i =>
            $$"""{"id": {{i}}, "title": "Mission *{{i}}*", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "{{Carbine}}"}]}"""));
        state = Stucked("discord.db", "{\"missions\": [" + missions + "]}");
        ActivateAll();
        WrongWeapon(5);
        var messages = string.Join("\n", Report(discord: true)).Split("\n---8<---\n");
        Assert.True(messages.Length > 1);
        Assert.All(messages, m => Assert.InRange(m.Length, 1, 2000));
        Assert.StartsWith("**missions stuck, the last 14 days: 40 objectives listed of 40 with candidates among 40 open on 1 accounts**\n- mission 1 Mission \\*1\\* | Harvest a Test Deer with the Test Carbine |", messages[0]);
    }
}
