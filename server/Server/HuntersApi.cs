// The hunterscore board, one hunter's place on it and the minigames' boards, on the website's host: public, no cookie. The website's
// server calls them over SERVER_URL; a browser never does, so the reverse proxy does not have to route /hunters/ or /leaderboards/. Online only.
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

        group.MapGet("/{handle}", (string handle) => state.PublicAccount(handle) is { } id && state.PublicUser(id) is var p
                ? Json(200, new JsonObject
                {
                    ["handle"] = p["handle"]?.DeepClone(), ["hunterscore"] = p["hunterscore"]?.DeepClone(), ["rank"] = p["rank"]?.DeepClone(),
                    ["membership"] = p["membershipTitle"]?.DeepClone() ?? "", ["avatar"] = p["avatar"]?.DeepClone(),
                })
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("Hunter").WithSummary("A hunter's profile header: hunterscore, place on the board, membership and avatar")
            .WithDescription("`{handle, hunterscore, rank, membership, avatar}` (the handle as the account spells it, the membership's title, \"\" without one, "
                             + "the avatar's picture URL); 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/statistics", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.StatisticsPage(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterStatistics").WithSummary("A hunter's statistics tab: weapons, animals, distance and hunt time")
            .WithDescription("`{weapons: [{weapon, ammo, shots, accuracy, kills, killsPerHit, longest}], animals: [{name, spotted, tracked, harvested}], collectables: [], "
                             + "distanceKm, huntSeconds}`: the restored statistics plus the closed server hunts; 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/skills", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicSkills(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterSkills").WithSummary("A hunter's skills: the species' spotting and tracking, the weapons' shooting")
            .WithDescription("`{species: [{name, spotting, tracking}], weapons: [{name, group, value}]}`, each number a level plus the fraction of its span (as Skill/list); "
                             + "404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/trophies", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicTrophies(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterTrophies").WithSummary("One page of a hunter's trophies")
            .WithDescription($"`{{page, pages, total, rows: [{{name, date}}]}}`, {State.TrophiesPageSize} rows a page: the restored trophies, then the harvests made here (as `/game/user/trophies`), "
                             + "the hidden ones left out; `name` is the species' name, `date` the day of the harvest (UTC, yyyy-MM-dd). 404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/dogs", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicDogs(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterDogs").WithSummary("A hunter's dogs: name, level and skills")
            .WithDescription("`{dogs: [{name, type, level, percent, skills: [{title, level}], trophies: [string]}]}`, one per owned dog (`type` is the item's name, `level` from its experience "
                             + "and the connect's `dog_skills`, `percent` the way to the next level); 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/achievements", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicAchievements(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterAchievements").WithSummary("A hunter's achievements: the categories' progress, the number unlocked and the latest")
            .WithDescription("`{categories: [{name, level, done, total}], unlocked, recent: [{title, description, date}]}` over the connect's achievement definitions: `done` of `total` thresholds "
                             + "have an earn time, `unlocked` counts them all, `recent` the last ten by earn time (`date` UTC, yyyy-MM-dd); 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/friends", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicFriends(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterFriends").WithSummary("One page of a hunter's friends")
            .WithDescription($"`{{page, pages, total, rows: [{{handle, hunterscore}}]}}`, {State.FriendsPageSize} rows a page, by handle (upper case, ordinal): the friends of docs/server.md#friends. "
                             + "404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        app.MapGroup("/leaderboards").RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)")
            .MapGet("/{kind}", (string kind, HttpContext ctx) =>
            {
                if (!Store.GameColumns.ContainsKey(kind)) return Json(404, new JsonObject { ["error"] = "no such board" });
                var page = int.TryParse(ctx.Request.Query["page"], out var p) && p > 1 ? p : 1;
                var board = state.Store.GameBoard(kind, state.LocalAccount);
                var total = board.Count;
                var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
                page = Math.Min(page, pages);
                var rows = board.Skip((page - 1) * PageSize).Take(PageSize).Select((r, i) => (Rank: (page - 1) * PageSize + i + 1, r.Handle, r.Score));
                return Json(200, new JsonObject
                {
                    ["page"] = page, ["pages"] = pages, ["total"] = total,
                    ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject { ["rank"] = r.Rank, ["handle"] = r.Handle, ["value"] = r.Score })]),
                });
            })
            .WithName("GameBoard").WithSummary("One page of a minigame's board: lanebandit or bustthrough")
            .WithDescription($"`{{page, pages, total, rows: [{{rank, handle, value}}]}}`, {PageSize} rows a page, highest first; equal scores in account id order. The best scores restored from the players' backups (statistics); an account without one, or with 0, is not on it, nor is a banned one. 404 for another kind.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
    }
}
