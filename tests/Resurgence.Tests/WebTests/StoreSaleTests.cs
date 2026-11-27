using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>An item on sale shows a SALE ribbon with the percent, the old price next to the new one for each price on sale, and when the sale ends; an item that is not on sale looks as before.</summary>
public sealed class StoreSaleTests : UiContext
{
    sealed class One(StoreItem item, StoreBundle? bundle = null) : IStoreService
    {
        public Task<IReadOnlyList<StoreCategory>> CategoriesAsync() => Task.FromResult<IReadOnlyList<StoreCategory>>([new("all", "All Items", [new("all", "All Items", [])])]);
        public Task<Page<StoreItem>> ItemsAsync(string view, string search, int page) => Task.FromResult(new Page<StoreItem>([item], 1, 1));
        public Task<IReadOnlyList<StoreBundle>> BundlesAsync() => Task.FromResult<IReadOnlyList<StoreBundle>>(bundle is null ? [] : [bundle]);
        public Task<bool> HasSalesAsync() => Task.FromResult(false);
        public Task<StoreItem?> ItemAsync(int id) => Task.FromResult<StoreItem?>(item);
        public Task<StoreBundle?> BundleAsync(int id) => Task.FromResult(bundle);
        public void RefreshOwned() { }
        public Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1) => Task.FromResult(Result.Success);
    }

    static readonly StoreSale Weekend = new("Made-up weekend", 25, new(2026, 11, 30, 7, 0, 0, TimeSpan.Zero));

    IRenderedComponent<Store> List(StoreItem item)
    {
        Services.AddSingleton<IStoreService>(new One(item));
        var cut = Render<Store>(p => p.Add(s => s.Key, "all"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".card")));
        return cut;
    }

    IRenderedComponent<ItemDetail> Page(StoreItem item)
    {
        Services.AddSingleton<IStoreService>(new One(item));
        return Render<ItemDetail>(p => p.Add(i => i.Id, item.Id));
    }

    static string[] Texts(IEnumerable<AngleSharp.Dom.IElement> elements) => [.. elements.Select(e => e.TextContent.Trim())];

    [Fact]
    public void ASaleItemOnTheListShowsTheRibbonBothOldPricesAndTheEnd()
    {
        var cut = List(new StoreItem(5, "Made-up Rifle", "", "0", 75, 100, 150, 0, true, 0, "", WasGms: 200, Sale: Weekend));

        Assert.Equal("SALE -25%", cut.Find(".card .ribbon").TextContent);
        Assert.Equal(["100 em$", "200 gm$"], Texts(cut.FindAll(".price s")));
        Assert.Equal(["75 em$", "150 gm$"], Texts(cut.FindAll(".price .sale")));
        Assert.Equal("Sale ends Nov 30th 07:00 UTC", cut.Find(".card .ends").TextContent);
        Assert.Contains("or 150 gm$", cut.Find(".price").TextContent);   // a space between the word and the sale price
    }

    [Fact]
    public void AGmOnlySaleStrikesTheGmPriceAlone()
    {
        var cut = List(new StoreItem(5, "Made-up Rifle", "", "0", 75, null, 150, 0, true, 0, "", WasGms: 200, Sale: Weekend));

        Assert.Equal("SALE -25%", cut.Find(".card .ribbon").TextContent);
        Assert.Equal(["200 gm$"], Texts(cut.FindAll(".price s")));
        Assert.Equal(["150 gm$"], Texts(cut.FindAll(".price .sale")));
    }

    [Fact]
    public void AnItemSoldForGmAloneShowsItsOldGmPrice()
    {
        var cut = List(new StoreItem(5, "Made-up Rifle", "", "0", 0, null, 150, 0, true, 0, "", WasGms: 200, Sale: Weekend));

        Assert.Equal(["200 gm$"], Texts(cut.FindAll(".price s")));
        Assert.Equal(["150 gm$"], Texts(cut.FindAll(".price .sale")));
    }

    [Fact]
    public void AnItemWithoutASaleShowsNoRibbonOldPriceOrEnd()
    {
        var cut = List(new StoreItem(6, "Made-up Pistol", "", "0", 80, null, 100, 0, true, 0, ""));

        Assert.Empty(cut.FindAll(".card .ribbon"));
        Assert.Empty(cut.FindAll(".price s"));
        Assert.Empty(cut.FindAll(".price .sale"));
        Assert.Empty(cut.FindAll(".card .ends"));
    }

    // a discount of the day has no named sale: the ribbon says SALE and no end is shown
    [Fact]
    public void AnEmDiscountWithoutASaleRecordKeepsTheOldLook()
    {
        var cut = List(new StoreItem(5, "Made-up Rifle", "", "0", 75, 100, 0, 0, true, 0, ""));

        Assert.Equal("SALE", cut.Find(".card .ribbon").TextContent);
        Assert.Equal(["100 em$"], Texts(cut.FindAll(".price s")));
        Assert.Empty(cut.FindAll(".card .ends"));
    }

    [Fact]
    public void ASaleItemsPageShowsTheRibbonBothOldPricesAndTheEnd()
    {
        var cut = Page(new StoreItem(5, "Made-up Rifle", "", "0", 75, 100, 150, 0, true, 0, "", WasGms: 200, Sale: Weekend));

        Assert.Equal("SALE -25%", cut.Find(".item .ribbon").TextContent);
        Assert.Equal(["200 gm$", "100 em$"], Texts(cut.FindAll(".buy s")));
        Assert.Equal("Sale ends Nov 30th 07:00 UTC", cut.Find(".ends").TextContent);
    }

    [Fact]
    public void AnItemsPageWithoutASaleShowsNothingExtra()
    {
        var cut = Page(new StoreItem(6, "Made-up Pistol", "", "0", 80, null, 100, 0, true, 0, ""));

        Assert.Empty(cut.FindAll(".item .ribbon"));
        Assert.Empty(cut.FindAll(".buy s"));
        Assert.Empty(cut.FindAll(".ends"));
    }

    static StoreBundle Pack(bool sale) => sale
        ? new(7, "Made-up Pack", "", 72, true, [new(1, "Made-up Rifle", 1, 0, 90)], PreSale: 90, Sale: Weekend)
        : new(8, "Made-up Plain Pack", "", 50, true, [new(1, "Made-up Rifle", 1, 0, 50)]);

    static readonly StoreItem Filler = new(6, "Made-up Pistol", "", "0", 80, null, 100, 0, true, 0, "");

    [Fact]
    public void ABundleOnSaleShowsTheSaleRibbonTheOldPriceAndTheEndOnTheListAndItsPage()
    {
        Services.AddSingleton<IStoreService>(new One(Filler, Pack(true)));
        var list = Render<Store>(p => p.Add(s => s.Key, "bundles"));
        list.WaitForAssertion(() => Assert.NotEmpty(list.FindAll(".card")));
        Assert.Equal("SALE -25%", list.Find(".card .ribbon").TextContent);
        Assert.Equal(["90 em$"], Texts(list.FindAll(".price s")));
        Assert.Equal(["72 em$"], Texts(list.FindAll(".price .sale")));
        Assert.Equal("Sale ends Nov 30th 07:00 UTC", list.Find(".card .ends").TextContent);

        var page = Render<BundleDetail>(p => p.Add(b => b.Id, 7));
        Assert.Equal("SALE -25%", page.Find(".item .ribbon").TextContent);
        Assert.Equal(["90 em$"], Texts(page.FindAll(".buy s")));
        Assert.Equal("Sale ends Nov 30th 07:00 UTC", page.Find(".ends").TextContent);
    }

    [Fact]
    public void ABundleNotOnSaleKeepsTheBundleRibbonAndNothingExtra()
    {
        Services.AddSingleton<IStoreService>(new One(Filler, Pack(false)));
        var list = Render<Store>(p => p.Add(s => s.Key, "bundles"));
        list.WaitForAssertion(() => Assert.NotEmpty(list.FindAll(".card")));
        Assert.Equal("BUNDLE", list.Find(".card .ribbon").TextContent);
        Assert.Empty(list.FindAll(".price s"));
        Assert.Empty(list.FindAll(".card .ends"));

        var page = Render<BundleDetail>(p => p.Add(b => b.Id, 8));
        Assert.Equal("BUNDLE", page.Find(".item .ribbon").TextContent);
        Assert.Empty(page.FindAll(".buy s"));
        Assert.Empty(page.FindAll(".ends"));
    }

    // on sale and partly owned: the struck price is the one before both reductions, never the sale price
    [Fact]
    public void ASaleBundleThePlayerPartlyOwnsStrikesTheListPriceBeforeTheSale()
    {
        var pack = new StoreBundle(7, "Made-up Pack", "", 36, true, [new(1, "Made-up Rifle", 1, 1, 90)], WasPrice: 72, OwnedItems: 1, OfItems: 2, PreSale: 90, Sale: Weekend);
        Assert.Equal(90, pack.StruckPrice);
        Services.AddSingleton<IStoreService>(new One(Filler, pack));
        var list = Render<Store>(p => p.Add(s => s.Key, "bundles"));
        list.WaitForAssertion(() => Assert.NotEmpty(list.FindAll(".card")));
        Assert.Equal(["90 em$"], Texts(list.FindAll(".price s")));
        Assert.Equal(["36 em$"], Texts(list.FindAll(".price .sale")));

        var page = Render<BundleDetail>(p => p.Add(b => b.Id, 7));
        Assert.Equal(["90 em$"], Texts(page.FindAll(".buy s")));
    }
}
