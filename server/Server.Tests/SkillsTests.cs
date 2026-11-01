using System.Text.Json.Nodes;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

public class SkillsTests
{
    static double V(double xp) => Py.D(Skills.Value(xp));

    [Fact]
    public void ValuesMatchKnownPairs()
    {
        // known pairs (Skill/list vs Statistics/total): 4 spots 1.54, 14 permitted .243 hits 2.437
        Assert.Equal(1.007, V(Skills.FRESH_XP));
        Assert.Equal(1.54, V(Skills.FRESH_XP + 8));
        Assert.Equal(2.437, V(Skills.FRESH_XP + 28));
        // crawl modes: 50 spots 4.168, 100 spots 5.668, 300 spots 9.445; the cap
        Assert.Equal([4.168, 5.668, 9.445], new[] { 50, 100, 300 }.Select(n => V(Skills.FRESH_XP + 2 * n)));
        Assert.Equal("20", Py.Dumps(Skills.Value(Skills.Threshold(20))));
        Assert.Equal("20", Py.Dumps(Skills.Value(1e6)));
        Assert.All(Enumerable.Range(0, 60), n => Assert.Equal(Py.Round(Skills.FRESH_XP + 2 * n, 1), Skills.Xp(V(Skills.FRESH_XP + 2 * n))));
    }

    [Fact]
    public void HitXp()
    {
        // mule deer; pheasant on the ground, flying
        Assert.Equal(2, Skills.HitXp(1, JsonValue.Create(3)));
        Assert.Equal(1, Skills.HitXp(7, JsonValue.Create(3)));
        Assert.Equal(2, Skills.HitXp(7, JsonValue.Create(6)));
    }

    [Fact]
    public void TrackXpMatchesRecordedHunt()
    {
        // one hunt, mule deer clues 16 on a buck + 2 on a doe, tracking 2.703 -> 2.983 (+8.4 XP)
        var clues = Enumerable.Range(1, 16).Select(n => Skills.TrackXp(n)).ToList();
        Assert.Equal(8.4, Py.Round(Scoring.Sum(clues) + Skills.TrackXp(1) + Skills.TrackXp(2), 1));
        Assert.Equal(2.703, V(Skills.FRESH_XP + 36));
        Assert.Equal(2.983, V(Skills.FRESH_XP + 36 + 8.4));
    }
}
