// The hunterscore board and one hunter's place on it, on the website's host: public, no cookie. The website's server calls
// them over SERVER_URL; a browser never does, so the reverse proxy does not have to route /hunters/. Online only.
using System.Text.Json.Nodes;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class HuntersApi
{
    public const int PageSize = 30;

    static Reply Json(int status, JsonNode body) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")], Py.DumpBytes(body), "hunters");

    public static void MapHunters(this WebApplication app, Game game)
    {
        var state = game.State!;
        if (!state.Online) return;
        var group = app.MapGroup("/hunters").RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)");

        group.MapGet("", (HttpContext ctx) =>
            {
                var page = int.TryParse(ctx.Request.Query["page"], out var p) && p > 1 ? p : 1;
                var total = state.Store.Board(state.LocalAccount, 0, 0).Total;
                var pages = Math.Max(1, (int)((total + PageSize - 1) / PageSize));
                page = Math.Min(page, pages);
                var (rows, _) = state.Store.Board(state.LocalAccount, (page - 1) * PageSize, PageSize);
                return Json(200, new JsonObject
                {
                    ["page"] = page, ["pages"] = pages, ["total"] = total,
                    ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject { ["rank"] = r.Rank, ["handle"] = r.Handle, ["hunterscore"] = r.Hunterscore })]),
                });
            })
            .WithName("HuntersBoard").WithSummary("One page of the hunterscore board")
            .WithDescription($"`{{page, pages, total, rows: [{{rank, handle, hunterscore}}]}}`, {PageSize} rows a page, highest first; equal hunterscores in account id order. Banned accounts are not on it.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json");

        group.MapGet("/{handle}", (string handle) => state.Store.Hunter(handle, state.LocalAccount) is { } h
                ? Json(200, new JsonObject { ["handle"] = h.Handle, ["hunterscore"] = h.Hunterscore, ["rank"] = h.Rank })
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("Hunter").WithSummary("A hunter's hunterscore and place on the board")
            .WithDescription("`{handle, hunterscore, rank}` (the handle as the account spells it); 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
    }
}
