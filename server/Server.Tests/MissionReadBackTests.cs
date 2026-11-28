using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The operator's file reads back every line `coverage --rules` prints (order words, places), can undo an order the title implies, and refuses bad input at the start with the entry's key.</summary>
public class MissionReadBackTests : MissionTestBase
{
    // mission 1: a start, a harvest of the same hunt (waits for the start), an arrive objective after a visit, and an ordered harvest
    const string Ordered = "{\"missions\": [{\"id\": 1, \"title\": \"Ordered\", \"dependencies\": [], \"rewards\": [], \"reserves\": [2], \"objectives\": ["
        + "{\"id\": 0, \"title\": \"Start at Test Lodge\"}, {\"id\": 1, \"title\": \"At any point during the same hunt, harvest a Test Deer\"}, "
        + "{\"id\": 2, \"title\": \"Go to Tower 2\"}, {\"id\": 3, \"title\": \"After visiting Tower 2, harvest a Test Fox\"}, {\"id\": 4, \"title\": \"Then harvest a Test Fox\"}]}]}";

    static string Words(IEnumerable<string> lines, int objective) => lines.Single(l => l.StartsWith($"mission 1 objective {objective}")).Split(" => ")[1];

    static List<string> RuleLines(State s) => [.. s.RuleLines()];

    static JsonObject Entries(IEnumerable<string> lines) =>
        Rules([.. lines.Where(l => !Words(lines, int.Parse(l.Split(' ')[3].TrimEnd(':'))).StartsWith("start", StringComparison.Ordinal))
            .Select(l => ($"1/{l.Split(' ')[3].TrimEnd(':')}", l[(l.IndexOf(" => ", StringComparison.Ordinal) + 4)..]))]);

    [Fact]
    public void TestAfterObjectiveAndPlacesPrintedByCoverageReadBackAsTheSameLines()
    {
        var read = RuleLines(New("rb-read.db", Ordered));
        Assert.Contains(read, l => l.Contains("after objective 0", StringComparison.Ordinal));  // the same-hunt harvest waits for the start
        Assert.Contains(read, l => l.Contains("places: ", StringComparison.Ordinal));  // the arrive objective names its place's define
        var back = RuleLines(New("rb-back.db", Ordered, rules: Entries(read)));
        Assert.Equal(read.Select(l => l.StartsWith("mission 1 objective 0:", StringComparison.Ordinal) ? l : l.Replace($" objective {l.Split(' ')[3].TrimEnd(':')}:", $" objective {l.Split(' ')[3].TrimEnd(':')} (override):", StringComparison.Ordinal)), back);
    }

    [Fact]
    public void TestTheFileUndoesTheOrderATitleImpliesWithUnordered()
    {
        var plain = RuleLines(New("rb-order.db", Ordered));
        Assert.Contains("after objective 2", Words(plain, 3));
        var state2 = New("rb-unordered.db", Ordered, rules: Rules(("1/3", "harvest; species: Test Fox; unordered")));
        var line = RuleLines(state2).Single(l => l.StartsWith("mission 1 objective 3", StringComparison.Ordinal));
        Assert.EndsWith("=> harvest; species: Test Fox; unordered", line);
        Assert.DoesNotContain("after", line, StringComparison.Ordinal);
        state = state2;
        ActivateAll();
        Hb(Shot(1, species: 5));  // a fox: objective 3 is no longer waiting for the visit to the tower (objective 4's `Then` order waits for objective 3)
        using var con = state.Store.Open(ME);
        Assert.Contains("3", string.Join(",", JsonNode.Parse((string)con.Scalar("SELECT objectives FROM missions WHERE account_id = $a AND mission_id = 1")!)!.AsArray()));
        Assert.Equal(line, RuleLines(New("rb-unordered-back.db", Ordered, rules: Rules(("1/3", line.Split(" => ")[1])))).Single(l => l.StartsWith("mission 1 objective 3", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("harvest; species: Test Deer; ordered; unordered", "one of")]
    [InlineData("harvest; species: Test Deer; after objective 0; ordered", "one of")]
    [InlineData("harvest; species: Test Deer; after objective 99", "after objective 99")]
    [InlineData("harvest; species: Test Deer; after objective 1", "after objective 1")]  // not before objective 0's own place: objective 1 is itself
    [InlineData("harvest; species: Test Deer; together; together", "given twice")]
    [InlineData("harvest; species: Test Deer; named animal near (10, 20) m, 0 animals", "a herd size")]
    [InlineData("harvest; species: Test Deer; named animal near (10, 20) m, 21 animals", "a herd size")]
    [InlineData("harvest; species: Test Deer; named animal near (10, 20) m, 99999999999999999999 animals", "a herd size")]
    [InlineData("track; species: Test Deer; clues: footprints x1; footprints of each of Test Deer, Test Fox within 0 m of (10, 20)", "a radius")]
    [InlineData("track; species: Test Deer; clues: footprints x1; footprints of each of Test Deer, Test Fox within 99999999999999999999 m of (10, 20)", "a radius")]
    [InlineData("harvest; species: Test Deer; footprints of each of Test Deer, Test Fox within 300 m of (10, 20)", "cannot check")]
    [InlineData("arrive; places: TEST_A; place: TEST_B", "an arrive rule is")]
    [InlineData("arrive", "an arrive rule is")]
    public void TestBadInputStopsTheStartWithTheEntrysKey(string rule, string fragment)
    {
        var catalogue = "{\"missions\": [{\"id\": 1, \"title\": \"m\", \"dependencies\": [], \"rewards\": [], \"objectives\": [{\"id\": 0, \"title\": \"Harvest a Test Deer\"}, {\"id\": 1, \"title\": \"Harvest a Test Deer\"}]}]}";
        var e = Assert.Throws<InvalidDataException>(() => New("rb-bad.db", catalogue, rules: Rules(("1/1", rule))));
        Assert.Contains("\"1/1\"", e.Message);
        Assert.Contains(fragment, e.Message);
    }
}

/// <summary>Readings that were too wide: a count with an organ shot is not one shot for all, and `another` spotted animal is counted in the hunt when the title says one hunt.</summary>
public class MissionNarrowReadingTests : MissionTestBase
{
    static List<string> Lines(State s) => [.. s.RuleLines()];

    [Fact]
    public void TestTwoAnimalsEachWithOneHeartShotAreNotOneShotKillingBoth()
    {
        var lines = Lines(New("nr-heart.db", Catalogue(["Harvest two Test Deer each with one heart shot", "Harvest two Test Deer with a single shot", "Harvest a Test Deer with one heart shot"])));
        Assert.Contains("not understood", lines[0], StringComparison.Ordinal);  // a count with no time and no shot is not read
        Assert.DoesNotContain("killed by one shot", lines[0], StringComparison.Ordinal);
        Assert.Contains("in the hunt: 2 killed by one shot", lines[1], StringComparison.Ordinal);
        Assert.Contains("one shot", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("not understood", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TestAnotherSpottedAnimalIsCountedInTheHuntWhenTheTitleSaysOneHunt()
    {
        string Mission(int id, string second) => $"{{\"id\": {id}, \"title\": \"m\", \"dependencies\": [], \"rewards\": [], \"objectives\": [{{\"id\": 0, \"title\": \"Spot a Test Deer\"}}, {{\"id\": 1, \"title\": \"{second}\"}}]}}";
        var lines = Lines(New("nr-spot.db", "{\"missions\": [" + Mission(1, "Spot another Test Deer") + "," + Mission(2, "Spot another Test Deer in the same hunt") + "]}"));
        Assert.Contains("in the mission: 2 different animals", lines.Single(l => l.StartsWith("mission 1 objective 1", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("in the hunt: 2 different animals", lines.Single(l => l.StartsWith("mission 2 objective 1", StringComparison.Ordinal)), StringComparison.Ordinal);
    }
}
