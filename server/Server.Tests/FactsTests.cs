using Resurgence.Server;

namespace Resurgence.Server.Tests;

public class FactsTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"format":2}""")]
    [InlineData("""{"format":"1"}""")]
    [InlineData("""{"format":1,"ranks":{"cap":1,"rows":[{"category":"x"}]}}""")]
    public void AFileOfTheWrongShapeIsRefusedWithItsMessage(string json) =>
        Assert.Throws<InvalidDataException>(() => Facts.Parse(json));

    [Fact]
    public void TheShippedRankTableIsUsable()
    {
        var facts = Facts.Default;
        Assert.True(facts.RankCap > 0);
        Assert.NotEmpty(facts.Ranks);
        foreach (var r in facts.Ranks)
        {
            Assert.NotEmpty(r.Ids);
            if (r.Category == "animal")
                Assert.True(r.Ids[0] < facts.Animals.Length);
        }
    }
}
