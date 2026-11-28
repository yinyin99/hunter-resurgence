using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The weapon families (docs/server.md, "Weapon families") of an invented catalogue: one weapon type per family, however its items relate.</summary>
public class WeaponFamiliesTests : StateTestBase
{
    // 1-2 same name; 10-11 a variant chain under another name; 20-21 the shares-skill pair; 22-25 a finish-narrowed pair, a same-name item and an "All variations" description;
    // 40-42 a chain and a name in one family; 51 and 55 a base with the higher id; 60 alone; 70 ammunition; 80-81 a name written with another case and gauge spelling;
    // 90 and 91-92 descriptions naming no whole weapon name; 95-96 no definitions: the lowest id
    const string Catalogue = """
        [{"id": 1, "name": "Test Hawk Rifle", "variationname": "Wood", "cat": 1}, {"id": 2, "name": "Test Hawk Rifle", "variationname": "Camo", "cat": 1},
         {"id": 10, "name": "Test Arrow Bow", "variationname": "Plain", "cat": 1}, {"id": 11, "name": "Test Arrow Bow \"Gold\"", "variationname": "Gilded", "cat": 1},
         {"id": 20, "name": "Compound Test Bow \"Falcon\"", "variationname": "Camo", "cat": 1, "description": "Power: 60 lbs\nThis bow shares skill level and rank with the Compound Test Bow \"Ember\"."},
         {"id": 21, "name": "Compound Test Bow \"Ember\"", "variationname": "Oriental", "cat": 1, "description": "This bow shares skill level and rank with the Compound Test Bow \"Falcon\"."},
         {"id": 22, "name": "Test Pump Shotgun Winter", "variationname": "Winter Camo", "cat": 1, "description": "Note: The Winter version shares the same skill of the Test Pump Shotgun (Carbon). Start over? No."},
         {"id": 23, "name": "Test Pump Shotgun", "variationname": "Carbon", "cat": 1}, {"id": 24, "name": "Test Pump Shotgun", "variationname": "Wood", "cat": 1},
         {"id": 25, "name": "Test Sawed Shotgun", "variationname": "Black", "cat": 1, "description": "Note: All variations of the Test Pump Shotgun share the same weapon skill. This means you keep your level."},
         {"id": 40, "name": "Test Elk Rifle", "variationname": "Wood", "cat": 1}, {"id": 41, "name": "Test Elk Rifle II", "variationname": "Wood", "cat": 1}, {"id": 42, "name": "Test Elk Rifle II", "variationname": "Camo", "cat": 1},
         {"id": 51, "name": "Test Duck Gun Royal", "variationname": "Royal", "cat": 1}, {"id": 55, "name": "Test Duck Gun", "variationname": "Plain", "cat": 1},
         {"id": 60, "name": "Test Lone Rifle", "variationname": "Plain", "cat": 1},
         {"id": 70, "name": "Test Hawk Rifle", "variationname": "Plain", "cat": 3},
         {"id": 80, "name": "Test 12GA Rifle", "variationname": "Plain", "cat": 1}, {"id": 81, "name": "test 12 ga rifle", "variationname": "Camo", "cat": 1},
         {"id": 90, "name": "Test Recurve Bow", "variationname": "Plain", "cat": 1, "description": "This bow shares skill level and rank with the Nonexistent Bow."},
         {"id": 91, "name": "Test Heavy Recurve Bow", "variationname": "Plain", "cat": 1}, {"id": 92, "name": "Test Recurve Bow II", "variationname": "Plain", "cat": 1, "description": "All variations of the Recurve Bow share the same weapon skill."},
         {"id": 95, "name": "Test Moose Rifle", "variationname": "Plain", "cat": 1}, {"id": 96, "name": "Test Moose Rifle", "variationname": "Camo", "cat": 1}]
        """;

    const string Definitions = """
        [{"id": 10, "define": "TEST_BOW"}, {"id": 11, "define": "TEST_BOW_GOLD", "parent": "TEST_BOW"},
         {"id": 40, "define": "TEST_ELK"}, {"id": 41, "define": "TEST_ELK_II", "parent": "TEST_ELK"},
         {"id": 51, "define": "TEST_DUCK_ROYAL", "parent": "TEST_DUCK"}, {"id": 55, "define": "TEST_DUCK"},
         {"id": 70, "define": "TEST_AMMO", "parent": "TEST_ELK"}]
        """;

    static WeaponFamilies Build(string items = Catalogue, string? defs = Definitions) =>
        WeaponFamilies.Build(JsonNode.Parse(items)!.AsArray().OfType<JsonObject>(), defs is null ? null : JsonNode.Parse(defs)!.AsArray());

    static string Members(long id) => string.Join(",", Build().Of(id)!.Members);

    [Fact]
    public void TestItemsOfOneNameAreOneFamilyWhateverTheirFinish() => Assert.Equal("1,2", Members(2));

    [Fact]
    public void TestAVariantChainIsOneFamilyUnderAnotherName() => Assert.Equal("10,11", Members(11));

    [Fact]
    public void TestTheSharesSkillPairIsOneFamilyWithoutAnyDefinition()
    {
        var families = Build(defs: null);
        Assert.Equal([20L, 21L], families.Of(21)!.Members);
        Assert.Equal(families.Of(20), families.Of(21));
    }

    [Fact]
    public void TestAFinishInParenthesesNarrowsTheSharedWeaponAndAnAllVariationsSentenceNamesIt() =>
        Assert.Equal("22,23,24,25", Members(25));  // 22 shares with the Carbon one (23), 24 has the same name as 23, 25's description names that name

    [Fact]
    public void TestOneFamilyAcrossTwoRelations() => Assert.Equal("40,41,42", Members(42));  // 40-41: the chain, 41-42: the name

    [Fact]
    public void TestAWeaponWithNoRelationIsItsOwnFamily()
    {
        var f = Build().Of(60)!;
        Assert.Equal(60, f.Representative);
        Assert.Equal([60L], f.Members);
    }

    [Fact]
    public void TestADescriptionNamingNoWholeWeaponNameAddsNothing()
    {
        Assert.Equal([90L], Build().Of(90)!.Members);  // "Nonexistent Bow"
        Assert.Equal([92L], Build().Of(92)!.Members);  // "Recurve Bow" is not the heavy one's name nor the II's: no word-subset guess
        Assert.Equal([91L], Build().Of(91)!.Members);
    }

    [Fact]
    public void TestANonWeaponHasNoFamily()
    {
        var families = Build();
        Assert.Null(families.Of(70));  // an ammunition of a weapon's name, with a chain to a weapon
        Assert.Null(families.Of(9999));
        Assert.DoesNotContain(70, families.All.SelectMany(f => f.Members));
    }

    [Fact]
    public void TestNamesCompareWithoutCaseAndWithTheGaugeSpelledEitherWay() => Assert.Equal("80,81", Members(81));

    [Fact]
    public void TestTheRepresentativeIsTheMemberWithNoParentThenTheLowestId()
    {
        var families = Build();
        Assert.Equal(55, families.Of(51)!.Representative);  // the base weapon, though 51 has the lower id
        Assert.Equal("55,51", Members(51));
        Assert.Equal(40, families.Of(42)!.Representative);
        Assert.Equal(1, families.Of(2)!.Representative);  // no definitions for either: the lowest id
        Assert.Equal(95, families.Of(96)!.Representative);
        Assert.Equal(10, families.Of(11)!.Representative);
        Assert.Equal(22, families.Of(25)!.Representative);  // no definition for any of them: the lowest id
    }

    [Fact]
    public void TestEveryWeaponIsInExactlyOneFamilyAndTheFamiliesAreInOrder()
    {
        var families = Build();
        var members = families.All.SelectMany(f => f.Members).ToArray();
        Assert.Equal(members.Distinct().Count(), members.Length);
        Assert.Equal(JsonNode.Parse(Catalogue)!.AsArray().Count(i => (long)i!["cat"]! == 1), members.Length);
        Assert.Equal(families.All.Select(f => f.Representative).Order(), families.All.Select(f => f.Representative));
    }

    [Fact]
    public void TestTheGeneratedTableIsStableAndMatchesTheCommittedOutput()
    {
        // the same catalogue in another order and built twice: the same bytes
        var shuffled = string.Join(",", JsonNode.Parse(Catalogue)!.AsArray().Reverse().Select(i => i!.ToJsonString()));
        var table = string.Join("\n", Build().Table());
        Assert.Equal(table, string.Join("\n", Build($"[{shuffled}]").Table()));
        Assert.Equal(Expected.ReplaceLineEndings("\n").TrimEnd(), table);
    }

    // what `admin weapon-families` prints for the catalogue above: the committed copy of the generator's output
    const string Expected = """
        | id | name | finish | family | why |
        |---|---|---|---|---|
        | 1 | Test Hawk Rifle | Wood | 1 | name |
        | 2 | Test Hawk Rifle | Camo | 1 | name |
        | 10 | Test Arrow Bow | Plain | 10 | chain |
        | 11 | Test Arrow Bow "Gold" | Gilded | 10 | chain |
        | 20 | Compound Test Bow "Falcon" | Camo | 20 | shares |
        | 21 | Compound Test Bow "Ember" | Oriental | 20 | shares |
        | 22 | Test Pump Shotgun Winter | Winter Camo | 22 | shares |
        | 23 | Test Pump Shotgun | Carbon | 22 | name+shares |
        | 24 | Test Pump Shotgun | Wood | 22 | name+shares |
        | 25 | Test Sawed Shotgun | Black | 22 | shares |
        | 40 | Test Elk Rifle | Wood | 40 | chain |
        | 41 | Test Elk Rifle II | Wood | 40 | chain+name |
        | 42 | Test Elk Rifle II | Camo | 40 | name |
        | 51 | Test Duck Gun Royal | Royal | 55 | chain |
        | 55 | Test Duck Gun | Plain | 55 | chain |
        | 60 | Test Lone Rifle | Plain | 60 | alone |
        | 80 | Test 12GA Rifle | Plain | 80 | name |
        | 81 | test 12 ga rifle | Camo | 80 | name |
        | 90 | Test Recurve Bow | Plain | 90 | alone |
        | 91 | Test Heavy Recurve Bow | Plain | 91 | alone |
        | 92 | Test Recurve Bow II | Plain | 92 | alone |
        | 95 | Test Moose Rifle | Plain | 95 | name |
        | 96 | Test Moose Rifle | Camo | 95 | name |

        23 weapons, 12 families, 8 with several members.
        """;

    [Fact]
    public void TestTheStateKeepsTheFamiliesOfItsCatalogue()
    {
        var app = APP;
        var items = app["items"]!.AsArray();
        items.First(i => (long)i!["id"]! == 5)!["cat"] = 1;
        items.Add(JsonNode.Parse("""{"id": 706, "name": "Test Rifle Variant", "cat": 1}"""));
        items.Add(JsonNode.Parse("""{"id": 900, "name": "Test Rifle", "variationname": "Camo", "cat": 1}"""));
        var state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, app, LOCALE, TEXTURES, itemDefs: ITEM_DEFS);
        Assert.Equal([5L, 706L, 900L], state.Weapons.Of(900)!.Members);  // 5 and 706: the definitions' chain; 900: the name of 5
        Assert.Null(state.Weapons.Of(2));  // a scope
    }

    [Fact]
    public void TestWeaponsDerivedFromABaseThatIsNoCatalogueWeaponShareOneFamily()
    {
        // 300 and 301 derive from TEST_ABSTRACT_BASE (a definition that is no catalogue weapon: 399 is ammunition, 298 has no item at all); 302 derives from another such base
        var families = WeaponFamilies.Build(JsonNode.Parse("""
            [{"id": 300, "name": "Test Ridge Rifle", "cat": 1}, {"id": 301, "name": "Test Ridge Carbine", "cat": 1}, {"id": 302, "name": "Test Plain Rifle", "cat": 1}, {"id": 399, "name": "Test Round", "cat": 3}]
            """)!.AsArray().OfType<JsonObject>(), JsonNode.Parse("""
            [{"id": 298, "define": "TEST_ABSTRACT_BASE"}, {"id": 300, "define": "TEST_RIDGE", "parent": "TEST_ABSTRACT_BASE"}, {"id": 301, "define": "TEST_RIDGE_CARBINE", "parent": "TEST_RIDGE"},
             {"id": 302, "define": "TEST_PLAIN", "parent": "TEST_AMMO_BASE"}, {"id": 399, "define": "TEST_AMMO_BASE"}]
            """)!.AsArray());
        Assert.Equal([300L, 301L], families.Of(301)!.Members);
        Assert.Equal(300, families.Of(301)!.Representative);  // every member has a parent: the lowest id
        Assert.Equal([302L], families.Of(302)!.Members);  // a lone derived weapon is its own family
        Assert.Contains("| 301 | Test Ridge Carbine |  | 300 | chain |", families.Table());
    }
}
