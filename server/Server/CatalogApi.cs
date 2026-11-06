// The store's catalogue on the website's host: public, no cookie, the same for everyone. The website's server calls it over
// SERVER_URL; a browser never does, so the reverse proxy does not have to route /catalog/. Online only.
using System.Text.Json.Nodes;
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
        app.MapGroup("/catalog").RequireHost(game.Web).WithGroupName("game").WithTags("Catalogue (web_host)")
            .MapGet("/store", () => state.StoreCatalogue() is var c && c["items"]!.AsArray().Count > 0
                ? Json(200, c)
                : Json(404, new JsonObject { ["error"] = "no catalogue" }))
            .WithName("StoreCatalogue").WithSummary("The store: categories, items with today's price, bundles")
            .WithDescription("`{categories: [{key, name, children: [{key, name}]}], items: [{id, name, variant, description, category, ems, was, gms, buyable}], bundles: [{id, name, description, price, items: [{id, name}]}]}` from the application catalogue the server loaded (docs/server.md, Starting data); `ems` and `gms` are today's prices, `was` the em$ price before an active discount, null without one. 404 when the catalogue has no items.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
    }
}
