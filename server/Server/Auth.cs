// Sign-in with Steam on the website's host (its pages, /signin/..., are the website's) and the launcher's start page, the cookie session that
// follows it, and the tokens the game is given (session, access, refresh, the JWT of User/jwt). docs/server.md#sign-in-and-tokens.
using System.Buffers.Text;
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
    /// <summary>The signed access JWT of the current build: the game reads its `exp` and asks for a new one before it runs out.</summary>
    public static readonly TimeSpan AccessJwt = TimeSpan.FromSeconds(900);
    public static readonly TimeSpan Cookie = TimeSpan.FromDays(30);
    /// <summary>From the click on "sign in" to the end of the handle choice or the confirmation.</summary>
    public static readonly TimeSpan Login = TimeSpan.FromMinutes(15);
    /// <summary>The launcher waits this long for the player's browser.</summary>
    public static readonly TimeSpan Link = TimeSpan.FromMinutes(10);
    /// <summary>A code the loopback sign-in gave the app (its redirect) lives this long: the app exchanges it at once.</summary>
    public static readonly TimeSpan AppCode = TimeSpan.FromMinutes(3);
}

public static class Handles
{
    static readonly Regex Valid = new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{2,19}\z", RegexOptions.ECMAScript);

    /// <summary>What is wrong with a handle, null when it is fine. Uniqueness (case-insensitive) is the database's.</summary>
    public static string? Problem(string handle) =>
        Valid.IsMatch(handle) ? null : "A name has 3 to 20 letters, digits, - or _ and starts with a letter or a digit.";
}

/// <summary>The game's credentials for one account: what Launch/start, User/jwt and Oauth/token hand out, and the signed
/// JWTs of the current build (the launch URL, /auth/jwt/refresh).</summary>
public sealed class GameTokens(Store store, string webHost)
{
    /// <summary>The launch URL the launcher hands to the game: a fresh session, access and refresh JWT. The scheme
    /// carries the game's name (the client's environment check looks for it anywhere in the URL); the host must not
    /// contain dev or beta.</summary>
    public string LaunchUrl(long account, string? session = null) =>
        $"{ProtocolLiterals.LaunchScheme}://{webHost}/{session ?? store.CreateToken(account, Lifetimes.Session)}/lang/en_US"
        + $"/access_token/{Access(account, GameScope)}/refresh_token/{Refresh(account, GameScope)}";

    /// <summary>The `scp` claim: 4 for the game client, 1 for the launcher page and the website.</summary>
    public const int GameScope = 4, WebScope = 1;

    public string Access(long account, int scope) => Sign(account, "access", scope, Lifetimes.AccessJwt);

    public string Refresh(long account, int scope) => Sign(account, "refresh", scope, Lifetimes.Refresh);

    /// <summary>An HS256 JWT with the claims of the current build, kept in `tokens` like every other token (so a ban or a
    /// delete ends it). The client never checks the signature; <see cref="RefreshedAccount"/> does.</summary>
    public string Sign(long account, string type, int scope, TimeSpan life)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject
        {
            ["sub"] = account, ["iss"] = ProtocolLiterals.JwtIssuer, ["scp"] = scope, ["typ"] = type, ["iat"] = now,
            ["exp"] = now + (long)life.TotalSeconds, ["jti"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), ["user_id"] = account,
        };
        var signed = B64("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray()) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return store.AddToken(signed + "." + B64(HMACSHA256.HashData(store.JwtKey, Encoding.ASCII.GetBytes(signed))), account, life);
    }

    /// <summary>The account of a refresh JWT that this server signed, that is not expired and is still in `tokens`; else null.</summary>
    public long? RefreshedAccount(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            byte[] Unb64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
            var mac = HMACSHA256.HashData(store.JwtKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(mac, Unb64(parts[2]))) return null;
            var claims = JsonNode.Parse(Unb64(parts[1]))!;
            return (string?)claims["typ"] == "refresh" && (long)claims["exp"]! > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? store.ResolveToken(token) : null;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or NullReferenceException or InvalidCastException)
        {
            return null;  // not one of ours
        }
    }

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
/// <param name="siteHost">Our website's domain: the player's own browser signs in there and sees the website's pages (the launcher's start page sends it there for Steam: the browser trusts our certificate and does not reach
/// the game's host), and the launcher learns the result by polling.</param>
public sealed class SignIn(State state, SteamOpenId steam, Func<DateTimeOffset>? clock = null, string? siteHost = null, Claims? claims = null, string? discord = null, Action<string>? signupAlert = null)
{
    public const string SessionCookie = "hr_session";
    const string LoginCookie = "hr_login";

    /// <summary>One sign-in in progress in one browser. Steam: set when Steam confirmed the id. Account: set when the
    /// account exists (first sign-in: after the name). Link: the launcher waiting for it, if any. App: the app on this PC that asked for it, if any.
    /// Typed, Old, Restore and Error: the last name form and what was wrong with it, for the website's page to show again.</summary>
    sealed class Login(string id, string next, string? link, AppRequest? app, DateTimeOffset expires)
    {
        public readonly string Id = id, Next = next;
        public readonly string? Link = link;
        public readonly AppRequest? App = app;
        public DateTimeOffset Expires = expires;
        public string? Steam;
        public long? Account;
        public string Typed = "", Old = "";
        public bool Restore;
        public string? Error;
    }

    /// <summary>What an app (the launcher or the backup tool) sent to the start route: SHA-256 of its secret verifier, the port it listens on at 127.0.0.1, and its random state.</summary>
    sealed record AppRequest(byte[] Challenge, int Port, string State);

    /// <summary>The one-time code that the redirect to the app carries: the player confirmed, the exchange needs the verifier of this challenge.</summary>
    sealed record AppCodeEntry(long Account, byte[] Challenge, DateTimeOffset Expires);

    /// <summary>A launcher that asked to be signed in: Account is set once the player confirmed in the browser.</summary>
    sealed class LinkEntry(DateTimeOffset expires)
    {
        public readonly DateTimeOffset Expires = expires;
        public long? Account;
    }

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly ConcurrentDictionary<string, Login> logins = new();
    readonly ConcurrentDictionary<string, LinkEntry> links = new();
    readonly ConcurrentDictionary<string, AppCodeEntry> appCodes = new();
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

    /// <summary>The website's page of a step (`/signin/handle`, `confirm`, `done`, `problem`; docs/website.md#sign-in): the player's browser sees the website's pages only,
    /// on our domain. A fixed path of our own host, never one a request names.</summary>
    string Page(Req r, string step) => (siteHost is null || r.Host == siteHost ? "" : "https://" + siteHost) + "/signin/" + step;

    /// <summary>The website's problem page; <paramref name="reason"/> is one of its fixed keys (expired, link, app, steam, banned, form, confirmed).</summary>
    Reply Problem(Req r, string reason) => Redirect(Page(r, "problem?reason=" + reason));

    static Reply Redirect(string to, params string[] cookies) =>
        new(302, [("Location", to), ("Cache-Control", "no-store"), .. cookies.Select(c => ("Set-Cookie", c))], [], "auth");

    static Reply Json(int status, JsonNode body, params string[] cookies) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"),
                     .. cookies.Select(c => ("Set-Cookie", c))], Py.DumpBytes(body), "auth");

    /// <summary>A local path only: after the sign-in the browser is never sent to another site.</summary>
    static string SafeNext(string? next) =>
        next is { Length: > 0 and <= 200 } && next[0] == '/' && !next.StartsWith("//", StringComparison.Ordinal) && next.All(c => c is > ' ' and < '\x7f' and not '\\')
            ? next : "/";

    /// <summary>A browser's sign-in runs on our website's host, whose pages it shows: a start on another host (the game's, through a player's forwarder) goes there first,
    /// with the same fixed path and query. Null on the website's host.</summary>
    Reply? ToSite(Req r) => siteHost is null || r.Host == siteHost ? null : Redirect("https://" + siteHost + r.Path + (r.Query == "" ? "" : "?" + r.Query));

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
        foreach (var (id, c) in appCodes) if (c.Expires <= now()) appCodes.TryRemove(id, out _);
    }

    // ---- the Steam round trip

    /// <summary>GET /auth/steam/login?next=/path[&link=code]: to Steam. A cookie ties the way back to this browser.</summary>
    public Reply Start(Req r)
    {
        if (ToSite(r) is { } moved) return moved;
        var q = Py.ParseQs(r.Query);
        var link = q.TryGetValue("link", out var l) ? l[0] : null;
        if (link is not null && !(links.TryGetValue(link, out var entry) && entry.Expires > now()))
            return Problem(r, "link");
        return BeginLogin(r, SafeNext(q.TryGetValue("next", out var n) ? n[0] : null), link, null);
    }

    /// <summary>GET /auth/app/start?challenge=&amp;port=&amp;state=: an app on this PC asks to be signed in (RFC 8252 loopback redirect, RFC 7636 PKCE). Nothing but a well-formed request starts a sign-in; the answer
    /// can only go to 127.0.0.1 on that port, so a link sent to someone else gives the sender nothing.</summary>
    public Reply StartApp(Req r)
    {
        if (ToSite(r) is { } moved) return moved;
        var q = Py.ParseQs(r.Query);
        string Q(string k) => q.TryGetValue(k, out var v) ? v[0] : "";
        if (!Challenge(Q("challenge"), out var challenge) || !Token(Q("state"), 16, 128)
            || !int.TryParse(Q("port"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            return Problem(r, "app");
        return BeginLogin(r, "/", null, new AppRequest(challenge, port, Q("state")));
    }

    /// <summary>A challenge is the SHA-256 of the verifier in base64url without padding: 43 characters, 32 bytes.</summary>
    static bool Challenge(string s, out byte[] hash)
    {
        hash = new byte[32];
        return s.Length == 43 && Token(s, 43, 43) && Base64Url.TryDecodeFromChars(s, hash, out var n) && n == 32;
    }

    /// <summary>The characters of an RFC 7636 verifier (also the state and the challenge), a length range.</summary>
    static bool Token(string s, int min, int max) =>
        s.Length >= min && s.Length <= max && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    Reply BeginLogin(Req r, string next, string? link, AppRequest? app)
    {
        Purge();
        if (logins.Count >= MaxPending) // full: the one that expires first goes, so a flood cannot lock the players out
            logins.TryRemove(logins.MinBy(kv => kv.Value.Expires).Key, out _);
        var login = new Login(Rand(), next, link, app, now() + Lifetimes.Login);
        logins[login.Id] = login;
        return Redirect(SteamOpenId.LoginUrl(ReturnTo(r, login.Id), Origin(r) + "/"), Set(LoginCookie, login.Id, "/", Lifetimes.Login));
    }

    /// <summary>GET /auth/steam/return: Steam's answer. A known Steam id signs in; a new one picks a name first.</summary>
    public async Task<Reply> Return(Req r)
    {
        var id = Py.ParseQs(r.Query).TryGetValue("login", out var l) ? l[0] : null;
        if (id is null || CookieOf(r, LoginCookie) != id || Current(r) is not { Steam: null } login)
            return Problem(r, "expired");
        if (await steam.VerifyAsync(r.Query, ReturnTo(r, id)) is not { } steamId)
        {
            logins.TryRemove(id, out _);
            return Problem(r, "steam");
        }
        login.Steam = steamId;
        login.Expires = now() + Lifetimes.Login;
        return Store.AccountBySteam(steamId) is { } account ? Finish(r, login, account) : Redirect(Page(r, "handle"));
    }

    /// <summary>The account is known: a launcher or an app waits (ask the player to confirm), or this browser gets its cookie.</summary>
    Reply Finish(Req r, Login login, long account)
    {
        if (login.Link is not null || login.App is not null)
        {
            login.Account = account;
            return Redirect(Page(r, "confirm"));
        }
        logins.TryRemove(login.Id, out _);
        var cookie = Store.CreateWebSession(account, Lifetimes.Cookie);
        if (Store.ResolveWebSession(cookie) is null)  // a banned account gets no session
        {
            Store.DeleteWebSession(cookie);
            return Problem(r, "banned");
        }
        return Redirect(login.Next, Set(SessionCookie, cookie, "/", Lifetimes.Cookie), Clear(LoginCookie, "/"));
    }

    /// <summary>GET /auth/pending: the sign-in in progress in this browser, for the website's page of its step (docs/website.md#sign-in): the name form
    /// (`handle`, with the last form and its error), or the confirmation for a launcher that shows a code (`confirm`) or an app on this PC (`app`); 404 without one.
    /// It reads only, and only this browser's cookie names it; `csrf` is what the step's POST must carry.</summary>
    public Reply Pending(Req r)
    {
        if (r.Header("X-Requested-With") is null) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        if (Current(r) is not { Steam: not null } login || login.Link is { } link && !links.ContainsKey(link))
            return Json(404, new JsonObject { ["status"] = "expired" });
        if (login.Account is not { } account)
            return Json(200, new JsonObject
            {
                ["step"] = "handle", ["csrf"] = login.Id, ["offer"] = claims is { Enabled: true },
                ["handle"] = login.Typed, ["old"] = login.Old, ["restore"] = login.Restore, ["error"] = login.Error,
            });
        return Json(200, new JsonObject
        {
            ["step"] = login.Link is null ? "app" : "confirm", ["csrf"] = login.Id, ["handle"] = Store.HandleOf(account) ?? "",
            ["code"] = login.Link is { } c ? UserCode(c) : null,
        });
    }

    /// <summary>GET and POST /auth/handle: the first sign-in picks the name (Steam's is not read, nothing is stored of it). GET goes to the website's page;
    /// a refused POST keeps the form and its error (a key of that page: invalid, taken, unknown, old-taken) and goes back there.</summary>
    public Reply ChooseHandle(Req r)
    {
        if (Current(r) is not { Steam: { } steamId, Account: null } login)
            return Problem(r, "expired");
        if (Store.AccountBySteam(steamId) is { } existing)  // another tab of this sign-in was faster
            return Finish(r, login, existing);
        var offer = claims is { Enabled: true };
        if (r.Method == "GET") return Redirect(Page(r, "handle"));
        var form = Py.ParseQs(Encoding.UTF8.GetString(r.Body));
        string F(string k) => form.TryGetValue(k, out var v) ? v[0] : "";
        if (!OwnPost(r) || F("csrf") != login.Id) return Problem(r, "form");
        var handle = F("handle").Trim();
        var old = F("old").Trim();
        var restore = offer && F("kind") == "old";
        string? error = null;
        ArchivedPlayer? player = null;
        if (restore)  // the lookup is free here: there is no account yet, and each try is one POST of one sign-in
        {
            player = claims!.Peek(old);
            if (player is null) error = "unknown";
            else if (handle == "")
            {
                if (Handles.Problem(player.Handle) is null && Store.AccountByHandle(player.Handle) is null) handle = player.Handle;
                else error = "old-taken";
            }
        }
        long account = 0;
        error ??= Handles.Problem(handle) is null ? null : "invalid";
        if (error is null)
            lock (create)
            {
                if (Store.AccountByHandle(handle) is not null) error = "taken";
                else
                    try
                    {
                        account = Store.NextServerId();
                        state.CreateAccount(account, handle, steamId);
                    }
                    catch (SqliteException e) when (e.SqliteErrorCode == 19)  // taken between the check and the insert
                    {
                        error = "taken";
                    }
            }
        if (error is not null)
        {
            (login.Typed, login.Old, login.Restore, login.Error) = (handle.Length > 20 ? handle[..20] : handle, old.Length > 20 ? old[..20] : old, restore, error);
            return Redirect(Page(r, "handle"));
        }
        var outcome = "fresh start";
        if (player is not null)  // restored or kept for review: the player's inbox says which
        {
            var o = claims!.Claim(account, old, useOldName: handle == player.Handle, confirmOverwrite: true);
            outcome = $"claimed {Alerts.Name(player.Handle)} (hunterscore {player.Hunterscore}): "
                      + (o.Kind == "restored" ? "restored" : o.Kind == "review" ? "waiting for review" : $"refused ({o.Kind})");
        }
        signupAlert?.Invoke($"New account: {Alerts.Name(handle)}, account {account}, {Alerts.Steam(steamId)}, {outcome}.");
        return Finish(r, login, account);
    }

    /// <summary>GET and POST /auth/confirm: the player's browser says the launcher that shows the code, or the app on this PC, may be signed in. The app gets a one-time code at 127.0.0.1.
    /// GET goes to the website's page.</summary>
    public Reply Confirm(Req r)
    {
        if (Current(r) is not { Account: { } account } login || (login.Link is null && login.App is null)
            || (login.Link is { } code && !links.ContainsKey(code)))
            return Problem(r, "expired");
        if (r.Method == "GET") return Redirect(Page(r, "confirm"));
        if (!OwnPost(r) || (Py.ParseQs(Encoding.UTF8.GetString(r.Body)).TryGetValue("csrf", out var csrf) ? csrf[0] : "") != login.Id)
            return Problem(r, "form");
        if (!logins.TryRemove(login.Id, out _)) return Problem(r, "confirmed"); // a double click makes one code
        if (login.App is { } app)
        {
            Purge();
            if (appCodes.Count >= MaxPending)
                appCodes.TryRemove(appCodes.MinBy(kv => kv.Value.Expires).Key, out _);
            var appCode = Rand(32);
            appCodes[appCode] = new AppCodeEntry(account, app.Challenge, now() + Lifetimes.AppCode);
            // the redirect goes to 127.0.0.1 and nowhere else: the host is a literal, the port a validated number, the state url-safe
            return Redirect($"http://127.0.0.1:{app.Port}/?code={appCode}&state={app.State}", Clear(LoginCookie, "/"));
        }
        if (links.TryGetValue(login.Link!, out var entry)) entry.Account = account;
        return Redirect(Page(r, "done"), Clear(LoginCookie, "/"));
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
            ["url"] = (siteHost is not null ? "https://" + siteHost : Origin(r)) + "/auth/steam/login?link=" + code, ["expires"] = (long)Lifetimes.Link.TotalSeconds,
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

    /// <summary>POST /auth/app/exchange (form `code` and `verifier`): the session cookie, once, for the app whose verifier hashes to the challenge the code is bound to. The code is spent
    /// by the first try with a well-formed verifier, right or wrong, so a guess costs the code; every failure gives the same answer.</summary>
    public Reply ExchangeApp(Req r)
    {
        if (!OwnPost(r)) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        var form = Py.ParseQs(Encoding.UTF8.GetString(r.Body));
        string F(string k) => form.TryGetValue(k, out var v) ? v[0] : "";
        var (code, verifier) = (F("code"), F("verifier"));
        if (!Token(verifier, 43, 128) || !appCodes.TryRemove(code, out var entry) || entry.Expires <= now()
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)), entry.Challenge))
            return Json(400, new JsonObject { ["status"] = "invalid" });
        var cookie = Store.CreateWebSession(entry.Account, Lifetimes.Cookie);
        return Json(200, new JsonObject { ["status"] = "ok" }, Set(SessionCookie, cookie, "/", Lifetimes.Cookie));
    }

    // ---- the session

    /// <summary>GET /auth/me: who the cookie is, with the hunterscore, the place on the board and the wallet (em, gm).</summary>
    public Reply Me(Req r)
    {
        if (Account(r) is not { } account) return Json(401, new JsonObject { ["error"] = "signed out" });
        var wallet = state.Wallet(account);
        return Json(200, new JsonObject
        {
            ["userId"] = account, ["handle"] = Store.HandleOf(account),
            ["hunterscore"] = Store.AccountJson(account)["hunterscore"] is { } hs && Py.IsNum(hs) ? Py.L(hs) : 0,
            ["rank"] = Store.Rank(account, state.LocalAccount), ["avatar"] = state.AvatarUrl(account, siteHost is not null ? "https://" + siteHost : Origin(r)),
            ["ems"] = wallet["em"] is { } em && Py.IsNum(em) ? Py.L(em) : 0, ["gms"] = wallet["gm"] is { } gm && Py.IsNum(gm) ? Py.L(gm) : 0,
        });
    }

    /// <summary>POST /auth/signout.</summary>
    public Reply SignOut(Req r)
    {
        if (!OwnPost(r)) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        if (CookieOf(r, SessionCookie) is { } cookie) Store.DeleteWebSession(cookie);
        return new(204, [("Cache-Control", "no-store"), ("Set-Cookie", Clear(SessionCookie, "/"))], [], "auth");
    }

    /// <summary>POST /auth/launch: a launch URL with fresh tokens for the signed-in player, for the start page. A form with `reserve` and `lodge` or `tent` (the guid of one of the
    /// player's placed tents in that reserve), and `time` in minutes and `loadout`, a number each, first opens the hunt there as start_single_player does; a form with `tutorial`
    /// opens that tutorial's session instead; 400 for anything else, and for a tent that is not the player's or not in that reserve.</summary>
    public Reply Launch(Req r, GameTokens tokens)
    {
        if (!OwnPost(r)) return Json(400, new JsonObject { ["error"] = "not from a page of this server" });
        if (Account(r) is not { } account) return Json(401, new JsonObject { ["error"] = "signed out" });
        var form = Py.ParseQs(Encoding.UTF8.GetString(r.Body));
        string F(string key) => form.TryGetValue(key, out var v) ? v[0] : "";
        Reply Url(string? session = null) => Json(200, new JsonObject { ["url"] = tokens.LaunchUrl(account, session) });
        if (F("tutorial") is { Length: > 0 } tutorial)
        {
            var id = Py.IsDigit(tutorial) && long.TryParse(tutorial, out var n) ? (n == 0 ? 1 : n) : 0;  // 0 is the first tutorial, as at start_tutorial
            if (state.Tutorial(id) is null) return Json(400, new JsonObject { ["error"] = "unknown tutorial" });
            var session = Store.CreateToken(account, Lifetimes.Session);
            state.NoteTutorial(account, session, id);
            return Url(session);
        }
        var (reserve, lodge, tent, time, loadout) = (F("reserve"), F("lodge"), F("tent"), F("time"), F("loadout"));
        if (reserve + lodge + tent + time + loadout == "") return Url();  // the start page sends none: the launch URL alone
        static bool Whole(string s, bool optional = false) => s == "" ? optional : long.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);  // digits that fit a long
        if (!Whole(reserve) || !Whole(lodge, tent != "") || !Whole(tent, lodge != "") || (lodge != "" && tent != "") || !Whole(time, true) || !Whole(loadout, true))
            return Json(400, new JsonObject { ["error"] = "reserve and a lodge or a tent are needed, time and loadout are numbers" });
        if (tent != "" && !state.IsPlacedTent(account, long.Parse(tent), long.Parse(reserve)))
            return Json(400, new JsonObject { ["error"] = "that is not a tent of yours in this reserve" });
        var start = tent != "" ? $"start={tent}&loctype=TENT" : $"lodge_id={lodge}";
        state.NoteLaunch(account, Encoding.UTF8.GetBytes($"reserve={reserve}&{start}&time={time}&loadout_id={loadout}"));
        return Url();
    }

    /// <summary>GET / on the signup host and /launcher on the game's web host: the launcher's start page.</summary>
    public Reply StartPage(Req r) =>
        Html(200, AuthPages.Start(Account(r) is { } account ? Store.HandleOf(account) : null, discord));
}
