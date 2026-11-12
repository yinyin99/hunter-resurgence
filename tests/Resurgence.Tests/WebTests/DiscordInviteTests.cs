using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Layouts;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>The "Join the Discord" button next to the feed's widget and the one-time dialog after sign-in (docs/website.md).</summary>
public sealed class DiscordInviteTests : UiContext
{
    const string Invite = "https://example.test/invite";

    sealed class Fake(params bool[] answers) : IDiscordInviteService
    {
        public int Asked { get; private set; }
        public Task<bool> FirstAsync() => Task.FromResult(answers[Asked++]);
    }

    [Fact]
    public void WithAnInviteTheWidgetHasTheButton()
    {
        Services.AddSingleton(new Branding(new() { ["link_discord"] = Invite }));
        var link = Render<Resurgence.Ui.Components.DiscordWidget>().Find("a.join");
        Assert.Equal(Invite, link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
    }

    [Fact]
    public void WithoutAnInviteTheWidgetHasNoButton() => Assert.Empty(Render<Resurgence.Ui.Components.DiscordWidget>().FindAll(".discord, .join"));

    [Fact]
    public async Task TheDialogOpensOnlyForTheFirstTimeAndNeverOverAnotherDialog()
    {
        var modal = new ModalService();
        var service = new Fake(true, false, true);

        await modal.OfferDiscordAsync(service);
        Assert.Equal(typeof(DiscordInviteModal), modal.Type);
        modal.Close();

        await modal.OfferDiscordAsync(service);  // the account was invited already
        Assert.Null(modal.Type);

        modal.Alert("something else");
        await modal.OfferDiscordAsync(service);  // another dialog is open: the server is not even asked
        Assert.Equal(typeof(AlertDialog), modal.Type);
        Assert.Equal(2, service.Asked);
    }

    [Fact]
    public void TheDialogJoinsThroughTheInviteOrCloses()
    {
        Services.AddSingleton(new Branding(new() { ["link_discord"] = Invite }));
        var modal = Services.GetRequiredService<ModalService>();
        modal.Show<DiscordInviteModal>();
        var cut = Render<DiscordInviteModal>();
        Assert.Equal(Invite, cut.Find("#discord-join").GetAttribute("href"));

        cut.Find("#discord-later").Click();
        Assert.Null(modal.Type);
    }

    [Fact]
    public void ASignedInPlayerGetsTheDialogOnceTheShellIsThere()
    {
        var service = new Fake(true);
        Services.AddSingleton<IDiscordInviteService>(service);
        Services.AddSingleton(new Branding(new() { ["link_discord"] = Invite }));
        var cut = Render<ShellLayout>();
        var modal = Services.GetRequiredService<ModalService>();
        cut.WaitForAssertion(() => Assert.Equal(typeof(DiscordInviteModal), modal.Type));
        Assert.Equal(1, service.Asked);
    }

    [Fact]
    public void WithoutAnInviteTheServerIsNotAsked()
    {
        var service = new Fake(true);
        Services.AddSingleton<IDiscordInviteService>(service);
        Render<ShellLayout>();
        Assert.Equal(0, service.Asked);
        Assert.Null(Services.GetRequiredService<ModalService>().Type);
    }
}
