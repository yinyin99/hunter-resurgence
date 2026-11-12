using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class InventoryPageTests : UiContext
{
    sealed class Items(params InventoryItem[] items) : IInventoryService
    {
        public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>(items);
    }

    [Fact]
    public void ItemsShowGroupedWithTheirPictureSlotUnitsAndFlagItemZeroToo()
    {
        Services.AddSingleton<IInventoryService>(new Items(new(0, "Made-up Rifle", "Blue", "4", "Rifles", null, true), new(9, "Made-up Refill", "", "5", "Refills", 3, false)));

        Services.GetRequiredService<HostMode>().Live = true;
        var cut = Render<Inventory>();

        Assert.Equal(["Rifles", "Refills"], cut.FindAll(".group h3").Select(h => h.TextContent));
        Assert.Contains("store-item-0", cut.Markup);
        Assert.Contains("store-item-9", cut.Markup);
        Assert.Single(cut.FindAll(".equipped"));
        Assert.Contains("3", cut.Find(".units").TextContent);
    }

    [Fact]
    public void WithoutItemsThePageSaysSo()
    {
        Services.AddSingleton<IInventoryService>(new Items());
        Assert.Empty(Render<Inventory>().FindAll(".card"));
        Assert.Single(Render<Inventory>().FindAll(".none"));
    }
}
