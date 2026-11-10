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
            .WithDescription("`{species: [{name, image, spotting, tracking}], weapons: [{id, name, group, value}]}`, each number a level plus the fraction of its span (as Skill/list); `image` is the species' define in lower case (null without one), `id` the weapon's item id; the dog species and species 56 are left out. "
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

        group.MapGet("/{handle}/ranks", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicRanks(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterRanks").WithSummary("A hunter's ranks and milestones")
            .WithDescription("`{cap, ranks: [{id, category, name, value}], milestones: [{category, title, steps, value}]}`: `ranks` one per rank row of the facts file (`category` animals, "
                             + "weapons or collectables; `value` the count of `/game/user/<id>/ranks`, `cap` the most a row adds to the hunterscore), `milestones` one per achievement "
                             + "definition of the connect reply (`category` animals, weapons, exploration or day-mission; `steps` its thresholds, `value` the counter its path names); "
                             + "404 for a handle that is unknown or banned.")
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

        group.MapGet("/{handle}/hunts", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicHunts(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterHunts").WithSummary("One page of a hunter's hunt history")
            .WithDescription($"`{{page, pages, total, rows: [{{id, start, seconds, reserve, lodge, collectables, kills}}]}}`, {State.HuntsPageSize} rows a page, newest first: the hunts made here, then the restored ones "
                             + "(as `Expedition/list`); `start` is Unix seconds, `seconds` 0 for a hunt that is open, `lodge` \"#id\" (the catalogue has no lodge names). 404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/hunts/{id}", (string handle, string id) => state.PublicAccount(handle) is { } account && (id == "latest" ? state.PublicHunt(account, null) : long.TryParse(id, out var n) ? state.PublicHunt(account, n) : null) is { } hunt
                ? Json(200, hunt)
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterHunt").WithSummary("One hunt of a hunter: its statistics and its kills")
            .WithDescription("`{id, reserve, lodge, start, end, weapons, animals, collectables, achievements, distanceKm, seconds, antlers, kills}` of `Public_user/expedition` (`id` a hunt id, or `latest`): `weapons` and `animals` are the "
                             + "rows of `/hunters/<handle>/statistics` for the hunt (empty while it is open, `end` null), `collectables`, `achievements` and `antlers` are empty, `kills` are `{id, species, weapon, ammo, distanceM, score, weightKg, when, sheet}` "
                             + "of the last hit (`when` Unix seconds, `sheet` true when `/hunters/<handle>/score/<id>` answers). 404 for a handle that is unknown or banned, and for a hunt that is unknown.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/score/{kill}", (string handle, string kill) => state.PublicAccount(handle) is { } id && long.TryParse(kill, out var killId) && state.PublicSheet(id, killId) is { } sheet
                ? Json(200, sheet)
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterScore").WithSummary("The score sheet of a kill, as data")
            .WithDescription("The sheet of `/user/<handle>/score/` (the website's page) as JSON: `kind` (`score` for a typical antler sheet, `antler` for the others), `facts`, `rows` (`{label, a, b, c}`), `abnormal` (`{right, left}`), "
                             + "`sums`, then the kill's `species`, `weapon`, `scope`, `ammo`, `distanceM`, `state`, `bodyPart`, `stance`, `platform`, `weightKg`, `type`, `woundTime`, `integrity`, `shotLocation`, `shots`, `when` (Unix seconds), "
                             + "`expeditionId`, `score` and `harvestValue`. 404 for a handle that is unknown or banned, and for a kill without a harvest here (a restored one).")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
        group.MapGet("/{handle}/gallery", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicGallery(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1, $"https://{game.Web}"))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterGallery").WithSummary("One page of a hunter's gallery")
            .WithDescription($"`{{page, pages, total, rows: [{{id, type, url, thumbnail, label, animal}}]}}`, {State.GalleryPageSize} rows a page, newest first: the photos of `Gallery/list` "
                             + "(`url` the full picture, `thumbnail` its small one, `animal` the species of the kill it shows, `label` that kill's score). 404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        // the photos of the gallery tab are on the website's own domain: the game's host is not one a browser can reach
        app.MapGet(State.PHOTO_PREFIX + "{**name}", (string name) => state.PhotoFile(State.PHOTO_PREFIX + name) is { } file
                ? new Reply(200, [("Content-Type", "image/jpeg")], File.ReadAllBytes(file), "state") : new Reply(404, [], [], "state"))
            .RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)")
            .WithName("WebPhoto").WithSummary("A stored photo, on the website's host")
            .WithDescription("The photos of `GET /hunters/<handle>/gallery`, as the game host serves them: `<account id>/<file name>`; public, the link is the access.")
            .Produces(200, contentType: "image/jpeg").Produces(404);

        app.MapGroup("/leaderboards").RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)")
            .MapGet("/{kind}", (string kind, HttpContext ctx) =>
            {
                if (!Store.GameIds.ContainsKey(kind)) return Json(404, new JsonObject { ["error"] = "no such board" });
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
            .WithDescription($"`{{page, pages, total, rows: [{{rank, handle, value}}]}}`, {PageSize} rows a page, highest first; equal scores in account id order. The best scores: the higher of the restored one (statistics) and the best the account's hunts reported; an account without one, or with 0, is not on it, nor is a banned one. 404 for another kind.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
    }
}
