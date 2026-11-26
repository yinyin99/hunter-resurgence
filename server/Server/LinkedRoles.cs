// Discord Linked Roles: a player links a Discord account to their game account once, is paid 2500 gm$ the first time ever, and the Discord server reads their
// hunterscore as role data (docs/server.md#discord-linked-roles). Discord's documented flow: GET /discord/link is the application's Linked Roles Verification URL,
// Discord's consent page follows, GET /discord/callback receives the code; the browser then lands on a page of the website that tells the result.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

/// <summary>What <see cref="LinkedRoles.Link"/> did: the pair is linked and the reward paid now (Paid) or paid before (Linked), or nothing was written because the Discord account is
/// linked to another hunter (Taken) or the hunter to another Discord account (Other).</summary>
public enum LinkResult { Paid, Linked, Taken, Other }

/// <summary>The Discord application is the project's own: <paramref name="clientId"/> (also its application id) and <paramref name="clientSecret"/> are the operator's settings.
/// <paramref name="webHost"/> is the website's domain, where the redirect address the operator registered lives.</summary>
public sealed class LinkedRoles(State state, HttpClient http, string clientId, string clientSecret, string webHost)
{
    /// <summary>The gm$ paid once ever, per game account and per Discord id.</summary>
    public const long Reward = 2500;
    /// <summary>The `wallet_log` reason of the reward.</summary>
    public const string RewardSummary = "Reward for linking Discord";
    public const string PlatformName = "Hunter Resurgence";
    /// <summary>How long the `state` of a started link is good: from the click in Discord to the consent.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    /// <summary>How often the linked accounts whose hunterscore changed are pushed again.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    const string Api = "https://discord.com/api/v10";
    const string UserAgent = "DiscordBot (https://github.com/yinyin99/hunter-resurgence, 1)";
    const string Scope = "identify role_connections.write";

    readonly Store store = state.Store;
    // ponytail: one push at a time on the whole server. A refresh token is rotated at every refresh, so two refreshes of one account at once would lose the newer one; per-account locks if the sweep ever gets long
    readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>A Discord id (a snowflake): digits only; it goes into a URL and a SQL value, so nothing else is let in.</summary>
    public static bool IsDiscordId(string s) => s.Length is >= 15 and <= 25 && s.All(char.IsAsciiDigit);

    string RedirectUri => $"https://{webHost}/discord/callback";

    // ---- the keyed values: the server's secret key file (<db>.jwtkey) gives each purpose its own key, so a copy of the database holds nothing readable

    byte[] Key(string purpose) => HMACSHA256.HashData(store.JwtKey, Encoding.ASCII.GetBytes(purpose));

    /// <summary>The ledger's value of a Discord id: an HMAC-SHA256 keyed with the server's secret, so the ledger neither names the Discord id nor can be matched against a list of ids without the key.</summary>
    string Ledger(string discordId) => Convert.ToHexStringLower(HMACSHA256.HashData(Key("discord-reward"), Encoding.ASCII.GetBytes(discordId)));

    /// <summary>The `state` of a link started in this session (the session cookie's value): the time it ends and an HMAC of both, so it is good for this browser's session only.</summary>
    public string StateFor(string session, long? ends = null)
    {
        var end = ends ?? Store.Now() + (long)StateLifetime.TotalSeconds;
        return $"{end}.{Convert.ToHexStringLower(HMACSHA256.HashData(Key("discord-state"), Encoding.UTF8.GetBytes($"{session}.{end}")))}";
    }

    bool GoodState(string session, string? given) =>
        given is { Length: <= 100 } && given.IndexOf('.') is > 0 and var dot
        && long.TryParse(given.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end > Store.Now()
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(StateFor(session, end)), Encoding.ASCII.GetBytes(given));

    /// <summary>A token sealed with AES-256-GCM; the Discord id is the associated data, so it is not valid on another row. Base64 of nonce, tag and ciphertext.</summary>
    string Seal(string token, string discordId)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key("discord-tokens"), 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(discordId));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    /// <summary>The token <see cref="Seal"/> made; null for a value that is no sealed token of this Discord id under this server's key.</summary>
    string? Unseal(object? sealedToken, string discordId)
    {
        if (sealedToken is not string text) return null;
        try
        {
            var all = Convert.FromBase64String(text);
            if (all.Length < 28) return null;
            var plain = new byte[all.Length - 28];
            using var aes = new AesGcm(Key("discord-tokens"), 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain, Encoding.ASCII.GetBytes(discordId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }

    // ---- the browser's two requests

    static Reply Redirect(string to) => new(302, [("Location", to), ("Cache-Control", "no-store")], [], "discord");

    /// <summary>The website's page of the result (`/signin/discord`); <paramref name="result"/> is one of its fixed keys: linked, rewarded, taken, other, error.</summary>
    static Reply Done(string result) => Redirect("/signin/discord?result=" + result);

    /// <summary>The session cookie's value and its account; null without a session.</summary>
    (string Cookie, long Account)? Session(Req r) =>
        SignIn.CookieOf(r, SignIn.SessionCookie) is { } cookie && store.ResolveWebSession(cookie) is { } account ? (cookie, account) : null;

    /// <summary>GET /discord/link, the application's Linked Roles Verification URL: a signed-out player goes through the Steam sign-in and comes back here; a signed-in one to Discord's consent page.</summary>
    public Reply Start(Req r)
    {
        if (Session(r) is not { } signedIn)
            return Redirect("/auth/steam/login?next=/discord/link");
        return Redirect("https://discord.com/oauth2/authorize?" + string.Join('&', new (string, string)[]
        {
            ("client_id", clientId), ("response_type", "code"), ("redirect_uri", RedirectUri), ("scope", Scope), ("prompt", "consent"), ("state", StateFor(signedIn.Cookie)),
        }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2))));
    }

    /// <summary>GET /discord/callback: Discord's answer. The state must be this session's, then the code is traded for the player's tokens, the Discord user is read, the pair is linked (and paid once),
    /// the role data is pushed, and the browser goes to the website's result page.</summary>
    public async Task<Reply> Callback(Req r)
    {
        if (Session(r) is not { } signedIn)
            return Redirect("/auth/steam/login?next=/discord/link");  // the sign-in ended meanwhile: start again
        var (session, account) = signedIn;
        var q = Py.ParseQs(r.Query);
        string? Q(string key) => q.TryGetValue(key, out var v) && v.Count == 1 ? v[0] : null;
        if (!GoodState(session, Q("state")) || Q("code") is not { Length: > 0 and <= 512 } code)
            return Done("error");  // another session's or an old state, a tampered one, or the player said no (no code)
        try
        {
            var (status, body) = await Token(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", RedirectUri));
            if (ReadTokens(body) is not { } tokens || !Grants(body))
                return Fail(account, $"Discord refused the code ({status})");
            var (meStatus, me) = await Send(HttpMethod.Get, "/users/@me", null, tokens.Access);
            if (Str(me?["id"]) is not { } discordId || !IsDiscordId(discordId))
                return Fail(account, $"Discord gave no user ({meStatus})");
            var result = Link(account, discordId, tokens);
            if (result is LinkResult.Taken) return Done("taken");
            if (result is LinkResult.Other) return Done("other");
            await PushAsync(account);  // a failed push is logged and the sweep tries again; the link and the reward stand
            return Done(result is LinkResult.Paid ? "linked" : "rewarded");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or SqliteException)
        {
            return Fail(account, e.Message);
        }
    }

    static Reply Fail(long account, string why)
    {
        Console.Error.WriteLine($"discord link of account {account}: {why}");
        return Done("error");
    }

    // ---- the link and the reward

    /// <summary>Link the account and the Discord id with the player's tokens, and pay the reward when neither has ever been paid. One transaction: a Discord id linked to another account, or an account linked to another
    /// Discord id, writes nothing; the same pair again only takes the new tokens. The reward is one ledger row (`discord_rewards`) whose key (the Discord id's hash) and unique account both refuse a second one,
    /// so a repeated or concurrent request, an unlink and relink, a second account with the same Discord id and a recreated account all find it paid.</summary>
    public LinkResult Link(long account, string discordId, Tokens tokens)
    {
        using var con = store.Open(account);
        con.Begin();
        if (con.Scalar("SELECT account_id FROM discord_links WHERE discord_id = ?1", discordId) is long holder && holder != account)
            return LinkResult.Taken;
        if (con.Scalar("SELECT discord_id FROM discord_links WHERE account_id = $a") is string mine && mine != discordId)
            return LinkResult.Other;
        con.Exec("INSERT INTO discord_links (account_id, discord_id, access_token, refresh_token, expires_ts, pushed_score, linked_ts) VALUES ($a, ?1, ?2, ?3, ?4, NULL, ?5)"
                 + " ON CONFLICT (account_id) DO UPDATE SET access_token = ?2, refresh_token = ?3, expires_ts = ?4, pushed_score = NULL",
                 discordId, Seal(tokens.Access, discordId), Seal(tokens.Refresh, discordId), Store.Now() + tokens.Life, Store.Now());
        var paid = con.Exec("INSERT OR IGNORE INTO discord_rewards (hash, account_id) VALUES (?1, $a)", Ledger(discordId)) == 1;
        if (paid)
        {
            var balance = State.AddCurrency(con, "gm", Reward, RewardSummary) ?? throw new InvalidOperationException("the account has no gm$ wallet");
            con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = balance.DeepClone(), ["type"] = "gm", ["update"] = 15 }));
            Uploads.Notify(con, $"Thank you for linking your Discord account: {Reward} gm$ were added to your wallet.");
        }
        con.Commit();
        return paid ? LinkResult.Paid : LinkResult.Linked;
    }

    // ---- the role data

    /// <summary>Push the account's hunterscore to its Discord role connection, refreshing the access token first when it has ended. True when Discord took it. Never throws for Discord's or the network's sake: a failure
    /// is logged and the next sweep tries again; a refresh token Discord no longer accepts (the player removed the app) is dropped, and the link stays (so the rules and the reward stand) until the player links again.</summary>
    public async Task<bool> PushAsync(long account)
    {
        await gate.WaitAsync();
        try
        {
            return await Push(account);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or SqliteException)
        {
            Console.Error.WriteLine($"discord push of account {account}: {e.Message}");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<bool> Push(long account)
    {
        object[]? row;
        using (var con = store.Open())
            row = con.Rows("SELECT l.discord_id, l.access_token, l.refresh_token, l.expires_ts, a.handle, COALESCE(json_extract(a.account, '$.hunterscore'), 0) FROM discord_links l"
                           + " JOIN accounts a ON a.id = l.account_id WHERE l.account_id = ?1 AND l.refresh_token IS NOT NULL", account).FirstOrDefault();
        if (row is null) return false;
        var (discordId, expires, handle, score) = ((string)row[0], (long)row[3], (string)row[4], Convert.ToInt64(row[5], CultureInfo.InvariantCulture));
        var access = Unseal(row[1], discordId);
        if (access is null || expires <= Store.Now() + 60)
        {
            if (Unseal(row[2], discordId) is not { } refresh)
            {
                Drop(account, "its tokens cannot be read with this server's key", (string)row[2]!);
                return false;
            }
            var (status, body) = await Token(("grant_type", "refresh_token"), ("refresh_token", refresh));
            if (ReadTokens(body) is not { } fresh)
            {
                if (status == 400 && Str(body?["error"]) == "invalid_grant") Drop(account, "Discord no longer accepts its refresh token", (string)row[2]!);
                else Console.Error.WriteLine($"discord push of account {account}: the token refresh failed ({status})");
                return false;
            }
            access = fresh.Access;
            // only while the row still holds the tokens read above: a player who linked again meanwhile keeps the new ones
            using var con = store.Open();
            con.Exec("UPDATE discord_links SET access_token = ?1, refresh_token = ?2, expires_ts = ?3 WHERE account_id = ?4 AND refresh_token = ?5",
                     Seal(fresh.Access, discordId), Seal(fresh.Refresh, discordId), Store.Now() + fresh.Life, account, (string)row[2]!);
        }
        var put = new JsonObject { ["platform_name"] = PlatformName, ["platform_username"] = handle, ["metadata"] = new JsonObject { ["hunterscore"] = score } };
        var (code, _) = await Send(HttpMethod.Put, $"/users/@me/applications/{clientId}/role-connection", new StringContent(put.ToJsonString(), Encoding.UTF8, "application/json"), access);
        using var after = store.Open();
        if (code is < 200 or >= 300)
        {
            Console.Error.WriteLine($"discord push of account {account}: Discord answered {code}");
            if (code == 401) after.Exec("UPDATE discord_links SET expires_ts = 0 WHERE account_id = ?1", account);  // the access token is not good: refresh it next time
            return false;
        }
        after.Exec("UPDATE discord_links SET pushed_score = ?1 WHERE account_id = ?2", score, account);
        return true;
    }

    void Drop(long account, string why, string held)
    {
        Console.Error.WriteLine($"discord push of account {account}: {why}");
        using var con = store.Open();
        con.Exec("UPDATE discord_links SET access_token = NULL, refresh_token = NULL, expires_ts = 0 WHERE account_id = ?1 AND refresh_token = ?2", account, held);  // not the tokens of a link made meanwhile
    }

    /// <summary>Push every linked account whose hunterscore is not the one last pushed (every account just linked is one); the number of pushes Discord took. Run every <see cref="SweepEvery"/>.
    /// ponytail: the hunterscore is recomputed on every heartbeat with hunt events, so a push from there would be one call per few seconds per player; this one asks every few minutes. A push that keeps failing is tried at every sweep.</summary>
    public async Task<int> SweepAsync()
    {
        List<long> due;
        try
        {
            using var con = store.Open();
            due = [.. con.Rows("SELECT l.account_id FROM discord_links l JOIN accounts a ON a.id = l.account_id WHERE l.refresh_token IS NOT NULL AND a.banned = 0"
                               + " AND l.pushed_score IS NOT COALESCE(json_extract(a.account, '$.hunterscore'), 0)").Select(r => (long)r[0])];
        }
        catch (SqliteException e)
        {
            Console.Error.WriteLine("discord sweep: " + e.Message);
            return 0;
        }
        var pushed = 0;
        foreach (var account in due)
            try
            {
                if (await PushAsync(account)) pushed++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"discord push of account {account}: {e.Message}");  // one account's failure never stops the others
            }
        return pushed;
    }

    // ---- Discord's API

    /// <summary>A token reply read: both tokens and how long (seconds) the access token lasts.</summary>
    public sealed record Tokens(string Access, string Refresh, long Life);

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static Tokens? ReadTokens(JsonObject? b) =>
        Str(b?["access_token"]) is { Length: > 0 } access && Str(b?["refresh_token"]) is { Length: > 0 } refresh && b?["expires_in"] is JsonValue life && life.TryGetValue<long>(out var seconds) && seconds > 0
            ? new(access, refresh, seconds) : null;

    /// <summary>Did the token reply grant both scopes asked for (`scope` is a space-separated list).</summary>
    static bool Grants(JsonObject? b) => (Str(b?["scope"]) ?? "").Split(' ') is var granted && granted.Contains("identify") && granted.Contains("role_connections.write");

    Task<(int Status, JsonObject? Body)> Token(params (string, string)[] form) =>
        Send(HttpMethod.Post, "/oauth2/token", new FormUrlEncodedContent([.. form.Select(p => KeyValuePair.Create(p.Item1, p.Item2)), KeyValuePair.Create("client_id", clientId), KeyValuePair.Create("client_secret", clientSecret)]), null);

    async Task<(int Status, JsonObject? Body)> Send(HttpMethod method, string path, HttpContent? content, string? bearer)
    {
        using var request = new HttpRequestMessage(method, Api + path) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        using var reply = await http.SendAsync(request);
        JsonObject? body = null;
        try { body = JsonNode.Parse(await reply.Content.ReadAsStringAsync()) as JsonObject; }
        catch (JsonException) { }  // an empty or foreign body: no object
        return ((int)reply.StatusCode, body);
    }
}

public static class LinkedRolesApi
{
    /// <summary>`/discord/link` and `/discord/callback` on the website's domain, when the operator set the Discord application's id and secret ([docs/server.md](docs/server.md#discord-linked-roles)).</summary>
    public static void MapLinkedRoles(this WebApplication app, Game game)
    {
        if (app.Services.GetService<LinkedRoles>() is not { } linked) return;
        var group = app.MapGroup("/discord").RequireHost(game.Web).WithGroupName("game").WithTags("Discord Linked Roles (web_host, session cookie)");
        group.MapGet("/link", AuthApi.Do(game, linked.Start)).WithName("DiscordLink").WithSummary("The application's Linked Roles Verification URL: to Discord's consent page")
             .WithDescription("A signed-out player is sent through the Steam sign-in and back here. Redirects to `https://discord.com/oauth2/authorize` with the scopes `identify role_connections.write`, `prompt=consent` and a `state` bound to the session cookie.");
        group.MapGet("/callback", AuthApi.Do(game, linked.Callback)).WithName("DiscordCallback").WithSummary("Discord's answer: links the accounts, pays the reward once, pushes the role data")
             .WithDescription("Needs the session cookie and the `state` of `/discord/link`. Always a redirect to the website's `/signin/discord?result=<linked|rewarded|taken|other|error>`.")
             .QueryParams(("code", "the authorization code"), ("state", "the state of /discord/link"));
    }
}
