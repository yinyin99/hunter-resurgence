using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Every species the game can harvest, shot ethically, gets its score and its gm$ from the facts file's tables alone (made-up measurements, no variants file).</summary>
public class HarvestScoreTests : StateTestBase
{
    // families whose female has no antlers: a doe scores nothing, as the game does
    static readonly HashSet<string> MaleOnly = ["bc_deer", "elk", "moose", "cic_roe", "cic_red", "cic_fallow", "tines"];
    const int Ammo = 10;
    double gm = 33;  // CONNECT's balance

    public HarvestScoreTests()
    {
        var connect = CONNECT;
        connect["species"] = new JsonArray(Enumerable.Range(0, 59).Select(i => (JsonNode?)new JsonObject { ["scale_factor"] = 1.0, ["variant_id"] = i }).ToArray());
        connect["ethical"] = new JsonArray(Enumerable.Range(0, 59).Select(_ => (JsonNode?)new JsonArray(Ammo)).ToArray());
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES);
        Scoring.Load(O("""{"game_web_host": "static.test"}"""), null);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
    }

    static JsonObject Antler() => O("""
        {"circumference_h1": 200, "circumference_h2": 180, "circumference_h3": 160, "circumference_h4": 140, "palm_length": 300, "palm_width": 200,
         "brow_palm_width": 100, "top_palm_width": 100, "top_palm_length": 100, "nof_points_excluding_brows": 6, "nof_brow_points": 2,
         "points": [{"length": 60000, "type": 0}, {"length": 300, "type": 1}, {"length": 300, "type": 2}, {"length": 300, "type": 3}]}
        """);

    /// <summary>One harvest of the species and gender: the kill's update 6 and whether the gm$ balance rose.</summary>
    (JsonNode Update, bool Paid) Harvest(int species, int gender, int ammo = Ammo)
    {
        var ind = 1000 + species * 2 + gender;
        var fam = Facts.Default.Families.GetValueOrDefault(species);
        var e7 = O($$"""{"event": 7, "individual": {{ind}}, "species": {{species % 100}}, "gender": {{gender}}, "weight_grams": 200000}""");
        e7["feature"] = fam == "skull" ? (60000L << 16) | 60000 : 200000L * 1000;  // skull: width and length in 1/100 mm; the weight families: milligrams
        List<JsonNode> events = [e7];
        if (fam is "turkey" or "pheasant")
        {
            var male = gender == 0;
            var bird = O($$"""{"event": 22, "individual": {{ind}}, "tail": 400}""");
            (bird["beard"], bird["left_spur"], bird["right_spur"]) = (male ? 150000 : 0, male ? 15000 : 0, male ? 20000 : 0);
            events.Add(bird);
        }
        else if (fam is not null && (gender == 0 || !MaleOnly.Contains(fam)) && fam is not ("weight_lb" or "weight_mg" or "weight_kg" or "weight_g" or "skull" or "tusks"))
        {
            var e22 = O($$"""{"event": 22, "individual": {{ind}}, "greatest_spread": 900, "tip_to_tip_spread": 800, "inside_spread_of_main_beams": 700, "weight": 9000, "volume": 200000, "color": 2, "pearling": 1, "coronets": 1, "age_rings": 1, "horn_curve": 1 }""");
            (e22["left_antler"], e22["right_antler"]) = (Antler(), Antler());
            events.Add(e22);
        }
        else if (fam == "tusks" && gender == 0)  // a sow sends no trophy event at all
        {
            var e22 = O($$"""{"event": 22, "individual": {{ind}} }""");
            (e22["left_antler"], e22["right_antler"]) = (O("""{"circumference_h1": 100, "points": [{"length": 100, "type": 0}]}"""), O("""{"circumference_h1": 100, "points": [{"length": 100, "type": 0}]}"""));
            events.Add(e22);
        }
        events.Add(O($$"""{"event": 1, "individual": {{ind}}, "ammunition_id": {{ammo}}, "weapon_id": 5, "animal_state": 3}"""));
        events.Add(O($$"""{"event": 3, "individual": {{ind}}, "extended_species": {{species}} }"""));
        var wallet = Hb([.. events]).Where(u => Py.Eq(u!["update"], 15) && Py.Eq(u["type"], "gm")).ToList();
        var score = Assert.Single(Hb(), u => Py.Eq(u!["update"], 6) && Py.Eq(u["individual"], ind))!;
        var paid = wallet.Count > 0 && Py.D(wallet[^1]!["amount"]) > gm;
        if (paid) gm = Py.D(wallet[^1]!["amount"]);
        return (score, paid);
    }

    [Fact]
    public void EveryScoredSpeciesPaysItsEthicalMaleAndFemale()
    {
        var species = Facts.Default.Families.Keys.Order().ToList();
        Assert.Equal(Facts.Default.Variants.Keys.Order(), species);  // a family and a row of bounds for each, 100 and up the non-typical deer
        foreach (var sp in species)
            foreach (var gender in new[] { 0, 1 })
            {
                var (update, paid) = Harvest(sp, gender);
                double score = Py.D(update["score"]), css = Py.D(update["css"]);
                var what = $"species {sp} ({Facts.Default.Families[sp]}), gender {gender}";
                if (gender == 1 && MaleOnly.Contains(Facts.Default.Families[sp]))
                {
                    Assert.True(score == 0 && css == 0 && !paid, what + ": a doe scores nothing");
                    continue;
                }
                Assert.True(Py.Eq(update["trophy_shot"], 1) && score > 0, what + ": score");
                Assert.True(css > 0, what + ": css");
                Assert.True(paid, what + ": gm$");
            }
    }

    [Theory]
    [InlineData(22), InlineData(31), InlineData(32), InlineData(48), InlineData(56)]  // the three dogs, the sasquatch, the t-rex: the game scores none of them
    public void ASpeciesWithoutScoreRulesGetsNoScore(int species)
    {
        var (update, paid) = Harvest(species, 0);
        Eq("""[1, 0, 0.0, "No score"]""", T(update["trophy_shot"], update["score"], update["css"], update["score_text"]));
        Assert.False(paid);
        Assert.DoesNotContain(species, Facts.Default.Families.Keys);
        Assert.DoesNotContain(species, Facts.Default.Variants.Keys);
    }

    [Fact]
    public void TheEventsSilverAmmunitionIsPermittedOnEverySpeciesAndAnyOtherAmmunitionIsNot()
    {
        var (silver, paid) = Harvest(17, 1, 1142);  // a rabbit: the connect lists 10 only
        Assert.True(paid && Py.Eq(silver["trophy_shot"], 1));
        var (other, notPaid) = Harvest(17, 0, 11);
        Eq("""[0, "Disqualified ", -1]""", T(other["trophy_shot"], other["score_text"], other["score"]));
        Assert.False(notPaid);
    }

    [Fact]
    public void PheasantTurkeyAndSowScoreFromTheirTrophyEvent()
    {
        // pheasant: tail + spurs in inches; turkey: weight in pounds + 10 x spurs + 2 x beard; a sow: her weight, with no trophy event at all
        Eq("17.126", Scoring.Score(7, Scoring.Features(7, new JsonObject(), O("""{"event": 22, "tail": 400, "left_spur": 15000, "right_spur": 20000}"""), 1.0), 1618, null).Score);
        Eq("21.8896", Scoring.Score(2, Scoring.Features(2, new JsonObject(), O("""{"event": 22, "beard": 100000, "left_spur": 10000, "right_spur": 20000}"""), 1.0), 1000, null).Score);
        Eq("220.4623", Scoring.Score(10, Scoring.Features(10, new JsonObject(), new JsonObject(), 1.0), 100000, null).Score);
    }
}
