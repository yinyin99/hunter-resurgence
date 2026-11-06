using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The store the community server sent, shared by every visitor: the website asks again only after <see cref="Lifetime"/>, and after a failure not before <see cref="Pause"/>
/// (a page reads the store several times, and a server that hangs would cost a time-out each).</summary>
public sealed class StoreCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(10);

    sealed record Held(DateTime At, ServerStore Store);

    volatile Held? held;
    long failedAt;  // ticks of the last failure, 0 for none

    public ServerStore? Get(DateTime now) => held is { } h && now - h.At < Lifetime ? h.Store : null;

    public bool Paused(DateTime now) => Volatile.Read(ref failedAt) is var t && t != 0 && now - new DateTime(t, DateTimeKind.Utc) < Pause;

    public void Set(DateTime now, ServerStore store)
    {
        held = new Held(now, store);
        Volatile.Write(ref failedAt, 0);
    }

    public void Fail(DateTime now) => Volatile.Write(ref failedAt, now.Ticks);
}

/// <summary>The store of the community server (<see cref="ServerCatalogApi.Store"/>): the original's items, prices, sales and bundles. A server that does not answer is an empty store,
/// never the mock's. What the player owns is not known (<c>Owned</c> 0, the live site does not show it), nor the limits of owning, nor a capacity cost; buying is not served yet.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
/// <param name="cookie">Only its <see cref="ServerCookie.Host"/> is used: the server picks its site by <c>Host</c>, so the request carries the host the browser used (never the session cookie: the store is the same for everyone).</param>
public sealed class ServerStoreService(HttpClient http, ServerCookie cookie, StoreCache cache, ILogger<ServerStoreService> log) : IStoreService
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

    public Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1) => Task.FromResult(Result.Fail("Store.Oops"));

    static StoreItem Item(ServerItem i) => new(i.Id, i.Name, i.Variant, i.Category, i.Ems, i.Was, i.Gms, Owned: 0, i.Buyable, Capacity: 0, i.Description);

    // a line is one unit of an item at its full price; the page adds up the lines of one item
    static StoreBundle Bundle(ServerBundle b, ServerStore s) => new(b.Id, b.Name, b.Description, b.Price, Buyable: true,
        [.. b.Items.Select(x => s.Items.FirstOrDefault(i => i.Id == x.Id) is { } i
            ? new BundleLine(i.Id, i.Name, 1, 0, i.Was ?? i.Ems, i.Variant)
            : new BundleLine(x.Id, x.Name, 1, 0, 0))]);

    async Task<ServerStore> LoadAsync()
    {
        var now = DateTime.UtcNow;
        if (cache.Get(now) is { } held) return held;
        if (cache.Paused(now)) return Empty;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ServerCatalogApi.Store);
            if (cookie.Host is not null) request.Headers.Host = cookie.Host;
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                // a reply that lacks a list is not a store: it would break every page until it expires
                if (await response.Content.ReadFromJsonAsync<ServerStore>() is { Categories: not null, Items: not null, Bundles: not null } store)
                {
                    cache.Set(now, store);
                    return store;
                }
                log.LogWarning("The server answered {Path} with a reply that is not a store: an empty store", ServerCatalogApi.Store);
            }
            else log.LogWarning("The server answered {Path} with {Status}: an empty store", ServerCatalogApi.Store, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): an empty store", ServerCatalogApi.Store, e.Message);
        }
        cache.Fail(now);
        return Empty;
    }
}
