using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The store of the community server (<see cref="ServerCatalogApi.Store"/>): the original's items, prices, sales and bundles. A server that does not answer is an empty store,
/// never the mock's. What the player owns is not known (<c>Owned</c> 0, the live site does not show it), nor the limits of owning, nor a capacity cost. Buying is the server's
/// (<see cref="ServerAccountApi.Buy"/>), with em$ or gm$.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
/// <param name="cookie">The server picks its site by <c>Host</c>, so every request carries the host the browser used. The store is the same for everyone and is asked without the
/// session cookie; a purchase carries it.</param>
public sealed class ServerStoreService(HttpClient http, ServerCookie cookie, ServerCache<ServerStore> cache, ILogger<ServerStoreService> log) : IStoreService
{
    public const int PageSize = 12;

    static readonly ServerStore Empty = new([], [], []);

    public async Task<IReadOnlyList<StoreCategory>> CategoriesAsync()
    {
        var s = await LoadAsync();
        // sales, all and bundles lead (all and bundles open their one sub-link, as the store does); the server's own categories follow, its "all" is the lead one
        List<StoreCategory> categories = [];
        if (s.Items.Any(i => i.Was is not null)) categories.Add(new("sales", "Sales", []));
        categories.Add(new("all", "All Items", [new("all", "All Items", [])]));
        if (s.Bundles.Length > 0) categories.Add(new("bundles", "Bundles", [new("bundles", "Bundles", [])]));
        categories.AddRange(s.Categories.Where(c => c.Key != "all").Select(c => new StoreCategory(c.Key, c.Name, [.. (c.Children ?? []).Select(x => new StoreCategory(x.Key, x.Name, []))])));
        return categories;
    }

    /// <summary>A category key is a sub-category's: a top-level category opens its default (first) sub-category, so its key lists only that one's items.</summary>
    public async Task<Page<StoreItem>> ItemsAsync(string view, string search, int page)
    {
        var s = await LoadAsync();
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = s.Items.Where(i => (view == "all" || (view == "sales" ? i.Was is not null : i.Category == view))
            && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Variant.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var pages = Math.Max(1, (hits.Count + PageSize - 1) / PageSize);
        var n = Math.Clamp(page, 1, pages);
        return new Page<StoreItem>([.. hits.Skip((n - 1) * PageSize).Take(PageSize).Select(Item)], n, pages);
    }

    public async Task<IReadOnlyList<StoreBundle>> BundlesAsync()
    {
        var s = await LoadAsync();
        return [.. s.Bundles.Select(b => Bundle(b, s))];
    }

    public async Task<bool> HasSalesAsync() => (await LoadAsync()).Items.Any(i => i.Was is not null);

    public async Task<StoreItem?> ItemAsync(int id) => (await LoadAsync()).Items.Where(i => i.Id == id).Select(Item).FirstOrDefault();

    public async Task<StoreBundle?> BundleAsync(int id)
    {
        var s = await LoadAsync();
        return s.Bundles.Where(b => b.Id == id).Select(b => Bundle(b, s)).FirstOrDefault();
    }

    public async Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1)
    {
        if (cookie.Value is null) return Result.Fail("Store.Oops");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.Buy);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("id", $"{id}"), KeyValuePair.Create("count", $"{count}"), KeyValuePair.Create("bundle", bundle ? "1" : "0"), KeyValuePair.Create("wallet", gms ? "SILVER" : "GOLD")]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result.Success;
            log.LogWarning("The server refused {Path} for {Id} ({Status})", ServerAccountApi.Buy, id, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.Buy, e.Message);
        }
        return Result.Fail("Store.Oops");
    }

    static StoreItem Item(ServerItem i) => new(i.Id, i.Name, i.Variant, i.Category, i.Ems, i.Was, i.Gms, Owned: 0, i.Buyable, Capacity: 0, i.Description);

    // a line is one unit of an item at its full price; the page adds up the lines of one item
    static StoreBundle Bundle(ServerBundle b, ServerStore s) => new(b.Id, b.Name, b.Description, b.Price, b.Buyable,
        [.. b.Items.Select(x => s.Items.FirstOrDefault(i => i.Id == x.Id) is { } i
            ? new BundleLine(i.Id, i.Name, 1, 0, i.Was ?? i.Ems, i.Variant)
            : new BundleLine(x.Id, x.Name, 1, 0, 0))]);

    async Task<ServerStore> LoadAsync() =>
        await cache.LoadAsync(http, cookie.Host, ServerCatalogApi.Store, s => s is { Categories: not null, Items: not null, Bundles: not null }, log) ?? Empty;
}
