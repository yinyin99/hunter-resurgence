using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The weapons an objective that names one accepts (docs/server.md, "Weapons of a mission"): every member of the named weapon's family, an explicit finish exactly, a class as before, every
/// other condition unchanged. Invented catalogue: 301 and 302 one name, 303 under another name by the definitions' chain; 310 and 311 two bows that share a skill; 109 and 110 (the fixture's) one name
/// and two finishes; 320 a handgun, 321 its event variant (an event class, no handgun) by the chain.</summary>
public class MissionWeaponTests : MissionTestBase
{
    static readonly string[] Weapons =
    [
        """{"id": 301, "name": "Test Orion Rifle", "variationname": "Wood", "cat": 1, "subcat": {"name": "Rifles"}}""",
        """{"id": 302, "name": "Test Orion Rifle", "variationname": "Camo", "cat": 1, "subcat": {"name": "Rifles"}}""",
        """{"id": 303, "name": "Test Vega Carbine", "variationname": "Plain", "cat": 1, "subcat": {"name": "Rifles"}}""",
        """{"id": 310, "name": "Compound Test Bow \"Falcon\"", "variationname": "Camo", "cat": 1, "subcat": {"name": "Bows & Crossbows"}, "description": "This bow shares skill level and rank with the Compound Test Bow \"Ember\"."}""",
        """{"id": 311, "name": "Compound Test Bow \"Ember\"", "variationname": "Dusk", "cat": 1, "subcat": {"name": "Bows & Crossbows"}, "description": "This bow shares skill level and rank with the Compound Test Bow \"Falcon\"."}""",
        """{"id": 320, "name": "Test Hollow Pistol", "variationname": "Plain", "cat": 1, "subcat": {"name": "Handguns"}}""",
        """{"id": 321, "name": "Test Spook Pistol", "variationname": "Dusk", "cat": 1, "subcat": {"name": "Test Event"}}""",
    ];

    const string Definitions = """
        [{"id": 301, "define": "TEST_ORION"}, {"id": 303, "define": "TEST_VEGA", "parent": "TEST_ORION"},
         {"id": 320, "define": "TEST_HOLLOW"}, {"id": 321, "define": "TEST_SPOOK", "parent": "TEST_HOLLOW"}]
        """;

    State Make(string file, string[] titles, JsonObject? rules = null) => New(file, Catalogue(titles), rules: rules, items: Weapons, itemDefs: Definitions);

    /// <summary>The ids `coverage --rules` lists for the objective of mission `mission` (ascending, comma-separated) and the basis it gives them.</summary>
    static (string Basis, string Ids) Listed(State s, int mission)
    {
        var line = s.MissionCoverage(rules: true).Single(l => l.StartsWith($"mission {mission} objective 0: ", StringComparison.Ordinal) && !l.Contains(" => ", StringComparison.Ordinal));
        var parts = line[$"mission {mission} objective 0: ".Length..].Split(": ", 2);
        return (parts[0], string.Join(",", parts[1].Split("; ").Select(w => w.Split(' ')[0])));
    }

    static string Rule(State s, int mission) => s.MissionCoverage(rules: true).Single(l => l.Contains($"mission {mission} objective 0", StringComparison.Ordinal) && l.Contains(" => ", StringComparison.Ordinal));

    [Fact]
    public void TestTheFinishesOfOneNameAreOneWeapon()
    {
        state = Make("same-name.db", ["Harvest a Test Deer with the Test Twin Pistol"]);
        Assert.Equal(("named weapon", "109,110"), Listed(state, 1));  // the Standard and the Dusk one
    }

    [Fact]
    public void TestTheVariantChainIsOneWeaponUnderAnotherNameInBothDirections()
    {
        state = Make("chain.db", ["Harvest a Test Deer with the Test Orion Rifle", "Harvest a Test Deer with the Test Vega Carbine"]);
        Assert.Equal(("named weapon", "301,302,303"), Listed(state, 1));
        Assert.Equal(("named weapon", "301,302,303"), Listed(state, 2));
        ActivateAll();
        Hb(Shot(1, weapon: 303));  // the carbine counts for the rifle's objective ...
        Assert.Equal("1,2", Completed());
        state = Make("chain2.db", ["Harvest a Test Deer with the Test Vega Carbine"]);
        ActivateAll();
        Hb(Shot(1, weapon: 302));  // ... and the rifle's other finish for the carbine's
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestTheEngravedTwinCountsForItsBasesObjectiveWithoutAnyDefinition()
    {
        state = New("shares.db", Catalogue(["Harvest a Test Deer with the Compound Test Bow \"Falcon\"", "Harvest a Test Deer with the Compound Test Bow \"Ember\""]), items: Weapons);  // no item definitions
        Assert.Equal(("named weapon", "310,311"), Listed(state, 1));
        Assert.Equal(("named weapon", "310,311"), Listed(state, 2));
        ActivateAll();
        Hb(Shot(1, weapon: 311));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestXOrYIsTheUnionOfTheirFamilies()
    {
        state = Make("or.db", ["Harvest a Test Deer with the Test Orion Rifle or the Compound Test Bow \"Falcon\""]);
        Assert.Equal(("named weapon", "301,302,303,310,311"), Listed(state, 1));
    }

    [Fact]
    public void TestAClassConditionTakesTheEventFinishOfAWeaponOfItsClass()
    {
        state = Make("class.db", ["Harvest a Test Deer with any handgun", "Harvest a Test Deer with the Test Hollow Pistol"]);
        var (basis, ids) = Listed(state, 1);
        Assert.Equal("class", basis);
        Assert.Contains("320", ids.Split(','));
        Assert.Contains("321", ids.Split(','));  // the event finish of the class's handgun is a handgun too
        Assert.Equal(("named weapon", "320,321"), Listed(state, 2));  // and the weapon named is its whole family
        Assert.Contains("Test Hollow Pistol", Rule(state, 1));  // the class holds the whole family: its name is printed bare
    }

    [Fact]
    public void TestAnExceptedWeaponIsRefusedWithItsFamily()
    {
        state = Make("except.db", ["Harvest a Test Deer with any weapon except the Test Orion Rifle"]);
        var (basis, ids) = Listed(state, 1);
        Assert.Equal("any weapon but", basis);
        Assert.DoesNotContain(ids.Split(','), id => id is "301" or "302" or "303");  // the carbine is the rifle's family: refused with it
        Assert.Contains("320", ids.Split(','));
    }

    [Fact]
    public void TestAnAmmunitionConditionStillRefusesAFamilyMemberLoadedWithAnotherAmmunition()
    {
        state = Make("ammo.db", ["Harvest a Test Deer with the Test Orion Rifle using Test Round ammunition"]);
        ActivateAll();
        Hb(Shot(1, weapon: 303, ammunition: 71));  // the carbine counts as the rifle, the arrows are not the objective's ammunition
        Assert.Equal("", Completed());
        Hb(Shot(2, weapon: 303, ammunition: 52));
        Assert.Equal("1", Completed());
    }

    [Fact]
    public void TestAnOverrideNamingAWeaponTakesItsFamilyAndAnExplicitFinishStaysExact()
    {
        var rules = Rules(("1/0", "harvest; species: Test Deer; weapon: Test Orion Rifle"), ("2/0", "harvest; species: Test Deer; weapon: Test Orion Rifle (Camo)"),
            ("3/0", "harvest; species: Test Deer; weapon: Test Twin Pistol (Dusk), Compound Test Bow \"Falcon\""));
        state = Make("override-family.db", ["Harvest a Test Deer while whistling", "Harvest a Test Deer while humming", "Harvest a Test Deer while singing"], rules);
        Assert.Equal(("named weapon", "301,302,303"), Listed(state, 1));  // a name is the whole family
        Assert.Equal(("named finish", "302"), Listed(state, 2));  // a finish is that item only: the other finish and the carbine are refused
        Assert.Equal(("named finish", "110,310,311"), Listed(state, 3));  // the finish exactly, the other name with its family
        ActivateAll();
        Hb(Shot(1, weapon: 301));
        Assert.Equal("1", Completed());
        Hb(Shot(2, weapon: 302));
        Assert.Equal("1,2", Completed());
    }

    [Fact]
    public void TestEveryPrintedWeaponConditionReadsBackAsTheSameRule()
    {
        string[] titles = ["Harvest a Test Deer with any handgun", "Harvest a Test Deer with the Test Orion Rifle", "Harvest a Test Deer with the Compound Test Bow \"Ember\"",
            "Harvest a Test Deer with the Test Twin Pistol or the Test Hollow Pistol", "Harvest any animal except a Test Fox with any shotgun"];
        var read = RuleLines(Make("print.db", titles));
        var rules = Rules([.. read.Select(l => (l.Split(' ')[1] + "/0", l[(l.IndexOf(" => ", StringComparison.Ordinal) + 4)..]))]);
        Assert.Equal(read.Select(l => l.Replace(" objective 0:", " objective 0 (override):", StringComparison.Ordinal)), RuleLines(Make("back.db", titles, rules)));
    }

    static string[] RuleLines(State s) => [.. s.RuleLines()];

    [Fact]
    public void TestTheTwoTablesHaveAFixedShapeAndTheSameCatalogueGivesTheSameLines()
    {
        string[] titles = ["Harvest a Test Deer with the Test Orion Rifle", "Harvest a Test Deer with any handgun", "Harvest a Test Deer"];
        string[] Lines(string file) => [.. Make(file, titles).MissionWeaponTables().SelectMany(t => t.Lines.Prepend("# " + t.File))];
        var lines = Lines("tables.db");
        Assert.Equal(lines, Lines("tables2.db"));
        var objectives = lines.SkipWhile(l => l != "# mission-objectives.md").Skip(1).TakeWhile(l => !l.StartsWith("# ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["| mission | objective | title | weapon condition | accepted weapons |", "|---|---|---|---|---|",
            "| 1 | 0 | Harvest a Test Deer with the Test Orion Rifle | named weapon | 301 Test Orion Rifle (Wood); 302 Test Orion Rifle (Camo); 303 Test Vega Carbine (Plain) |"], objectives[..3]);
        Assert.StartsWith("| 2 | 0 | Harvest a Test Deer with any handgun | class | ", objectives[3]);
        Assert.Equal("| 3 | 0 | Harvest a Test Deer | none | |", objectives[4]);
        Assert.StartsWith("| daily ", objectives[5]);  // then the facts file's daily missions
        Assert.Equal("", objectives[^2]);
        Assert.Matches(@"^\d+ objectives: class \d+, named weapon \d+, none \d+\.$", objectives[^1]);
        var byWeapon = lines.SkipWhile(l => l != "# weapon-missions.md").Skip(1).ToArray();
        Assert.Equal(["| id | name | finish | family | accepted by (mission/objective) |", "|---|---|---|---|---|"], byWeapon[..2]);
        Assert.Contains("| 303 | Test Vega Carbine | Plain | 301 | 1/0 |", byWeapon);  // the family's representative is the base weapon
        Assert.Contains("| 320 | Test Hollow Pistol | Plain | 320 | 2/0 |", byWeapon);
        Assert.Contains("| 321 | Test Spook Pistol | Dusk | 320 | 2/0 |", byWeapon);  // an event finish of the class's handgun: the class takes it
        Assert.Matches(@"^\d+ weapons, \d+ accepted by at least one objective, \d+ by none\.$", byWeapon[^1]);
    }

    [Fact]
    public void TestTheMissionsPageHasTheWeaponsOfAnObjectiveThatNamesOneAndNothingForTheOthers()
    {
        state = Make("page.db", ["Harvest a Test Deer with the Test Orion Rifle", "Harvest a Test Deer with any handgun", "Harvest a Test Deer"],
            Rules(("3/0", "harvest; species: Test Deer; weapon: Test Twin Pistol (Dusk), Test Orion Rifle (Camo)")));
        var page = state.WebMissions(ME)!["missions"]!.AsArray().OfType<JsonObject>().ToDictionary(m => (long)m["id"]!, m => string.Join("|", m["objective_weapons"]!.AsArray().Select(o => string.Join(",", o!.AsArray()))));
        Assert.Equal("Test Orion Rifle,Test Vega Carbine", page[1]);  // one line per weapon name, the finishes of a name grouped under it
        Assert.Equal("", page[2]);  // a class names no weapon
        Assert.Equal("Test Orion Rifle (Camo),Test Twin Pistol (Dusk)", page[3]);  // a finish named: that one only
    }
}
