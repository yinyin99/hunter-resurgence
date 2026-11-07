using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class ItemDetailTests : UiContext
{
    [Fact]
    public void SignedOutOnlyTheLoginButtonShows()
    {
        Services.GetRequiredService<ISessionService>().SignOutAsync().GetAwaiter().GetResult();

        var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1227));

        var login = Services.GetRequiredService<IStringLocalizer<Strings>>()["Store.Login"].Value;
        Assert.Equal(login, cut.Find(".buy button").TextContent);
        Assert.DoesNotContain("em$", cut.Find(".buy").TextContent);
        Assert.Empty(cut.FindAll("#buy-item"));
    }

    [Fact]
    public void SignedInThePriceAndTheBuyButtonShow()
    {
        var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1227));

        Assert.Contains("em$", cut.Find(".buy b").TextContent);
        Assert.Single(cut.FindAll("#buy-item"));
    }

    // the live site buys from the server, with em$ only, and has no bank to send a player short of em$ to
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLiveBuyDialogOffersEmsOnlyAndNoBank(bool live)
    {
        Services.GetRequiredService<HostMode>().Live = live;
        Assert.Single(Render<ItemDetail>(p => p.Add(i => i.Id, 1227)).FindAll("#buy-item"));

        var cut = Render<Resurgence.Ui.Modals.BuyModal>(p => p.Add(m => m.Id, 1227).Add(m => m.Name, "Hat").Add(m => m.Ems, 100_000).Add(m => m.Gms, 5));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".buttons")));
        Assert.Equal(live ? 0 : 1, cut.FindAll("a[href='/bank/ems']").Count);
        Assert.Equal(!live, cut.FindAll(".buttons button").Any(b => b.TextContent.Contains("gm$")));
        if (live) Assert.True(Assert.Single(cut.FindAll(".buttons button")).HasAttribute("disabled"));  // too dear, and no bank to send to
    }

    // the stand-in pictures are named after the mock's fictional ids: a real item of the same id must not get one
    [Theory]
    [InlineData(false, "item-1227", "store-item-1227")]
    [InlineData(true, "store-item-1227", "\"item-1227\"")]
    public void TheLiveSiteLooksForTheRealItemsOwnPictureNotTheMocksStandIn(bool live, string present, string absent)
    {
        Services.GetRequiredService<HostMode>().Live = live;

        var markup = Render<ItemDetail>(p => p.Add(i => i.Id, 1227)).Markup;

        Assert.Contains(present, markup);
        Assert.DoesNotContain(absent, markup);
    }
}
