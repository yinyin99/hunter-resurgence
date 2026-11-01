// Sign-in with Steam on the website's host and on the signup host (the launcher's start page), the cookie session that
// follows it, and the tokens the game is given (session, access, refresh, the JWT of User/jwt). docs/server.md#sign-in-and-tokens.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Resurgence.Server;

/// <summary>How long each credential lives. The game's session has no renewal (the client keeps it for the whole
/// run), the access token is renewed with the refresh token (Oauth/token), the JWT is fetched again by the client
/// shortly before it expires. Every token resolves everywhere: these are limits on a leak, not different powers.</summary>
public static class Lifetimes
{
    public static readonly TimeSpan Session = TimeSpan.FromDays(7);
    public static readonly TimeSpan Access = TimeSpan.FromDays(1);
    /// <summary>Refresh tokens start with this letter (the other tokens are hex or a JWT, which starts with e): only they renew.</summary>
    public const string RefreshPrefix = "r";
    public static readonly TimeSpan Refresh = TimeSpan.FromDays(30);
    public static readonly TimeSpan Jwt = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Cookie = TimeSpan.FromDays(30);
    /// <summary>From the click on "sign in" to the end of the handle choice or the confirmation.</summary>
    public static readonly TimeSpan Login = TimeSpan.FromMinutes(15);
    /// <summary>The launcher waits this long for the player's browser.</summary>
    public static readonly TimeSpan Link = TimeSpan.FromMinutes(10);
}

public static class Handles
{
    static readonly Regex Valid = new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{2,19}\z", RegexOptions.ECMAScript);

    /// <summary>What is wrong with a handle, null when it is fine. Uniqueness (case-insensitive) is the database's.</summary>
    public static string? Problem(string handle) =>
        Valid.IsMatch(handle) ? null : "A name has 3 to 20 letters, digits, - or _ and starts with a letter or a digit.";
}

/// <summary>The game's credentials for one account: what Launch/start, User/jwt and Oauth/token hand out.</summary>
public sealed class GameTokens(Store store, string gameserverHost)
{
    /// <summary>The launch URL the old launcher hands to the game. The scheme carries the game's name (the client's
    /// environment check looks for it anywhere in the URL); the host must not contain dev or beta.</summary>
    public string LaunchUrl(long account) =>
        $"{ProtocolLiterals.LaunchScheme}://{gameserverHost}/{store.CreateToken(account, Lifetimes.Session)}/lang/en_US"
        + $"/access_token/{store.CreateToken(account, Lifetimes.Access)}/refresh_token/{store.CreateToken(account, Lifetimes.Refresh, Lifetimes.RefreshPrefix)}";

    public JsonObject Launch(long account) => new()
    {
        ["access_token"] = store.CreateToken(account, Lifetimes.Access),
        ["refresh_token"] = store.CreateToken(account, Lifetimes.Refresh, Lifetimes.RefreshPrefix),
        ["session"] = store.CreateToken(account, Lifetimes.Session),
        ["status"] = "ok",
    };

    /// <summary>A token in the shape of a JWT: the client reads the expiry from the payload to know when to ask again.
    /// The third part is random, not a signature; only this server resolves it.</summary>
    public string Jwt(long account)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var head = B64("""{"alg":"HS256","typ":"JWT"}""");
        var body = B64($$"""{"sub":"{{account}}","iat":{{now}},"exp":{{now + (long)Lifetimes.Jwt.TotalSeconds}}}""");
        return store.CreateToken(account, Lifetimes.Jwt, $"{head}.{body}.");
    }

    /// <summary>A fresh access token for a refresh token that resolves; the refresh token stays (it is not rotated).</summary>
    public JsonObject Renew(string refresh, long account) => new()
    {
        ["access_token"] = store.CreateToken(account, Lifetimes.Access),
        ["refresh_token"] = refresh,
        ["expires"] = DateTimeOffset.UtcNow.Add(Lifetimes.Access).ToUnixTimeSeconds(),
        ["user_id"] = account,
        ["account_id"] = account,
    };
}

/// <summary>The sign-in itself. One instance per server: the logins in progress are in memory (a restart only makes a
/// player start the sign-in again), the accounts and the cookie sessions are in the database.</summary>
public sealed class SignIn(State state, SteamOpenId steam, Func<DateTimeOffset>? clock = null)
{
    public const string SessionCookie = "hr_session";
    const string LoginCookie = "hr_login";

    /// <summary>One sign-in in progress in one browser. Steam: set when Steam confirmed the id. Account: set when the
    /// account exists (first sign-in: after the name). Link: the launcher waiting for it, if any.</summary>
    sealed class Login(string id, string next, string? link, DateTimeOffset expires)
    {
        public readonly string Id = id, Next = next;
        public readonly string? Link = link;
        public DateTimeOffset Expires = expires;
        public string? Steam;
        public long? Account;
    }

    /// <summary>A launcher that asked to be signed in: Account is set once the player confirmed in the browser.</summary>
    sealed class LinkEntry(DateTimeOffset expires)
    {
        public readonly DateTimeOffset Expires = expires;
        public long? Account;
    }

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly ConcurrentDictionary<string, Login> logins = new();
    readonly ConcurrentDictionary<string, LinkEntry> links = new();
    readonly Lock create = new();  // the next user id and the insert are one step: one server process (ADR 0001)
    const int MaxPending = 10_000;

    Store Store => state.Store;

    // ---- small pieces

    static string Rand(int bytes = 16) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    static string Origin(Req r) => "https://" + r.Host;

    static string ReturnTo(Req r, string login) => Origin(r) + "/auth/steam/return?login=" + login;

    public static string? CookieOf(Req r, string name) => CookieOf(r.Header("Cookie"), name);

    public static string? CookieOf(string? header, string name)
    {
        foreach (var part in (header ?? "").Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim() == name) return part[(eq + 1)..].Trim();
        }
        return null;
    }

    static string Set(string name, string value, string path, TimeSpan age) =>
        $"{name}={value}; Path={path}; Max-Age={(long)age.TotalSeconds}; HttpOnly; Secure; SameSite=Lax";

    static string Clear(string name, string path) => $"{name}=; Path={path}; Max-Age=0; HttpOnly; Secure; SameSite=Lax";

    static readonly (string, string)[] PageHeaders =
        [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"),
         ("Referrer-Policy", "same-origin"), ("X-Frame-Options", "DENY")];

    static Reply Html(int status, string html, params string[] cookies) =>
        new(status, [.. PageHeaders, .. cookies.Select(c => ("Set-Cookie", c))], Encoding.UTF8.GetBytes(html), "auth");

    static Reply Problem(int status, string text) => Html(status, AuthPages.Problem(text));

    static Reply Redirect(string to, params string[] cookies) =>
        new(302, [("Location", to), ("Cache-Control", "no-store"), .. cookies.Select(c => ("Set-Cookie", c))], [], "auth");

    static Reply Json(int status, JsonNode body, params string[] cookies) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"),
                     .. cookies.Select(c => ("Set-Cookie", c))], Py.DumpBytes(body), "auth");

    /// <summary>A local path only: after the sign-in the browser is never sent to another site.</summary>
    static string SafeNext(string? next) =>
        next is { Length: > 0 and <= 200 } && next[0] == '/' && !next.StartsWith("//", StringComparison.Ordinal) && next.All(c => c is > ' ' and < '\x7f' and not '\\')
            ? next : "/";

    /// <summary>A state-changing request comes from our own page: its Origin is ours, or (an old browser's XHR, which
    /// sends none) it carries the header a cross-site form cannot set.</summary>
    static bool OwnPost(Req r) =>
        r.Method == "POST" && (r.Header("Origin") is { } o ? o == Origin(r) : r.Header("X-Requested-With") is not null);

    long? Account(Req r) => CookieOf(r, SessionCookie) is { } cookie ? Store.ResolveWebSession(cookie) : null;

    Login? Current(Req r) =>
        CookieOf(r, LoginCookie) is { } id && logins.TryGetValue(id, out var login) && login.Expires > now() ? login : null;

    void Purge()
    {
        foreach (var (id, l) in logins) if (l.Expires <= now()) logins.TryRemove(id, out _);
        foreach (var (id, l) in links) if (l.Expires <= now()) links.TryRemove(id, out _);
    }

    // ---- the Steam round trip

    /// <summary>GET /auth/steam/login?next=/path[&link=code]: to Steam. A cookie ties the way back to this browser.</summary>
    public Reply Start(Req r)
    {
        var q = Py.ParseQs(r.Query);
        var link = q.TryGetValue("link", out var l) ? l[0] : null;
        if (link is not null && !(links.TryGetValue(link, out var entry) && entry.Expires > now()))
            return Problem(400, "This sign-in link ran out of time. Start again from the launcher.");
        Purge();
        if (logins.Count >= MaxPending) // full: the one that expires first goes, so a flood cannot lock the players out
            logins.TryRemove(logins.MinBy(kv => kv.Value.Expires).Key, out _);
        var login = new Login(Rand(), SafeNext(q.TryGetValue("next", out var n) ? n[0] : null), link, now() + Lifetimes.Login);
        logins[login.Id] = login;
        return Redirect(SteamOpenId.LoginUrl(ReturnTo(r, login.Id), Origin(r) + "/"), Set(LoginCookie, login.Id, "/auth", Lifetimes.Login));
    }

    /// <summary>GET /auth/steam/return: Steam's answer. A known Steam id signs in; a new one picks a name first.</summary>
    public async Task<Reply> Return(Req r)
    {
        var id = Py.ParseQs(r.Query).TryGetValue("login", out var l) ? l[0] : null;
        if (id is null || CookieOf(r, LoginCookie) != id || Current(r) is not { Steam: null } login)
            return Problem(400, "This sign-in ran out of time, or was started in another browser. Start again.");
        if (await steam.VerifyAsync(r.Query, ReturnTo(r, id)) is not { } steamId)
        {
            logins.TryRemove(id, out _);
            return Problem(400, "Steam did not confirm the sign-in. Start again.");
        }
        login.Steam = steamId;
        login.Expires = now() + Lifetimes.Login;
        return Store.AccountBySteam(steamId) is { } account ? Finish(login, account) : Redirect("/auth/handle");
    }

    /// <summary>The account is known: a launcher waits (ask the player to confirm), or this browser gets its cookie.</summary>
    Reply Finish(Login login, long account)
    {
        if (login.Link is not null)
        {
            login.Account = account;
            return Redirect("/auth/confirm");
        }
        logins.TryRemove(login.Id, out _);
        var cookie = Store.CreateWebSession(account, Lifetimes.Cookie);
        if (Store.ResolveWebSession(cookie) is null)  // a banned account gets no session
        {
            Store.DeleteWebSession(cookie);
            return Problem(403, "This account may not sign in.");
        }
        return Redirect(login.Next, Set(SessionCookie, cookie, "/", Lifetimes.Cookie), Clear(LoginCookie, "/auth"));
    }

    /// <summary>GET and POST /auth/handle: the first sign-in picks the name (Steam's is not read, nothing is stored of it).</summary>
    public Reply ChooseHandle(Req r)
    {
        if (Current(r) is not { Steam: { } steamId, Account: null } login)
            return Problem(400, "This sign-in ran out of time. Start again.");
        if (Store.AccountBySteam(steamId) is { } existing)  // another tab of this sign-in was faster
            return Finish(login, existing);
        if (r.Method == "GET") return Html(200, AuthPages.Handle(login.Id, "", null));
        var form = Py.ParseQs(Encoding.UTF8.GetString(r.Body));
        string F(string k) => form.TryGetValue(k, out var v) ? v[0] : "";
        if (!OwnPost(r) || F("csrf") != login.Id) return Problem(400, "This form is not from this server.");
        var handle = F("handle").Trim();
        var error = Handles.Problem(handle);
        long account = 0;
        if (error is null)
            lock (create)
            {
                if (Store.AccountByHandle(handle) is not null) error = "This name is taken.";
                else
                    try
                    {
                        account = Store.NextServerId();
                        state.CreateAccount(account, handle, steamId);
                    }
                    catch (SqliteException e) when (e.SqliteErrorCode == 19)  // taken between the check and the insert
                    {
                        error = "This name is taken.";
                    }
            }
        return error is null ? Finish(login, account) : Html(200, AuthPages.Handle(login.Id, handle, error));
    }

    /// <summary>GET and POST /auth/confirm: the player's browser says the launcher that shows the code may be signed in.</summary>
    public Reply Confirm(Req r)
    {
        if (Current(r) is not { Link: { } code, Account: { } account } login || !links.TryGetValue(code, out var entry))
            return Problem(400, "This sign-in ran out of time. Start again from the launcher.");
        if (r.Method == "GET")
            return Html(200, AuthPages.Confirm(login.Id, Store.HandleOf(account) ?? "", UserCode(code)));
        if (!OwnPost(r) || (Py.ParseQs(Encoding.UTF8.GetString(r.Body)).TryGetValue("csrf", out var c) ? c[0] : "") != login.Id)
            return Problem(400, "This form is not from this server.");
        entry.Account = account;
        logins.TryRemove(login.Id, out _);
        return Html(200, AuthPages.Done(), Clear(LoginCookie, "/auth"));
    }

    // ---- the launcher waits for the player's browser

    /// <summary>The short form the launcher shows and the browser asks the player to compare.</summary>
    static string UserCode(string code) => $"{code[..4]}-{code[4..8]}".ToUpperInvariant();

    /// <summary>POST /auth/link: the start page asks for a code and the address to open in the player's browser.</summary>
    public Reply NewLink(Req r)
    {
        if (!OwnPost(r)) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        Purge();
        if (links.Count >= MaxPending)
            links.TryRemove(links.MinBy(kv => kv.Value.Expires).Key, out _);
        var code = Rand();
        links[code] = new LinkEntry(now() + Lifetimes.Link);
        return Json(200, new JsonObject
        {
            ["code"] = code, ["userCode"] = UserCode(code),
            ["url"] = Origin(r) + "/auth/steam/login?link=" + code, ["expires"] = (long)Lifetimes.Link.TotalSeconds,
        });
    }

    /// <summary>GET /auth/link/&lt;code&gt;: pending, or ok together with the cookie (once). The code is the secret.</summary>
    public Reply PollLink(Req r)
    {
        var code = r.Path["/auth/link/".Length..];
        if (!links.TryGetValue(code, out var entry) || entry.Expires <= now())
            return Json(404, new JsonObject { ["status"] = "expired" });
        if (entry.Account is not { } account)
            return Json(200, new JsonObject { ["status"] = "pending" });
        links.TryRemove(code, out _);
        var cookie = Store.CreateWebSession(account, Lifetimes.Cookie);
        return Json(200, new JsonObject { ["status"] = "ok" }, Set(SessionCookie, cookie, "/", Lifetimes.Cookie));
    }

    // ---- the session

    /// <summary>GET /auth/me: who the cookie is.</summary>
    public Reply Me(Req r) =>
        Account(r) is { } account
            ? Json(200, new JsonObject { ["userId"] = account, ["handle"] = Store.HandleOf(account) })
            : Json(401, new JsonObject { ["error"] = "signed out" });

    /// <summary>POST /auth/signout.</summary>
    public Reply SignOut(Req r)
    {
        if (!OwnPost(r)) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        if (CookieOf(r, SessionCookie) is { } cookie) Store.DeleteWebSession(cookie);
        return new(204, [("Cache-Control", "no-store"), ("Set-Cookie", Clear(SessionCookie, "/"))], [], "auth");
    }

    /// <summary>POST /auth/launch: a launch URL with fresh tokens for the signed-in player, for the start page.</summary>
    public Reply Launch(Req r, GameTokens tokens)
    {
        if (!OwnPost(r)) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        return Account(r) is { } account
            ? Json(200, new JsonObject { ["url"] = tokens.LaunchUrl(account) })
            : Json(401, new JsonObject { ["error"] = "signed out" });
    }

    /// <summary>GET / on the signup host: the launcher's start page.</summary>
    public Reply StartPage(Req r) =>
        Html(200, AuthPages.Start(Account(r) is { } account ? Store.HandleOf(account) : null));
}
