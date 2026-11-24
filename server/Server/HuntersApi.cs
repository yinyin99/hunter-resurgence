// The hunterscore board, the hunters' profiles and the minigames' boards, on the website's host: every /hunters/ route for a signed-in player only (the session cookie; a hunt page also shows his own taxidermy offers).
// No board lists every player: each shows its top 100 and his own window (docs/server.md#who-sees-whom). The website's server calls them over SERVER_URL, the launcher on the public domain: the reverse proxy routes /hunters/ and the boards under /leaderboards/ here. Online only.
using System.Text.Json.Nodes;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class HuntersApi
{
    public const int PageSize = 30;

    /// <summary>The species boards: the rows of the overview for each species and of a page of a species' top 100.</summary>
    public const int OverviewTop = 3, SpeciesPage = 25;

    static Reply Json(int status, JsonNode body) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")], Py.DumpBytes(body), "hunters");

    /// <summary>The reply of a /hunters/ route to a request without a session: as the account routes refuse it.</summary>
    static Reply SignedOut => Json(401, new JsonObject { ["status"] = "signed_out", ["message"] = "Sign in first." });

    public static void MapHunters(this WebApplication app, Game game)
    {
        var state = game.State!;
        var site = $"https://{game.Web}";  // the avatar pictures are on the website's own host, like the gallery's
        long? SignedIn(HttpContext ctx) => SignIn.CookieOf(ctx.Request.Headers.Cookie.ToString(), SignIn.SessionCookie) is { } c ? state.Store.ResolveWebSession(c) : null;
        if (!state.Online) return;
        var group = app.MapGroup("/hunters").RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)")
            .AddEndpointFilter(async (f, next) =>
            {
                if (SignedIn(f.HttpContext) is not { } account) return SignedOut;
                // every read counts in the player's read limit (State.ReadAllowed): walking the profiles is slow and raises the alert
                if (!state.ReadAllowed(account)) return Json(429, new JsonObject { ["status"] = "rate_limited", ["message"] = "Too many reads: wait a minute." });
                return await next(f);
            });

        group.MapGet("", (HttpContext ctx) =>
            {
                if (SignedIn(ctx) is not { } me) return SignedOut;
                var top = state.Store.Board(state.LocalAccount, 0, State.BoardTop);
                var pages = Math.Max(1, (top.Count + PageSize - 1) / PageSize);
                var page = Math.Min(int.TryParse(ctx.Request.Query["page"], out var p) && p > 1 ? p : 1, pages);
                var (from, count) = State.Window(state.Store.Rank(me, state.LocalAccount));
                JsonNode? Row((long Rank, long Id, string Handle, long Hunterscore) r) =>
                    new JsonObject { ["rank"] = r.Rank, ["handle"] = r.Handle, ["hunterscore"] = r.Hunterscore, ["avatar"] = state.AvatarUrl(r.Id, site) };
                return Json(200, new JsonObject
                {
                    ["page"] = page, ["pages"] = pages,
                    ["rows"] = new JsonArray([.. top.Skip((page - 1) * PageSize).Take(PageSize).Select(Row)]),
                    ["around"] = new JsonArray([.. (count > 0 ? state.Store.Board(state.LocalAccount, from, count) : []).Select(Row)]),
                });
            })
            .WithName("HuntersBoard").WithSummary("One page of the top 100 of the hunterscore board, and the signed-in player's window")
            .WithDescription($"`{{page, pages, rows: [{{rank, handle, hunterscore, avatar}}], around: [...]}}`: the top {State.BoardTop} in pages of {PageSize} rows, highest first; equal hunterscores in account id order. `around` holds the rows of the signed-in player's place "
                             + $"and the {State.BoardAround} above and below it that are past the top {State.BoardTop} (empty in the top {State.BoardTop - State.BoardAround}); no other row is ever answered. Banned accounts are not on it. `avatar` is the picture of the player's page (docs/server.md#avatars), null for none. 401 `signed_out` without a session.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(401, contentType: "application/json");

        group.MapGet("/{handle}", (string handle) => state.PublicAccount(handle) is { } id && state.PublicUser(id) is var p
                ? Json(200, new JsonObject
                {
                    ["handle"] = p["handle"]?.DeepClone(), ["hunterscore"] = p["hunterscore"]?.DeepClone(), ["rank"] = p["rank"]?.DeepClone(),
                    ["membership"] = p["membershipTitle"]?.DeepClone() ?? "", ["avatar"] = state.AvatarUrl(id, site), ["background"] = state.ProfileBackground(id),
                })
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("Hunter").WithSummary("A hunter's profile header: hunterscore, place on the board, membership, avatar and header picture")
            .WithDescription("`{handle, hunterscore, rank, membership, avatar, background}` (the handle as the account spells it, the membership's title, \"\" without one, "
                             + "the avatar's picture URL (docs/server.md#avatars, null for none), the header picture the player chose as the restored profile names it, null without one); 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/statistics", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.StatisticsPage(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterStatistics").WithSummary("A hunter's statistics tab: weapons, animals, distance and hunt time")
            .WithDescription("`{weapons: [{weapon, ammo, shots, accuracy, kills, killsPerHit, longest}], animals: [{name, spotted, tracked, harvested}], collectables: [], "
                             + "distanceKm, distanceKmHere, huntSeconds, huntSecondsHere}`: the restored statistics plus the server hunts (the open one so far); `distanceKmHere` is the server hunts' distance alone; "
                             + "`huntSecondsHere` is the part played on this server only, in whole seconds "
                             + "(hunts that started before the setting `playtime_since` left out; a closed hunt with no heartbeat after its start counts 0); 404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/skills", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicSkills(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterSkills").WithSummary("A hunter's skills: the species' spotting and tracking, the weapons' shooting")
            .WithDescription("`{species: [{name, image, spotting, tracking}], weapons: [{id, name, variation, group, value}]}`, each number a level plus the fraction of its span (as Skill/list); `image` is the species' define in lower case (null without one), `id` the weapon's item id, `variation` its variation name (null without one); the dog species and species 56 are left out. "
                             + "404 for a handle that is unknown or banned.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/trophies", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicTrophies(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterTrophies").WithSummary("One page of a hunter's trophies")
            .WithDescription($"`{{page, pages, total, rows: [{{name, date, competitionId, competition, image}}]}}`, {State.TrophiesPageSize} rows a page: the restored competition trophies (`Trophy/list`), "
                             + "the hidden ones left out; `name` is the trophy's name, `date` the day it was won (UTC, yyyy-MM-dd), `competitionId` and `competition` the competition's id and name (null without them), "
                             + "`image` the picture's file name without the extension (null without one). 404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/dogs", (string handle) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicDogs(id))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterDogs").WithSummary("A hunter's dogs: name, level and skills")
            .WithDescription("`{dogs: [{id, name, type, variation, level, percent, skills: [{skill, title, type, level}], ribbons}]}`, one per owned dog (`id` is the item's id, `type` the item's name, `variation` its variation name "
                             + "or null, `level` from its experience and the connect's `dog_skills`, `percent` the way to the next level, `ribbons` the ribbon trophies reached, `skills` the breed skills once each); 404 for a handle that is unknown or banned.")
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

        group.MapGet("/{handle}/best", (string handle, HttpContext ctx) =>
            {
                var seasons = state.Store.Seasons();
                var q = ctx.Request.Query["season"].ToString();
                long season = 0;
                if (q != "" && !(long.TryParse(q, out season) && (season == 0 || seasons.Any(s => s.No == season))))
                    return Json(404, new JsonObject { ["error"] = "no such season" });
                return state.PublicAccount(handle) is { } id ? Json(200, state.PublicBest(id, season)) : Json(404, new JsonObject { ["error"] = "not found" });
            })
            .WithName("HunterBest").WithSummary("A hunter's personal bests of a season, one per species")
            .WithDescription("`{rows: [{species, score, range, weapon, ts, kill}]}`, one per species with a best score in `season` (0 all time, the default; 1 the restored harvests, 2 and later this server's seasons, docs/server.md#leaderboards-and-seasons): "
                             + "`range` in metres, `ts` Unix seconds, `weapon` the item's name (\"\" when unknown); 404 for a handle that is unknown or banned and for a season that does not exist.")
            .QueryParams(("season", "the season number, 0 all time"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/friends", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id && SignedIn(ctx) == id
                ? Json(200, state.PublicFriends(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1, site))
                : Json(403, new JsonObject { ["error"] = "not yours" }))
            .WithName("HunterFriends").WithSummary("One page of the signed-in player's own friends")
            .WithDescription($"`{{page, pages, total, rows: [{{handle, hunterscore, avatar}}]}}`, {State.FriendsPageSize} rows a page, by handle (upper case, ordinal): the friends of docs/server.md#friends. "
                             + "Only for the signed-in player's own handle (a player sees only his own friends): 403 for any other handle, known or not.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(403, contentType: "application/json");

        group.MapGet("/{handle}/hunts", (string handle, HttpContext ctx) => state.PublicAccount(handle) is { } id
                ? Json(200, state.PublicHunts(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterHunts").WithSummary("One page of a hunter's hunt history")
            .WithDescription($"`{{page, pages, total, rows: [{{id, start, seconds, reserve, lodge, collectables, kills}}]}}`, {State.HuntsPageSize} rows a page, newest first: the hunts made here, then the restored ones "
                             + "(as `Expedition/list`); `start` is Unix seconds, `seconds` the time since it started for a hunt that is open, `lodge` \"#id\" (the catalogue has no lodge names). 404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/hunts/{id}", (string handle, string id, HttpContext ctx) => state.PublicAccount(handle) is { } account
                && (id == "latest" ? state.PublicHunt(account, null, SignedIn(ctx) == account, SignedIn(ctx) ?? 0, $"https://{game.Web}") : long.TryParse(id, out var n) ? state.PublicHunt(account, n, SignedIn(ctx) == account, SignedIn(ctx) ?? 0, $"https://{game.Web}") : null) is { } hunt
                ? Json(200, hunt)
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterHunt").WithSummary("One hunt of a hunter: its statistics and its kills")
            .WithDescription("`{id, reserve, lodge, start, end, weapons, animals, collectables, achievements, distanceKm, seconds, antlers, kills}` of `Public_user/expedition` (`id` a hunt id, or `latest`): `weapons` and `animals` are the "
                             + "rows of `/hunters/<handle>/statistics` for the hunt (those of its events so far while it is open, `end` null, `distanceKm` 0 and `seconds` the time since it started), `collectables`, `achievements` and `antlers` are empty, `kills` are `{id, species, weapon, ammo, distanceM, score, weightKg, when, sheet}` "
                             + "of the last hit (`when` Unix seconds, `sheet` true when `/hunters/<handle>/score/<id>` answers). A signed-in player (the `hr_session` cookie) sees each kill's `photo`, the keyed link of the picture on the website's host (null for a kill without one); on his own hunt a kill made here also has "
                             + "`taxidermy: {end, ems, gms, skull, done}`: `end` (ISO UTC) is 72 hours after the harvest (after the day the taxidermy went live, for an older harvest), `ems` and `gms` the price, `skull` whether the species has a skull to mount, `done` `taxidermy`, `skull` or null; "
                             + "a restored kill and anyone else's view have none. 404 for a handle that is unknown or banned, and for a hunt that is unknown.")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        group.MapGet("/{handle}/score/{kill}", (string handle, string kill) => state.PublicAccount(handle) is { } id && long.TryParse(kill, out var killId) && state.PublicSheet(id, killId) is { } sheet
                ? Json(200, sheet)
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterScore").WithSummary("The score sheet of a kill, as data")
            .WithDescription("The sheet of `/user/<handle>/score/` (the website's page) as JSON: `kind` (`score` for a typical antler sheet, `antler` for the others), `facts`, `rows` (`{label, a, b, c}`), `abnormal` (`{right, left}`), "
                             + "`sums`, then the kill's `species`, `weapon`, `scope`, `ammo`, `distanceM`, `state`, `bodyPart`, `stance`, `platform`, `weightKg`, `type`, `woundTime`, `integrity`, `shotLocation`, `shots`, `when` (Unix seconds), "
                             + "`expeditionId`, `score` and `harvestValue`. 404 for a handle that is unknown or banned, and for a kill without a harvest here (a restored one).")
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
        Reply Refuse(int status, string kind, string message) => Json(status, new JsonObject { ["status"] = kind, ["message"] = message, ["report"] = null });
        group.MapGet("/{handle}/gallery", (string handle, HttpContext ctx) => SignedIn(ctx) is not { } viewer ? SignedOut
                : state.PublicAccount(handle) is { } id ? Json(200, state.PublicGallery(id, int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1, $"https://{game.Web}", viewer))
                : Json(404, new JsonObject { ["error"] = "not found" }))
            .WithName("HunterGallery").WithSummary("One page of a hunter's gallery")
            .WithDescription($"`{{page, pages, total, rows: [{{id, type, url, thumbnail, label, animal, kill, share}}]}}`, {State.GalleryPageSize} rows a page, newest first: the photos of `Gallery/list` "
                             + "(`url` the signed-in player's link of the picture (`/photos/...`: the original, on every gallery), `thumbnail` the same link, for the tile, `animal` the species of the kill it shows, `label` that kill's score, `kill` its id when `/hunters/<handle>/score/<kill>` answers, else null; "
                             + "`share`, on the player's own rows only, the bare link of the original, for the Share window). 401 `signed_out` without a session; 404 for a handle that is unknown or banned.")
            .QueryParams(("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(401, contentType: "application/json").Produces(404, contentType: "application/json");

        // the photos of the gallery tab are on the website's own domain: the game's host is not one a browser can reach
        app.MapGet(State.PHOTO_PREFIX + "{**name}", (string name, HttpContext ctx) =>
            {
                var q = ctx.Request.Query;
                var (status, file) = state.WebPhoto(name, SignedIn(ctx) ?? state.LinkViewer(name, q["viewer"], q["until"], q["key"]));
                return file is not null  // never kept by the browser: the next read asks again, with the session it has then
                    ? new Reply(200, [("Content-Type", "image/jpeg"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"), ("Content-Security-Policy", "sandbox")], File.ReadAllBytes(file), "state")
                    : status switch
                    {
                        401 => SignedOut,
                        429 => Refuse(429, "rate_limited", "Too many reads: wait a minute."),
                        _ => new Reply(404, [], [], "state"),
                    };
            })
            .RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)")
            .WithName("WebPhoto").WithSummary("A stored photo, on the website's host")
            .WithDescription("The photos of `GET /hunters/<handle>/gallery`: `<account id>/<name>`, for a signed-in player: the `hr_session` cookie, or the keyed link of the gallery's rows (`viewer`, `until`, `key`; the launcher's pages send no cookie). "
                             + "The file is the original, for every signed-in player; every read of another player's picture counts in the account's picture limit (600 a minute, separate from the profile reads; 429 `rate_limited`). 401 `signed_out` without a session or a valid link. `Cache-Control: no-store`. "
                             + "The imported avatar (`avatar.jpg`) is served to anyone. The game's host serves the originals to the game as before.")
            .QueryParams(("viewer", "the account the link is for"), ("until", "Unix seconds the link holds until"), ("key", "the server's key of the link"))
            .Produces(200, contentType: "image/jpeg").Produces(401, contentType: "application/json").Produces(429, contentType: "application/json").Produces(404);

        var boards = app.MapGroup("/leaderboards").RequireHost(game.Web).WithGroupName("game").WithTags("Hunters (web_host)")
            .AddEndpointFilter(async (f, next) => SignedIn(f.HttpContext) is null ? SignedOut : await next(f));

        // the season a request asks for: `season` (0 all time, else a stored one), the current one when missing; null for none such
        long? SeasonOf(HttpContext ctx)
        {
            var seasons = state.Store.Seasons();
            var q = ctx.Request.Query["season"].ToString();
            return q == "" ? seasons[^1].No : long.TryParse(q, out var n) && (n == 0 || seasons.Any(s => s.No == n)) ? n : null;
        }
        JsonObject RowJson(State.BoardRow r) => new()
        {
            ["rank"] = r.Rank, ["handle"] = r.Handle, ["value"] = r.Value, ["weaponName"] = state.WeaponName(r.Weapon),
            ["ts"] = r.Ts, ["kill"] = r.Kill, ["avatar"] = state.AvatarUrl(r.Account, site),
        };
        Reply NoSuchSeason() => Json(404, new JsonObject { ["error"] = "no such season" });

        boards.MapGet("/seasons", () =>
            {
                var seasons = state.Store.Seasons();
                return Json(200, new JsonObject
                {
                    ["current"] = seasons[^1].No,
                    ["seasons"] = new JsonArray([.. seasons.Select(s => (JsonNode?)new JsonObject { ["no"] = s.No, ["start"] = s.Start, ["end"] = s.End })]),
                });
            })
            .WithName("LeaderboardSeasons").WithSummary("The seasons of the species boards")
            .WithDescription("`{current, seasons: [{no, start, end}]}`, oldest first: `start` and `end` Unix seconds (both inclusive), `end` null for the current season. Season 0 (all time) is not listed; every board and the Hall of Fame also take it. "
                             + "Every /leaderboards/ route answers 401 `signed_out` without a session.")
            .Produces(200, contentType: "application/json");

        boards.MapGet("/halloffame", (HttpContext ctx) => SeasonOf(ctx) is not { } season ? NoSuchSeason()
                : Json(200, new JsonObject
                {
                    ["rows"] = new JsonArray([.. state.HallOfFame(season).Select(h => (JsonNode?)new JsonObject
                    {
                        ["id"] = h.Species, ["species"] = state.SpeciesLabel(h.Species),
                        ["score"] = h.Score is { } s ? RowJson(s) : null, ["range"] = h.Range is { } r ? RowJson(r) : null,
                    })]),
                }))
            .WithName("LeaderboardHallOfFame").WithSummary("The Hall of Fame of an ended season")
            .WithDescription("`{rows: [{id, species, score: Row, range: Row}]}`, species id order: the best score and the best range of each species of the ended season `season`; `rows` is empty for season 0 (all time) and the current season, which have no Hall of Fame. "
                             + "`Row` is `{rank, handle, value, weaponName, ts, kill, avatar}` as on the boards. 404 for a season that does not exist.")
            .QueryParams(("season", "the season number"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        boards.MapGet("/{kind}", (string kind, HttpContext ctx) =>
            {
                if (kind is "score" or "range")
                    return SeasonOf(ctx) is not { } season ? NoSuchSeason()
                        : Json(200, new JsonObject
                        {
                            ["boards"] = new JsonArray([.. state.Boards(kind == "range", season).Select(b => (JsonNode?)new JsonObject
                            {
                                ["species"] = b.Key, ["name"] = state.SpeciesLabel(b.Key), ["top"] = new JsonArray([.. b.Value.Take(OverviewTop).Select(r => (JsonNode?)RowJson(r))]),
                            })]),
                        });
                if (!Store.GameIds.ContainsKey(kind)) return Json(404, new JsonObject { ["error"] = "no such board" });
                var board = state.Store.GameBoard(kind, state.LocalAccount);
                var top = Math.Min(board.Count, State.BoardTop);
                var pages = Math.Max(1, (top + PageSize - 1) / PageSize);
                var page = Math.Min(int.TryParse(ctx.Request.Query["page"], out var p) && p > 1 ? p : 1, pages);
                var (from, count) = State.Window(SignedIn(ctx) is { } me ? board.FindIndex(r => r.Id == me) + 1 : 0);
                JsonArray Rows(int skip, int take) =>
                    new([.. board.Skip(skip).Take(take).Select((r, i) => (JsonNode?)new JsonObject { ["rank"] = skip + i + 1, ["handle"] = r.Handle, ["value"] = r.Score, ["avatar"] = state.AvatarUrl(r.Id, site) })]);
                return Json(200, new JsonObject
                {
                    ["page"] = page, ["pages"] = pages,
                    ["rows"] = Rows((page - 1) * PageSize, Math.Min(PageSize, top - (page - 1) * PageSize)),
                    ["around"] = Rows(from, count),
                });
            })
            .WithName("GameBoard").WithSummary("The species boards of a season (score or range), or one page of a minigame's board (lanebandit or bustthrough)")
            .WithDescription($"`score` and `range`: `{{boards: [{{species, name, top: [Row]}}]}}`, one per species with a ranked hunter in `season` (0 all time, the current season when missing), species id order, the best {OverviewTop} each; "
                             + "`Row` is `{rank, handle, value, weaponName, ts, kill, avatar}` (`value` the score, or the range in metres; `weaponName` the weapon item's name or \"\"; `ts` the kill's Unix seconds; `kill` its id for `/hunters/<handle>/score/<kill>`, 0 for a restored kill). "
                             + $"`lanebandit` and `bustthrough`: `{{page, pages, rows: [{{rank, handle, value, avatar}}], around: [...]}}`, the top {State.BoardTop} in pages of {PageSize} rows, highest first; equal scores in account id order; `around` as on /hunters (the signed-in player's window past the top). The best scores: the higher of the restored one (statistics) and the best the account's hunts reported; an account without one, or with 0, is not on it, nor is a banned one. "
                             + "404 for another kind and for a season that does not exist.")
            .QueryParams(("season", "the season number"), ("page", "1 for the first (minigames)"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        boards.MapGet("/{kind}/{species:long}", (string kind, long species, HttpContext ctx) =>
            {
                if (kind is not ("score" or "range")) return Json(404, new JsonObject { ["error"] = "no such board" });
                if (SeasonOf(ctx) is not { } season) return NoSuchSeason();
                var all = state.Boards(kind == "range", season).GetValueOrDefault(species) ?? [];
                var top = Math.Min(all.Count, State.BoardTop);
                var pages = Math.Max(1, (top + SpeciesPage - 1) / SpeciesPage);
                var page = Math.Min(int.TryParse(ctx.Request.Query["page"], out var p) && p > 1 ? p : 1, pages);
                var (from, count) = State.Window(SignedIn(ctx) is { } me ? all.FirstOrDefault(r => r.Account == me)?.Rank ?? 0 : 0);
                return Json(200, new JsonObject
                {
                    ["page"] = page, ["pages"] = pages,
                    ["rows"] = new JsonArray([.. all.Take(top).Skip((page - 1) * SpeciesPage).Take(SpeciesPage).Select(r => (JsonNode?)RowJson(r))]),
                    ["around"] = new JsonArray([.. all.Skip(from).Take(count).Select(r => (JsonNode?)RowJson(r))]),
                });
            })
            .WithName("SpeciesBoard").WithSummary("One page of the top 100 of a species' score or range board")
            .WithDescription($"`{{page, pages, rows: [Row], around: [Row]}}`: the best {State.BoardTop} hunters of the species `species` (its id) in `season` (0 all time, the current season when missing), {SpeciesPage} rows a page; `Row` as on the overview; `around` as on /hunters (the signed-in player's window past the top). A species nobody ranks: `rows` empty. 404 for a kind other than `score` or `range` and for a season that does not exist.")
            .QueryParams(("season", "the season number"), ("page", "1 for the first"))
            .Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");

        boards.MapGet("/{kind}/{species:long}/me", (string kind, long species, HttpContext ctx) =>
            {
                if (kind is not ("score" or "range")) return Json(404, new JsonObject { ["error"] = "no such board" });
                if (SignedIn(ctx) is not { } account) return SignedOut;
                if (SeasonOf(ctx) is not { } season) return NoSuchSeason();
                return (state.Boards(kind == "range", season).GetValueOrDefault(species) ?? []).FirstOrDefault(r => r.Account == account) is { } row
                    ? Json(200, RowJson(row)) : Json(404, new JsonObject { ["error"] = "not ranked" });
            })
            .WithName("SpeciesBoardMe").WithSummary("The signed-in hunter's place on a species' board")
            .WithDescription("`Row` of the hunter the session cookie (`hr_session`) names, with its place on the whole board (also beyond the top 100); 404 when it has no ranked harvest of the species in the season, 401 when signed out.")
            .QueryParams(("season", "the season number"))
            .Produces(200, contentType: "application/json").Produces(401, contentType: "application/json").Produces(404, contentType: "application/json");
    }
}
