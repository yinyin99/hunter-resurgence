using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The species boards and the hall of fame answer for a season: the current one (2) by default, a selector for all time (0) and each season.</summary>
public sealed class LeaderboardSeasonsTests : UiContext
{
    // a query parameter comes from the address
    IRenderedComponent<Leaderboards> Board(string path, int? season = null)
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/leaderboards/" + path + (season is { } n ? "?season=" + n : ""));
        return Render<Leaderboards>(p => p.Add(l => l.Path, path));
    }

    string[] Options(IRenderedComponent<Leaderboards> cut) => cut.FindAll("select#leaderboard-season-selector option").Select(o => o.GetAttribute("value")!).ToArray();

    [Fact]
    public void ABoardShowsTheCurrentSeasonByDefaultAndOffersAllTime()
    {
        var cut = Board("score");

        Assert.Equal(["0", "1", "2"], Options(cut));
        Assert.Equal("2", cut.Find("select#leaderboard-season-selector option[selected]").GetAttribute("value"));
        Assert.Contains("Season 2", cut.Find("h4").TextContent);
        Assert.All(cut.FindAll("a.btn.full"), a => Assert.DoesNotContain("season=", a.GetAttribute("href")));  // the current season is the address without one
    }

    [Theory]
    [InlineData(0, "all time")]
    [InlineData(1, "Season 1")]
    public void AnotherSeasonIsTheAddressesQueryAndItsLinksKeepIt(int season, string title)
    {
        var cut = Board("range", season);

        Assert.Contains(title, cut.Find("h4").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(season.ToString(), cut.Find("select#leaderboard-season-selector option[selected]").GetAttribute("value"));
        Assert.All(cut.FindAll("a.btn.full"), a => Assert.EndsWith("?season=" + season, a.GetAttribute("href")));
    }

    [Fact]
    public void ASpeciesBoardIsTheTop100WithTheWeaponAndTheDate()
    {
        var cut = Board("score/1", 1);

        Assert.Equal(25, cut.FindAll("table.board:not(.me) tbody tr").Count);
        Assert.Equal(["Rank", "", "Hunter", "Score", "Weapon", "Time of Harvest"], cut.FindAll("table.board thead th").Select(h => h.TextContent));
        var first = cut.FindAll("table.board:not(.me) tbody tr")[0];
        Assert.NotEmpty(first.QuerySelector("td.weapon")!.TextContent);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d$", first.QuerySelector("td.when")!.TextContent);
        Assert.Contains("season=1", cut.FindAll("nav.pager a").Last().GetAttribute("href"));
    }

    [Fact]
    public void TheHallOfFameIsTheLastEndedSeasonsAndNotOfferedForAllTimeOrTheCurrentSeason()
    {
        var cut = Board("halloffame");

        Assert.Equal(["1"], Options(cut));
        Assert.NotEmpty(cut.FindAll(".hof"));
        var empty = Board("halloffame", 2);
        Assert.Empty(empty.FindAll(".hof"));
        Assert.Single(empty.FindAll("p.norank"));
    }
}
