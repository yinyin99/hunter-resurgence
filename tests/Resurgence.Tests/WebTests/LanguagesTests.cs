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
}
