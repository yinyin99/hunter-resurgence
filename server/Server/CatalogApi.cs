// The store's catalogue on the website's host, the same for everyone: the store and its items for a signed-in player (the session cookie), the reserves and the open games
// for anyone. The website's server calls it over SERVER_URL, the launcher on the public domain, so the reverse proxy routes /catalog/ here. Online only.
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
        var group = app.MapGroup("/catalog").RequireHost(game.Web).WithGroupName("game").WithTags("Catalogue (web_host)");
        // the store is for signed-in players only (docs/server.md#the-store-catalogue)
        async ValueTask<object?> SignedIn(EndpointFilterInvocationContext f, EndpointFilterDelegate next) =>
            SignIn.Session(state.Store, f.HttpContext.Request.Headers.Cookie.ToString()) is null ? Json(401, new JsonObject { ["status"] = "signed_out", ["message"] = "Sign in first." }) : await next(f);
        group.MapGet("/store", () => state.StoreCatalogue() is var c && c["items"]!.AsArray().Count > 0
                ? Json(200, c)
                : Json(404, new JsonObject { ["error"] = "no catalogue" }))
            .WithName("StoreCatalogue").WithSummary("The store: categories, items with today's price, bundles")
            .WithDescription("`{categories: [{key, name, children: [{key, name}]}], items: [{id, name, variant, description, category, ems, was, gms, buyable, unlimited}], bundles: [{id, name, description, price, buyable, items: [{id, name}]}]}` from the application catalogue the server loaded (docs/server.md, Starting data); `ems` is today's em$ price and `was` the em$ price before an active discount (null without one); `gms` is the catalogue's gm$ price, never discounted, 0 for none; `buyable` is what `POST /account/buy` sells (for em$ when `ems` is above 0, for gm$ when `gms` is); `unlimited` is true for an ammunition while the server's `unlimited_ammo` is on: it is not sold (`buyable` false) because every player has all of it. 404 when the catalogue has no items.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json").Produces(401, contentType: "application/json").AddEndpointFilter(SignedIn);
        group.MapGet("/item/{id:int}", (int id) => state.StoreItemPage(id) is { } page ? Json(200, page) : Json(404, new JsonObject { ["error"] = "no item" }))
            .WithName("StoreItemPage").WithSummary("What a store item's page shows beyond the list: capacity cost, contents, what fits it and the permitted species")
            .WithDescription("`{capacity, contents, fits, ethical}` of an item `GET /catalog/store` lists: `capacity` is the catalogue's inventory weight divided by 1000, the number the store shows as the capacity cost (it has no unit; 0 for none); `contents` is `{count, name}` for an item the catalogue gives units (ten cartridges, a refill of uses) and null otherwise; "
                             + "`fits` is `[{title, key, items: [{id, name, variant}]}]`, the items that fit it under the title of their category and the store's key of it, the one `GET /catalog/store` names (a weapon: its sights and ammunition; an ammunition: its weapons); "
                             + "`ethical` is `{ammo: [{id, name, variant}], species: [{id, name, permitted: [bool]}]}` for a weapon (its ammunition) and for an ammunition (itself), null for another item: one flag per ammunition and species, by the rule the server judges kills by (docs/server.md, Scoring and paying a harvest). 404 for an item the store does not show.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json").Produces(401, contentType: "application/json").AddEndpointFilter(SignedIn);
        group.MapGet("/reserves", () => state.LaunchCatalogue() is var c && c["reserves"]!.AsArray().Count > 0
                ? Json(200, c)
                : Json(404, new JsonObject { ["error"] = "no reserves" }))
            .WithName("LaunchCatalogue").WithSummary("The reserves with their starts and the tutorials, for the launcher's map")
            .WithDescription("`{reserves: [{id, name, starts: [{id, kind, name, x, y}]}], tutorials: [{id, name, reserve}]}` from the application catalogue the server loaded; `kind` is lodge, campsite, tower or interest, `id` the number `POST /auth/launch` takes as `lodge`, "
                             + "`x` and `y` percent (two decimals) of the reserve's map picture, from its left and top: world x to the right and z down over the square the facts file gives the reserve's map. 404 when the catalogue has no reserves.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
        group.MapGet("/games", () => Json(200, JsonNode.Parse(state.NetworkListReply())!))
            .WithName("GamesList").WithSummary("The open multiplayer sessions, for the website and the launcher")
            .WithDescription("What `GET /v1/Network/list` answers (docs/server.md, Multiplayer), on the website's host so that the reverse proxy reaches it: `[{id, name, handle, host, description, password, public, singleplayer, freeplayers, hs_min, hs_max, max_players, start_ts, clients_len, clients, reserve}]`, `[]` without a session.")
            .Produces(200, contentType: "application/json");
    }
}
