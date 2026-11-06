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

    [Fact]
    public void OnTheLiveSiteThePriceShowsAndThereIsNoBuyButton()
    {
        Services.GetRequiredService<HostMode>().Live = true;

        var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1227));

        Assert.Contains("em$", cut.Find(".buy b").TextContent);
        Assert.Empty(cut.FindAll("#buy-item"));
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
