using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

/// <summary>A license without an end (<c>Expires</c> 0: the server gives every player one while its <c>all_licenses</c> is on) is not for sale again.</summary>
public sealed class UnlimitedLicenseTests : UiContext
{
    sealed class One(StoreItem item) : IStoreService
    {
        public Task<IReadOnlyList<StoreCategory>> CategoriesAsync() => Task.FromResult<IReadOnlyList<StoreCategory>>([new("all", "All Items", [new("all", "All Items", [])])]);
        public Task<Page<StoreItem>> ItemsAsync(string view, string search, int page) => Task.FromResult(new Page<StoreItem>([item], 1, 1));
        public Task<IReadOnlyList<StoreBundle>> BundlesAsync() => Task.FromResult<IReadOnlyList<StoreBundle>>([]);
        public Task<bool> HasSalesAsync() => Task.FromResult(false);
        public Task<StoreItem?> ItemAsync(int id) => Task.FromResult<StoreItem?>(item);
        public Task<StoreBundle?> BundleAsync(int id) => Task.FromResult<StoreBundle?>(null);
        public void RefreshOwned() { }
        public Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1) => Task.FromResult(Result.Success);
    }

    [Fact]
    public void ALicenseWithoutAnEndShowsTheUnlimitedLineAndNoBuyButton()
    {
        Services.AddSingleton<IStoreService>(new One(new StoreItem(5, "Made-up License", "", "0", 10, null, 0, 1, true, 0, "", Expires: 0)));

        var list = Render<Store>(p => p.Add(s => s.Key, "all"));
        list.WaitForAssertion(() => Assert.Equal("You have an unlimited license", list.Find(".card .own").TextContent));
        Assert.Empty(list.FindAll(".card .btn-primary"));

        var page = Render<ItemDetail>(p => p.Add(i => i.Id, 5));
        Assert.Equal("You have an unlimited license", page.Find(".own").TextContent);
        Assert.Empty(page.FindAll("#buy-item"));
    }

    [Fact]
    public void ALicenseThatEndsCanBeBoughtAgain()
    {
        Services.AddSingleton<IStoreService>(new One(new StoreItem(5, "Made-up License", "", "0", 10, null, 0, 1, true, 0, "", Expires: 1000)));
        var page = Render<ItemDetail>(p => p.Add(i => i.Id, 5));
        Assert.NotNull(page.Find("#buy-item"));
    }
}
