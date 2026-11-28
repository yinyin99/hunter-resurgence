using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Resurgence.Ui;
using Resurgence.Services;
using Resurgence.Ui.Layouts;

namespace Resurgence.Tests.WebTests;

public sealed class ShellLayoutTests : UiContext
{
    [Fact]
    public async Task DisposingAfterAReloadDoesNotThrowWhenTheModuleIsGone()
    {
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("watchScroll", _ => true);
        shell.SetupVoid("unwatchScroll").SetException(new JSException("gone"));
        var cut = Render<ShellLayout>();
        shell.VerifyInvoke("watchScroll");

        await cut.Instance.DisposeAsync();
        shell.VerifyInvoke("unwatchScroll");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]  // the launcher's Hunt now is a drop-down: nothing to watch
    public void TheWebsiteWatchesTheHuntNowLinkAndTheLauncherDoesNot(bool launcher)
    {
        Services.GetRequiredService<HostMode>().Launcher = launcher;
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.SetupVoid("watchScroll", _ => true).SetVoidResult();
        shell.SetupVoid("watchKeys", _ => true).SetVoidResult();
        shell.SetupVoid("watchLaunch").SetVoidResult();
        var cut = Render<ShellLayout>();
        cut.WaitForAssertion(() => shell.VerifyInvoke("watchScroll"));
        if (launcher) shell.VerifyNotInvoke("watchLaunch");
        else cut.WaitForAssertion(() => shell.VerifyInvoke("watchLaunch"));
    }

    // the page is the main column alone: the copy of the original site's empty right column is gone, on the website and in the launcher
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheShellHasNoRightColumn(bool launcher)
    {
        Services.GetRequiredService<HostMode>().Launcher = launcher;
        var shell = JSInterop.SetupModule("./_content/Resurgence.Ui/shell.js");
        shell.Mode = JSRuntimeMode.Loose;
        var cut = Render<ShellLayout>(p => p.Add(l => l.Body, "<p id=\"page\">page</p>"));
        Assert.NotNull(cut.Find(".body > main #page"));
        Assert.Empty(cut.FindAll(".sidebar"));
        Assert.Single(cut.Find(".body").Children);
    }

    // a page for signed-in players (the store, a profile, the missions) is the sign-in prompt while signed out, on the website and in the launcher: the page is not built
    [Theory]
    [InlineData(false, "/store/item/7")]
    [InlineData(true, "/profile/Fox/score/5")]
    [InlineData(false, "/missions/daily")]
    [InlineData(true, "/servers")]        // the launcher's open games
    public async Task APageForSignedInPlayersIsTheSignInPromptWhileSignedOut(bool launcher, string path)
    {
        Services.GetRequiredService<HostMode>().Launcher = launcher;
        await Services.GetRequiredService<ISessionService>().SignOutAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);
        var cut = Render<ShellLayout>(p => p.Add(l => l.Body, "<p id=\"page\">page</p>"));
        cut.WaitForAssertion(() => Assert.Contains("Sign in to see this page", cut.Markup));
        Assert.Empty(cut.FindAll("#page"));
    }

    [Theory]
    [InlineData("/download", true)]   // a public page, signed out
    [InlineData("/store", false)]     // signed in
    public async Task APublicPageOrASignedInPlayerSeesThePage(string path, bool signOut)
    {
        if (signOut) await Services.GetRequiredService<ISessionService>().SignOutAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);
        var cut = Render<ShellLayout>(p => p.Add(l => l.Body, "<p id=\"page\">page</p>"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#page")));
        Assert.DoesNotContain("Sign in to see this page", cut.Markup);
    }

    /// <summary>A session service that gives its answers in turn, the last one again once they run out, and counts the reads.</summary>
    sealed class Answers(params Session?[] answers) : ISessionService
    {
        public int Reads;
        public Task<Session?> CurrentAsync() => Task.FromResult(answers[Math.Min(Reads++, answers.Length - 1)]);
        public event Action? Changed { add { } remove { } }
        public string? DiscordLinkUrl => "/discord/link";
        public Task<Result> RequestCodeAsync(string email) => throw new NotSupportedException();
        public Task<Result> SignInAsync(string email, string code, bool register = false) => throw new NotSupportedException();
        public Task<Result> SetHandleAsync(string handle) => throw new NotSupportedException();
        public Task<StartOutcome> StartAsync() => throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<bool> LicenseAcceptedAsync() => Task.FromResult(true);
        public Task AcceptLicenseAsync() => Task.CompletedTask;
    }

    // the player links in the browser, which the launcher is not told of: while the link is offered, each page reads the session once more; a read with no answer keeps the session
    [Fact]
    public void TheLauncherReadsTheSessionAgainOncePerPageWhileTheDiscordLinkIsOffered()
    {
        var sessions = new Answers(new Session("Hunter01", 7080, DiscordLinked: false), null, new Session("Hunter01", 7080, DiscordLinked: true));
        Services.AddSingleton<ISessionService>(sessions);
        Services.GetRequiredService<HostMode>().Launcher = true;
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/store");
        var cut = Render<ShellLayout>(p => p.Add(l => l.Body, "<p>1</p>"));
        int Pill() => cut.FindAll("a.discord-pill").Count;
        void Page(string path)
        {
            nav.NavigateTo(path);
            cut.Render(p => p.Add(l => l.Body, $"<p>{path}</p>"));
        }
        Assert.Equal((1, 1), (Pill(), sessions.Reads));  // not linked: the pill shows, and the first page read the session once
        cut.Render(p => p.Add(l => l.Body, "<p>again</p>"));
        Assert.Equal(1, sessions.Reads);  // the same page rendered again asks nothing
        Page("/missions/daily");  // the server does not answer: the session and the pill stay
        Assert.Equal((1, 2), (Pill(), sessions.Reads));
        Assert.Contains("Hunter01", cut.Markup);
        Assert.Equal("/missions/daily", cut.Find("main").TextContent);  // still signed in: the page, not the sign-in prompt
        Page("/servers");  // linked meanwhile: the pill goes
        Assert.Equal((0, 3), (Pill(), sessions.Reads));
        Assert.Contains("Hunter01", cut.Markup);
        Page("/store");  // no longer offered: no more reads
        Assert.Equal(3, sessions.Reads);
    }
}
