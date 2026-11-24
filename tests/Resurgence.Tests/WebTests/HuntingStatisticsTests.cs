using System.Net;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>The playtime and the distance of the Statistics tab (the total and the part on this server), and the server-wide "Hunting statistics" board.</summary>
public sealed class HuntingStatisticsTests : UiContext
{
    /// <summary>An IProfileService that answers only TotalsAsync, with what the test sets.</summary>
    public class Stats : DispatchProxy
    {
        public static Totals Answer = new([], [], [], 0, 0);
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m!.Name != nameof(IProfileService.TotalsAsync) ? throw new NotSupportedException(m.Name) : Task.FromResult(Answer);
    }

    sealed class Server(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    IReadOnlyList<string> MiscRows(Totals totals)
    {
        Stats.Answer = totals;
        Services.AddSingleton(DispatchProxy.Create<IProfileService, Stats>());
        var cut = Render<StatisticsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "lifetime"));
        return [.. cut.FindAll("table.misc td").Select(td => td.TextContent)];
    }

    [Fact]
    public void TheStatisticsTabShowsTheTotalAndThePartOnThisServer()
    {
        // 903 h of hunting, 3 of them here; 2,005 km, 5 of them here
        var rows = MiscRows(new([], [], [], 2005, 903 * 3600, 5, 3 * 3600));

        Assert.Equal(["Distance Travelled", "2005.00 km (1245.85 miles) total · 5.00 km (3.11 miles) on Hunter Resurgence",
            "Hunt Duration", "37d 15h 0m 00s total · 3h 0m 00s on Hunter Resurgence"], rows);
    }

    [Fact]
    public void AnOlderServerThatSendsNoPartShowsTheTotalsOnly()
    {
        var rows = MiscRows(new([], [], [], 2005, 903 * 3600));

        Assert.Equal(["Distance Travelled", "2005.00 km (1245.85 miles)", "Hunt Duration", "37d 15h 0m 00s"], rows);
    }

    [Fact]
    public void AServerThatSendsOnlyOnePartShowsThatOneAndTheOtherTotalAlone()
    {
        var rows = MiscRows(new([], [], [], 2005, 903 * 3600, null, 3 * 3600));

        Assert.Equal(["Distance Travelled", "2005.00 km (1245.85 miles)", "Hunt Duration", "37d 15h 0m 00s total · 3h 0m 00s on Hunter Resurgence"], rows);
    }

    const string Fixture = """
        {"reserves":[{"reserve":"Reserve A","hours":120.5,"gmPerHour":1234.6,"gmPerHarvest":310.4,"harvestsPerHour":5.84,"players":12},
                     {"reserve":"Reserve B","hours":60.25,"gmPerHour":800,"gmPerHarvest":200,"harvestsPerHour":4,"players":7}],
         "species":[{"species":"Fox","hours":40,"gmPerHour":900.6,"gmPerHarvest":150,"harvestsPerHour":6,"players":9,"bestReserve":"Reserve B"}],
         "since":1700000000}
        """;

    IRenderedComponent<Leaderboards> Board(HttpStatusCode code, string body, out Server server)
    {
        server = new Server(code, body);
        Services.AddSingleton<ILeaderboardsService>(new ServerLeaderboardsService(new HttpClient(server) { BaseAddress = new Uri("http://server.test") }, new ServerCookie(), NullLogger<ServerLeaderboardsService>.Instance));
        return Render<Leaderboards>(p => p.Add(l => l.Path, "hunting"));
    }

    [Fact]
    public void TheHuntingStatisticsAreTwoTablesOfTheServersFiguresWithoutAnyHunter()
    {
        var cut = Board(HttpStatusCode.OK, Fixture, out var server);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("table.stats").Count));
        Assert.Equal("/stats/hunting", server.Path);
        var tables = cut.FindAll("table.stats");
        Assert.Equal(["Reserve", "Hours hunted", "gm$ per hour", "gm$ per harvest", "Harvests per hour", "Hunters"], tables[0].QuerySelectorAll("th").Select(h => h.TextContent));
        Assert.Equal(["Reserve A", "120.5", "1235", "310", "5.8", "12"], tables[0].QuerySelectorAll("tbody tr")[0].QuerySelectorAll("td").Select(c => c.TextContent));
        Assert.Equal(2, tables[0].QuerySelectorAll("tbody tr").Length);
        Assert.Equal(["Species", "Hours hunted", "gm$ per hour", "gm$ per harvest", "Harvests per hour", "Hunters", "Best reserve"], tables[1].QuerySelectorAll("th").Select(h => h.TextContent));
        Assert.Equal(["Fox", "40.0", "901", "150", "6.0", "9", "Reserve B"], tables[1].QuerySelectorAll("tbody tr")[0].QuerySelectorAll("td").Select(c => c.TextContent));
        Assert.Empty(cut.FindAll(".norank"));
        Assert.Empty(cut.FindAll("table.stats a"));  // no hunter's name or profile link
    }

    [Fact]
    public void WithoutAnyFigureTheBoardSaysSo()
    {
        var cut = Board(HttpStatusCode.OK, """{"reserves":[],"species":[]}""", out _);

        cut.WaitForAssertion(() => Assert.Equal("No hunting statistics yet.", cut.Find(".norank").TextContent));
        Assert.Empty(cut.FindAll("table.stats"));
    }

    [Fact]
    public void AServerThatDoesNotAnswerIsATryAgainNotAnEmptyBoard()
    {
        var cut = Board(HttpStatusCode.InternalServerError, "{}", out _);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("p[role=alert]")));
        Assert.Empty(cut.FindAll("table.stats"));
    }

    [Fact]
    public void TheHuntingStatisticsAreATabOfTheBoards()
    {
        var cut = Render<Leaderboards>(p => p.Add(l => l.Path, "score"));

        Assert.Contains("/leaderboards/hunting", cut.FindAll(".tabs a").Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public void SignedOutTheHuntingStatisticsAskTheServerNothing()
    {
        Services.AddSingleton<ISessionService>(new Resurgence.Services.Mocks.MockSessionService(() => null));
        var cut = Board(HttpStatusCode.OK, Fixture, out var server);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a.btn-primary")));
        Assert.Null(server.Path);
        Assert.Empty(cut.FindAll("table.stats"));
    }
}
