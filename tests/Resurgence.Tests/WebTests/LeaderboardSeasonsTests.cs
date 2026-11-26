using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
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
    /// <summary>The server sends the species in id order; the page lists them A to Z.</summary>
    sealed class Unsorted : ILeaderboardsService
    {
        static readonly string[] Names = ["Whitetail Deer", "Alpine Ibex", "Mallard", "Bison"];
        static readonly Ranked One = new(1, "a", 1);
        public Task<(int Current, IReadOnlyList<Season> Seasons)> SeasonsAsync() =>
            Task.FromResult<(int, IReadOnlyList<Season>)>((2, [new(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1)), new(2, DateTimeOffset.UnixEpoch.AddDays(1), null)]));
        public Task<IReadOnlyList<Board>> BoardsAsync(string kind, int season) => Task.FromResult<IReadOnlyList<Board>>([.. Names.Select((n, i) => new Board(i, n, [One]))]);
        public Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season) => Task.FromResult(new Page<Ranked>([], 1, 1));
        public Task<Ranked?> MyRankAsync(string kind, int id, int season) => Task.FromResult<Ranked?>(null);
        public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>([.. Names.Select(n => new HallEntry(n, One, One))]);
        public Task<HuntingStats> HuntingAsync() => Task.FromResult(new HuntingStats([], [], []));
    }

    /// <summary>The typical and the non-typical rack of a deer are two boards of the server, named apart.</summary>
    sealed class TwoDeer : ILeaderboardsService
    {
        static readonly Ranked One = new(1, "a", 1);
        public Task<(int Current, IReadOnlyList<Season> Seasons)> SeasonsAsync() => new Unsorted().SeasonsAsync();
        public Task<IReadOnlyList<Board>> BoardsAsync(string kind, int season) =>
            Task.FromResult<IReadOnlyList<Board>>([new Board(1, "Mule Deer (Typical)", [One]), new Board(101, "Mule Deer (Non-Typical)", [One])]);
        public Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season) => Task.FromResult(new Page<Ranked>([One], 1, 1));
        public Task<Ranked?> MyRankAsync(string kind, int id, int season) => Task.FromResult<Ranked?>(null);
        public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>([]);
        public Task<HuntingStats> HuntingAsync() => Task.FromResult(new HuntingStats([], [], []));
    }

    /// <summary>A service whose boards come when the test says, or fail: the page's states while it waits and after.</summary>
    sealed class Waiting : ILeaderboardsService
    {
        public TaskCompletionSource<IReadOnlyList<Board>> Answer { get; private set; } = new();
        public int Asked { get; private set; }
        public Task<(int Current, IReadOnlyList<Season> Seasons)> SeasonsAsync() => new Unsorted().SeasonsAsync();
        public Task<IReadOnlyList<Board>> BoardsAsync(string kind, int season)
        {
            Asked++;
            return Answer.Task;
        }
        public Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season) => Task.FromResult(new Page<Ranked>([], 1, 1));
        public Task<Ranked?> MyRankAsync(string kind, int id, int season) => Task.FromResult<Ranked?>(null);
        public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>([]);
        public Task<HuntingStats> HuntingAsync() => Task.FromResult(new HuntingStats([], [], []));
        public void Again() => Answer = new();
    }

    [Fact]
    public void ABoardShowsLoadingWhileTheServerIsComputingThenItsRows()
    {
        var service = new Waiting();
        Services.AddSingleton<ILeaderboardsService>(service);
        var cut = Board("score");

        Assert.NotEmpty(cut.FindAll(".skeleton"));
        Assert.Empty(cut.FindAll("p.norank"));  // not "Nobody is ranked here yet": nothing has answered
        Assert.Empty(cut.FindAll("section.tile"));
        service.Answer.SetResult([new Board(1, "Mallard", [new Ranked(1, "a", 1)])]);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("section.tile")));
        Assert.Empty(cut.FindAll(".skeleton"));
    }

    [Fact]
    public void AFailedLoadShowsTheErrorAndNotTheEmptyTextAndRetryLoadsIt()
    {
        var service = new Waiting();
        Services.AddSingleton<ILeaderboardsService>(service);
        var cut = Board("score");
        service.Answer.SetException(new BoardUnavailableException("timed out"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=alert]")));

        Assert.Contains("could not be loaded", cut.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("Nobody is ranked", cut.Markup);
        Assert.Empty(cut.FindAll("section.tile"));

        service.Again();
        cut.Find("button.btn").Click();
        Assert.NotEmpty(cut.FindAll(".skeleton"));
        Assert.Empty(cut.FindAll("[role=alert]"));
        service.Answer.SetResult([new Board(1, "Mallard", [new Ranked(1, "a", 1)])]);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("section.tile")));
        Assert.Equal(2, service.Asked);
    }

    [Fact]
    public void AnEmptyBoardTheServerAnsweredStillSaysNobodyIsRanked()
    {
        var service = new Waiting();
        Services.AddSingleton<ILeaderboardsService>(service);
        var cut = Board("score");
        service.Answer.SetResult([]);

        cut.WaitForAssertion(() => Assert.Equal("Nobody is ranked here yet", cut.Find("p.norank").TextContent.Trim()));
    }

    [Theory]
    [InlineData("score", "section.tile h4")]
    [InlineData("halloffame", ".hof h4")]
    public void TheSpeciesAreListedAToZ(string path, string names)
    {
        Services.AddSingleton<ILeaderboardsService>(new Unsorted());
        var cut = Board(path);

        Assert.Equal(["Alpine Ibex", "Bison", "Mallard", "Whitetail Deer"], cut.FindAll(names).Select(h => h.TextContent.Trim()));
    }

    [Fact]
    public void TheTypicalAndTheNonTypicalBoardsAreTwoEntriesOfTheSpeciesList()
    {
        Services.AddSingleton<ILeaderboardsService>(new TwoDeer());
        var cut = Board("score");

        Assert.Equal(["Mule Deer (Non-Typical)", "Mule Deer (Typical)"], cut.FindAll("section.tile h4").Select(h => h.TextContent.Trim()));
        Assert.Equal(["/leaderboards/score/101", "/leaderboards/score/1"], cut.FindAll("a.btn.full").Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public void AChosenBoardIsKeptWhenTheSeasonChanges()
    {
        Services.AddSingleton<ILeaderboardsService>(new TwoDeer());
        var cut = Board("score/101", 1);

        Assert.Equal("Mule Deer (Non-Typical)", cut.Find("h3").TextContent.Trim());
        cut.Find("select#leaderboard-season-selector").Change("0");

        Assert.EndsWith("/leaderboards/score/101?season=0", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public async Task SignedOutABoardAsksForASignInAndReadsNothing()
    {
        var service = new Waiting();
        Services.AddSingleton<ILeaderboardsService>(service);
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        var cut = Board("score");

        Assert.Equal("Sign in to see this page", cut.FindAll("h2").Last().TextContent.Trim());
        Assert.Equal(0, service.Asked);
        Assert.Empty(cut.FindAll(".skeleton"));
    }

    [Fact]
    public void ABoardIsItsTop100ThenThePlayersOwnPlaceWith50AboveAndBelow()
    {
        var cut = Board("bustthrough");  // the mock player is 96th

        Assert.Equal(["Your Rank", "Top 100", "Around your rank"], cut.FindAll(".rank-label").Select(l => l.TextContent.Trim()));
        var around = cut.FindAll("table.board").Last().QuerySelectorAll("tbody tr");
        Assert.Equal(46, around.Length);  // 101 to 146: the places of the top are not repeated
        Assert.StartsWith("101", around[0].TextContent.Trim());
        Assert.Equal(4, cut.FindAll("nav.pager a").Count(a => int.TryParse(a.TextContent, out _)));  // 100 places, 30 a page
    }
}
