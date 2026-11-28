using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui;
using Resurgence.Ui.Components;
using Resurgence.Ui.Pages;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

/// <summary>The referral block of the news page, the form that enters a code, the progress page and the invitation link /join/&lt;code&gt; (#929).</summary>
public sealed class ReferralTests : UiContext
{
    /// <summary>The mock's page, with <paramref name="enter"/> as the answer to every code.</summary>
    sealed class Fixed(Referral page, Result<ReferralEntered>? enter = null) : IReferralService
    {
        public List<string> Entered { get; } = [];

        public Task<Referral?> GetAsync() => Task.FromResult<Referral?>(page);

        public Task<Result<ReferralEntered>> EnterAsync(string code)
        {
            Entered.Add(code);
            return Task.FromResult(enter ?? Result<ReferralEntered>.Fail("Referral.Error.failed"));
        }
    }

    string T(string key, params object[] args) => Services.GetRequiredService<IStringLocalizer<Strings>>()[key, args].Value;

    void Use(string? mock = null) => Services.AddSingleton<IReferralService>(new MockReferralService(mock));

    BunitJSModuleInterop Shell()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.Mode = JSRuntimeMode.Loose; // a call that is not set up (the deadline's local time) gets no answer
        shell.SetupVoid("copy", _ => true).SetVoidResult();
        return shell;
    }

    static IEnumerable<string> Cells(AngleSharp.Dom.IElement table, int column) => table.QuerySelectorAll("tbody tr").Select(r => r.Children[column].TextContent.Trim());

    // ---- the block of the news page

    [Fact]
    public void TheBlockShowsTheCodeTheInvitationLinkAndACopyButtonForEach()
    {
        Use();
        var shell = Shell();
        var cut = Render<ReferralBlock>();

        Assert.Equal("K7M2QX9P", cut.Find("#referral-code").GetAttribute("value"));
        Assert.Equal("https://example.com/join/K7M2QX9P", cut.Find("#referral-link").GetAttribute("value"));
        var buttons = cut.FindAll(".copy button");
        Assert.Equal([T("Referral.Copy"), T("Referral.Copy")], buttons.Select(b => b.TextContent));
        buttons[1].Click();
        Assert.Equal("https://example.com/join/K7M2QX9P", Assert.Single(shell.Invocations["copy"]).Arguments.Single());
        Assert.Equal(T("Referral.Copied"), cut.FindAll(".copy button")[1].TextContent);
        Assert.Equal("/referral", cut.Find("section a").GetAttribute("href"));
    }

    [Fact]
    public void TheBlockSaysHowManyRecruitsQualifiedAndWhereTheNextRewardLevelIs()
    {
        Use();
        Assert.Equal(T("Referral.Summary", 2, 3), Render<ReferralBlock>().Find(".summary").TextContent);
    }

    [Fact]
    public async Task SignedOutThereIsNoBlock()
    {
        Use();
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        Assert.Empty(Render<ReferralBlock>().FindAll("section"));
    }

    [Fact]
    public void TheNewsPageHasTheBlockInItsRightColumnForASignedInPlayerOnly()
    {
        Use();
        Shell();
        Assert.Equal(["referral-code"], Render<Feed>().FindAll(".side #referral-code").Select(i => i.Id));
    }

    [Fact]
    public async Task TheNewsPageHasNoBlockSignedOut()
    {
        Use();
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        Assert.Empty(Render<Feed>().FindAll("#referral-code"));
    }

    // ---- entering a code

    [Fact]
    public void TheFormSaysUntilWhenACodeCanBeEnteredInTheBrowsersOwnTime()
    {
        Use();
        var shell = Shell();
        shell.Setup<string?>("localTime", _ => true).SetResult("Dec 1, 2026, 10:00 AM");
        var cut = Render<ReferralBlock>();

        cut.WaitForAssertion(() => Assert.Equal(T("Referral.EnterUntil", "Dec 1, 2026, 10:00 AM"), cut.Find("form .note").TextContent));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("own")]
    [InlineData("loop")]
    [InlineData("already")]
    [InlineData("expired")]
    [InlineData("banned")]
    [InlineData("failed")]
    public void EachRefusalHasItsOwnText(string reason)
    {
        var page = new MockReferralService().GetAsync().Result!;
        var service = new Fixed(page, Result<ReferralEntered>.Fail("Referral.Error." + reason));
        Services.AddSingleton<IReferralService>(service);
        Shell();
        var cut = Render<ReferralBlock>();

        cut.Find("#referral-enter").Input(" abcd 2345 ");
        cut.Find("form").Submit();

        Assert.Equal(["abcd 2345"], service.Entered);
        Assert.Equal(T("Referral.Error." + reason), cut.Find("form .error").TextContent);
        Assert.NotEmpty(cut.FindAll("form"));
    }

    [Fact]
    public void AnAcceptedCodeNamesTheRecruiterAndSaysTheRewardIsToBeAnnounced()
    {
        Use();
        Shell();
        var cut = Render<ReferralBlock>();

        cut.Find("#referral-enter").Input("ABCD2345");
        cut.Find("form").Submit();

        Assert.Equal(T("Referral.Entered", "Moss_Walker") + " " + T("Referral.Tba"), cut.Find("p.ok").TextContent);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void AnAcceptedCodeShowsTheRewardWhenOneIsSet()
    {
        Use("referral:rewards");
        Shell();
        var cut = Render<ReferralBlock>();

        cut.Find("#referral-enter").Input("ABCD2345");
        cut.Find("form").Submit();

        Assert.Equal(T("Referral.Entered", "Moss_Walker") + " " + T("Referral.EnteredReward", "250 em$, 1× 20 GA Semi-Automatic Shotgun"), cut.Find("p.ok").TextContent);
    }

    [Fact]
    public void TheMocksOwnCodeIsRefusedAndTheFormStaysOpen()
    {
        Use();
        Shell();
        var cut = Render<ReferralBlock>();

        cut.Find("#referral-enter").Input("k7m2-qx9p");
        cut.Find("form").Submit();

        Assert.Equal(T("Referral.Error.own"), cut.Find("form .error").TextContent);
    }

    [Fact]
    public void NoFormOnceACodeWasEnteredAndTheBlockKeepsToItsCode()
    {
        Use("referral:entered");
        var cut = Render<ReferralBlock>();

        Assert.Empty(cut.FindAll("form"));
        Assert.NotNull(cut.Find("#referral-code"));
    }

    // ---- the progress page

    [Fact]
    public void TheRecruitsAreInTheServersOrderEachWithABarForTheHoursAndForTheScore()
    {
        Use();
        Shell();
        var cut = Render<ReferralProgress>();

        Assert.Equal(["Ranger_Test", "Elk_Hunter", "Duck_Caller", "Fresh_Boots", "Moss_Walker", "Old_Bear"], cut.FindAll(".recruits li .who a").Select(a => a.TextContent));
        Assert.Equal([false, false, false, false, true, true], cut.FindAll(".recruits li").Select(li => li.ClassList.Contains("qualified")));
        var first = cut.Find(".recruits li");
        Assert.Equal(["width:88%", "width:80%"], first.QuerySelectorAll(".bar i").Select(i => i.GetAttribute("style")));
        Assert.Equal([T("Referral.OfHours", "1.75", "2"), T("Referral.OfScore", 8, 10)], first.QuerySelectorAll(".meter b").Select(b => b.TextContent));
        // more than asked for fills the bar and no more
        Assert.Equal(["width:100%", "width:100%"], cut.FindAll(".recruits li").Last().QuerySelectorAll(".bar i").Select(i => i.GetAttribute("style")));
        // the qualified are marked, the others are not
        Assert.Equal([T("Referral.Qualified"), T("Referral.Qualified")], cut.FindAll(".recruits .tag.done").Select(t => t.TextContent));
        Assert.Equal(T("Referral.Qualification", "2", 10), cut.Find("h3 + p.note").TextContent);
    }

    [Fact]
    public void WithoutRecruitsThePageSaysSo()
    {
        Use("referral:none");
        Shell();
        var cut = Render<ReferralProgress>();

        Assert.Empty(cut.FindAll(".recruits"));
        Assert.Contains(T("Referral.NoRecruits"), cut.Markup);
    }

    [Fact]
    public void WithoutRewardsSetEveryLevelSaysTheRewardIsToBeAnnounced()
    {
        Use();
        Shell();
        var cut = Render<ReferralProgress>();

        var tiers = cut.Find("table.tiers");
        Assert.Equal(["1", "3", "5", "10", "20"], Cells(tiers, 0));
        Assert.Equal(Enumerable.Repeat(T("Referral.Tba"), 5), Cells(tiers, 1));
        Assert.Equal(T("Referral.TierNow", 1, T("Referral.Tba")) + " " + T("Referral.TierNext", 3, T("Referral.Tba")), cut.FindAll("h3 + p.note")[1].TextContent);
    }

    [Fact]
    public void WithRewardsSetEveryLevelSaysWhatItGives()
    {
        Use("referral:rewards");
        Shell();
        var cut = Render<ReferralProgress>();

        var tiers = cut.FindAll("table.tiers");
        Assert.Equal(["2,500 gm$", "100 em$", "1× 20 GA Semi-Automatic Shotgun", "10,000 gm$, 250 em$, 1× 3-9x40mm Rifle Scope", "25,000 gm$, 500 em$, 1× .300 Bolt-Action Rifle, 4× .300 Nosler AccuBond Ammunition"], Cells(tiers[0], 1));
        Assert.Equal(["✓", "", "", "", ""], Cells(tiers[0], 2));
        Assert.Equal(T("Referral.TierNow", 1, "2,500 gm$") + " " + T("Referral.TierNext", 3, "100 em$"), cut.FindAll("h3 + p.note")[1].TextContent);
        Assert.Equal(T("Referral.EnterReward", "250 em$, 1× 20 GA Semi-Automatic Shotgun"), cut.Find("form p.note:last-child").TextContent);
    }

    [Fact]
    public void TheDiscordMilestonesCountTheRecruitsWhoLinkedAndNameTheNextOne()
    {
        Use();
        Shell();
        var cut = Render<ReferralProgress>();

        Assert.Equal(T("Referral.DiscordCount", 2), cut.FindAll("h3 + p.note")[2].TextContent);
        var milestones = cut.FindAll("table.tiers")[1];
        Assert.Equal(["1", "3"], Cells(milestones, 0));
        Assert.Equal(["✓", ""], Cells(milestones, 2));
        Assert.Contains(T("Referral.DiscordNext", 3, T("Referral.Tba")), cut.Markup);
    }

    [Fact]
    public void ThePageHasTheCodeAndTheLinkWithTheirCopyButtons()
    {
        Use();
        Shell();
        var cut = Render<ReferralProgress>();

        Assert.Equal(["K7M2QX9P", "https://example.com/join/K7M2QX9P"], cut.FindAll(".share input").Select(i => i.GetAttribute("value")));
        Assert.Equal(2, cut.FindAll(".share button").Count);
    }

    [Fact]
    public void AnEnteredCodeIsShownInPlaceOfTheForm()
    {
        Use("referral:entered");
        Shell();
        var cut = Render<ReferralProgress>();

        Assert.Empty(cut.FindAll("form"));
        Assert.Contains(T("Referral.EnteredBy", "BEAR4TWC", "Moss_Walker"), cut.Markup);
    }

    [Fact]
    public void TheSettingsMenuLeadsToThePage()
    {
        Assert.Contains(Render<SettingsMenu>().FindAll("a"), a => a.GetAttribute("href") == "/referral");
        Assert.Equal(["/referral", "/join/{Code}"], typeof(ReferralProgress).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Select(r => r.Template).OrderDescending());
    }

    // ---- the invitation link

    [Fact]
    public void AnInvitationLinkFillsTheCodeInForASignedInPlayerWhoCanStillEnterOne()
    {
        Use();
        Shell();
        var cut = Render<ReferralProgress>(p => p.Add(x => x.Code, "ABCD2345"));

        Assert.Equal("ABCD2345", cut.Find("#referral-enter").GetAttribute("value"));
    }

    [Fact]
    public void TheCodeOfAnInvitationLinkIsOnlyANoteWhenACodeCannotBeEnteredAnyMore()
    {
        Use("referral:entered");
        Shell();
        var cut = Render<ReferralProgress>(p => p.Add(x => x.Code, "ABCD2345"));

        Assert.Empty(cut.FindAll("form"));
        Assert.Equal(T("Referral.JoinClosed", "ABCD2345"), cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public void SignedOutTheInvitationLinkGoesToTheSignInAndComesBackToItself()
    {
        Use();
        Services.AddSingleton<ISessionService>(new ServerSessionService(new HttpClient { BaseAddress = new Uri("http://server.test") }, new ServerCookie(), NullLogger<ServerSessionService>.Instance));
        var nav = Services.GetRequiredService<NavigationManager>();

        Render<ReferralProgress>(p => p.Add(x => x.Code, "ABCD2345"));

        Assert.EndsWith("/auth/steam/login?next=%2Fjoin%2FABCD2345", nav.Uri);
    }

    [Fact]
    public async Task SignedOutWithoutAServerSignInThePageNamesTheCodeAndOffersTheSignIn()
    {
        Use();
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        var cut = Render<ReferralProgress>(p => p.Add(x => x.Code, "ABCD2345"));

        Assert.Contains(T("Referral.JoinSignIn", "ABCD2345"), cut.Markup);
        Assert.NotNull(cut.Find("a.btn-primary"));
        Assert.Empty(cut.FindAll("#referral-code"));
    }
}
