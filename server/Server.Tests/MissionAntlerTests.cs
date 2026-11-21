using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The typical points of the antlers a title asks (`at least 8 typical points`, `more than`, `less than`, `12-point`): both antlers of the trophy event added; a female, a kill
/// with no trophy event and a non-typical rack never meet them; the rollback reopens what a female's kill finished. Invented events.</summary>
public class MissionAntlerTests : MissionTestBase
{
    /// <summary>The kill with a trophy event (22) between its spawn and its harvest: `left` and `right` typical points (a main beam and its tines) and an abnormal point on the left.</summary>
    static JsonNode[] Rack(JsonNode[] kill, int individual, int left, int right)
    {
        static string Side(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"length\": 500, \"type\": {i}}}")) + "]";
        return [kill[0], O($$$"""{"event": 22, "individual": {{{individual}}}, "left_antler": {"points": {{{Side(left)}}}, "abnormals": [{"length": 40}]}, "right_antler": {"points": {{{Side(right)}}}, "abnormals": []}}"""), .. kill[1..]];
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
        Assert.False(state.MissionCoverage().Any(l => l.StartsWith("mission 1", StringComparison.Ordinal)));  // the title is understood
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
}
