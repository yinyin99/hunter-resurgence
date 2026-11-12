using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Pages.SignIn;

namespace Resurgence.Tests.WebTests;

/// <summary>The browser pages of the sign-in (docs/website.md#sign-in): they draw what the server's step says and post back to it with the step's token.</summary>
public sealed class SignInPagesTests : UiContext
{
    const string Invite = "https://discord.test/invite";

    sealed class Step(SignInStep? step) : ISignInService
    {
        public Task<SignInStep?> PendingAsync() => Task.FromResult(step);
    }

    internal void Use(SignInStep? step, bool discord = false)
    {
        Services.AddSingleton<ISignInService>(new Step(step));
        if (discord) Services.AddSingleton(new Branding(new() { ["link_discord"] = Invite }));
    }

    [Fact]
    public void TheNameFormPostsTheStepsTokenToTheServerAndOffersRestoreOnlyWithAnArchive()
    {
        Use(new("handle", "tok-1", Offer: false));
        var plain = Render<Handle>();
        var form = plain.Find("form");
        Assert.Equal(("post", "/auth/handle"), (form.GetAttribute("method"), form.GetAttribute("action")));
        Assert.Equal("tok-1", plain.Find("input[name=csrf]").GetAttribute("value"));
        Assert.Empty(plain.FindAll("input[name=kind]"));
        Assert.Single(plain.FindAll("button[type=submit]"));
    }

    [Fact]
    public void TheRestoreOfferShowsTheOldNameFieldAndTheLastError()
    {
        Use(new("handle", "tok-2", Offer: true, Handle: "Fresh", Old: "nobody", Restore: true, Error: "unknown"));
        var cut = Render<Handle>();
        Assert.Equal("nobody", cut.Find("input[name=old]").GetAttribute("value"));
        Assert.Equal("Fresh", cut.Find("input[name=handle]").GetAttribute("value"));
        Assert.Equal("old", cut.Find("input[name=kind][checked]").GetAttribute("value"));
        Assert.Contains("did not keep", cut.Find("[role=alert]").TextContent);
        cut.Find("input[name=kind][value=new]").Change("new");
        Assert.Empty(cut.FindAll("input[name=old]"));
    }

    [Fact]
    public void ATypedNameIsDataNotMarkup()
    {
        Use(new("handle", "t", Handle: "\"><script>alert(1)</script>"));
        var cut = Render<Handle>();
        Assert.Empty(cut.FindAll("script"));
        Assert.Equal("\"><script>alert(1)</script>", cut.Find("input[name=handle]").GetAttribute("value"));
    }

    [Theory]
    [InlineData("confirm", "AB12-CD34")]
    [InlineData("app", null)]
    public void TheConfirmationNamesTheAccountAndPostsTheToken(string step, string? code)
    {
        Use(new(step, "tok-3", Handle: "Looper", Code: code), discord: true);
        var cut = Render<Confirm>();
        Assert.Equal(("post", "/auth/confirm"), (cut.Find("form").GetAttribute("method"), cut.Find("form").GetAttribute("action")));
        Assert.Equal("tok-3", cut.Find("input[name=csrf]").GetAttribute("value"));
        Assert.Equal("Looper", cut.Find("p b").TextContent);
        Assert.Equal(code is not null, cut.Markup.Contains("AB12-CD34"));
        Assert.Equal(Invite, cut.Find("a.btn").GetAttribute("href"));  // the invite line of #425
    }

    static SignInPagesTests With(SignInStep? step, bool discord = false)
    {
        var t = new SignInPagesTests();
        t.Use(step, discord);
        return t;
    }

    string Text(string key) => Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Strings>>()[key].Value;

    [Fact]
    public void WithoutAStepEveryStepPageSaysTheSignInRanOut()
    {
        using var none = With(null);
        var expired = none.Text("SignInPage.Problem.Expired");
        Assert.Contains(expired, none.Render<Handle>().Markup);
        Assert.Contains(expired, none.Render<Confirm>().Markup);
        using var other = With(new("handle", "t"));  // a step of another page is not this page's
        Assert.Contains(expired, other.Render<Confirm>().Markup);
    }

    [Fact]
    public void TheDiscordInviteShowsOnlyWhenSetAndAnUnknownProblemIsTheExpiredOne()
    {
        using var plain = With(null);
        Assert.Empty(plain.Render<Done>().FindAll("a"));
        using var invited = With(null, discord: true);
        Assert.Equal(Invite, invited.Render<Done>().Find("a").GetAttribute("href"));
        Assert.Contains(plain.Text("SignInPage.Problem.Banned"), plain.Render<Resurgence.Ui.Components.SignInProblem>(p => p.Add(x => x.Reason, "banned")).Markup);
        Assert.Contains(plain.Text("SignInPage.Problem.Expired"), plain.Render<Resurgence.Ui.Components.SignInProblem>(p => p.Add(x => x.Reason, "<x>")).Markup);
    }
}
