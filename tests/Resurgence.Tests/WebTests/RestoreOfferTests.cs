using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Layouts;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

/// <summary>The one-time "Do you have a backup of your profile?" dialog after the Discord invitation (docs/website.md).</summary>
public sealed class RestoreOfferTests : UiContext
{
    sealed class Fake(params bool[] answers) : IRestoreOfferService
    {
        public int Asked { get; private set; }
        public Task<bool> FirstAsync() => Task.FromResult(answers[Asked++]);
    }

    [Fact]
    public async Task TheDialogOpensOnlyForTheFirstTimeAndNeverOverAnotherDialog()
    {
        var modal = new ModalService();
        var service = new Fake(true, false, true);

        await modal.OfferRestoreAsync(service);
        Assert.Equal(typeof(RestoreOfferModal), modal.Type);
        modal.Close();

        await modal.OfferRestoreAsync(service);  // offered already
        Assert.Null(modal.Type);

        modal.Alert("something else");
        await modal.OfferRestoreAsync(service);  // another dialog is open: the server is not even asked
        Assert.Equal(typeof(AlertDialog), modal.Type);
        Assert.Equal(2, service.Asked);
    }

    [Fact]
    public async Task ItWaitsForTheDiscordDialogToClose()
    {
        var modal = new ModalService();
        modal.Show<DiscordInviteModal>();
        var service = new Fake(true);

        var offer = modal.OfferRestoreAsync(service);
        Assert.Equal(0, service.Asked);
        Assert.Equal(typeof(DiscordInviteModal), modal.Type);

        modal.Close();
        await offer;
        Assert.Equal(typeof(RestoreOfferModal), modal.Type);
    }

    [Fact]
    public void TheButtonOpensTheRestorePageAndLaterCloses()
    {
        var modal = Services.GetRequiredService<ModalService>();
        modal.Show<RestoreOfferModal>();
        var cut = Render<RestoreOfferModal>();
        Assert.Equal("/restore", cut.Find("#restore-offer-go").GetAttribute("href"));
        cut.Find("#restore-offer-go").Click();
        Assert.Null(modal.Type);

        modal.Show<RestoreOfferModal>();
        cut.Find("#restore-offer-later").Click();
        Assert.Null(modal.Type);
    }

    [Fact]
    public void ASignedInPlayerGetsTheDialogOnceTheShellIsThereWithoutADiscordInvite()
    {
        var service = new Fake(true);
        Services.AddSingleton<IRestoreOfferService>(service);
        var modal = Services.GetRequiredService<ModalService>();
        Render<ShellLayout>().WaitForAssertion(() => Assert.Equal(typeof(RestoreOfferModal), modal.Type));
    }
}
