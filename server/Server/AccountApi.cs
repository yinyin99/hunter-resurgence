// The account's own data on the website's host, for the signed-in player (the session cookie): buy from the store, upload a backup zip, see
// what became of it, delete everything. The website's pages call these from their server over SERVER_URL; a browser
// never needs to, so the reverse proxy does not have to route /account/. Online only. docs/server.md#upload-a-backup-zip.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class AccountApi
{
    static Reply Json(int status, JsonNode body, params string[] cookies) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"),
                     .. cookies.Select(c => ("Set-Cookie", c))], Py.DumpBytes(body), "account");

    static Reply Outcome(UploadOutcome o) =>
        Json(o.Status, new JsonObject { ["status"] = o.Kind, ["message"] = o.Message, ["report"] = o.Report });

    static Reply Refuse(int status, string kind, string message) => Outcome(new(status, kind, message));

    /// <summary>A refusal that does not read the body: the reply is logged from a request with an empty one, so a too large upload
    /// is never buffered just to be turned away.</summary>
    static Reply Refuse(HttpContext ctx, int status, string kind, string message)
    {
        var r = ctx.Request;
        var headers = r.Headers;
        ctx.Items[typeof(Req)] = new Req(r.Method, r.Host.Host.ToLowerInvariant(), r.Path.Value ?? "", r.QueryString.Value?.TrimStart('?') ?? "",
            k => headers.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null, [], r.IsHttps);
        return Refuse(status, kind, message);
    }

    /// <summary>A state-changing request comes from our own page: a same-origin Origin, or the header a cross-site form cannot set.</summary>
    static bool OwnPost(HttpContext ctx)
    {
        var r = ctx.Request;
        return r.Method == "POST" && (r.Headers.Origin.Count > 0 ? r.Headers.Origin[0] == $"https://{r.Host.Host.ToLowerInvariant()}" : r.Headers.ContainsKey("X-Requested-With"));
    }

    public static void MapAccount(this WebApplication app, Game game)
    {
        if (app.Services.GetService<Uploads>() is not { } uploads) return;
        var state = game.State!;
        var group = app.MapGroup("/account").RequireHost(game.Web).WithGroupName("game").WithTags("Account (web_host, session cookie)");

        long? Account(HttpContext ctx) => SignIn.CookieOf(ctx.Request.Headers.Cookie.ToString(), SignIn.SessionCookie) is { } c ? state.Store.ResolveWebSession(c) : null;

        group.MapGet("/restore", (HttpContext ctx) =>
            {
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var (restored, review) = uploads.Status(account);
                return Json(200, new JsonObject
                {
                    ["restoredUserId"] = restored,
                    ["review"] = new JsonArray([.. review.Select(x => (JsonNode?)new JsonObject { ["ts"] = x.Ts, ["note"] = x.Note })]),
                });
            })
            .WithName("AccountRestoreStatus").WithSummary("What became of the player's uploads")
            .WithDescription("`restoredUserId`: the backup's user id the account restored (once), or null; `review`: the zips kept for the operator.");

        group.MapPost("/restore", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                if (ctx.Request.ContentLength > Uploads.MaxZipBytes) return Refuse(ctx, 413, "too_large", TooLarge);
                ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = Uploads.MaxZipBytes;
                using var slot = uploads.Begin(account);
                if (slot is null)
                    return Refuse(ctx, 429, "busy", "An upload of yours is still being processed, or the server is busy. Try again in a minute.");
                byte[] zip;
                try
                {
                    using var body = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(body);
                    zip = body.ToArray();
                }
                catch (BadHttpRequestException)
                {
                    return Refuse(413, "too_large", TooLarge);
                }
                return Outcome(uploads.Accept(account, zip));
            })
            .WithName("AccountRestore").WithSummary("Upload a backup zip (raw body)")
            .WithDescription("The zip is the request body (at most 64 MiB), one upload at a time per account. A readable zip is kept for an operator "
                             + "and never restored automatically (`server review approve <id>`). "
                             + "Answers `{status, message}`: status `review`, `already`, `taken`, `invalid`, `busy` or `too_large`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/delete", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                if (!form.TryGetValue("confirm", out var c) || c[0] != "delete")
                    return Refuse(400, "invalid", "Deleting needs confirm=delete.");
                if (uploads.Busy(account)) return Refuse(429, "busy", "An upload of yours is still being processed. Try again in a minute.");
                uploads.Delete(account);
                return Json(200, new JsonObject { ["status"] = "deleted", ["message"] = "Your account and all its data were deleted." },
                            $"{SignIn.SessionCookie}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax");
            })
            .WithName("AccountDelete").WithSummary("Delete the account and all its data (form `confirm=delete`)")
            .WithDescription("Deletes the account's rows, tokens, sessions, kept backup zips and photos, its Steam id and handle. Signs the browser out.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/buy", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                string One(string key, string missing) => form.TryGetValue(key, out var v) ? v[0] : missing;
                if (!long.TryParse(One("id", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || !int.TryParse(One("count", "1"), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    return Refuse(400, "invalid", "Buying needs a number id and count.");
                return state.WebBuy(account, id, One("bundle", "0") == "1", count) is { } ems
                    ? Json(200, new JsonObject { ["status"] = "bought", ["ems"] = ems })
                    : Refuse(409, "refused", "This is not for sale, or you do not have enough em$.");
            })
            .WithName("AccountBuy").WithSummary("Buy from the store with em$ (form `id`, `count`, `bundle=1` for a bundle)")
            .WithDescription("The item or bundle of `GET /catalog/store` at its price: em$ only. Answers `{status: bought, ems}` (the new balance), "
                             + "or 409 `refused` for something not for sale or a balance too low. The game gets the items with its next heartbeat or connect.")
            .Produces(200, contentType: "application/json");
    }

    const string TooLarge = "This file is too large: a backup zip is at most 64 MiB.";
}
