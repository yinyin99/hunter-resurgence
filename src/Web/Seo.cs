using System.Xml.Linq;
using Resurgence.Services;
using Resurgence.Ui;

namespace Resurgence.Web;

/// <summary>What search engines read (docs/website.md#search-engines): one URL per page and language, and the sitemap.</summary>
public static class Seo
{
    /// <summary>The page's URL in a language: the path with only the queries that change what it shows, <c>page</c> (above 1) and <c>lang</c>
    /// (not for English, the bare URL). Any other query (<c>?ref=</c>, <c>?next=</c>) is the same page, and <c>/feed</c> is <c>/</c>.</summary>
    public static string Url(HttpRequest r, string lang)
    {
        var path = r.Path.Value is "/feed" or null or "" ? "/" : r.Path.Value;
        List<string> query = [];
        if (int.TryParse(r.Query["page"], out var page) && page > 1) query.Add($"page={page}");
        if (lang != "en") query.Add($"lang={lang}");
        return $"{r.Scheme}://{r.Host}{path}{(query.Count > 0 ? "?" + string.Join('&', query) : "")}";
    }

    /// <summary>The open pages, every store category, item and bundle, and the profile of every hunter on the hunterscore board; English URLs only,
    /// the pages name their other languages (hreflang).</summary>
    // ponytail: one board and store walk per request; cache it if the sitemap is fetched often or the board grows past a few thousand hunters
    public static async Task<string> SitemapAsync(string origin, IStoreService store, ILeaderboardsService boards)
    {
        List<string> paths = ["/", "/store", "/store/bundles", "/leaderboards", "/download", "/help", "/your-data"];
        void Categories(IEnumerable<StoreCategory> cs) { foreach (var c in cs) { paths.Add($"/store/{c.Key}"); Categories(c.Children); } }
        Categories(await store.CategoriesAsync());
        for (int p = 1, pages = 1; p <= pages; p++)
        {
            var items = await store.ItemsAsync("all", "", p);
            pages = items.Pages;
            paths.AddRange(items.Rows.Select(i => $"/store/item/{i.Id}"));
        }
        paths.AddRange((await store.BundlesAsync()).Select(b => $"/store/bundle/{b.Id}"));
        for (int p = 1, pages = 1; p <= pages; p++)
        {
            var board = await boards.RowsAsync("hunterscore", 0, p);
            pages = board.Pages;
            paths.AddRange(board.Rows.Select(r => $"/profile/{Uri.EscapeDataString(r.Handle)}"));
        }
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XElement(ns + "urlset", paths.Distinct().Select(p => new XElement(ns + "url", new XElement(ns + "loc", origin + p))));
    }
}
