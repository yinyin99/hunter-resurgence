using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Every species the game can harvest, shot ethically, gets its score and its gm$ from the facts file's tables alone (made-up measurements, no variants file).</summary>
[Collection("ProcessState")]  // Scoring.Load replaces the process-wide variant table with the facts file's
public class HarvestScoreTests : StateTestBase
{
    // families whose female has no antlers: a doe scores nothing, as the game does
    static readonly HashSet<string> MaleOnly = ["bc_deer", "elk", "moose", "cic_roe", "cic_red", "cic_fallow", "tines"];
    const int Ammo = 10;
    double gm = 33;  // CONNECT's balance
    int seed;

    public HarvestScoreTests()
    {
        var connect = CONNECT;
        connect["species"] = new JsonArray(Enumerable.Range(0, 59).Select(i => (JsonNode?)new JsonObject { ["scale_factor"] = 1.0, ["variant_id"] = i }).ToArray());
        connect["ethical"] = new JsonArray(Enumerable.Range(0, 59).Select(_ => (JsonNode?)new JsonArray(Ammo)).ToArray());
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES);
        Scoring.Load(O("""{"game_web_host": "static.test"}"""), null);
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
    }

    public override void Dispose()
    {
        base.Dispose();
        PinScoring();
    }

    static JsonObject Antler() => O("""
        {"circumference_h1": 200, "circumference_h2": 180, "circumference_h3": 160, "circumference_h4": 140, "palm_length": 300, "palm_width": 200,
         "brow_palm_width": 100, "top_palm_width": 100, "top_palm_length": 100, "nof_points_excluding_brows": 6, "nof_brow_points": 2,
         "points": [{"length": 60000, "type": 0}, {"length": 300, "type": 1}, {"length": 300, "type": 2}, {"length": 300, "type": 3}]}
        """);

    /// <summary>One harvest of the species and gender: the kill's update 6 and whether the gm$ balance rose.</summary>
    (JsonNode Update, bool Paid) Harvest(int species, int gender, int ammo = Ammo, int? individual = null)
    {
        var ind = individual ?? 1000 + species * 2 + gender;
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
        events.Add(O($$"""{"event": 3, "individual": {{ind}}, "extended_species": {{species}}, "antlers_seed": {{++seed}} }"""));  // a seed makes each kill's event 3 unique, as a real one is
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

    // the confirm-kill screen shows round(css) of update 6 as the reward: a bonus must show there as it is paid, and stop at its end
    [Fact]
    public void AHarvestGmBonusDoublesTheShownRewardAndThePaymentUntilItsEnd()
    {
        var start = gm;
        var normal = Py.D(Harvest(0, 0).Update["css"]);
        Assert.Equal(Py.RoundInt(normal), gm - start);
        state.HarvestGmBonus = (2, state.Clock() + 3600);
        start = gm;
        var doubled = Py.D(Harvest(0, 0).Update["css"]);
        Assert.Equal(Py.Round(normal * 2, 1), doubled);
        Assert.Equal(Py.RoundInt(doubled), gm - start);
        state.HarvestGmBonus = (2, state.Clock() - 1);
        start = gm;
        Assert.Equal(normal, Py.D(Harvest(0, 0).Update["css"]));
        Assert.Equal(Py.RoundInt(normal), gm - start);
    }

    (double Amount, double? Base, long? Event) LastReward()
    {
        using var con = state.Store.Open(ME);
        var r = con.Rows("SELECT amount, base, bonus_event FROM wallet_log WHERE account_id = $a AND summary LIKE 'CSS Reward%' ORDER BY n DESC LIMIT 1")[0];
        return (Convert.ToDouble(r[0]), r[1] is double b ? b : null, r[2] is long e ? e : null);
    }

    // docs/server.md#reward-bases-and-events: a payment keeps what it would have been without the bonus and the event that changed it
    [Fact]
    public void AHarvestPaidUnderABonusRecordsItsBaseAndItsEventAndOutsideOneTheBaseIsTheAmount()
    {
        Harvest(0, 0);
        var (plain, plainBase, plainEvent) = LastReward();
        Assert.Equal(plain, plainBase);
        Assert.Null(plainEvent);
        state.HarvestGmBonus = (2, state.Clock() + 3600);
        Harvest(0, 0);
        var (doubled, doubledBase, doubledEvent) = LastReward();
        Assert.Equal(plain, doubledBase);
        Assert.InRange(doubled, 2 * plain - 1, 2 * plain + 1);  // the shown css is rounded to a decimal first, so a small amount can be off by one
        using (var con = state.Store.Open())
            Assert.Equal(doubledEvent, con.Scalar("SELECT id FROM events_log WHERE kind = 'gm_bonus' AND applies_to = 'harvest_gm' AND multiplier = 2") as long?);
        state.HarvestGmBonus = (2, state.Clock() - 1);
        Harvest(0, 0);
        var (after, afterBase, afterEvent) = LastReward();
        Assert.Equal((plain, (double?)plain, (long?)null), (after, afterBase, afterEvent));
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

    [Fact]
    public void ElkCrownPointsAddTheirLengthInInchesToTheScoreAndSoToTheGms()
    {
        double Total(string? crown)
        {
            var left = Antler();
            if (crown is not null) left["crown_points"] = new JsonArray(O(crown));
            var e22 = O("""{"event": 22, "greatest_spread": 900, "tip_to_tip_spread": 800, "inside_spread_of_main_beams": 700}""");
            (e22["left_antler"], e22["right_antler"]) = (left, Antler());
            return Py.D(Scoring.Score(3, Scoring.Features(3, new JsonObject(), e22, 1.0), 200000, null).Score);
        }
        var (with, without) = (Total("""{"length": 1270}"""), Total(null));
        Assert.Equal(50.0, Math.Round(with - without, 4));  // 1270 mm = 50 in
        Assert.True(Py.RoundInt(Scoring.Css(3, with)) > Py.RoundInt(Scoring.Css(3, without)));  // the gm$ paid is round(css)
    }

    [Fact]
    public void RescoreScoresAndPaysAnUnpaidHarvestOnceAndLeavesARefusedOneAndADryRunWritesNothing()
    {
        Harvest(17, 1);          // scorable
        Harvest(17, 0, 11);      // disqualified: ammunition the connect does not list
        using (var con = state.Store.Open(ME))  // as a harvest made before the scoring fix: no score, no gm$
        {
            con.Exec("UPDATE harvests SET body = json_set(body, '$.score', 0, '$.score_type', '') WHERE account_id = $a");
            con.Exec("DELETE FROM wallet_log WHERE account_id = $a AND summary LIKE 'CSS Reward%'");
        }
        var before = state.Wallet(ME)["gm"]!.GetValue<double>();
        string Pages() { using var con = state.Store.Open(ME); return string.Join("|", con.Rows("SELECT body FROM harvests WHERE account_id = $a ORDER BY kill_id").Select(r => (string)r[0])); }
        var pages = Pages();

        var (_, rescored, paid, gms, _) = Assert.Single(state.Rescore(ME, dryRun: true));
        Assert.True(rescored == 1 && paid == 1 && gms > 0);
        Assert.True(pages == Pages() && before == state.Wallet(ME)["gm"]!.GetValue<double>());

        Assert.Equal((rescored, paid, gms), Assert.Single(state.Rescore(ME, dryRun: false)) is var (_, r, p, g, _) ? (r, p, g) : default);
        Assert.Equal(before + gms, state.Wallet(ME)["gm"]!.GetValue<double>());
        Assert.NotEqual(pages, Pages());
        using (var con = state.Store.Open(ME))
            Assert.Equal(1L, con.Scalar("SELECT COUNT(*) FROM wallet_log WHERE account_id = $a AND summary LIKE 'CSS Reward (Animal Id: %'"));
        var again = Pages();
        var second = Assert.Single(state.Rescore(ME, dryRun: false));
        Assert.Equal((0, 0, 0L), (second.Rescored, second.Paid, second.Gms));
        Assert.Equal(again, Pages());
    }

    [Fact]
    public void ADoeWithTheIndividualIdOfAnEarlierBuckHasNoMeasurementsAndRescoreZeroesAHarvestThatTookThem()
    {
        var sp = Facts.Default.Families.First(f => f.Value == "bc_deer").Key;
        var ind = 1000 + sp * 2;
        var (buck, _) = Harvest(sp, 0);
        Assert.True(Py.D(buck["score"]) > 0);
        var (doe, paid) = Harvest(sp, 1, individual: ind);  // the id is reused in a later hunt; a doe sends no event 22
        Eq("""[0, "No score", false]""", T(doe["score"], doe["score_text"], paid));

        // a doe scored before the fix took the buck's antlers: a stored score above 0 that was paid
        using (var con = state.Store.Open(ME))
            con.Exec("UPDATE harvests SET body = json_set(body, '$.score', 500, '$.score_type', 'typical') WHERE account_id = $a AND kill_id = (SELECT MAX(kill_id) FROM kills WHERE account_id = $a)");
        using (var con = state.Store.Open(ME))  // the doe's reward, as it was paid
            con.Exec("INSERT INTO wallet_log (account_id, ts, currency, amount, after, summary, item_ids) SELECT $a, 0, 'gm', 123, 0, 'CSS Reward (Animal Id: ' || MAX(kill_id) || ')', '[]' FROM kills WHERE account_id = $a");
        var wallet = state.Wallet(ME)["gm"]!.GetValue<double>();
        var killed = state.Rescore(ME, dryRun: true).Single();
        var one = Assert.Single(killed.Harvests);
        Assert.Equal((sp, 500.0, 0.0, 123L), (one.Species, one.OldScore, one.NewScore, one.GmsPaid));
        Assert.Empty(state.Rescore(ME, dryRun: true, onlyKill: 1).Single().Harvests);  // one kill alone: the buck is not the one
        Assert.Single(state.Rescore(ME, dryRun: true, onlyKill: one.Kill).Single().Harvests);
        Assert.Equal(0, state.Rescore(ME, dryRun: false).Single().Paid);
        Assert.Empty(state.Rescore(ME, dryRun: false).Single().Harvests);  // the page now says 0
        Assert.Equal(wallet, state.Wallet(ME)["gm"]!.GetValue<double>());  // nothing paid is taken back
    }
}
