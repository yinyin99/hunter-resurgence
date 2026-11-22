using System.Xml.Linq;

namespace Resurgence.Web;

/// <summary>What search engines read (docs/website.md#search-engines): one URL per page and language, which pages are indexed, and the sitemap.</summary>
public static class Seo
{
    /// <summary>The page's URL in a language: the path (no trailing slash) with only the queries that change what it shows, <c>page</c> (above 1) and <c>lang</c>
    /// (not for English, the bare URL). Any other query (<c>?ref=</c>, <c>?next=</c>) is the same page, and <c>/feed</c> is <c>/</c>.</summary>
    public static string Url(Uri page, string lang)
    {
        var path = page.AbsolutePath.TrimEnd('/') is "/feed" or "" ? "/" : page.AbsolutePath.TrimEnd('/');
        List<string> query = [];
        if (int.TryParse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(page.Query).GetValueOrDefault("page"), out var n) && n > 1) query.Add($"page={n}");
        if (lang != "en") query.Add($"lang={lang}");
        return $"{page.GetLeftPart(UriPartial.Authority)}{path}{(query.Count > 0 ? "?" + string.Join('&', query) : "")}";
    }

    /// <summary>The public pages (docs/website.md#search-engines): the news, the download, the help and the terms and privacy pages. Every other page is for signed-in players, or a step of signing in,
    /// and is not indexed.</summary>
    static readonly string[] Public = ["/", "/download", "/help", "/your-data", "/terms"];

    /// <summary>Whether search engines may index the page at <paramref name="path"/>: a public page (<c>/feed</c> is <c>/</c>, the help's tabs are the help); the response of any other path sends <c>noindex</c>.</summary>
    public static bool Indexed(string path)
    {
        path = path.TrimEnd('/') is "" or "/feed" ? "/" : path.TrimEnd('/');
        return Public.Contains(path, StringComparer.OrdinalIgnoreCase) || path.StartsWith("/help/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The public pages; English URLs only, the pages name their other languages (hreflang).</summary>
    public static string Sitemap(string origin)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XElement(ns + "urlset", Public.Select(p => new XElement(ns + "url", new XElement(ns + "loc", origin + p))));
    }
}
