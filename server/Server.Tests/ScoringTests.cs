using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

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
            Scoring.Load(O("""{"game_web_host": "web.test"}"""), Path.Combine(tmp, "missing.json"));  // no file: an empty table
            Assert.Empty(Scoring.Variants);
            Assert.Equal(0.0, Scoring.Css(1, System.Text.Json.Nodes.JsonValue.Create(168.8186)));
            Scoring.Variants = new() { [1] = (30, 30, 1.1) };  // max == min: no division by zero
            Assert.Equal(0.0, Scoring.Css(1, System.Text.Json.Nodes.JsonValue.Create(100.0)));
        }
        finally
        {
            (Scoring.Variants, Scoring.StaticHost) = (variants, host);
        }
    }
}
