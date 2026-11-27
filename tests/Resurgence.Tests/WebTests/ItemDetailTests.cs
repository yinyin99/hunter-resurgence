using System.Globalization;
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

    // 1225 is the mock's ammunition: it holds ten cartridges, three weapons fire it and a table says which species it is permitted for
    [Fact]
    public void AnAmmunitionShowsItsContentsItsWeaponsWithPicturesAndTheSpeciesTable()
    {
        var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1225));

        var contains = Services.GetRequiredService<IStringLocalizer<Strings>>()["Store.Contains"].Value;
        Assert.Contains($"{contains} 10 cartridges", cut.Find(".data").TextContent);
        var weapons = cut.FindAll(".fits .fit");
        Assert.Equal(3, weapons.Count);
        Assert.All(weapons, w => Assert.NotNull(w.QuerySelector(".asset-slot")));
        Assert.Equal(5, cut.FindAll(".ethics tbody tr").Count);
    }

    // the species names are the player's language's texts, sorted by them, and a permitted species has a tick
    [Fact]
    public void TheSpeciesAreTranslatedAndSortedAndMarked()
    {
        var before = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("fr");
        try
        {
            var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1225));

            var strings = Services.GetRequiredService<IStringLocalizer<Strings>>();
            Assert.Single(cut.FindAll(".ethics th .asset-slot"));
            Assert.Equal("Rifle Ammo A (Black)", cut.Find(".ethics th .nm").TextContent);
            var weapon = Render<ItemDetail>(p => p.Add(i => i.Id, 1226));
            Assert.Equal(3, weapon.FindAll(".ethics th .asset-slot").Count);
            Assert.All(weapon.FindAll(".ethics th .nm"), s => Assert.StartsWith("Rifle Ammo", s.TextContent));
            var names = cut.FindAll(".ethics tbody tr td:first-child").Select(n => n.TextContent).ToList();
            Assert.Contains(strings["Term.species.14"].Value, names);
            Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
            Assert.NotEmpty(cut.FindAll(".ethics td.yes"));
            Assert.NotEmpty(cut.FindAll(".ethics td.no"));
        }
        finally { (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = before; }
    }

    // the fit titles are the store's category texts and the units are the catalogue's unit word by a text, not the catalogue's English
    [Fact]
    public void TheFitTitlesAndTheContentsAreInThePlayersLanguage()
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("fr");
        try
        {
            var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1225));

            var strings = Services.GetRequiredService<IStringLocalizer<Strings>>();
            Assert.Equal(strings["Store.Cat.0"].Value, cut.Find(".fits h4").TextContent);
            Assert.Equal("Contient : 10 cartouches", cut.FindAll(".data p")[1].TextContent);
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    // the live site buys from the server, with em$ and gm$, and has no bank to send a player short of em$ to
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLiveBuyDialogOffersBothButtonsAndNoBank(bool live)
    {
        Services.GetRequiredService<HostMode>().Live = live;
        Assert.Single(Render<ItemDetail>(p => p.Add(i => i.Id, 1227)).FindAll("#buy-item"));

        var cut = Render<Resurgence.Ui.Modals.BuyModal>(p => p.Add(m => m.Id, 1227).Add(m => m.Name, "Hat").Add(m => m.Ems, 100_000).Add(m => m.Gms, 5));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".buttons")));
        Assert.Equal(live ? 0 : 1, cut.FindAll("a[href='/bank/ems']").Count);
        Assert.Contains(cut.FindAll(".buttons button"), b => b.TextContent.Contains("gm$"));
        if (live) Assert.True(cut.Find(".buttons button").HasAttribute("disabled"));  // the em$ button: too dear, and no bank to send to
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

    // the mock's first item (1227) is on a 20 % sale in em$ and gm$: the page of the store's own item route shows the ribbon, both old prices, higher than the sale prices, and the end
    [Fact]
    public void TheMocksItemOnSaleShowsTheSaleOnItsPage()
    {
        var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 1227));

        Assert.Equal("SALE -20%", cut.Find(".item .ribbon").TextContent);
        Assert.Equal(["100 gm$", "3 em$"], cut.FindAll(".buy s").Select(s => s.TextContent.Trim()));
        Assert.Equal(["or 80 gm$", "2 em$"], cut.FindAll(".buy .sale").Select(s => s.TextContent.Trim()));
        Assert.Equal("Sale ends Nov 30th 07:00 UTC", cut.Find(".ends").TextContent);
    }
}
