using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class InventoryPageTests : UiContext
{
    sealed class Items(params InventoryItem[] items) : IInventoryService
    {
        public readonly List<long> Sold = [];
        public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>([.. items.Where(i => !Sold.Contains(i.Guid))]);
        public Task<Result> SellAsync(long guid) { Sold.Add(guid); return Task.FromResult(Result.Success); }
    }

    [Fact]
    public void ItemsShowGroupedWithTheirPictureSlotUnitsAndFlagItemZeroToo()
    {
        Services.AddSingleton<IInventoryService>(new Items(new(0, "Made-up Rifle", "Blue", "4", "Rifles", null, true), new(9, "Made-up Refill", "", "5", "Refills", 3, false), new(7, "Made-up Loose", "", "", "", null, false)));

        Services.GetRequiredService<HostMode>().Live = true;
        var cut = Render<Inventory>();

        Assert.Equal(["Rifles", "Refills", Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Strings>>()["Inv.Other"].Value], cut.FindAll(".group h3").Select(h => h.TextContent));
        Assert.Contains("store-item-0", cut.Markup);
        Assert.Contains("store-item-9", cut.Markup);
        Assert.Single(cut.FindAll(".equipped"));
        Assert.Contains("3", cut.Find(".units").TextContent);
    }

    [Fact]
    public void WithoutItemsThePageSaysSo()
    {
        Services.AddSingleton<IInventoryService>(new Items());
        var cut = Render<Inventory>();
        Assert.Empty(cut.FindAll(".card"));
        Assert.Single(cut.FindAll(".none"));
    }

    [Fact]
    public void OnlyACopyTheServerOffersToBuyBackHasASellButtonAndTheConfirmationNamesTheAmountAndTheCurrency()
    {
        var items = new Items(new(1, "Made-up Rifle", "", "4", "Rifles", null, false, Guid: 11, Sell: new("gm", 40)),
                              new(2, "Made-up Scope", "", "4", "Rifles", null, false, Guid: 12),
                              new(3, "Made-up Tent", "", "4", "Rifles", null, true, Guid: 13),
                              new(4, "Made-up Boots", "", "4", "Rifles", null, false, Guid: 14, Sell: new("em", 7)));
        Services.AddSingleton<IInventoryService>(items);
        var modal = Services.GetRequiredService<ModalService>();
        var raised = 0;
        modal.Changed += () => raised++;
        var cut = Render<Inventory>();

        Assert.Equal(2, cut.FindAll("button.sell").Count);
        Assert.Contains("40 gm$", cut.FindAll("button.sell")[0].TextContent);
        cut.FindAll("button.sell")[1].Click();

        var text = (string)modal.Parameters!["Text"]!;
        Assert.Contains("Made-up Boots", text);
        Assert.Contains("7 em$", text);
        Assert.Empty(items.Sold);  // nothing is sold before the answer
        ((TaskCompletionSource<bool>)modal.Parameters["Answer"]!).SetResult(true);

        cut.WaitForAssertion(() => Assert.Equal([14L], items.Sold));
        cut.WaitForAssertion(() => Assert.Equal(2, raised));  // the confirmation opened, then the balances changed after the answer
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.sell")));  // read again: the sold copy is gone
    }

    [Fact]
    public void ANoToTheConfirmationSellsNothing()
    {
        var items = new Items(new InventoryItem(1, "Made-up Rifle", "", "4", "Rifles", null, false, Guid: 11, Sell: new("gm", 40)));
        Services.AddSingleton<IInventoryService>(items);
        var modal = Services.GetRequiredService<ModalService>();
        var cut = Render<Inventory>();

        cut.Find("button.sell").Click();
        ((TaskCompletionSource<bool>)modal.Parameters!["Answer"]!).SetResult(false);

        Assert.Empty(items.Sold);
        Assert.Single(cut.FindAll("button.sell"));
    }
}
