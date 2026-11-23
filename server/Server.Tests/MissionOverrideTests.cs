using System.Text.Json.Nodes;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>The operator's `mission_rules.json`: an entry replaces the rule of one objective, in the words `coverage --rules` prints; a bad entry stops the start. Invented catalogue and events.</summary>
public class MissionOverrideTests : MissionTestBase
{
    static JsonObject Rules(params (string Key, string Rule)[] entries) => new(entries.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Rule)));

    static List<string> RuleLines(State s) => [.. s.MissionCoverage(rules: true).SkipWhile(l => l != "rules:").Skip(1).TakeWhile(l => l != "warnings:")];

    [Fact]
    public void TestAnObjectiveTheReaderDoesNotUnderstandIsMetByTheEventItsEntryDescribesAndNotByAnother()
    {
        var catalogue = Catalogue(["Harvest a Test Deer while whistling"]);
        state = New("override.db", catalogue, rules: Rules(("1/0", "harvest; species: Test Deer; distance: >100 m; weapon: Test Carbine")));
        ActivateAll();
        Hb(Shot(1, weapon: 51));  // the shotgun
        Hb(Shot(2, distance: 5000));  // 50 m
        Hb(Shot(3, species: 5));  // a fox
        Assert.Equal("", Completed());
        Hb(Shot(4));  // the carbine at 150 m
        Assert.Equal("1", Completed());
        var report = state.MissionCoverage(rules: true).ToList();
        Assert.Contains("understood: 1, not understood: 0 (never met until a version reads them)", report);
        Assert.Contains("objectives overridden by mission_rules.json: 1", report);
        Assert.Contains("mission 1 objective 0 (override): Harvest a Test Deer while whistling", report);
        Assert.Contains("mission 1 objective 0 (override): Harvest a Test Deer while whistling => harvest; species: Test Deer; distance: >100 m; weapon: Test Carbine", report);
        var plain = New("plain.db", catalogue).MissionCoverage().ToList();  // without the file: as before
        Assert.Contains("mission 1 objective 0: Harvest a Test Deer while whistling", plain);
        Assert.DoesNotContain(plain, l => l.Contains("override", StringComparison.Ordinal));
    }

    [Fact]
    public void TestAnEntryForAnUnderstoodObjectiveReplacesItsRule()
    {
        state = New("widen.db", Catalogue(["Harvest a Test Deer with the Test Carbine", "Harvest a Test Deer with the Test Carbine"]),
            rules: Rules(("1/0", "harvest; species: Test Deer; weapon: Test Carbine, Test Shotgun")));
        ActivateAll();
        Hb(Shot(1, weapon: 51));
        Assert.Equal("1", Completed());  // mission 2 keeps the title's rule
        Hb(Shot(2));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestTheStartFinishesAnOverriddenObjectiveFromTheHarvestsAlreadyMade()
    {
        var catalogue = Catalogue(["Harvest a Test Deer while whistling"]);
        state = New("later.db", catalogue);
        ActivateAll();
        Hb(Shot(1));
        Assert.Equal("", Completed());
        var later = New("later.db", catalogue, rules: Rules(("1/0", "harvest; species: Test Deer; weapon: Test Carbine")));
        Assert.Equal(1, later.ConfirmMissions());
        Assert.Equal("1", Completed(of: later));
    }

    [Fact]
    public void TestTheStuckReportTakesTwoObjectivesForEqualByTheirRulesNotTheirTitles()
    {
        // mission 1: one title twice, the second overridden; mission 2: two titles overridden to one rule
        const string Carbine = "Harvest a Test Deer with the Test Carbine";
        var catalogue = $$"""
            {"missions": [{"id": 1, "title": "m", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "{{Carbine}}"}, {"id": 1, "title": "{{Carbine}}"}]},
                          {"id": 2, "title": "n", "dependencies": [], "rewards": [], "objectives": [{"id": 0, "title": "Harvest a Test Deer while whistling"}, {"id": 1, "title": "Harvest a Test Deer while humming"}]}]}
            """;
        const string Shotgun = "harvest; species: Test Deer; weapon: Test Shotgun";
        state = New("stuck.db", catalogue, rules: Rules(("1/1", Shotgun), ("2/0", Shotgun), ("2/1", Shotgun)));
        state.Clock = () => 1_800_000_000;
        ActivateAll();
        for (var i = 1; i <= 5; i++)
            Hb(Shot(i, weapon: 60));  // the .243 rifle: refused by every objective
        var listed = state.StuckMissions(14).Skip(1).Select(l => string.Join(" | ", l.Split(" | ")[..2])).Order(StringComparer.Ordinal);
        Assert.Equal([$"mission 1 m | {Carbine}", $"mission 1 m | {Carbine}", "mission 2 n | Harvest a Test Deer while whistling"], listed);  // 2/1 is equal to 2/0, which is open before it
    }

    [Fact]
    public void TestEveryPrintedRuleReadsBackAsTheSameRule()
    {
        string[] titles =
        [
            "Then harvest a male Test Deer with the Test Carbine from over 100 meters",
            "Harvest a female Test Deer using Test Round ammunition in Test Reserve with one shot",
            "Harvest a Test Deer with a double lung shot at 100% Harvest Value",
            "Harvest a Test Deer weighing at least 80 kg (approx. 176 lbs) from under 40 meters",
            "Harvest an airborne Test Deer with a heart shot",
            "Harvest an unspooked Test Deer from a Tree Stand",
            "Harvest \"Patient Zero\" by the large beach (X: 6.273, Y: 6.330)",
            "Harvest a Test Deer without a scope",
            "Harvest a Test Deer using any shotgun",
            "Harvest a Test Deer with any permitted ammunition",
            "Harvest a Test Deer with a score of 150 or higher",
            "Harvest a Test Deer with at least 8 typical points",
            "Harvest a dark Test Deer",
            "Harvest a Halloween Test Deer",  // coats the game names nowhere print as `coat <id>`
            "Harvest a Test Deer with a color other than Common",
            "Harvest a Test Deer while prone",
            "Harvest a Test Deer without hitting the skull",
            "Harvest a Test Deer with lung and/or heartshots only",
            "Harvest a Test Deer using a rifle mounted with a Test Dot sight",
            "Harvest a Test Deer with a scoped weapon",
            "Harvest a Test Deer without using any type of buckshot",
            "Harvest a Test Deer. The Test Deer should not be running away or charging",
            "Harvest any animal except a Test Fox",
            "Harvest a Test Fox or a Test Deer between 50 and 150 meters",
            "Harvest a Test Deer without using a stand",
            "Spot a Test Deer from a Tree Stand",
            "ID 3 Test Deer tracks from the same animal",
            "ID droppings or a call from a female Test Deer in Test Reserve",
            "Locate any clue of a Test Deer",
        ];
        var read = RuleLines(New("read.db", Catalogue(titles)));
        Assert.Equal("", string.Join('\n', read.Where(l => l.Contains("not understood", StringComparison.Ordinal))));  // every title is read, so every kind of condition is printed
        var rules = Rules([.. read.Select(l => (l.Split(' ')[1] + "/0", l[(l.IndexOf(" => ", StringComparison.Ordinal) + 4)..]))]);
        Assert.Equal(read.Select(l => l.Replace(" objective 0:", " objective 0 (override):", StringComparison.Ordinal)), RuleLines(New("back.db", Catalogue(titles), rules: rules)));
    }

    [Fact]
    public void TestEachKindOfBadEntryStopsTheStartWithItsKey()
    {
        var catalogue = Catalogue(["Harvest a Test Deer", "Spot a Test Deer"]);
        (string Key, JsonNode Rule, string Says)[] bad =
        [
            ("9/0", "harvest", "the catalogue has no mission 9"),
            ("1/5", "harvest", "mission 1 has no objective 5"),
            ("1-0", "harvest", "a key is <mission id>/<objective id>"),
            ("1/0", 5, "a rule is a string of words"),
            ("1/0", "", "the rule is empty"),
            ("1/0", "arrive", "\"arrive\" is no kind of rule the file sets"),
            ("1/0", "harvest; whistling", "\"whistling\" is no condition the server knows"),
            ("1/0", "harvest; weapon: Test Blaster", "no weapon is named \"Test Blaster\""),
            ("1/0", "harvest; ammunition: Test Carbine", "no ammunition is named \"Test Carbine\""),
            ("1/0", "harvest; species: Test Unicorn", "no species is named \"Test Unicorn\""),
            ("1/0", "harvest; coat: tartan", "no coat is named \"tartan\""),
            ("1/0", "harvest; reserve: Test Moon", "no reserve is named \"Test Moon\""),
            ("1/0", "harvest; distance: about 100 m", "\"about\" is no bound"),
            ("1/0", "harvest; organs, one of: spleen", "\"spleen\" is no organ"),
            ("1/0", "harvest; state: dancing", "\"dancing\" is no state"),
            ("1/0", "harvest; clues: footprints x1", "a harvest rule cannot check \"clues: footprints x1\""),
            ("2/0", "spot; weapon: Test Carbine", "a spot rule cannot check \"weapon: Test Carbine\""),
            ("2/0", "track; species: Test Deer", "a track rule has one clues condition"),
            ("1/0", "harvest; male; female", "a gender, a species or \"ordered\" is given twice"),
            ("1/0", "harvest; species: Test Deer; species: Test Fox", "a gender, a species or \"ordered\" is given twice"),
            ("1/0", "harvest; distance: >=100 >=200 m", "\">=100 >=200 m\" has two bounds of one side"),
        ];
        foreach (var (key, rule, says) in bad)
            Assert.StartsWith($"mission_rules.json \"{key}\": {says}", Assert.Throws<InvalidDataException>(() => New("bad.db", catalogue, rules: new JsonObject { [key] = rule.DeepClone() })).Message);
        var file = Path.Combine(tmp, "mission_rules.json");
        File.WriteAllText(file, "{\"1/0\": \"harvest\", \"1/0\": \"spot\"}");
        Assert.Throws<InvalidDataException>(() => State.ReadMissionRules(file));
        File.WriteAllText(file, "{\n  // why: the title's weapon is spelled another way\n  \"1/0\": \"harvest; weapon: Test Carbine\",\n}");
        Assert.Equal("harvest; weapon: Test Carbine", (string)State.ReadMissionRules(file)["1/0"]!);
    }
}
