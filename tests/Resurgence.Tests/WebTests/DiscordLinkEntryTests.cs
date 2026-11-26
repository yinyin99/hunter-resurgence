using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages.SignIn;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>The entry points of Discord Linked Roles (the feed's card, the pill on every page, the Settings menu item) and the result page's role and progress
/// (docs/website.md#discord-linked-roles).</summary>
public sealed class DiscordLinkEntryTests : UiContext
{
    sealed class Sessions(Session? session, string? url) : ISessionService
    {
        public Task<Session?> CurrentAsync() => Task.FromResult(session);
        public event Action? Changed { add { } remove { } }
        public string? DiscordLinkUrl => url;
        public Task<Result> RequestCodeAsync(string email) => throw new NotSupportedException();
        public Task<Result> SignInAsync(string email, string code, bool register = false) => throw new NotSupportedException();
        public Task<Result> SetHandleAsync(string handle) => throw new NotSupportedException();
        public Task<StartOutcome> StartAsync() => throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<bool> LicenseAcceptedAsync() => Task.FromResult(true);
        public Task AcceptLicenseAsync() => Task.CompletedTask;
    }

    /// <summary>What the launcher's bridge was asked to open.</summary>
    sealed class Opened : ILogger<MockLauncherBridge>
    {
        public List<string> Urls { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) is var line && line.StartsWith("open https://")) Urls.Add(line[5..]);
        }
    }

    const string Path = "/discord/link";

    Session? Use(Session? session, string? url = Path)
    {
        Services.AddSingleton<ISessionService>(new Sessions(session, url));
        return session;
    }

    string Text(string key) => Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Strings>>()[key].Value;

    /// <summary>How many of the three entry points show: the card, the pill and the menu item.</summary>
    (int Card, int Pill, int Item) Entries(Session? session)
    {
        var card = Render<DiscordLinkCard>().FindAll(".card").Count;
        var pill = Render<DiscordPill>(p => p.Add(x => x.Session, session)).FindAll("a.discord-pill").Count;
        var item = Render<SettingsMenu>().FindAll("li a").Count(a => a.GetAttribute("href") == Path);
        return (card, pill, item);
    }

    [Theory]
    [InlineData(false, false, false, true, 0)]   // signed out
    [InlineData(true, false, false, true, 0)]    // an account that has not picked its username yet
    [InlineData(true, true, false, true, 1)]     // signed in, not linked
    [InlineData(true, true, true, true, 0)]      // linked
    [InlineData(true, true, false, false, 0)]    // nothing to link at (no community server)
    public void TheEntryPointsShowForASignedInHunterWhoHasNotLinkedAndNowhereElse(bool session, bool handle, bool linked, bool server, int shown)
    {
        var s = Use(session ? new(handle ? "Hunter01" : "", 7080, DiscordLinked: linked) : null, server ? Path : null);
        Assert.Equal((shown, shown, shown), Entries(s));
    }

    [Fact]
    public void TheMocksDiscordStatesShowBothSides()
    {
        string? state = null;
        Services.AddSingleton<ISessionService>(new MockSessionService(() => state));
        Assert.Equal((1, 1, 1), Entries(Services.GetRequiredService<ISessionService>().CurrentAsync().Result));
        state = "discord:linked";
        var linked = Services.GetRequiredService<ISessionService>().CurrentAsync().Result;
        Assert.Equal((true, 7080), (linked!.DiscordLinked, linked.HunterScore));
        Assert.Equal((0, 0, 0), Entries(linked));
        state = "discord:veteran";
        Assert.Equal(25000, Services.GetRequiredService<ISessionService>().CurrentAsync().Result!.HunterScore);
    }

    [Fact]
    public void OnTheWebsiteTheButtonIsAPlainLinkToThePageOfTheSite()
    {
        Use(new("Hunter01", 7080));
        Assert.Equal(Path, Render<DiscordLinkCard>().Find("a.link").GetAttribute("href"));
    }

    [Fact]
    public void InTheLauncherTheButtonOpensTheBrowserAtTheSitesAddress()
    {
        const string Site = "https://site.test/discord/link";
        var log = new Opened();
        Services.AddSingleton<ILauncherBridge>(new MockLauncherBridge(log, null));
        Use(new("Hunter01", 7080), Site);
        Services.GetRequiredService<HostMode>().Launcher = true;
        var link = Render<DiscordLinkCard>().Find("a.link");
        link.Click();
        Assert.Equal([Site], log.Urls);  // the launcher's pages are local: the player's browser opens the site
    }

    [Theory]
    [InlineData(7080, "Verified hunter", 12920)]
    [InlineData(19999, "Verified hunter", 1)]
    [InlineData(20000, "Veteran", 0)]
    [InlineData(25000, "Veteran", 0)]
    public void TheResultPageShowsTheScoreTheRoleAndTheWayToVeteranWithEnglishRoleNamesInEveryLanguage(int score, string role, int more)
    {
        Use(new("Hunter01", score, DiscordLinked: true));
        Services.AddSingleton(new Branding(new() { ["link_discord"] = "https://discord.test/invite" }));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/signin/discord?result=linked");
        var old = Thread.CurrentThread.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "en", "fr", "ru" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(lang);
                var cut = Render<DiscordLink>();
                Assert.Equal(score + " HunterScore", cut.Find(".score").TextContent);
                Assert.Equal(role, cut.Find(".role span").TextContent);  // the Discord role's own name, in every language
                Assert.Equal(role == "Veteran" ? "veteran" : "verified", cut.Find(".role span").ClassList.Single());
                Assert.Equal(Text("DiscordLink.Role").Replace("{0}", role), cut.Find(".role").TextContent);
                Assert.Single(cut.FindAll("a.btn"));
                if (more == 0) { Assert.Empty(cut.FindAll(".bar, .more")); continue; }
                var bar = cut.Find(".bar");
                Assert.Equal(("progressbar", "0", "20000", score.ToString()), (bar.GetAttribute("role"), bar.GetAttribute("aria-valuemin"), bar.GetAttribute("aria-valuemax"), bar.GetAttribute("aria-valuenow")));
                Assert.Equal($"width: {score * 100 / 20000}%", cut.Find(".fill").GetAttribute("style"));
                Assert.Equal("Veteran", cut.Find(".more span").TextContent);
                Assert.Equal(Text("DiscordLink.More").Replace("{0}", more.ToString()).Replace("{1}", "Veteran"), cut.Find(".more").TextContent);
            }
        }
        finally { Thread.CurrentThread.CurrentUICulture = old; }
    }

    [Fact]
    public void TheResultPageHidesTheWayBackWithoutAnInviteAndShowsNoScoreForAnError()
    {
        Use(new("Hunter01", 7080, DiscordLinked: true));
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/signin/discord?result=rewarded");
        Assert.Empty(Render<DiscordLink>().FindAll("a.btn"));
        nav.NavigateTo("/signin/discord?result=taken");
        var taken = Render<DiscordLink>();
        Assert.Empty(taken.FindAll(".score, .role, .bar"));
    }
}
