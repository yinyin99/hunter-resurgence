using System.Globalization;
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

    void Serve(Totals totals)
    {
        Stats.Answer = totals;
        Services.AddSingleton(DispatchProxy.Create<IProfileService, Stats>());
    }

    IReadOnlyList<string> MiscRows(Totals totals)
    {
        Serve(totals);
        var cut = Render<StatisticsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "lifetime"));
        // each row: its label, then the figures (a value, and its small label when there are two)
        return [.. cut.FindAll("table.misc td:first-child, table.misc .val, table.misc .what").Select(e => e.TextContent)];
    }

    /// <summary>The server sends the typical and the non-typical deer under their full names; the page translates each.</summary>
    [Fact]
    public void TheStatisticsTabTranslatesTheTypicalAndTheNonTypicalDeer()
    {
        Serve(new([], [new("Mule Deer (Typical)", 4, 3, 2), new("Mule Deer (Non-Typical)", 2, 1, 1)], [], 0, 0));
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var english = Render<StatisticsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "lifetime"));
            Assert.Equal(["Mule Deer (Typical)", "Mule Deer (Non-Typical)"], english.FindAll("table.tbl-c td:first-child").Select(e => e.TextContent));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            var german = Render<StatisticsTab>(p => p.Add(t => t.Handle, "h").Add(t => t.Sub, "lifetime"));
            Assert.Equal(["Maultierhirsch (typisch)", "Maultierhirsch (atypisch)"], german.FindAll("table.tbl-c td:first-child").Select(e => e.TextContent));
        }
        finally { CultureInfo.CurrentUICulture = ui; }
    }

    [Fact]
    public void TheStatisticsTabShowsTheTotalAndThePartOnThisServer()
    {
        // 903 h of hunting, 3 of them here; 2,005 km, 5 of them here
        var rows = MiscRows(new([], [], [], 2005, 903 * 3600, 5, 3 * 3600));

        Assert.Equal(["Distance Travelled", "2005.00 km (1245.85 miles)", "total", "5.00 km (3.11 miles)", "on Hunter Resurgence",
            "Hunt Duration", "37d 15h", "total", "3h", "on Hunter Resurgence"], rows);
    }

    [Fact]
    public void AnOlderServerThatSendsNoPartShowsTheTotalsOnly()
    {
        var rows = MiscRows(new([], [], [], 2005, 903 * 3600));

        Assert.Equal(["Distance Travelled", "2005.00 km (1245.85 miles)", "Hunt Duration", "37d 15h"], rows);
    }

    [Fact]
    public void AServerThatSendsOnlyOnePartShowsThatOneAndTheOtherTotalAlone()
    {
        var rows = MiscRows(new([], [], [], 2005, 903 * 3600, null, 3 * 3600));

        Assert.Equal(["Distance Travelled", "2005.00 km (1245.85 miles)", "Hunt Duration", "37d 15h", "total", "3h", "on Hunter Resurgence"], rows);
    }

    static void InEnglish(Action check)
    {
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-US", useUserOverride: false);  // the dates follow the culture
        try { check(); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before; }
    }

    const string Fixture = """
        {"reserves":[{"reserve":"Reserve A","hours":120.5,"gmPerHour":1234.6,"gmPerHarvest":310.4,"comparableGmPerHour":617.4,"comparableGmPerHarvest":155.2,"harvestsPerHour":5.84,"players":12},
                     {"reserve":"Reserve B","hours":60.25,"gmPerHour":800,"gmPerHarvest":200,"comparableGmPerHour":400,"comparableGmPerHarvest":100,"harvestsPerHour":4,"players":7}],
         "species":[{"species":"Fox","hours":40,"gmPerHour":900.6,"gmPerHarvest":150,"comparableGmPerHour":450.4,"comparableGmPerHarvest":75,"harvestsPerHour":6,"players":9,"bestReserve":"Reserve B"}],
         "since":1700000000,
         "events":[{"name":"weekend","kind":"gm_bonus","start":"2026-11-21T00:00:00Z","end":"2026-11-24T00:00:00Z","multiplier":2},{"name":"evening","kind":"gm_bonus","start":"2026-11-24T00:00:00Z","end":"2026-11-28T00:00:00Z","multiplier":1.25}]}
        """;

    IRenderedComponent<Leaderboards> Board(HttpStatusCode code, string body, out Server server)
    {
        server = new Server(code, body);
        Services.AddSingleton<ILeaderboardsService>(new ServerLeaderboardsService(new HttpClient(server) { BaseAddress = new Uri("http://server.test") }, new ServerCookie(), NullLogger<ServerLeaderboardsService>.Instance));
        return Render<Leaderboards>(p => p.Add(l => l.Path, "hunting"));
    }

    static string[] Row(AngleSharp.Dom.IElement tr) => [.. tr.Children.Select(c => c.TextContent)];

    [Fact]
    public void TheHuntingStatisticsAreTwoTablesOfTheServersBaseFiguresWithoutAnyHunter() => InEnglish(() =>
    {
        var cut = Board(HttpStatusCode.OK, Fixture, out var server);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".hunting table").Count));
        Assert.Equal("/stats/hunting", server.Path);
        Assert.Equal(["Per reserve", "Per species"], cut.FindAll(".hunting h4").Select(h => h.TextContent));
        var tables = cut.FindAll(".hunting table");
        Assert.Equal(["Reserve", "gm$ per hour", "gm$ per harvest", "Harvests per hour", "Hours hunted", "Hunters"], tables[0].QuerySelectorAll("thead th").Select(h => h.TextContent));
        var reserves = tables[0].QuerySelectorAll("tbody tr");
        Assert.Equal(["Reserve A", "617", "155", "5.8", "120.5", "12"], Row(reserves[0]));  // the comparable figures, not the paid ones; the best rate first
        Assert.Equal(["Reserve B", "400", "100", "4.0", "60.3", "7"], Row(reserves[1]));
        Assert.Equal(["Species", "gm$ per hour", "gm$ per harvest", "Harvests per hour", "Hours hunted", "Hunters", "Best reserve"], tables[1].QuerySelectorAll("thead th").Select(h => h.TextContent));
        Assert.Equal(["Fox", "450", "75", "6.0", "40.0", "9", "Reserve B"], Row(Assert.Single(tables[1].QuerySelectorAll("tbody tr"))));
        Assert.Equal(["width:100%", "width:64.8%"], reserves.Select(r => r.QuerySelector(".bar3d > i")!.GetAttribute("style")!.TrimEnd(';')));  // each bar is its share of the best
        Assert.All(cut.FindAll(".bar3d"), x => Assert.Equal("true", x.GetAttribute("aria-hidden")));  // the number beside a bar says it
        Assert.Empty(cut.FindAll(".norank"));
        Assert.Empty(cut.FindAll(".hunting a"));  // no hunter's name or profile link
    });

    [Fact]
    public void TheBonusEventsOfThePeriodAreListedInTheStripThatSaysTheFiguresAreAtTheBase() => InEnglish(() =>
    {
        var cut = Board(HttpStatusCode.OK, Fixture, out _);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".base")));
        Assert.Equal("gm$ at the normal rate, bonuses left out.", cut.Find(".base p").TextContent);
        Assert.Equal(["×2, 21–23 Nov", "×1.25, 24–27 Nov"], cut.FindAll(".base .events li").Select(x => x.TextContent));
    });

    [Fact]
    public void WithoutABonusEventTheStripHasNoChips() => InEnglish(() =>
    {
        var cut = Board(HttpStatusCode.OK, Fixture[..Fixture.IndexOf("\"events\"")].TrimEnd().TrimEnd(',') + "}", out _);

        cut.WaitForAssertion(() => Assert.Equal("gm$ at the normal rate, bonuses left out.", cut.Find(".base").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".events"));
    });

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
    public async Task SignedOutTheHuntingStatisticsAskTheServerNothing()
    {
        var session = new Resurgence.Services.Mocks.MockSessionService();  // signed in until it signs out
        await session.SignOutAsync();
        Services.AddSingleton<ISessionService>(session);
        var cut = Board(HttpStatusCode.OK, Fixture, out var server);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a.btn-primary")));
        Assert.Null(server.Path);
        Assert.Empty(cut.FindAll("table.stats"));
    }
}
