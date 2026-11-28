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

    [Fact]
    public void TestAnEntryKeepsTheTitlesOrderUnlessItSaysOneAndOrderedIsNotReadAsAfterObjective()
    {
        // objective 3 is `After visiting Tower 2` (waits for objective 2), objective 4 is `Then harvest` (waits for every one before it)
        var s = New("rb-keep.db", Ordered, rules: Rules(("1/3", "harvest; species: Test Fox; ordered"), ("1/4", "harvest; species: Test Fox")));
        var lines = RuleLines(s);
        Assert.Equal("harvest; species: Test Fox; ordered", Words(lines, 3));  // every objective before it, not the visit alone
        Assert.Equal("harvest; species: Test Fox; ordered", Words(lines, 4));  // no order word: the title's `Then` stays
        Assert.Equal(s.RuleShapes().Where(l => l.StartsWith("mission 1 objective 3", StringComparison.Ordinal) || l.StartsWith("mission 1 objective 4", StringComparison.Ordinal)).Select(l => l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..]).Distinct().Count(), 1);
    }

    [Theory]
    [InlineData("first state: spooked")]  // an earlier operator file's word: every state word is read as the animal's state at the first hit
    [InlineData("first state: unspooked")]
    [InlineData("state: spooked")]
    public void TestTheEarlierFirstStateWordsStillLoadAndReadBackAsThemselves(string condition)
    {
        var rule = $"harvest; species: Test Deer; {condition}";
        var s = New("rb-first.db", Catalogue(["Harvest a Test Deer"]), rules: Rules(("1/0", rule)));
        Assert.EndsWith($"=> {rule}", RuleLines(s).Single());
        Assert.Equal(s.RuleShapes(), New("rb-first-back.db", Catalogue(["Harvest a Test Deer"]), rules: Rules(("1/0", rule))).RuleShapes());
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
    [InlineData("arrive; places: TEST_A; place: TEST_B", "an arrive rule has")]
    [InlineData("arrive; ordered; after objective 0", "one of")]
    [InlineData("arrive", "a bare \"arrive\" is for an objective whose title is an arrival")]
    [InlineData("arrive; ordered", "a bare \"arrive\"")]
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

/// <summary>The contract of docs/server.md: a line `coverage --rules` prints, copied into `mission_rules.json`, is the same rule. Compared on the rules themselves (<see cref="State.RuleShapes"/>: kind, species,
/// gender, order, zone, words, every condition with its ids and numbers), not on the printed words, for every objective of the catalogue the reader understands: the invented sample's, with and without its
/// operator's rules, and, privately, the real catalogue's (MISSION_CATALOGUE and MISSION_RULES, as for the real cases).</summary>
public class MissionReadBackShapeTests(MissionCaseSources sources, ITestOutputHelper output) : MissionTestBase, IClassFixture<MissionCaseSources>
{
    static readonly System.Text.RegularExpressions.Regex Line = new(@"\Amission (?<m>\d+) objective (?<o>\d+)(?: \(override\))?: .* => (?<rule>(?:harvest|spot|track|arrive)(?:;.*)?)\z");

    [Theory]
    [InlineData("sample", true)]
    [InlineData("sample", false)]
    [InlineData("real", true)]
    [InlineData("real", false)]
    public void TestEveryPrintedRuleReadsBackAsTheSameRule(string source, bool withTheOperatorsRules)
    {
        if (source == "real" && MissionCaseSources.RealMissing is { } why)
            Assert.Skip(why);
        var c = sources[source];
        var first = withTheOperatorsRules ? c.Engine : c.Rebuild("plain", null);
        var lines = first.RuleLines().ToList();
        var read = lines.Select(l => Line.Match(l)).Where(m => m.Success && !m.Value.Contains("not understood", StringComparison.Ordinal)).ToList();
        var back = c.Rebuild($"back-{withTheOperatorsRules}", Rules([.. read.Select(m => ($"{m.Groups["m"].Value}/{m.Groups["o"].Value}", m.Groups["rule"].Value))]));
        var (was, now) = (first.RuleShapes().ToDictionary(s => s[..s.IndexOf(": ", StringComparison.Ordinal)]), back.RuleShapes().ToDictionary(s => s[..s.IndexOf(": ", StringComparison.Ordinal)]));
        var different = read.Select(m => $"mission {m.Groups["m"].Value} objective {m.Groups["o"].Value}").Where(k => was[k] != now[k])
            .Select(k => $"{k}\n   printed: {read.First(m => $"mission {m.Groups["m"].Value} objective {m.Groups["o"].Value}" == k).Groups["rule"].Value}\n   was:     {was[k][(k.Length + 2)..]}\n   read:    {now[k][(k.Length + 2)..]}").ToList();
        output.WriteLine($"{source}, operator's rules {withTheOperatorsRules}: {read.Count} printed rules read back, {lines.Count - read.Count} not understood or not a rule (not loadable), {different.Count} different");
        Assert.True(different.Count == 0, string.Join('\n', different.Take(60)));
    }
}

/// <summary>A weapon of two classes (found by the cartridges its name joins and the ammunition they name) counts for a class only with that class's ammunition; the condition is printed and read back.</summary>
public class MissionBarrelTests : MissionTestBase
{
    static readonly string[] GunItems =
    [
        """{"id": 950, "name": "10GA/8x50R Test Combination Gun", "cat": 1, "subcat": {"name": "Test Advanced"}}""",
        """{"id": 951, "name": "10 GA Test Drill Shell", "cat": 3, "subcat": {"name": "Test Shotgun Ammo"}}""",
        """{"id": 952, "name": "8x50R Test Drill Round", "cat": 3, "subcat": {"name": "Test Rifle Ammo"}}""",
    ];

    const string Gun = "10GA/8x50R Test Combination Gun";

    [Theory]
    [InlineData("Harvest a Test Deer with any shotgun")]
    [InlineData("Harvest a Test Deer with the Test Carbine or any shotgun")]  // naming a weapon does not drop the barrel rule of the class
    public void TestTheBarrelRuleIsPrintedAndReadsBackAsTheSameRule(string title)
    {
        var s = New("barrel.db", Catalogue([title]), items: GunItems);
        var line = s.RuleLines().Single();
        Assert.Contains($"weapon barrels: {Gun} = 10 GA Test Drill Shell", line, StringComparison.Ordinal);
        var back = New("barrel-back.db", Catalogue([title]), items: GunItems, rules: Rules(("1/0", line[(line.IndexOf(" => ", StringComparison.Ordinal) + 4)..])));
        Assert.Equal(s.RuleShapes(), back.RuleShapes());
    }

    [Fact]
    public void TestTheCombinationGunCountsForAShotgunWithItsShellOnly()
    {
        state = New("barrel-kill.db", Catalogue(["Harvest a Test Deer with any shotgun", "Harvest a Test Deer with any rifle"]), items: GunItems);
        ActivateAll();
        Hb(Shot(1, weapon: 950, ammunition: 952));  // the rifle round: a rifle's kill
        Assert.Equal("2", Completed());
        Hb(Shot(2, weapon: 950, ammunition: 951));  // the shell: a shotgun's
        Assert.Equal("1,2", Completed());
    }

    [Theory]
    [InlineData("harvest; weapon: 10GA/8x50R Test Combination Gun; weapon barrels: 10GA/8x50R Test Combination Gun = 10 GA Test Drill Shell; weapon barrels: 10GA/8x50R Test Combination Gun = 8x50R Test Drill Round", "given twice")]
    [InlineData("harvest; weapon: Test Carbine; weapon barrels: 10GA/8x50R Test Combination Gun = 10 GA Test Drill Shell", "not one of the \"weapon\" condition's")]
    [InlineData("harvest; weapon: Test Carbine; weapon barrels: Test Blaster = 10 GA Test Drill Shell", "no weapon is named")]
    [InlineData("harvest; weapon barrels: 10GA/8x50R Test Combination Gun = 10 GA Test Drill Shell", "needs a \"weapon\" condition")]
    public void TestABadBarrelStopsTheStartWithTheEntrysKey(string rule, string fragment)
    {
        var e = Assert.Throws<InvalidDataException>(() => New("barrel-bad.db", Catalogue(["Harvest a Test Deer"]), items: GunItems, rules: Rules(("1/0", rule))));
        Assert.Contains("\"1/0\"", e.Message);
        Assert.Contains(fragment, e.Message);
    }
}
