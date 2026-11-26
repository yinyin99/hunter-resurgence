using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

[Collection("ProcessState")]  // Scoring.Load replaces the process-wide config
public class ScoringTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("scoring").FullName;
    public void Dispose() => DeleteDir(tmp);

    [Fact]
    public void TestLoadReadsTheVariantBoundsAndCssFollowsThem()
    {
        var (variants, host) = (Scoring.Variants, Scoring.StaticHost);
        try
        {
            var file = Path.Combine(tmp, "variants.json");
            File.WriteAllText(file, """[{"id":1,"min_score":30,"max_score":260,"css_multiplier":1.1}]""");
            Scoring.Load(O("""{"game_web_host": "web.test"}"""), file);
            Assert.Equal("web.test", Scoring.StaticHost);
            Assert.Equal((30.0, 260.0, 1.1), Scoring.Variants[1]);
            Assert.Equal(66.4, Scoring.Css(1, System.Text.Json.Nodes.JsonValue.Create(168.8186)));
            File.WriteAllText(file, """[{"id":50,"min_score":100,"max_score":200,"css_multiplier":2}]""");
            Scoring.Load(O("""{"game_web_host": "web.test"}"""), file);
            Assert.Equal((100.0, 200.0, 2.0), Scoring.Variants[50]);  // the file's own row wins over the facts file's
            Scoring.Load(O("""{"game_web_host": "web.test"}"""), Path.Combine(tmp, "missing.json"));  // no file: the facts file's bounds of every species
            Assert.Equal(Facts.Default.Variants.Keys.Order(), Scoring.Variants.Keys.Order());
            Assert.Equal((190.0, 390.0, 1.0), Scoring.Variants[50]);
            Assert.Equal(79.0, Scoring.Css(1, System.Text.Json.Nodes.JsonValue.Create(168.8186)));  // 1.2 x 100 x (168.8186 - 20) / 226
            Scoring.Variants = new() { [1] = (30, 30, 1.1) };  // max == min: no division by zero
            Assert.Equal(0.0, Scoring.Css(1, System.Text.Json.Nodes.JsonValue.Create(100.0)));
        }
        finally
        {
            (Scoring.Variants, Scoring.StaticHost) = (variants, host);
        }
    }
}

/// <summary>Recorded score sheets (score-vectors.json: measurements, weight, last hit's animal state, the original's score and score type; numbers only): every one scores as the original scored it.</summary>
public class ScoreVectorTests
{
    // the sheets drop precision the server keeps
    static readonly Dictionary<string, double> Tolerance = new()
    {
        ["weight_mg"] = 0.001, ["weight_kg"] = 0.001, ["weight_g"] = 1.0,
        ["caribou"] = 0.001, ["cic_horn"] = 0.001, ["cic_roe"] = 0.001, ["moose"] = 0.001, ["sheep"] = 0.001,
    };

    [Fact]
    public void EveryRecordedSheetScoresAsTheOriginalDid()
    {
        var rows = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "score-vectors.json")))!.AsArray();
        var families = Facts.Default.Families;
        List<string> bad = [];
        HashSet<int> males = [], females = [];
        foreach (var r in rows)
        {
            var (species, gender) = ((int)r!["species"]!, (int)r["gender"]!);
            (gender == 0 ? males : females).Add(species);
            var (score, type) = Scoring.Score(species, r["features"], r["weight"], r["state"]);
            var want = Math.Round((double)r["score"]!, 4);
            if (score is null || Math.Abs(Math.Round(Py.D(score), 4) - want) > Tolerance.GetValueOrDefault(families[species]) + 1e-9 || type != (string?)r["type"])
                bad.Add($"species {species} ({families[species]}), gender {gender}: want {want} {r["type"]}, got {score} {type}");
        }
        bad.AddRange(families.Keys.Except(males).Order().Select(s => $"species {s} ({families[s]}): no male sheet"));
        TestContext.Current.TestOutputHelper?.WriteLine($"{rows.Count} sheets: {males.Count} of {families.Count} species with a male row, {females.Count} with a female row");
        Assert.True(bad.Count == 0, $"{bad.Count} of {rows.Count}:\n" + string.Join("\n", bad));
        Assert.Equal(35, females.Count);  // no sheet: the 17 females without a score, and the pheasant, wild boar, Dall sheep, water buffalo and roebit
    }
}
