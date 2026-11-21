using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The typical points of the antlers a title asks (`at least 8 typical points`, `more than`, `less than`, `12-point`): both antlers of the trophy event added; a female, a kill
/// with no trophy event, a trophy with a side missing and a non-typical rack never meet them; the rollback reopens what a female's kill finished. Invented events.</summary>
public class MissionAntlerTests : MissionTestBase
{
    /// <summary>The kill with a trophy event (22) between its spawn and its harvest: `left` and `right` typical points (a main beam and its tines; no right antler at all for null) and an
    /// abnormal point on the left.</summary>
    static JsonNode[] Rack(JsonNode[] kill, int individual, int left, int? right)
    {
        static string Side(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"length\": 500, \"type\": {i}}}")) + "]";
        var rack = $"{{\"event\": 22, \"individual\": {individual}, \"left_antler\": {{\"points\": {Side(left)}, \"abnormals\": [{{\"length\": 40}}]}}"
            + (right is { } r ? $", \"right_antler\": {{\"points\": {Side(r)}, \"abnormals\": []}}" : "") + "}";
        return [kill[0], O(rack), .. kill[1..]];
    }

    const string Buck8 = "Harvest a Test Deer buck with at least 8 typical points.";

    [Fact]
    public void TestThePointsOfBothAntlersAreAddedAndAFemaleAKillWithoutATrophyAndANonTypicalRackNeverMeetThem()
    {
        state = New("antlers.db", Catalogue([Buck8]));
        ActivateAll();
        Hb(Rack(Shot(1), 1, 4, 3));  // 7 points
        Hb(Rack(Shot(2, gender: 1), 2, 5, 5));  // a doe with a rack of 10
        Hb(Shot(3));  // a buck with no trophy event
        Hb(Rack(Shot(4, species: 101), 4, 5, 5));  // a non-typical rack
        Assert.Equal("", Completed());
        Hb(Rack(Shot(5), 5, 5, 3));  // 8 points, the abnormal point is not counted
        Assert.Equal("1", Completed());
        Assert.DoesNotContain(state.MissionCoverage(), l => l.StartsWith("mission 1", StringComparison.Ordinal));  // the title is understood
    }

    [Fact]
    public void TestMoreThanLessThanAndTheNPointWordingsAreChecked()
    {
        state = New("wordings.db", Catalogue(["Harvest a Test Deer buck with more than 8 typical points.", "Harvest a Test Deer buck with less than 8 typical points.",
            "Harvest a Test Deer buck with at least ten typical points.", "Harvest a male 12-point Test Deer."]));
        ActivateAll();
        Hb(Rack(Shot(1), 1, 4, 4));
        Assert.Equal("", Completed());  // exactly 8 is neither more nor less
        Hb(Rack(Shot(2), 2, 4, 3));
        Assert.Equal("2", Completed());
        Hb(Rack(Shot(3), 3, 5, 5));
        Assert.Equal("1,2,3", Completed());
        Hb(Rack(Shot(4), 4, 6, 6));
        Assert.Equal("1,2,3,4", Completed());
    }

    [Fact]
    public void TestATrophyWithASideMissingHasNoTotalToCompare()
    {
        state = New("one-side.db", Catalogue(["Harvest a Test Deer buck with less than 8 typical points."]));
        ActivateAll();
        Hb(Rack(Shot(1), 1, 3, null));  // the right antler is not in the trophy event: the total is not known
        Assert.Equal("", Completed());
        Hb(Rack(Shot(2), 2, 3, 0));  // an empty right list counts for nothing
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestATrophySentAfterTheHarvestFinishesTheObjectiveAtTheNextStart()
    {
        state = New("late.db", Catalogue([Buck8]));
        ActivateAll();
        var kill = Rack(Shot(1), 1, 5, 5);
        Hb([kill[0], .. kill[2..]]);
        Assert.Equal("", Completed());
        Hb(kill[1]);  // the trophy event, after the harvest
        Assert.Equal("", Completed());
        var later = New("late.db", Catalogue([Buck8]));
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal("1", Completed(of: later));
    }

    [Fact]
    public void TestTheRollbackReopensWhatAFemalesKillFinishedAndPaysAgainAtTheNextBuck()
    {
        state = New("antlers-rollback.db", Catalogue(["Harvest a Test Deer"]));  // the earlier rules read none of the title
        ActivateAll();
        Hb(Rack(Shot(1, gender: 1), 1, 5, 3));
        Assert.Equal("1", Completed());
        var wallet = Gm();
        var strict = New("antlers-rollback.db", Catalogue([Buck8]));
        var dry = strict.RollbackMissions(apply: false).ToList();
        Assert.Contains("mission 1 (completed, active again) objectives 0", dry[0]);
        Assert.Equal("1", Completed(of: strict));  // a dry run writes nothing
        Assert.Equal("reopened 1 objectives in 1 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());
        Assert.Equal(("", wallet), (Completed(of: strict), Gm(strict)));  // reopened, nothing taken back
        state = strict;
        Hb(Rack(Shot(2), 2, 5, 5));
        Assert.Equal("1", Completed(of: strict));
        Assert.True(Gm(strict) >= wallet + 10);  // paid again at the buck (its own gm$ and the mission's)
        Assert.Equal("reopened 0 objectives in 0 missions; kept: not understood 0 finished objectives", strict.RollbackMissions(apply: true).Last());  // the buck's completion stands
    }

    [Fact]
    public void TestAnIndividualOfAnEarlierHuntHasItsOwnRackWhenTheNextHuntReusesItsNumber()
    {
        // two missions the earlier rules finish with the first kill; the same individual and the same harvest are seen in the next hunt with a bigger rack
        state = New("reuse.db", Catalogue(["Harvest a Test Deer", "Harvest a Test Deer"]));
        ActivateAll();
        Launch();
        Hb(Rack(Shot(1), 1, 3, 3));  // 6 points
        Launch();
        Hb(Rack(Shot(1), 1, 5, 5));  // 10 points
        Assert.Equal("1,2", Completed());
        var strict = New("reuse.db", Catalogue([Buck8, "Harvest a Test Deer buck with less than 8 typical points."]));
        // the first mission is met by the second kill, the second by the first: judged by its own rack each, none is reopened
        Assert.Equal("would reopen 0 objectives in 0 missions; kept: not understood 0 finished objectives (dry run: nothing written, --apply writes)", strict.RollbackMissions(apply: false).Last());
    }

    [Fact]
    public void TestTheGenderOfAnIndividualOfAnEarlierHuntIsNotTheNextHuntsWhenTheHarvestsAreAlike()
    {
        state = New("reuse-gender.db", Catalogue(["Harvest a Test Deer"]));
        ActivateAll();
        Launch();
        Hb(Rack(Shot(1), 1, 5, 5));  // a buck
        Launch();
        Hb(Rack(Shot(1, gender: 1), 1, 5, 5));  // a doe of the same number, the same harvest event
        Assert.Equal("1", Completed());
        var strict = New("reuse-gender.db", Catalogue([Buck8]));
        Assert.Equal("would reopen 0 objectives in 0 missions; kept: not understood 0 finished objectives (dry run: nothing written, --apply writes)", strict.RollbackMissions(apply: false).Last());
    }

    [Fact]
    public void TestATrophyOfTheNextHuntIsNotTheHarvestOfAnEarlierOnes()
    {
        state = New("cross-hunt.db", Catalogue([Buck8]));
        ActivateAll();
        Launch();
        Hb(Shot(1));  // a buck with no trophy event
        Launch();
        Hb(Rack(Shot(2), 1, 5, 5)[1]);  // the next hunt: a trophy event of the same number, no spawn of its own
        var later = New("cross-hunt.db", Catalogue([Buck8]));
        Assert.Equal(0, later.ConfirmMissions());
        Assert.Equal("", Completed(of: later));
    }
}
