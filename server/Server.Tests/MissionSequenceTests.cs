using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Objectives that need several events of one hunt: so many harvests within a time (of the first, of each), the n-th harvest of a hunt, two animals killed by one shot, and clues
/// of so many different animals within a time. Times are when the server received the events; invented catalogue and events (species 1 a deer, 5 a fox).</summary>
public class MissionSequenceTests : MissionTestBase
{
    double now = 1_800_000_000;

    State Timed(string file, string catalogue)
    {
        var s = New(file, catalogue);
        s.Clock = () => now;
        return s;
    }

    /// <summary>A fox harvested `seconds` after the start.</summary>
    void FoxAt(int individual, double seconds)
    {
        now = 1_800_000_000 + seconds;
        Hb(Shot(individual, species: 5));
    }

    /// <summary>A deer killed by the shot `shot`: its spawn, the hit and the harvest.</summary>
    static JsonNode[] DeerShot(int individual, int shot) =>
    [
        O($$"""{"event": 7, "individual": {{individual}}, "species": 1, "gender": 0, "weight_grams": 90000, "texture": 0, "special": 0}"""),
        O($$"""
            {"event": 1, "individual": {{individual}}, "shot_id": {{shot}}, "weapon_id": 50, "ammunition_id": 52, "distance_cms": 15000, "organ1": 33, "organ2": 6, "animal_state": 3,
             "location": 0, "scope_id": -1, "pose": 2048, "species": 2}
            """),
        O($$"""{"event": 3, "individual": {{individual}}, "extended_species": 1, "harvest_value": 100000, "trophy_integrity": 100000}"""),
    ];

    const string TwoFoxes = "Harvest two Test Foxes with 5 minutes of the first harvest.";

    [Fact]
    public void TestTwoHarvestsWithinTheTimeMeetItTwoFurtherApartDoNotAndExactlyTheTimeWithItsGraceDoes()
    {
        state = Timed("within.db", Catalogue([TwoFoxes]));
        ActivateAll();
        FoxAt(1, 0);
        FoxAt(2, 420);  // 7 minutes later
        Assert.Equal("", Completed());
        Hb(Shot(3));  // a deer: not one of them
        FoxAt(4, 420 + 180);  // 3 minutes after the second fox
        Assert.Equal("1", Completed());

        state = Timed("bound.db", Catalogue([TwoFoxes]));
        ActivateAll();
        FoxAt(1, 0);
        FoxAt(2, 311);  // 5 minutes and 11 seconds: past the 10 seconds of grace
        Assert.Equal("", Completed());
        FoxAt(3, 311 + 310);  // exactly 5 minutes and the grace after the second
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnotherHuntsHarvestNeverCounts()
    {
        state = Timed("hunts.db", Catalogue([TwoFoxes]));
        Launch();
        ActivateAll();
        FoxAt(1, 0);
        Launch();
        FoxAt(2, 60);
        Assert.Equal("", Completed());
        FoxAt(3, 120);
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestOfEachHarvestChainsTheGapsWhileWithinTheTimeSpansThemAll()
    {
        state = Timed("each.db", Catalogue(["Harvest three Test Foxes within 5 minutes of each harvest.", "Harvest 3 Test Foxes within 5 minutes.", "Harvest a Test Fox 5 minutes after another."]));
        ActivateAll();
        FoxAt(1, 0);
        FoxAt(2, 280);
        Assert.Equal("3", Completed());  // two within 5 minutes
        FoxAt(3, 560);  // each 280 seconds after the one before, 560 after the first
        Assert.Equal("1,3", Completed());
    }

    [Fact]
    public void TestTheNthHarvestIsCountedPerHuntWhenTheTitleOrTheMissionKeepsToOneHunt()
    {
        state = Timed("nth.db", Catalogue(["At any point during the same hunt, harvest a thirdn Test Fox", "Harvest a fifth Test Fox"], ", \"singleExpedition\": true"));
        Launch();
        ActivateAll();
        FoxAt(1, 0);
        FoxAt(2, 10);
        Launch();
        FoxAt(3, 3600);
        FoxAt(4, 3610);  // the second of this hunt
        Assert.Equal("", Completed());
        FoxAt(5, 3620);
        Assert.Equal("1", Completed());  // the third of the hunt
        Assert.Contains("mission 2 objective 0: Harvest a fifth Test Fox => harvest; species: Test Fox; in the hunt: 5", state.RuleLines());
    }

    [Fact]
    public void TestAnOrdinalOfAMissionNotKeptToOneHuntCountsAcrossHuntsAsEachHarvestFinishesOneObjective()
    {
        state = Timed("across.db", """
            {"missions": [{"id": 1, "title": "Pair", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "Harvest a Test Fox"}, {"id": 1, "title": "Harvest a second Test Fox"}]}]}
            """);
        Launch();
        ActivateAll();
        FoxAt(1, 0);
        Launch();
        FoxAt(2, 7200);
        Assert.Equal("1", Completed());
        Assert.Contains("mission 1 objective 1: Harvest a second Test Fox => harvest; species: Test Fox", state.RuleLines());
    }

    [Fact]
    public void TestTwoAnimalsOfOneShotMeetItTwoShotsDoNot()
    {
        state = Timed("shot.db", Catalogue(["Kill two Test Deer at the same time with a single shot."]));
        ActivateAll();
        Hb([.. DeerShot(1, 7), .. DeerShot(2, 8)]);
        Assert.Equal("", Completed());
        Hb([.. DeerShot(3, 9), .. DeerShot(4, 9)]);
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestCluesOfDifferentAnimalsWithinTheTimeMeetItCluesOfOneAnimalDoNot()
    {
        state = Timed("different.db", Catalogue(["Find the tracks of three different Test Deer within 5 minutes.", "ID footprints of a second different Test Fox in the same hunt."]));
        ActivateAll();
        for (var i = 0; i < 3; i++)
            Hb(Clue(1, 2));  // three footprints of one deer
        Hb(Clue(2, 9));  // a call of another: no footprints
        Assert.Equal("", Completed());
        now += 400;
        Hb(Clue(2, 2));
        now += 300;
        Hb(Clue(3, 2));  // three animals, but the first 700 seconds before
        Assert.Equal("", Completed());
        now += 10;
        Hb(Clue(4, 2));  // the second (310 seconds before: exactly 5 minutes and the grace), the third and this one
        Assert.Equal("1", Completed());
        Hb(Clue(5, 2, species: 5));
        Hb(Clue(5, 2, species: 5));
        Assert.Equal("1", Completed());
        Hb(Clue(6, 2, species: 5));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestTheStartFinishesWithHarvestsAlreadyMadeAndTheRollbackReopensWhatTheyDoNotMeet()
    {
        state = Timed("confirm.db", Catalogue(["Harvest two Test Foxes within 5 minutes while whistling."]));  // not read: never met
        ActivateAll();
        FoxAt(1, 0);
        FoxAt(2, 60);
        Assert.Equal("", Completed());
        var later = New("confirm.db", Catalogue(["Harvest two Test Foxes within 5 minutes."]));
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal("1", Completed(of: later));

        state = Timed("rollback.db", Catalogue(["Harvest a Test Fox."]));  // the earlier, laxer reading
        ActivateAll();
        FoxAt(1, 0);
        Assert.Equal("1", Completed());
        var strict = New("rollback.db", Catalogue(["Harvest two Test Foxes within 5 minutes."]));
        strict.Clock = () => now;
        Assert.Equal("reopened 1 objectives in 1 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
        var stuck = strict.StuckMissions(14, minCandidates: 1).ToList();
        Assert.Contains("| Within 1/1 | Within: 1 of 2 (1)", stuck[1]);
        state = strict;
        FoxAt(2, 100);
        Assert.Equal("1", Completed(of: strict));
        Assert.Equal("reopened 0 objectives in 0 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
    }
}
