using System.Text.Json.Nodes;
using Resurgence.Server;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Resurgence.Server.Tests;

public class PyTests
{
    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(33.0, "33.0")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1.2345678901234568e+17, "1.2345678901234568e+17")]
    [InlineData(-0.0, "-0.0")]
    public void Repr(double d, string expected) => Assert.Equal(expected, Py.Repr(d));

    [Theory]
    [InlineData(0.125, 2, "0.12")]
    [InlineData(0.375, 2, "0.38")]
    [InlineData(2.675, 2, "2.67")]
    [InlineData(-0.00001, 4, "-0.0000")]
    public void Fixed(double x, int n, string expected) => Assert.Equal(expected, Py.Fixed(x, n));

    [Fact]
    public void Dumps()
    {
        var o = new JsonObject { ["a"] = new JsonArray(1, 2.0, null, true), ["é"] = "x\n" };
        Assert.Equal("{\"a\": [1, 2.0, null, true], \"\\u00e9\": \"x\\n\"}", Py.Dumps(o));
        Assert.Equal("{\"a\":[1,2.0,null,true],\"\\u00e9\":\"x\\n\"}", Py.Dumps(o, compact: true));
        Assert.Equal("{\"x\": 1.5, \"y\": 100000.0}", Py.Dumps(JsonNode.Parse("{\"x\": 1.50, \"y\": 1E5}")));
    }

    [Fact]
    public void ParseQs()
    {
        var q = Py.ParseQs("a=1&b=&c&a=2+x%21");
        Assert.Equal(["a"], q.Keys);
        Assert.Equal(["1", "2 x!"], q["a"]);
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 2)]
    [InlineData(-0.5, 0)]
    public void RoundIntIsHalfToEven(double x, long expected) => Assert.Equal(expected, Py.RoundInt(x));

    [Fact]
    public void RoundKeepsTheBinaryValue()
    {
        Assert.Equal(2.67, Py.Round(2.675, 2));
        Assert.Equal(0.12, Py.Round(0.125, 2));
    }

    [Fact]
    public void DumpsEscapesLikePython()
    {
        Assert.Equal("\"\\ud83d\\ude00\\u007f\\u0001\"", Py.Dumps(JsonValue.Create("😀\u007f\u0001")));
        var big = "123456789012345678901234567890";
        Assert.Equal(big, Py.Dumps(JsonNode.Parse(big)));
    }

    [Fact]
    public void EqTruthyStrAndSql()
    {
        Assert.True(Py.Eq(JsonValue.Create(1), JsonValue.Create(1.0)));
        Assert.True(Py.Eq(JsonValue.Create(true), JsonValue.Create(1)));
        Assert.False(Py.Eq(JsonValue.Create("1"), JsonValue.Create(1)));
        foreach (var falsy in new[] { "0", "0.0", "\"\"", "[]", "{}", "null" })
            Assert.False(Py.Truthy(JsonNode.Parse(falsy)), falsy);
        Assert.Equal("True", Py.Str(JsonValue.Create(true)));
        Assert.Equal("None", Py.Str(null));
        Assert.Equal(1L, Py.Sql(JsonValue.Create(true)));
        Assert.Throws<OverflowException>(() => Py.Sql(JsonNode.Parse("123456789012345678901234567890")));
    }

    [Fact]
    public void ParseQsKeepsBlanksOnRequest()
    {
        var q = Py.ParseQs("a=&b", keepBlank: true);
        Assert.Equal([""], q["a"]);
        Assert.Equal([""], q["b"]);
    }
}
