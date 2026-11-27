using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The store of the community server (<see cref="ServerCatalogApi.Store"/>): the original's items, prices, sales and bundles. A server that does not answer is an empty store,
/// never the mock's. What the signed-in player owns comes from <paramref name="inventory"/> (<c>Owned</c>, <c>Expires</c>); it knows nothing of the limits of owning. An item's page adds what the list lacks
/// (<see cref="ServerCatalogApi.Item"/>: capacity, contents, what fits it, the permitted species); the server not answering leaves the page without it. Buying is the server's
/// (<see cref="ServerAccountApi.Buy"/>), with em$ or gm$.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
/// <param name="cookie">The server picks its site by <c>Host</c>, so every request carries the host the browser used. The store is for signed-in players only: every request carries the
/// session cookie, and without one the store is empty (the list is the same for everyone, so a signed-in player's read is kept for the others who are signed in).</param>
public sealed class ServerStoreService(HttpClient http, ServerCookie cookie, ServerCache<ServerStore> cache, IInventoryService inventory, ILogger<ServerStoreService> log) : IStoreService
{
    public const int PageSize = 12;

    // read once and kept until a page opens or a purchase goes through, so a search is not a request each
    Dictionary<int, (int Owned, bool Counted, long? Expires)>? owned;
    // what each bundle costs the player now (the server's own price, see ServerAccountApi.Bundles), kept the same way
    Dictionary<int, ServerBundleOffer>? offers;

    public void RefreshOwned() => (owned, offers) = (null, null);

    static readonly ServerStore Empty = new([], [], []);

    public async Task<IReadOnlyList<StoreCategory>> CategoriesAsync()
    {
        var s = await LoadAsync();
        // sales, all and bundles lead (all and bundles open their one sub-link, as the store does); the server's own categories follow, its "all" is the lead one
        List<StoreCategory> categories = [];
        if (s.Items.Any(i => i.OnSale)) categories.Add(new("sales", "Sales", []));
        categories.Add(new("all", "All Items", [new("all", "All Items", [])]));
        if (s.Bundles.Length > 0) categories.Add(new("bundles", "Bundles", [new("bundles", "Bundles", [])]));
        categories.AddRange(s.Categories.Where(c => c.Key != "all").Select(c => new StoreCategory(c.Key, c.Name, [.. (c.Children ?? []).Select(x => new StoreCategory(x.Key, x.Name, []))])));
        return categories;
    }

    /// <summary>A category key is a sub-category's: a top-level category opens its default (first) sub-category, so its key lists only that one's items.</summary>
    public async Task<Page<StoreItem>> ItemsAsync(string view, string search, int page)
    {
        var s = await LoadAsync();
        var own = await OwnAsync();
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = s.Items.Where(i => (view == "all" || (view == "sales" ? i.OnSale : i.Category == view))
            && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Variant.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var pages = Math.Max(1, (hits.Count + PageSize - 1) / PageSize);
        var n = Math.Clamp(page, 1, pages);
        return new Page<StoreItem>([.. hits.Skip((n - 1) * PageSize).Take(PageSize).Select(i => Item(i, own))], n, pages);
    }

    public async Task<IReadOnlyList<StoreBundle>> BundlesAsync()
    {
        var s = await LoadAsync();
        var prices = s.Bundles.Length > 0 ? await OffersAsync() : new Dictionary<int, ServerBundleOffer>();
        return [.. s.Bundles.Select(b => Bundle(b, s, prices))];
    }

    public async Task<bool> HasSalesAsync() => (await LoadAsync()).Items.Any(i => i.OnSale);

    public async Task<StoreItem?> ItemAsync(int id)
    {
        var s = await LoadAsync();
        var own = await OwnAsync();
        return s.Items.FirstOrDefault(i => i.Id == id) is { } i ? WithPage(Item(i, own), await PageAsync(id)) : null;
    }

    // a request of its own: the list holds a thousand items, and these (the weapons that fire an ammunition, a table of species) belong to one
    async Task<ServerItemPage?> PageAsync(int id)
    {
        try
        {
            using var request = cookie.Request(HttpMethod.Get, $"{ServerCatalogApi.Item}{id}");
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<ServerItemPage>();
            log.LogWarning("The server answered {Path}{Id} with {Status}: the item shows without its details", ServerCatalogApi.Item, id, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            log.LogWarning("The server did not answer {Path}{Id} ({Reason}): the item shows without its details", ServerCatalogApi.Item, id, e.Message);
        }
        return null;
    }

    static StoreItem WithPage(StoreItem item, ServerItemPage? page) => page is null ? item : item with
    {
        Capacity = page.Capacity,
        Contents = page.Contents is { } c ? new StoreContents(c.Count, c.Name) : null,
        Fits = page.Fits?.Select(f => new ItemFit(f.Title, [.. f.Items.Select(x => new StoreFit(x.Id, x.Name, x.Variant))], f.Key)).ToList(),
        Ethics = page.Ethical is { } e ? new EthicalMatrix([.. e.Ammo.Select(a => new StoreFit(a.Id, a.Name, a.Variant))], [.. e.Species.Select(x => new EthicalRow(x.Id, x.Name, x.Permitted))]) : null,
    };

    public async Task<StoreBundle?> BundleAsync(int id)
    {
        var s = await LoadAsync();
        var prices = s.Bundles.Any(b => b.Id == id) ? await OffersAsync() : new Dictionary<int, ServerBundleOffer>();
        return s.Bundles.Where(b => b.Id == id).Select(b => Bundle(b, s, prices)).FirstOrDefault();
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
            if (response.IsSuccessStatusCode) { RefreshOwned(); return Result.Success; }
            log.LogWarning("The server refused {Path} for {Id} ({Status})", ServerAccountApi.Buy, id, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.Buy, e.Message);
        }
        return Result.Fail("Store.Oops");
    }

    /// <summary>The player's items by id: units summed over a counted item's copies, else the number of copies; the latest expiry of a license.</summary>
    async Task<Dictionary<int, (int Owned, bool Counted, long? Expires)>> OwnAsync() => owned ??=
        (await inventory.ItemsAsync()).GroupBy(x => x.Id).ToDictionary(g => g.Key,
            g => (g.Any(x => x.Units is not null) ? g.Sum(x => x.Units ?? 0) : g.Count(), g.Any(x => x.Units is not null),
                  g.Any(x => x.Expires == 0) ? 0L : g.Max(x => x.Expires)));

    static StoreItem Item(ServerItem i, Dictionary<int, (int Owned, bool Counted, long? Expires)> own)
    {
        var (have, counted, expires) = own.GetValueOrDefault(i.Id);
        return new(i.Id, i.Name, i.Variant, i.Category, i.Ems, i.Was, i.Gms, have, i.Buyable, Capacity: 0, i.Description,
            OwnLimit: counted ? 0 : i.Limit, Counted: counted, Expires: expires, Unlimited: i.Unlimited, Fuel: i.Fuel, WasGms: i.Gwas, Sale: i.Sale is { } sale ? new(sale.Name, sale.Percent, sale.Until) : null);
    }

    // a line is one unit of an item at its full price; the page adds up the lines of one item. The price and what is already owned are the server's offer for this player.
    static StoreBundle Bundle(ServerBundle b, ServerStore s, Dictionary<int, ServerBundleOffer> offers)
    {
        BundleLine[] lines = [.. b.Items.Select(x => s.Items.FirstOrDefault(i => i.Id == x.Id) is { } i
            ? new BundleLine(i.Id, i.Name, 1, 0, i.Was ?? i.Ems, i.Variant, i.Limit)
            : new BundleLine(x.Id, x.Name, 1, 0, 0))];
        return offers.TryGetValue(b.Id, out var o)
            ? new StoreBundle(b.Id, b.Name, b.Description, o.Price, b.Buyable && o.Buyable, lines, o.Price < b.Price ? b.Price : 0, o.Owned, o.Of)
            : new StoreBundle(b.Id, b.Name, b.Description, b.Price, b.Buyable, lines);
    }

    /// <summary>What each bundle costs the player now, by bundle id; none without a session or when the server does not answer (the list prices then stand).</summary>
    async Task<Dictionary<int, ServerBundleOffer>> OffersAsync()
    {
        if (offers is not null) return offers;
        if (cookie.Value is not { } sent) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Bundles);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerBundleOffers>() is { Bundles: { } list })
                return offers = list.ToDictionary(o => o.Id);
            log.LogWarning("The server answered {Path} with {Status}: the list prices of the bundles stand", ServerAccountApi.Bundles, (int)response.StatusCode);
            cookie.Refused(response, sent);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): the list prices of the bundles stand", ServerAccountApi.Bundles, e.Message);
        }
        return [];
    }

    async Task<ServerStore> LoadAsync() => cookie.Value is null ? Empty
        : await cache.LoadAsync(http, cookie.Host, ServerCatalogApi.Store, s => s is { Categories: not null, Items: not null, Bundles: not null }, log, cookie.Value) ?? Empty;
}
