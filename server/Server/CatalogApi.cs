// The store's catalogue and the news on the website's host: public, no cookie, the same for everyone. The website's server calls
// them over SERVER_URL; a browser never does, so the reverse proxy does not have to route /catalog/. Online only.
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class CatalogApi
{
    static Reply Json(int status, JsonNode body) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")], Py.DumpBytes(body), "catalog");

    public static void MapCatalog(this WebApplication app, Game game)
    {
        var state = game.State!;
        if (!state.Online) return;
        var group = app.MapGroup("/catalog").RequireHost(game.Web).WithGroupName("game").WithTags("Catalogue (web_host)");
        group.MapGet("/store", () => state.StoreCatalogue() is var c && c["items"]!.AsArray().Count > 0
                ? Json(200, c)
                : Json(404, new JsonObject { ["error"] = "no catalogue" }))
            .WithName("StoreCatalogue").WithSummary("The store: categories, items with today's price, bundles")
            .WithDescription("`{categories: [{key, name, children: [{key, name}]}], items: [{id, name, variant, description, category, ems, was, gms, buyable}], bundles: [{id, name, description, price, buyable, items: [{id, name}]}]}` from the application catalogue the server loaded (docs/server.md, Starting data); `ems` and `gms` are today's prices, `was` the em$ price before an active discount, null without one; `buyable` is what `POST /account/buy` sells. 404 when the catalogue has no items.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
        group.MapGet("/news", () =>
                Feed(game) is { } feed && News(feed, (string?)game.Cfg["news_host"], [game.GameWeb, game.Web]) is var news && news["posts"]!.AsArray().Count > 0
                    ? Json(200, news)
                    : Json(404, new JsonObject { ["error"] = "no news" }))
            .WithName("News").WithSummary("The news: the recorded posts and featured slides")
            .WithDescription("`{posts: [{title, date, slug, excerpt}], featured: [{title, lead, linkText, target, image, date}]}` from the recorded `splash/feed_data` reply, else the mirror's (docs/server.md, The news): plain text, dates as `yyyy-MM-ddTHH:mm:ss`, `target` an address of the website or `news:<slug>`, `image` the name of the slide's picture or null. 404 without one or without posts.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
    }

    /// <summary>The feed the launcher gets, in the replay's order: a recording (on the API host or the game's web host, which the current build calls), else the mirror's saved reply.</summary>
    static JsonObject? Feed(Game game)
    {
        foreach (var host in new[] { game.Api, game.GameWeb })
            if (game.Recorded(new Req("GET", host, FEED_DATA, "locale=en_US", _ => null, [], true)) is { Tier: >= 1 } hit && Parse(hit.Body) is { } recorded)
                return recorded;
        foreach (var host in new[] { game.GameWeb, game.Api })
            if (MirrorPath(game.MirrorDir, host, FEED_DATA) is { } file && Parse(File.ReadAllBytes(file)) is { } saved)
                return saved;
        return null;
    }

    static JsonObject? Parse(byte[] body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    static readonly Regex NotSlot = new("[^a-z0-9_-]+");

    /// <summary>The picture of a slide by its file name (`…/2026/08/sale_805x380.png` is `sale_805x380`), so a new recording with a new picture is a new slot.</summary>
    static string? Image(JsonNode? url) =>
        Uri.TryCreate(Str(url), UriKind.Absolute, out var u) && Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(u.AbsolutePath)) is { Length: > 0 } name ? NotSlot.Replace(name.ToLowerInvariant(), "-") : null;

    static readonly Regex Anchor = new("<a\\b[^>]*>.*?</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex Tag = new("<[^>]+>");
    static readonly Regex Space = new("\\s+");

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>HTML to the plain text the website shows: links (the "Continue reading" one) and tags gone, entities decoded.</summary>
    static string Text(JsonNode? html, bool dropLinks = false)
    {
        var s = Str(html) ?? "";
        if (dropLinks) s = Anchor.Replace(s, " ");
        return Space.Replace(WebUtility.HtmlDecode(Tag.Replace(s, " ")), " ").Trim();
    }

    /// <summary>The last path segment of a post's address.</summary>
    static string Slug(Uri u) => u.AbsolutePath.Trim('/') is var p && p.Length > 0 ? p[(p.LastIndexOf('/') + 1)..] : "";

    /// <summary>The recorded feed `{posts: [{url, title, date, excerpt}], splashes: [{subject, description, linktext, link, image_url, start_ts}]}` as the website's
    /// news. A post needs an address, a title and a date. A slide needs a start time; its link becomes the website's address when it is a `#` route on one of
    /// siteHosts (`…/#store/sales` is `/store/sales`) or `news:<slug>` on newsHost; a slide that links elsewhere is left out, since the website could only send the player away to it.</summary>
    public static JsonObject News(JsonObject feed, string? newsHost, string[] siteHosts)
    {
        var posts = new JsonArray();
        foreach (var p in (feed["posts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!Uri.TryCreate(Str(p["url"]), UriKind.Absolute, out var u) || Slug(u) is not { Length: > 0 } slug || Text(p["title"]) is not { Length: > 0 } title
                || !DateTime.TryParseExact(Str(p["date"]), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            posts.Add(new JsonObject { ["title"] = title, ["date"] = date.ToString("s", CultureInfo.InvariantCulture), ["slug"] = slug, ["excerpt"] = Text(p["excerpt"], dropLinks: true) });
        }
        var featured = new JsonArray();
        foreach (var s in (feed["splashes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!Uri.TryCreate(Str(s["link"]), UriKind.Absolute, out var u) || !Py.IsNum(s["start_ts"]) || Py.D(s["start_ts"]) is < 0 or >= 253402300800) continue;
            var target = u.Fragment.TrimStart('#') is { Length: > 0 } route && siteHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) ? "/" + route.TrimStart('/')
                : newsHost is not null && u.Host.Equals(newsHost, StringComparison.OrdinalIgnoreCase) && Slug(u) is { Length: > 0 } slug ? "news:" + slug
                : null;
            if (target is null) continue;
            var date = DateTimeOffset.FromUnixTimeSeconds(Py.L(s["start_ts"])).UtcDateTime.ToString("s", CultureInfo.InvariantCulture);
            featured.Add(new JsonObject { ["title"] = Text(s["subject"]), ["lead"] = Text(s["description"]), ["linkText"] = Text(s["linktext"]), ["target"] = target, ["image"] = Image(s["image_url"]), ["date"] = date });
        }
        return new JsonObject { ["posts"] = posts, ["featured"] = featured };
    }
}
