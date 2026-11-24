// How many hunters are online and how many multiplayer games are open, on the website's host: public, no cookie, counts only. The launcher calls it on the
// public domain and the website's server over SERVER_URL, so the reverse proxy routes /stats/ here. Online only.
using System.Text.Json.Nodes;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class StatsApi
{
    public static void MapStats(this WebApplication app, Game game)
    {
        var state = game.State!;
        if (!state.Online) return;
        var stats = app.MapGroup("/stats").RequireHost(game.Web).WithGroupName("game").WithTags("Stats (web_host)");
        stats.MapGet("/game", () => new Reply(200, [("Content-Type", "application/json"), ("Cache-Control", "public, max-age=300"), ("X-Content-Type-Options", "nosniff")],
                Py.DumpBytes(new JsonObject { ["build"] = game.Cfg["game_build"]?.DeepClone() }), "stats"))
            .WithName("GameStats").WithSummary("The game build the server supports")
            .WithDescription("`{build}`: the Steam build id of the game version the server supports (settings.json `game_build`), null when the operator set none. The launcher compares it with the installed game's. Sent with `Cache-Control: public, max-age=300`.")
            .Produces(200, contentType: "application/json");
        stats.MapGet("/online", () =>
            {
                var (players, games) = state.OnlineCounts();
                return new Reply(200, [("Content-Type", "application/json"), ("Cache-Control", $"public, max-age={State.OnlineCacheSeconds}"), ("X-Content-Type-Options", "nosniff")],
                    Py.DumpBytes(new JsonObject { ["players"] = players, ["games"] = games }), "stats");
            })
            .WithName("OnlineStats").WithSummary("How many hunters are online and how many multiplayer games are open")
            .WithDescription($"`{{players, games}}`, counts only: `players` the accounts with a hunt open and a heartbeat in the last {State.HEARTBEAT_TIMEOUT} s (a banned account is not counted), `games` the open multiplayer sessions "
                             + $"(the list of `GET /catalog/games`). Counted at most once in {State.OnlineCacheSeconds} s and sent with `Cache-Control: public, max-age={State.OnlineCacheSeconds}`.")
            .Produces(200, contentType: "application/json");
        stats.MapGet("/hunting", (HttpContext ctx) =>
            {
                var signedIn = SignIn.CookieOf(ctx.Request.Headers.Cookie.ToString(), SignIn.SessionCookie) is { } cookie && state.Store.ResolveWebSession(cookie) is not null;
                return signedIn
                    ? new Reply(200, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")], Py.DumpBytes(state.HuntingStatistics()), "stats")
                    : new Reply(401, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")],
                                Py.DumpBytes(new JsonObject { ["status"] = "signed_out", ["message"] = "Sign in first." }), "stats");
            })
            .WithName("HuntingStats").WithSummary("Hunting statistics of the whole server: per reserve and per species")
            .WithDescription("`{reserves: [{reserve, hours, gmPerHour, gmPerHarvest, harvestsPerHour, players}], species: [{species, hours, gmPerHour, gmPerHarvest, harvestsPerHour, players, bestReserve}], since}` for a signed-in player (the session cookie; 401 `signed_out` without one). "
                             + $"Server-wide figures only, never a player's name, handle or rank: a row built from fewer than {State.HuntingMinPlayers} players is left out, and `bestReserve` (null when no reserve has that many players) counts only reserves of that many. `hours` is the playtime on this server since `since` (the setting `playtime_since`); "
                             + "a species' `hours` is the playtime on the reserves where its players harvested it. gm$ are the harvest rewards as paid (a time-limited bonus included); the mission, daily and dog rewards are not counted. "
                             + $"Computed on demand and kept {(int)State.HuntingTtl.TotalMinutes} minutes.")
            .Produces(200, contentType: "application/json").Produces(401, contentType: "application/json");
    }
}
