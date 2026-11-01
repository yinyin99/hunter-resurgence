// The endpoints of the sign-in (Auth.cs) on the website's host and the signup host, the launcher's own calls and the
// replies that hand the game its tokens. Online only: with offline_mode every one of these falls through to the replay,
// as before (the local server's player needs no sign-in).
using System.Text.Json.Nodes;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class AuthApi
{
    static Func<HttpContext, Task<Reply>> Do(Func<Req, Task<Reply>> handle) => async ctx => await handle(await Req.ReadAsync(ctx));

    static Func<HttpContext, Task<Reply>> Do(Func<Req, Reply> handle) => Do(r => Task.FromResult(handle(r)));

    public static void MapAuth(this WebApplication app, Game game)
    {
        var state = game.State!;
        var steam = app.Services.GetRequiredService<SteamOpenId>();
        var auth = new SignIn(state, steam);

        var group = app.MapGroup("/auth").RequireHost(game.Web, game.Signup).WithGroupName("game").WithTags("Sign-in (web_host, signup_host)");
        group.MapGet("/steam/login", Do(auth.Start)).WithName("SteamLogin").WithSummary("Send the browser to Steam")
             .WithDescription("`next`: a local path to return to; `link`: the code of a launcher waiting for this sign-in.")
             .QueryParams(("next", "local path"), ("link", "launcher code"));
        group.MapGet("/steam/return", Do(auth.Return)).WithName("SteamReturn").WithSummary("Steam's answer; sets the session cookie");
        group.MapMethods("/handle", ["GET", "POST"], Do(auth.ChooseHandle)).WithName("ChooseHandle").WithSummary("Pick a name at the first sign-in");
        group.MapMethods("/confirm", ["GET", "POST"], Do(auth.Confirm)).WithName("ConfirmLauncher").WithSummary("Let the launcher that shows the code sign in");
        group.MapPost("/link", Do(auth.NewLink)).WithName("NewLink").WithSummary("A launcher asks to be signed in by the player's browser");
        group.MapGet("/link/{code}", Do(auth.PollLink)).WithName("PollLink").WithSummary("The launcher waits: pending, or ok with the cookie");
        group.MapGet("/me", Do(auth.Me)).WithName("AuthMe").WithSummary("Who the session cookie is: `{userId, handle}` or 401");
        group.MapPost("/signout", Do(auth.SignOut)).WithName("AuthSignOut").WithSummary("End the session (needs `X-Requested-With` or a same-origin `Origin`)");
        group.MapPost("/launch", Do(r => auth.Launch(r, game.Tokens!))).WithName("AuthLaunch").WithSummary("A launch URL with fresh tokens, for the start page");

        // the launcher's start page; offline: the recorded one
        app.MapGet("/", Do(r => state.Online ? auth.StartPage(r) : game.Fallback(r))).RequireHost(game.Signup).ExcludeFromDescription();
    }
}
