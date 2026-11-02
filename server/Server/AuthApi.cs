// The endpoints of the sign-in (Auth.cs) on the website's host and the signup host, the launcher's own calls and the
// replies that hand the game its tokens. Online only: with offline_mode every one of these falls through to the replay,
// as before (the local server's player needs no sign-in).
using System.Text.Json;
using System.Text.Json.Nodes;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class AuthApi
{
    // offline_mode: none of this is on, the replay answers
    static Func<HttpContext, Task<Reply>> Do(Game game, Func<Req, Task<Reply>> handle) => async ctx =>
    {
        var r = await Req.ReadAsync(ctx);
        return game.State!.Online ? await handle(r) : game.Fallback(r);
    };

    static Func<HttpContext, Task<Reply>> Do(Game game, Func<Req, Reply> handle) => Do(game, r => Task.FromResult(handle(r)));

    public static void MapAuth(this WebApplication app, Game game)
    {
        var state = game.State!;
        var steam = app.Services.GetRequiredService<SteamOpenId>();
        var auth = new SignIn(state, steam, siteHost: game.Web, gameWebHost: game.GameWeb);

        var group = app.MapGroup("/auth").RequireHost(game.Web, game.Signup, game.GameWeb).WithGroupName("game").WithTags("Sign-in (web_host, signup_host, game_web_host)");
        group.MapGet("/steam/login", Do(game, auth.Start)).WithName("SteamLogin").WithSummary("Send the browser to Steam")
             .WithDescription("`next`: a local path to return to; `link`: the code of a launcher waiting for this sign-in.")
             .QueryParams(("next", "local path"), ("link", "launcher code"));
        group.MapGet("/steam/return", Do(game, auth.Return)).WithName("SteamReturn").WithSummary("Steam's answer; sets the session cookie");
        group.MapMethods("/handle", ["GET", "POST"], Do(game, auth.ChooseHandle)).WithName("ChooseHandle").WithSummary("Pick a name at the first sign-in");
        group.MapMethods("/confirm", ["GET", "POST"], Do(game, auth.Confirm)).WithName("ConfirmLauncher").WithSummary("Let the launcher that shows the code sign in");
        group.MapPost("/link", Do(game, auth.NewLink)).WithName("NewLink").WithSummary("A launcher asks to be signed in by the player's browser");
        group.MapGet("/link/{code}", Do(game, auth.PollLink)).WithName("PollLink").WithSummary("The launcher waits: pending, or ok with the cookie");
        group.MapGet("/me", Do(game, auth.Me)).WithName("AuthMe").WithSummary("Who the session cookie is: `{userId, handle, hunterscore, rank}` or 401");
        group.MapPost("/signout", Do(game, auth.SignOut)).WithName("AuthSignOut").WithSummary("End the session (needs `X-Requested-With` or a same-origin `Origin`)");
        group.MapPost("/launch", Do(game, r => auth.Launch(r, game.Tokens!))).WithName("AuthLaunch").WithSummary("A launch URL with fresh tokens, for the start page");

        // the current build's credentials, on the game's web host: on in both modes (a local server's player is the local account)
        var gameWeb = app.MapGroup("/auth").RequireHost(game.GameWeb).WithGroupName("game").WithTags("Game credentials (game_web_host)");
        gameWeb.MapPost("/jwt/refresh", Always(r => Refresh(game, r))).WithName("JwtRefresh")
             .WithSummary("A fresh access JWT and refresh JWT")
             .WithDescription($"For a refresh JWT of this server (body `refresh_token` or the cookie `{ProtocolLiterals.RefreshCookie}`), else, on a local server, the local account. "
                              + "The access JWT expires in 900 s; a stored token is never replayed.")
             .Produces(200, contentType: "application/json").Produces(401, contentType: "application/json");
        gameWeb.MapPost("/emotelauncher/token", Always(_ => new(401, Json(), SteamTicketRefused, "auth"))).WithName("SteamTicket")
             .WithSummary("The launcher's Steam ticket: always refused, so the launcher shows the sign-in page")
             .Produces(401, contentType: "application/json");

        // the launcher's start page (the launcher loads /launcher from the game's web host); offline: the recorded one
        app.MapGet("/launcher", Do(game, r => auth.StartPage(r))).RequireHost(game.GameWeb).ExcludeFromDescription();
        // the old launcher's start page; offline: the recorded one
        app.MapGet("/", Do(game, r => auth.StartPage(r))).RequireHost(game.Signup).ExcludeFromDescription();
    }

    static Func<HttpContext, Task<Reply>> Always(Func<Req, Reply> handle) => async ctx => handle(await Req.ReadAsync(ctx));

    static readonly byte[] SteamTicketRefused = """{"error":{"code":"auth.invalid_steam_ticket","message":"Invalid steam ticket. Token verification failed","details":[{"loc":null,"msg":null,"type":null}]}}"""u8.ToArray();

    /// <summary>POST /auth/jwt/refresh: the game and the launcher page ask for a new access JWT before the old one runs out.
    /// Only a refresh JWT this server signed (`typ` refresh: body or cookie) is a credential; an access JWT, a session id or
    /// any other token gets the 401. A local server answers for its one account. ponytail: a refresh token is not rotated
    /// (the game may keep presenting the one of its launch URL); it ends with its 30 days, a ban or a delete.</summary>
    internal static Reply Refresh(Game game, Req r)
    {
        JsonObject? body = null;
        try { body = r.Body.Length is > 0 and < 4096 ? JsonNode.Parse(r.Body) as JsonObject : null; }
        catch (JsonException) { }
        var tokens = game.Tokens!;
        var account = new[] { (string?)body?["refresh_token"], SignIn.CookieOf(r, ProtocolLiterals.RefreshCookie) }
            .Select(t => t is { Length: > 0 } ? tokens.RefreshedAccount(t) : null).FirstOrDefault(a => a is not null)
            ?? (game.State!.Online ? null : game.State.LocalAccount);  // a local server's one player needs no credential
        if (account is not { } id)
            return new(401, Json(), """{"error":{"code":"auth.invalid_refresh_token","message":"Invalid refresh token","details":[]}}"""u8.ToArray(), "denied");
        var scope = r.Header("X-App-Id") == "client" ? GameTokens.GameScope : GameTokens.WebScope;
        var (access, refresh) = (tokens.Access(id, scope), tokens.Refresh(id, scope));
        return new(200, [("Content-Type", "application/json"), ("Cache-Control", "no-store"),
                         ("Set-Cookie", $"{ProtocolLiterals.AccessCookie}={access}; HttpOnly; Max-Age=63072000; Path=/; SameSite=lax; Secure"),
                         ("Set-Cookie", $"{ProtocolLiterals.RefreshCookie}={refresh}; HttpOnly; Max-Age={(long)Lifetimes.Refresh.TotalSeconds}; Path=/auth/jwt/refresh; SameSite=lax; Secure")],
                   Py.DumpBytes(new JsonObject { ["expires_in"] = 63072000, ["access_token"] = access, ["refresh_token"] = refresh }), "auth");
    }
}
