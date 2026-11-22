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

    // a page for signed-in players (the store, a profile, the missions) is the sign-in prompt while signed out, on the website and in the launcher: the page is not built
    [Theory]
    [InlineData(false, "/store/item/7")]
    [InlineData(true, "/profile/Fox/score/5")]
    [InlineData(false, "/missions/daily")]
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
}