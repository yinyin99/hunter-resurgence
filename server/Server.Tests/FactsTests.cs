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

    const string Rule = """
        {"format": 1, "animals": ["a", "b", "c", "d"],
         "events": {"ev": {"ethical": {"ammo": [9], "species": [0, 1, 2], "formerly": [3]}, "creatures": [2], "coats": {"0": {"possessed": [5], "normal": [1]}, "1": {"normal": [3]}}}}}
        """;

    [Fact]
    public void TheRuleDataOfAnEventIsReadWhenItIsWellFormed() =>  // a missing list is an empty one
        Assert.Equal("ev", Facts.Parse(Rule).Proves(System.Text.Json.Nodes.JsonValue.Create(0), System.Text.Json.Nodes.JsonValue.Create(5), out _));

    [Theory]
    [InlineData("\"normal\": [1]", "\"normal\": [1, 5]", "events.ev.coats.0: coat 5 is in both lists")]
    [InlineData("\"species\": [0, 1, 2]", "\"species\": [1, 2]", "events.ev.coats.0: a species with a possessed version")]  // a coat entry for a species not in the event's list
    [InlineData("\"coats\": {", "\"coats\": {\"2\": {\"possessed\": [1], \"normal\": []}, ", "events.ev.coats.2: a species with a possessed version")]  // a creature has no coats
    [InlineData("\"creatures\": [2]", "\"creatures\": [\"x\"]", "events.ev.creatures: a list of whole ids")]
    [InlineData("\"creatures\": [2]", "\"creatures\": [7]", "events.ev.creatures: 7 is no species of `animals`")]
    [InlineData("\"species\": [0, 1, 2]", "\"species\": [0, 1, 2, 9]", "events.ev.ethical.species: 9 is no species of `animals`")]
    [InlineData("\"creatures\": [2]", "\"creatures\": [3]", "events.ev.creatures: 3 is not in ethical.species")]
    [InlineData("\"normal\": [3]", "\"normal\": []", "events.ev.coats.1: both lists empty")]
    [InlineData("\"ammo\": [9]", "\"ammo\": []", "events.ev.ethical.ammo: empty")]
    [InlineData("\"normal\": [1]", "\"normal\": [1.5]", "events.ev.coats.0.normal: a list of whole ids")]
    [InlineData("{\"0\": {", "{\"zero\": {", "events.ev.coats.zero: not a species id")]
    [InlineData("{\"0\": {", "{\"00\": {", "events.ev.coats.00: not a species id")]  // read as 0, but no lookup would find it
    [InlineData("\"1\": {\"normal\": [3]}", "\"1\": [3]", "events.ev.coats.1: an object")]
    [InlineData("\"formerly\": [3]", "\"formerly\": [9]", "events.ev.ethical.formerly: 9 is no species of `animals`")]
    public void ABadRuleEntryIsRefusedAtLoadNamingTheEntry(string from, string to, string message)
    {
        Assert.Contains(from, Rule);
        var e = Assert.Throws<InvalidDataException>(() => Facts.Parse(Rule.Replace(from, to)));
        Assert.Contains(message, e.Message);
    }

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
