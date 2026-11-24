using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>`coverage --rules`: every objective's rule in words, and the warnings where a rule disagrees with the data its mission carries beside the titles. Invented catalogue.</summary>
public class MissionRulesTests : MissionTestBase
{
    // one mission with one objective that is its title; `extra` is more JSON of the mission (`weapons`, `desc`, `species`)
    static string Mission(int id, string title, string extra = "") =>
        $"{{\"id\": {id}, \"title\": \"m\", \"dependencies\": [], \"rewards\": [], \"objectives\": [{{\"id\": 0, \"title\": \"{title}\"}}]{extra}}}";

    static string Missions(params string[] missions) => "{\"missions\": [" + string.Join(",", missions) + "]}";

    // a mission whose objectives are these titles
    static string Objectives(int id, string[] titles, string extra = "") =>
        $"{{\"id\": {id}, \"title\": \"m\", \"dependencies\": [], \"rewards\": [], \"objectives\": [{string.Join(",", titles.Select((t, i) => $"{{\"id\": {i}, \"title\": \"{t}\"}}"))}]{extra}}}";

    List<string> Warnings(string file, params string[] missions) => [.. New(file, Missions(missions)).MissionCoverage().SkipWhile(l => l != "warnings:").Skip(1)];

    [Fact]
    public void TestEveryObjectivesRuleIsPrintedInWordsInTheOrderOfMissionAndObjectiveTheSameEveryTime()
    {
        var missions = Missions(
            Mission(2, "Then harvest a male Test Deer with the Test Carbine from over 100 meters"),
            Mission(1, "Harvest a Test Deer using Test Round ammunition in Test Reserve with one shot"),
            Mission(3, "Harvest a Test Deer while whistling"),
            Mission(4, "Arrive at the Test Lodge"));
        var lines = New("rules.db", missions).MissionCoverage(rules: true).SkipWhile(l => l != "rules:").Skip(1).ToList();
        Assert.Equal(
        [
            "mission 1 objective 0: Harvest a Test Deer using Test Round ammunition in Test Reserve with one shot => harvest; species: Test Deer; ammunition: Test Round; one shot; reserve: Test Reserve",
            "mission 2 objective 0: Then harvest a male Test Deer with the Test Carbine from over 100 meters => harvest; species: Test Deer; male; ordered; distance: >100 m; weapon: Test Carbine",
            "mission 3 objective 0: Harvest a Test Deer while whistling => harvest; not understood, left over: whistling; species: Test Deer",
            "mission 4 objective 0: Arrive at the Test Lodge => arrive",
        ], lines);
        Assert.Equal(lines, New("rules2.db", missions).MissionCoverage(rules: true).SkipWhile(l => l != "rules:").Skip(1));
        Assert.DoesNotContain("rules:", New("norules.db", missions).MissionCoverage());
    }

    [Fact]
    public void TestAWeaponOfTheMissionsListThatNoObjectiveAcceptsIsWarnedWhenItsFamilyIsNamed()
    {
        var w = Warnings("weapons.db", Mission(1, "Harvest a Test Deer with the Heavy Test Bow", ", \"weapons\": [66, 67]"), Mission(2, "Harvest a Test Deer with the Heavy Test Bow", ", \"weapons\": [67]"),
            Mission(3, "Harvest a Test Deer with the Heavy Test Bow", ", \"weapons\": [67, 50]"), Mission(4, "Harvest a Test Deer", ", \"weapons\": [66, 67]"),
            Objectives(5, ["Harvest a Test Deer with the Heavy Test Bow", "Harvest a Test Deer"], ", \"weapons\": [66, 67]"));  // 5: an objective that asks nothing of the weapon is not counted
        Assert.Equal(["warning weapons: mission 1: the weapons list has \"test bow\", which no objective accepts, and objectives accept \"heavy test bow\"",
                      "warning weapons: mission 5: the weapons list has \"test bow\", which no objective accepts, and objectives accept \"heavy test bow\""], w);  // 3: another family; 4: anything goes
    }

    [Fact]
    public void TestTheRuleOfATitleThatNamesAShorterWeaponPrintsTheLongerNamesToo()
    {
        var lines = New("longer.db", Missions(Mission(1, "Harvest a Test Deer with the Test Revolver"), Mission(2, "Harvest a Test Deer with any Test Recurve Bow"),
            Mission(3, "Harvest a Test Deer with the Test Bolt Rifle"), Mission(4, "Harvest a Test Deer with the Test Heavy Recurve Bow"), Mission(5, "Harvest a Test Deer with the Test Magnum Revolver")))
            .MissionCoverage(rules: true).SkipWhile(l => l != "rules:").Skip(1).TakeWhile(l => l != "warnings:").ToList();
        Assert.Equal(
        [
            "mission 1 objective 0: Harvest a Test Deer with the Test Revolver => harvest; species: Test Deer; weapon: Test Magnum Revolver, Test Revolver",
            "mission 2 objective 0: Harvest a Test Deer with any Test Recurve Bow => harvest; species: Test Deer; weapon: Test Heavy Recurve Bow, Test Recurve Bow",
            "mission 3 objective 0: Harvest a Test Deer with the Test Bolt Rifle => harvest; species: Test Deer; weapon: Test Bolt Rifle, Test Maker Bolt Rifle",
            "mission 4 objective 0: Harvest a Test Deer with the Test Heavy Recurve Bow => harvest; species: Test Deer; weapon: Test Heavy Recurve Bow",  // the longer name stays exact
            "mission 5 objective 0: Harvest a Test Deer with the Test Magnum Revolver => harvest; species: Test Deer; weapon: Test Magnum Revolver",
        ], lines);
    }

    [Fact]
    public void TestADescriptionThatNamesAnItemNoObjectiveAcceptsWhileOthersAreNamedIsWarned()
    {
        var w = Warnings("desc.db", Mission(1, "Harvest a Test Deer with the Test Carbine", ", \"desc\": \"Use the Test Shotgun.\""), Mission(2, "Harvest a Test Deer with the Test Carbine", ", \"desc\": \"Use the Test Carbine.\""),
            Mission(3, "Harvest a Test Deer using Test Round ammunition", ", \"desc\": \"<b>Test Slug</b> Shells only\""), Mission(4, "Harvest a Test Deer", ", \"desc\": \"Use the Test Shotgun.\""));
        Assert.Equal(["warning description: mission 1: the description names \"test shotgun\", which no objective accepts, while objectives name other items",
                      "warning description: mission 3: the description names \"test slug\", which no objective accepts, while objectives name other items"], w);
    }

    [Fact]
    public void TestARuleThatAcceptsNoSpeciesOfTheMissionsListIsWarned()
    {
        var w = Warnings("species.db", Mission(1, "Harvest a Test Fox", ", \"species\": [1]"), Mission(2, "Harvest a Test Deer", ", \"species\": [1]"), Mission(3, "Harvest a Test Fox", ", \"species\": []"),
            Mission(4, "Arrive at Test Fox Bay", ", \"species\": [1]"), Mission(5, "Harvest any animal except a Test Deer", ", \"species\": [1]"));  // 4: an arrival checks no species; 5: the complement leaves the mission's species out on purpose
        Assert.Equal(["warning species: mission 1 objective 0: the rule accepts Test Fox, none of the mission's species (Test Deer)"], w);
    }

    [Fact]
    public void TestAConditionNoEventCanMeetIsWarnedAndAMissionWithoutTheProblemsHasNoWarnings()
    {
        var w = Warnings("never.db", Mission(1, "Harvest \\\"Patient Zero\\\" at over 100 meters"), Mission(2, "Harvest a Test Deer between 200 and 100 meters"),
            Mission(3, "Harvest \\\"Patient Zero\\\" by the large beach (X: 6.273, Y: 6.330)"), Mission(4, "Harvest a Test Deer without using any type of birdshot shells"));  // 4: an empty exclusion refuses nothing
        Assert.Equal(["warning never met: mission 1 objective 0: a named animal without a place",
                      "warning never met: mission 2 objective 0: distance: >=200 <=100 m: the lower bound is above the upper one"], w);
        Assert.DoesNotContain(New("clean.db", Missions(Mission(1, "Harvest a Test Deer with the Test Carbine", ", \"weapons\": [50], \"desc\": \"Test Carbine\", \"species\": [1]"))).MissionCoverage(), l => l.StartsWith("warning", StringComparison.Ordinal));
    }

    [Fact]
    public void TestAVariantWeaponIsPrintedWithItsVariantAndThePrintedRuleReadsBackToTheSameLine()
    {
        var catalogue = Missions(Mission(1, "Harvest a Test Deer"), Mission(2, "Harvest a Test Deer"));
        List<string> Print(State s) => [.. s.MissionCoverage(rules: true).SkipWhile(l => l != "rules:").Skip(1).TakeWhile(l => l != "warnings:")];
        static string Rule(string line) => line[(line.IndexOf("=> ", StringComparison.Ordinal) + 3)..];
        var first = Print(New("variant1.db", catalogue, rules: new System.Text.Json.Nodes.JsonObject
        {
            ["1/0"] = "harvest; species: Test Deer; weapon: Test Twin Pistol (Wolfsbane)", ["2/0"] = "harvest; species: Test Deer; weapon: Test Twin Pistol",
        }));
        Assert.EndsWith("=> harvest; species: Test Deer; weapon: Test Twin Pistol (Wolfsbane)", first[0]);
        Assert.EndsWith("=> harvest; species: Test Deer; weapon: Test Twin Pistol", first[1]);  // both variants: the name alone
        Assert.Equal(first, Print(New("variant2.db", catalogue, rules: new System.Text.Json.Nodes.JsonObject { ["1/0"] = Rule(first[0]), ["2/0"] = Rule(first[1]) })));
    }

    [Fact]
    public void TestAVariantWeaponAmongOtherWeaponsKeepsItsVariantWhenThePrintedRuleReadsBack()
    {
        var catalogue = Missions(Mission(1, "Harvest a Test Deer"));
        List<string> Print(State s) => [.. s.MissionCoverage(rules: true).SkipWhile(l => l != "rules:").Skip(1).TakeWhile(l => l != "warnings:")];
        var first = Print(New("variant3.db", catalogue, rules: new System.Text.Json.Nodes.JsonObject { ["1/0"] = "harvest; species: Test Deer; weapon: Test Twin Pistol (Wolfsbane), Test Carbine" }));
        Assert.EndsWith("=> harvest; species: Test Deer; weapon: Test Carbine, Test Twin Pistol (Wolfsbane)", first[0]);  // not the bare name: that would be both variants
        var again = New("variant4.db", catalogue, rules: new System.Text.Json.Nodes.JsonObject { ["1/0"] = first[0][(first[0].IndexOf("=> ", StringComparison.Ordinal) + 3)..] });
        Assert.Equal(first, Print(again));
    }
}
