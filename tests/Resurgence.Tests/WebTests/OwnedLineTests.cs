using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Components;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class OwnedLineTests : UiContext
{
    static StoreItem Item(int owned = 0, bool counted = false, long? expires = null, int id = 0) =>
        new(id, "Made-up Item", "", "0", 10, null, 0, owned, true, 0, "", Counted: counted, Expires: expires);

    static ComponentParameterCollectionBuilder<OwnedLine> Add(ComponentParameterCollectionBuilder<OwnedLine> p, StoreItem i) =>
        p.Add(l => l.Owned, i.Owned).Add(l => l.Counted, i.Counted).Add(l => l.Expires, i.Expires);

    string Text(IRenderedComponent<OwnedLine> cut) => cut.Find(".own").TextContent;

    [Fact]
    public void ACountedItemShowsItsAmountAndASingleOneShowsOwned()
    {
        Assert.Equal("You own: 12", Text(Render<OwnedLine>(p => Add(p, Item(12, counted: true)))));
        Assert.Equal("You own: 1", Text(Render<OwnedLine>(p => Add(p, Item(1, counted: true)))));
        Assert.Equal("Owned", Text(Render<OwnedLine>(p => Add(p, Item(1)))));
        Assert.Equal("You own: 2", Text(Render<OwnedLine>(p => Add(p, Item(2)))));
    }

    [Fact]
    public void ALicenseShowsItsEndOrThatItIsUnlimited()
    {
        var end = Item(1, expires: 1798761600);   // 2027-01-01
        Assert.Equal("Expires: 2027-01-01", Text(Render<OwnedLine>(p => Add(p, end))));
        Assert.Equal("You have a valid license until: 2027-01-01", Text(Render<OwnedLine>(p => Add(p, end).Add(l => l.Full, true))));
        Assert.Equal("You have an unlimited license", Text(Render<OwnedLine>(p => Add(p, Item(1, expires: 0)))));
    }

    [Fact]
    public void ALicenseThatHasEndedShowsNothing() => Assert.Empty(Render<OwnedLine>(p => Add(p, Item(1, expires: 1000))).FindAll(".own"));

    // the buy dialog says the same as the card
    [Fact]
    public void TheBuyDialogSaysWhatTheCardSays()
    {
        var one = Render<BuyModal>(p => p.Add(m => m.Id, 5).Add(m => m.Name, "Hat").Add(m => m.Ems, 10).Add(m => m.Owned, 1));
        Assert.Equal("Owned", one.Find(".own").TextContent);
        var licence = Render<BuyModal>(p => p.Add(m => m.Id, 5).Add(m => m.Name, "Hat").Add(m => m.Ems, 10).Add(m => m.Owned, 1).Add(m => m.Expires, 0L));
        Assert.Equal("You have an unlimited license", licence.Find(".own").TextContent);
    }

    [Fact]
    public void AnItemTheyDoNotOwnShowsNothing() => Assert.Empty(Render<OwnedLine>(p => Add(p, Item())).FindAll(".own"));

    // item 0 is an item like any other
    [Fact]
    public void ItemZeroShowsItsAmountOnTheStoreList()
    {
        Services.AddSingleton<IStoreService>(new OneItem(Item(3, counted: true, id: 0)));
        var cut = Render<Store>(p => p.Add(s => s.Key, "all"));
        cut.WaitForAssertion(() => Assert.Equal("You own: 3", cut.Find(".card .own").TextContent));
    }

    // the dialog's callback reads the store again, so the new amount shows, as the wallet does
    [Fact]
    public async Task AfterAPurchaseThePageShowsTheNewAmount()
    {
        var store = new OneItem(Item(0, id: 5));
        Services.AddSingleton<IStoreService>(store);
        var cut = Render<ItemDetail>(p => p.Add(i => i.Id, 5));
        Assert.Empty(cut.FindAll(".own"));

        cut.Find("#buy-item").Click();
        store.Item = Item(1, id: 5);
        var bought = (Microsoft.AspNetCore.Components.EventCallback)Services.GetRequiredService<ModalService>().Parameters!["Bought"]!;
        await cut.InvokeAsync(() => bought.InvokeAsync());

        Assert.Equal("Owned", cut.Find(".own").TextContent);
    }

    sealed class OneItem(StoreItem item) : IStoreService
    {
        public StoreItem Item { get; set; } = item;
        public Task<IReadOnlyList<StoreCategory>> CategoriesAsync() => Task.FromResult<IReadOnlyList<StoreCategory>>([new("all", "All Items", [new("all", "All Items", [])])]);
        public Task<Page<StoreItem>> ItemsAsync(string view, string search, int page) => Task.FromResult(new Page<StoreItem>([Item], 1, 1));
        public Task<IReadOnlyList<StoreBundle>> BundlesAsync() => Task.FromResult<IReadOnlyList<StoreBundle>>([]);
        public Task<bool> HasSalesAsync() => Task.FromResult(false);
        public Task<StoreItem?> ItemAsync(int id) => Task.FromResult<StoreItem?>(Item);
        public Task<StoreBundle?> BundleAsync(int id) => Task.FromResult<StoreBundle?>(null);
        public void RefreshOwned() { }
        public Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1) => Task.FromResult(Result.Success);
    }
}
