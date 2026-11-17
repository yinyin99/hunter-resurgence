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
    }
}
