using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

public class LanguagesTests
{
    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("no", "nb")]
    [InlineData("nn", "nb")]
    [InlineData("nb-NO", "nb")]
    [InlineData("EN", "en")]
    [InlineData("zz", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MatchPicksTheLanguageOfATag(string? tag, string? code) => Assert.Equal(code, Languages.Match(tag)?.Code);

    [Fact]
    public void FindIgnoresCase() => Assert.Equal("fr", Languages.Find("FR")?.Code);

    [Fact]
    public void EveryLanguageHasItsFlagFile() =>
        Assert.All(Languages.All, l => Assert.True(File.Exists(Repo.Path("src", "Ui", "wwwroot", "flags", l.Flag + ".svg")), l.Code));

    // #930: the server lists the launcher's languages for a game's language (`Games.Languages`); the server project is not visible here, so its source is read
    [Fact]
    public void TheServersGameLanguagesAreTheLaunchersLanguages()
    {
        var source = File.ReadAllText(Repo.Path("server", "Server", "Multiplayer.cs"));
        var list = System.Text.RegularExpressions.Regex.Match(source, @"public static readonly string\[\] Languages =\s*\[(.*?)\];", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        var codes = System.Text.RegularExpressions.Regex.Matches(list, "\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        Assert.Equal(Languages.All.Select(l => l.Code).Order(StringComparer.Ordinal), codes.Order(StringComparer.Ordinal));
    }
}
