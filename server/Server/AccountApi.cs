// The account's own data on the website's host, for the signed-in player (the session cookie): buy from the store, upload a backup zip, see
// what became of it, delete everything. The website's pages call these from their server over SERVER_URL; the backup tool and the launcher
// call them on the public domain, so the reverse proxy routes /account/ here. Online only. docs/server.md#upload-a-backup-zip.
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

    /// <summary>The request is logged with an empty body, so an upload is never buffered for the log (a refused one is never read at all).</summary>
    static void NoBody(HttpContext ctx)
    {
        var r = ctx.Request;
        var headers = r.Headers;
        ctx.Items[typeof(Req)] = new Req(r.Method, r.Host.Host.ToLowerInvariant(), r.Path.Value ?? "", r.QueryString.Value?.TrimStart('?') ?? "",
            k => headers.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null, [], r.IsHttps);
    }

    /// <summary>A body over one request's limit is refused unread; otherwise the limit is set, so a chunked one stops there too.</summary>
    static bool Oversized(HttpContext ctx)
    {
        if (ctx.Request.ContentLength > Uploads.MaxPartBytes) return true;
        ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = Uploads.MaxPartBytes;
        return false;
    }

    /// <summary>The query's `lang`, the language of the texts a page asks for (the launcher's code: `fr`, `pt-BR`); null for none.</summary>
    static string? Lang(HttpContext ctx) => ctx.Request.Query["lang"].FirstOrDefault();

    /// <summary>A state-changing request comes from our own page: a same-origin Origin, or the header a cross-site form cannot set.</summary>
    static bool OwnPost(HttpContext ctx)
    {
        var r = ctx.Request;
        return r.Method == "POST" && (r.Headers.Origin.Count > 0 ? r.Headers.Origin[0] == $"https://{r.Host.Host.ToLowerInvariant()}" : r.Headers.ContainsKey("X-Requested-With"));
    }

    public static void MapAccount(this WebApplication app, Game game)
    {
        if (app.Services.GetService<Uploads>() is not { } uploads || app.Services.GetService<Claims>() is not { } claims) return;
        var state = game.State!;
        var reports = app.Services.GetRequiredService<ProblemReports>();
        var discord = app.Services.GetService<LinkedRoles>();
        var site = $"https://{game.Web}";  // the avatar pictures are on the website's own host, like the gallery's
        var group = app.MapGroup("/account").RequireHost(game.Web).WithGroupName("game").WithTags("Account (web_host, session cookie)");

        long? Account(HttpContext ctx) => SignIn.CookieOf(ctx.Request.Headers.Cookie.ToString(), SignIn.SessionCookie) is { } c ? state.Store.ResolveWebSession(c) : null;

        group.MapGet("/restore", (HttpContext ctx) =>
            {
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var (restored, review, decisions, room) = uploads.Status(account);
                return Json(200, new JsonObject
                {
                    ["restoredUserId"] = restored,
                    ["review"] = new JsonArray([.. review.Select(x => (JsonNode?)new JsonObject { ["ts"] = x.Ts, ["note"] = x.Note, ["format"] = x.Format })]),
                    ["room"] = room,
                    ["decisions"] = new JsonArray([.. decisions.Select(x => (JsonNode?)new JsonObject
                    {
                        ["ts"] = x.Ts, ["kind"] = x.Kind, ["userId"] = x.UserId, ["handle"] = x.Handle, ["handleKept"] = x.HandleKept, ["reason"] = x.Reason,
                    })]),
                });
            })
            .WithName("AccountRestoreStatus").WithSummary("What became of the player's uploads")
            .WithDescription("`restoredUserId`: the backup's user id the account restored (once), or null; `review`: the zips kept for the operator, each with its time, note "
                             + "(the reason of a refused approve) and `format` (`ours`: our backup tool's zip, `other`: another tool's); `room`: the account can store one more zip (false at its file or byte limit); `decisions`: what the operator decided, each `{ts, kind: restored|rejected|refused, userId, handle, handleKept, reason}`.");

        group.MapGet("/archive", (HttpContext ctx) =>
            {
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var name = ctx.Request.Query["name"].ToString();
                if (name.Length == 0)
                    return Json(200, new JsonObject
                    {
                        ["enabled"] = claims.Enabled,
                        ["claimed"] = claims.Of(account) is { } c ? new JsonObject { ["state"] = c.State, ["handle"] = c.Handle, ["ts"] = c.Ts } : null,
                    });
                var found = claims.Lookup(account, name);
                if (found.Limited) return Json(429, new JsonObject { ["status"] = "limit", ["message"] = "You used your lookups for today. Try again tomorrow." });
                if (found.Player is not { } p) return Json(200, new JsonObject { ["found"] = false });
                return Json(200, new JsonObject
                {
                    ["found"] = true, ["handle"] = p.Handle, ["hunterscore"] = p.Hunterscore, ["items"] = p.Items, ["trophies"] = p.Trophies, ["lodges"] = p.Lodges,
                    ["skills"] = p.Skills, ["statistics"] = p.Statistics, ["achievements"] = p.Achievements, ["nameFree"] = found.NameFree,
                });
            })
            .WithName("AccountArchive").WithSummary("Look up an archived profile by name, or the account's own claim (no `name`)")
            .WithDescription("With `name`: one of the account's 5 lookups of the UTC day. `{found: false}`, or `{found: true, handle, hunterscore, items, trophies, lodges, skills, statistics, achievements, nameFree}` "
                             + "(counts and flags of what a claim would restore; `nameFree`: the archived handle could be taken); `429` `{status: limit}` over the limit. Without `name`: `{enabled, claimed}`, "
                             + "`claimed` null or the account's claim `{state: waiting|restored|rejected, handle, ts}`. `enabled` false: the server has no archive.")
            .QueryParams(("name", "The archived handle, any case, exact"))
            .Produces(200, contentType: "application/json");

        group.MapPost("/archive/claim", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                string One(string key) => form.TryGetValue(key, out var v) ? v[0] : "";
                var o = claims.Claim(account, One("name"), One("old_name") == "1", One("confirm") == "overwrite", announce: true);
                if (o.Kind != "review") Console.WriteLine($"claim of account {account}: {o.Kind}");
                return Outcome(new(o.Status, o.Kind, o.Message));
            })
            .WithName("AccountArchiveClaim").WithSummary("Claim an archived profile (form `name`, `old_name=1|0`, `confirm=overwrite`)")
            .WithDescription("One claim per account, ever. The archived profile replaces the account's items, skills, achievements, trophies and lodges; the user id never changes. Below hunterscore 10,000 it is restored at once "
                             + "(`restored`); from 10,000, or when another account claimed the same profile, an operator decides (`review`). `confirm=overwrite` is needed when the account has played (else `409` `confirm`). "
                             + "Answers `{status, message}`: `restored`, `review`, `refused` (409), `unknown` (404), `limit` (429) or `confirm` (409). `old_name=1` takes the archived handle when it is allowed and free.")
            .Produces(200, contentType: "application/json");

        // one line per upload outcome: a refused upload keeps nothing, so the log is its only record (the request line has no account or kind)
        Reply Logged(long account, UploadOutcome o)
        {
            if (o.Kind != "received") Console.WriteLine($"upload of account {account}: {o.Kind}: {o.Message.ReplaceLineEndings(" ")}"); // a reader's message can quote the zip
            return Outcome(o);
        }

        // an upload's requests: our own page, signed in; the body is never buffered for the log
        async Task<Reply> Upload(HttpContext ctx, Func<long, Task<Reply>> then)
        {
            NoBody(ctx);
            if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
            if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
            return await then(account);
        }

        group.MapPost("/restore", Task<Reply> (HttpContext ctx) => Upload(ctx, async account =>
            {
                if (Oversized(ctx)) return Logged(account, new(413, "too_large", TooLarge));
                if (uploads.Full(account) is { } full) return Logged(account, full);
                if (uploads.Start(account) is not { } id) return Logged(account, new(429, "busy", Busy));
                try
                {
                    var part = await uploads.Part(account, id, 0, ctx.Request.Body);
                    return Logged(account, part.Kind == "received" ? await uploads.Finish(account, id) : part);
                }
                finally
                {
                    uploads.Drop(id);
                }
            }))
            .WithName("AccountRestore").WithSummary("Upload a backup zip in one request (raw body)")
            .WithDescription("The zip is the request body (at most 90 MiB; a larger zip is sent in parts, below), one upload at a time per account. A readable zip is kept for an operator "
                             + "and never restored automatically (`server review approve <id>`). "
                             + "Answers `{status, message}`: status `review`, `already`, `taken`, `invalid`, `busy`, `quota` (too many zips or bytes already stored for the operator), `paused` (the server takes no uploads for now) or `too_large`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/restore/start", Task<Reply> (HttpContext ctx) => Upload(ctx, account => Task.FromResult(
                uploads.Full(account) is { } full ? Logged(account, full)
                : uploads.Start(account) is { } id ? Json(200, new JsonObject { ["status"] = "started", ["id"] = id, ["partBytes"] = Uploads.MaxPartBytes })
                : Logged(account, new(429, "busy", Busy)))))
            .WithName("AccountRestoreStart").WithSummary("Start an upload of a backup zip in parts")
            .WithDescription("Answers `{status: started, id, partBytes}`: send the zip in parts of at most `partBytes` to `POST /account/restore/<id>/<part>`, "
                             + "then `POST /account/restore/<id>/finish`. One upload at a time per account: a start drops the account's upload that waits for its next part (`429` `busy` while a part is received); one that gets no part for 30 minutes is deleted.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/restore/{id}/{part:int:min(0)}", (HttpContext ctx, string id, int part) => Upload(ctx, async account =>
                Logged(account, Oversized(ctx) ? new(413, "too_large", TooLarge) : await uploads.Part(account, id, part, ctx.Request.Body))))
            .WithName("AccountRestorePart").WithSummary("One part of an upload (raw body)")
            .WithDescription("Part `<part>`, from 0, in order, at most 90 MiB: the server appends it to the upload's file. The last part may be sent again (it replaces itself). "
                             + "Answers `{status: received}`; `409` `invalid` for a part out of order, `404` `invalid` for an upload not open, `413` `too_large` when the zip passes 1 GiB (the upload ends).")
            .Produces(200, contentType: "application/json");

        group.MapPost("/restore/{id}/finish", (HttpContext ctx, string id) => Upload(ctx, async account => Logged(account, await uploads.Finish(account, id))))
            .WithName("AccountRestoreFinish").WithSummary("Finish an upload: the joined zip is read like a zip sent at once")
            .WithDescription("Answers what `POST /account/restore` answers; the upload ends whatever the outcome.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/delete", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                if (!form.TryGetValue("confirm", out var c) || c[0] != "delete")
                    return Refuse(400, "invalid", "Deleting needs confirm=delete.");
                if (uploads.Busy(account) || reports.Busy(account)) return Refuse(429, "busy", "An upload or report of yours is still being processed. Try again in a minute.");
                if (discord is null) uploads.Delete(account);
                else await discord.UnlinkAsync(account, () => uploads.Delete(account));  // Discord's access is revoked first, with no push in between
                return Json(200, new JsonObject { ["status"] = "deleted", ["message"] = "Your account and all its data were deleted." },
                            $"{SignIn.SessionCookie}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax");
            })
            .WithName("AccountDelete").WithSummary("Delete the account and all its data (form `confirm=delete`)")
            .WithDescription("Deletes the account's rows (crash reports included), tokens, sessions, kept backup zips, problem reports and photos, its Steam id and handle. Signs the browser out. A linked Discord account first loses its two roles on the Discord server (with the role bot) and its role connection, and Discord's access is revoked (best effort: a failure is logged and the delete goes on). Kept: the Steam id and the time of the delete, for the 24-hour claim cooldown (removed by the next daily cleanup after that), and a ban record of the Steam id, if it was ever banned (Steam id, time, reason, lift time).")
            .Produces(200, contentType: "application/json");

        group.MapPost("/report", async (HttpContext ctx) =>
            {
                NoBody(ctx);  // a zip is never buffered for the log
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var (now, trigger) = (Store.Now(), ctx.Request.Headers["X-Report-Trigger"].ToString());
                if (reports.Begin(account, trigger, ctx.Request.ContentLength, now) is { } refused)
                    return Outcome(refused);
                try
                {
                    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = ProblemReports.MaxBytes + 1;  // a chunked body has no length to check
                    using var zip = new MemoryStream();
                    try
                    {
                        await ctx.Request.Body.CopyToAsync(zip);
                    }
                    catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                    {
                        return Refuse(413, "too_large", "A problem report is at most 6 MB.");
                    }
                    var (no, reference) = reports.Save(account, trigger, zip.ToArray(), now);
                    return no is { } failed ? Outcome(failed) : Json(200, new JsonObject { ["reference"] = reference });
                }
                finally
                {
                    reports.End(account);
                }
            })
            .WithName("AccountReport").WithSummary("Send a problem report: the zip as the body, header `X-Report-Trigger`")
            .WithDescription("The launcher's problem report (a zip of at most 6 MiB) from the signed-in player (`hr_session` cookie, header `X-Requested-With`). `X-Report-Trigger` is `manual`, `short`, `exitcode` or `nowindow`. "
                             + "`200` `{reference}` (`p<account>-<8 hex>`; the same zip twice is one report); `401` not signed in; `400` a trigger that is none of those or a body that is no zip; `413` over 6 MiB; "
                             + "`429` a report of the account is being received, or it sent 5 in the last 24 h; `507` the data disk has less than `upload_min_free_bytes` free. Kept 30 days for the operator; removed by `POST /account/delete`.")
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
                var gms = One("wallet", "GOLD") switch { "GOLD" => false, "SILVER" => true, _ => (bool?)null };
                if (gms is null) return Refuse(400, "invalid", "The wallet is GOLD (em$) or SILVER (gm$).");
                return state.WebBuy(account, id, One("bundle", "0") == "1", count, gms.Value) is { } balance
                    ? Json(200, new JsonObject { ["status"] = "bought", [gms.Value ? "gms" : "ems"] = balance })
                    : Refuse(409, "refused", $"This is not for sale, you do not have enough {(gms.Value ? "gm$" : "em$")}, or you own as many as allowed.");
            })
            .WithName("AccountBuy").WithSummary("Buy from the store with em$ or gm$ (form `id`, `count`, `wallet`, `bundle=1` for a bundle)")
            .WithDescription("The item or bundle of `GET /catalog/store` at its price (the sale price while a `store_sales` sale applies): `wallet` `GOLD` (the default) pays the em$ price, `SILVER` the gm$ price (items only). "
                             + "Answers `{status: bought, ems}` or `{status: bought, gms}` (the new balance of the wallet paid), 400 `invalid` for another wallet, "
                             + "or 409 `refused` for something not for sale, a balance too low or an own limit reached. The game gets the items with its next heartbeat or connect.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/sell", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                if (!form.TryGetValue("guid", out var guids) || !long.TryParse(guids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var guid))
                    return Refuse(400, "invalid", "Selling needs the number guid of a copy.");
                var (offer, balance, why) = state.WebSell(account, guid);
                return offer is null
                    ? Refuse(409, why, "This copy cannot be sold back.")
                    : Json(200, new JsonObject { ["status"] = "sold", ["currency"] = offer.Currency, ["refund"] = offer.Refund, ["balance"] = balance });
            })
            .WithName("AccountSell").WithSummary("Sell an unused item back to the store (form `guid`)")
            .WithDescription("The copy `guid` of `GET /account/inventory`, in the lodge and bought here or restored (docs/server.md#selling-back): it is removed and 40 % of the price paid (rounded down) is paid in the currency paid, "
                             + "with the wallet row `Sold: <item>`. Answers `{status: sold, currency: em|gm, refund, balance}`, 409 with `status` the reason (`unknown`, `hunting`, `equipped`, `placed`, `ammunition`, `consumable`, `license`, "
                             + "`lodge`, `service`, `start_kit`, `not_sold`, `no_proof`, `no_wallet`, `too_cheap`; nothing changes), 401 `signed_out`, or 400 `invalid`. The game gets the balance with its next heartbeat and the inventory at its next connect.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/harvests/{kill}/taxidermize", async (HttpContext ctx, string kill) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                if (ctx.Request.ContentLength is null or > 1024) return Json(400, new JsonObject { ["error"] = "invalid" });
                JsonObject? body = null;
                try { body = JsonNode.Parse(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync()) as JsonObject; }
                catch (System.Text.Json.JsonException) { }
                var (product, currency) = ((body?["product"] as JsonValue)?.TryGetValue<string>(out var p) == true ? p : null, (body?["currency"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null);
                if (product is not (State.TAXIDERMY or State.SKULL) || currency is not ("ems" or "gms"))
                    return Json(400, new JsonObject { ["error"] = "invalid" });
                if (!long.TryParse(kill, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    return Json(404, new JsonObject { ["error"] = "unknown" });
                var (status, error) = state.Taxidermize(account, id, product, currency == "gms" ? "gm" : "em");
                if (status != 200)
                    return Json(status, status == 402 ? new JsonObject { ["error"] = error, ["currency"] = currency } : new JsonObject { ["error"] = error });
                var wallet = state.Wallet(account);
                return Json(200, new JsonObject { ["done"] = product, ["ems"] = wallet["em"] is { } em ? Py.L(em) : 0, ["gms"] = wallet["gm"] is { } gm ? Py.L(gm) : 0 });
            })
            .WithName("AccountTaxidermize").WithSummary("Taxidermize or mount the skull of the player's own harvest (JSON `product`, `currency`)")
            .WithDescription("`{product: taxidermy|skull, currency: ems|gms}` (the `hr_session` cookie, header `X-Requested-With`): the price of the facts file (10 em$ or 2,000 gm$) is taken, the wallet gets the store item's row (`Taxidermy (immediate)`, "
                             + "or the skull mount's) and the game its new balance with the next heartbeat. 200 `{done, ems, gms}` (the wallet after); 402 `{error: not_enough, currency}`; 409 `{error: already}` (once only, either product); "
                             + "410 `{error: too_late}` (72 hours after the harvest, for a harvest made before the taxidermy went live 72 hours after that day); 403 `{error: disqualified}` (the ammunition rule judges the kill unethical); 404 for a kill that is unknown, a restored one or another player's; 400 for a body that is not as shown or a skull for a species that has none. "
                             + "The harvest's `taxidermy` field of `GET /hunters/<handle>/hunts/<id>` tells what is offered.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/wallet/spent", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WalletSpent(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountWalletSpent").WithSummary("The player's totals spent")
            .WithDescription("`{spent, silver_spent}` as `Wallet/spent` answers it: em$ and gm$ paid, restored plus this server's purchases.")
            .Produces(200, contentType: "application/json");

        foreach (var (kind, currency) in new[] { ("ems", "em"), ("gms", "gm") })
            group.MapGet($"/wallet/{kind}", (HttpContext ctx) => Account(ctx) is { } account
                    ? Json(200, state.WalletPage(account, currency, long.TryParse(ctx.Request.Query["offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset : 0))
                    : Refuse(401, "signed_out", "Sign in first."))
                .WithName($"AccountWallet{char.ToUpperInvariant(kind[0])}{kind[1..]}").WithSummary($"The player's {currency}$ history, one page (query `offset`)")
                .WithDescription($"The rows of `Wallet/{(currency == "em" ? "transactions" : "silver_transactions")}`: {State.WalletPageSize} rows from `offset`, newest first.")
                .QueryParams(("offset", "Rows to skip"))
                .Produces(200, contentType: "application/json");

        group.MapPost("/gallery/delete", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                return form.TryGetValue("id", out var id) && long.TryParse(id[0], NumberStyles.None, CultureInfo.InvariantCulture, out var photo) && state.DeletePhoto(account, photo)
                    ? Json(200, new JsonObject { ["status"] = "deleted" })
                    : Refuse(404, "invalid", "This is not a photo of yours.");
            })
            .WithName("AccountGalleryDelete").WithSummary("Delete a photo of the player's own gallery (form `id`)")
            .WithDescription("Deletes the photo's row and files: `{status: deleted}`; 404 `invalid` for an id that is none of the player's photos.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/gallery/zip", (HttpContext ctx) => Account(ctx) is { } account
                ? new Reply(200, [("Content-Type", "application/zip"), ("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff")], state.GalleryZip(account), "account")
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountGalleryZip").WithSummary("The player's own gallery as a zip of its pictures")
            .WithDescription("The pictures of `Gallery/list` (full size, one JPEG each, named by their link).")
            .Produces(200, contentType: "application/zip");

        group.MapGet("/loadouts", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, JsonNode.Parse(state.LoadoutsReply(account))!)
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountLoadouts").WithSummary("The player's saved loadouts, for the launcher's start picker")
            .WithDescription("What `GET /game/user/loadout` answers for the player: `[{name, color, items, id}]` ascending by id; `id` is the number `POST /auth/launch` takes as `loadout`.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/inventory", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebInventory(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountInventory").WithSummary("The player's own items")
            .WithDescription("`{items: [{id, name, variant, category, categoryName, units, slot, equipped, expires}]}`, one row per owned copy, oldest first: name, variant and the store's sub-category are the catalogue's, `units` is null for an item that is not counted, `equipped` is a slot above 0, `expires` is the end of a license or timed item in unix seconds (0 for an unlimited one), null for an item with none.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/bundles", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.BundleOffers(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountBundles").WithSummary("What each store bundle costs the player now")
            .WithDescription("`{bundles: [{id, price, owned, of, buyable}]}` for the bundles `GET /catalog/store` lists: a bundle gives at most the limit of each item and not again an item the player owns up to its limit, and its price drops by the share of those items "
                             + "(docs/server.md#ownership-limits). `price` is em$ as `POST /account/buy` charges it, `owned` of `of` items are not given again, `buyable` is false when the store does not sell the bundle or it would give nothing.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/placed", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.PlacedItems(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountPlaced").WithSummary("The player's own placed tents, callers, blinds, stands, tripods, towers and climbing ropes, for the launcher's map")
            .WithDescription("`{items: [{id, reserve, kind, name, x, y}]}`: `id` is the item's guid (the number `POST /auth/launch` takes as `tent` for a tent), `reserve` the web's reserve number, `kind` one of "
                             + "`tent`, `caller`, `groundblind`, `treestand`, `tripod`, `deployabletower`, `climbing`, `name` the catalogue's, `x` and `y` percent of the reserve's map picture, as the starts of `GET /catalog/reserves`. "
                             + "Only items in slot 23 with a position and a reserve.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/events", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.EventStatus(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountEvents").WithSummary("The player's seasonal events setting and the event that runs now")
            .WithDescription("`{play, event}`: `play` is whether the player plays seasonal events (on by default), `event` the event that runs now as `{name, define, reserves}` (the game's reserve numbers, "
                             + "empty for every reserve), or null outside its window or with none set. The launcher shows the event's pictures only while `play` is true.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/events/panel", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.EventPanel(account, Lang(ctx)) ?? new JsonObject { ["event"] = null })
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountEventPanel").WithSummary("The running event's panel: community missions, the player's event missions, store items")
            .WithDescription("For a player who plays seasonal events while an event runs: `{name, title, start, end, community, contributors, missions, items}` (`start` and `end` unix seconds; `community` `[{id, title, progress, target, gm, em}]` "
                             + "with the divided target, `contributors` the top 10 `[{handle, count}]`, `missions` the player's `[{id, title, state, objectives: [{title, done}], reward}]`, `items` `[{id, name}]`). Otherwise `{event: null}`. Optional query `lang`: the mission texts in that language (`fr`, `pt-BR`), English where it has none.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/events", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                if (!form.TryGetValue("play", out var play) || play[0] is not ("1" or "0")) return Refuse(400, "invalid", "This needs play=1 or play=0.");
                state.Store.SetPlaysEvents(account, play[0] == "1");
                return Json(200, state.EventStatus(account));
            })
            .WithName("AccountEventsSet").WithSummary("Play seasonal events or not (form `play=1` or `play=0`)")
            .WithDescription("Stored for the account; the game's next connect carries no event for a player who turned it off (a running hunt keeps the event it started with). Answers like `GET /account/events`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/settings", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                if (!form.TryGetValue("use_imperial", out var imperial) || imperial[0] is not ("1" or "0")) return Refuse(400, "invalid", "This needs use_imperial=1 or use_imperial=0.");
                state.Store.SetUsesImperial(account, imperial[0] == "1");
                return Json(200, new JsonObject { ["use_imperial"] = imperial[0] == "1" });
            })
            .WithName("AccountSettings").WithSummary("Imperial units or metric (form `use_imperial=1` or `use_imperial=0`)")
            .WithDescription("Stored with the account, so it follows the player to another PC. The game's next connect carries `account.use_imperial` (1 or 0: it shows yards and pounds when non-zero), and `GET /auth/me` says it as `imperial`. Answers `{use_imperial}`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/discord", (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                return Account(ctx) is { } account
                    ? Json(200, new JsonObject { ["first"] = state.Store.NoteDiscordInvite(account) })
                    : Refuse(401, "signed_out", "Sign in first.");
            })
            .WithName("AccountDiscord").WithSummary("Whether this is the first time the player is invited to the Discord")
            .WithDescription("`{first}`: true the first time for the account, false every time after (stored with the account). The website and the launcher show their one-time Discord dialog when it is true.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/referral", (HttpContext ctx) =>
                Account(ctx) is { } account ? Json(200, state.ReferralStatus(account, site)) : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountReferral").WithSummary("The account's referral code and its recruits' progress")
            .WithDescription("`code` (8 characters of A-Z and 2-9, stable) and `link` (`/join/<code>` on the website); `canEnter` and `enterUntil` (unix seconds; null when `canEnter` is false): whether a code may still be entered, once, "
                             + "at sign-up or within `enter_hours` (72) of the account's making, and for an account made before the feature, `existing_days` (31) after its release; `enteredCode` and `recruiter` (his handle) once entered, else null; "
                             + "`qualification` `{hours, score}`: the hours played here and hunterscore gained since the code was entered that make a recruit count; `recruits` `[{handle, hours, score, qualified, qualifiedAt, discordLinked, paid}]` "
                             + "(`hours` and `score` since his entry; not qualified first, the closest to qualifying first, then the qualified, the latest first; `paid`: counted for the tiers, a recruit over the weekly cap stays qualified and unpaid until he fits); "
                             + "`qualifiedCount` and `countedCount` (the qualified recruits, and those counted under the weekly cap: the tiers follow the latter); `tiers` `[{recruits, reward, reached}]` (reached by the counted recruits); `discord` `{linkedCount, milestones: [{recruits, reward, reached}]}` (counted recruits who linked Discord); `recruitReward`: what a recruit gets when he enters a code. "
                             + "A reward is `{gm, em, items: [{id, name, count}]}`, null while the operator has not set one (to be announced). The recruits are brought up to date first. 401 `signed_out`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/referral/code", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                string? code;
                try { code = (JsonNode.Parse(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync()) as JsonObject)?["code"]?.GetValue<string>(); }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { code = null; }
                if (code is null) return Refuse(400, "invalid", "This needs a JSON body {\"code\": \"ABCD2345\"}.");
                var (status, recruiter, reward) = state.EnterReferralCode(account, code);
                return status == "ok"
                    ? Json(200, new JsonObject { ["status"] = "ok", ["recruiter"] = recruiter, ["reward"] = state.RewardJson(reward) })
                    : Json(409, new JsonObject { ["status"] = status });
            })
            .WithName("AccountReferralCode").WithSummary("Enter a referral code (JSON `{code}`)")
            .WithDescription("Once per account, never changed. The code is read without case, spaces and dashes. Answers `{status: ok, recruiter, reward}` (the recruiter's handle; `reward` the recruit reward paid at once, null when none is set; the game gets it with its next heartbeat or connect), "
                             + "or 409 `{status}` with `unknown` (no such code), `own` (his own), `loop` (the code's owner is his recruit), `already` (he entered one), `expired` (the window closed) or `banned` (the code's owner is banned); 400 `invalid` for another body; 401 `signed_out`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/restore-offer", (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                return Account(ctx) is { } account
                    ? Json(200, new JsonObject { ["first"] = state.Store.NoteRestoreOffer(account) })
                    : Refuse(401, "signed_out", "Sign in first.");
            })
            .WithName("AccountRestoreOffer").WithSummary("Whether to offer the player the restore page now")
            .WithDescription("`{first}`: true the first time for the account, false every time after (stored with the account), and false when the account has restored a backup zip or has an upload waiting (an archive claim does not stop it: the player may have more in a zip). The website and the launcher show their one-time \"Do you have a backup?\" dialog when it is true.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/friends", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebFriends(account, site))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountFriends").WithSummary("The player's friends, the players who asked to be friends and those the player asked")
            .WithDescription("`{friends, requests, pending}`, each a list of `{handle, online, avatar}` by handle (upper case, ordinal), `online` only true for friends; `avatar` as in `GET /hunters/<handle>` (docs/server.md#avatars).")
            .Produces(200, contentType: "application/json");

        group.MapGet("/bans", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebBans(account, site))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountBans").WithSummary("The players the player banned from its multiplayer games")
            .WithDescription("`{bans: [{handle, avatar, ts}]}`, the latest first; `ts` Unix seconds, `avatar` as in `GET /hunters/<handle>` (docs/server.md#avatars). The in-game Ban button adds to it (`User/ban_player`).")
            .Produces(200, contentType: "application/json");

        group.MapPost("/bans/unban", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                return form.TryGetValue("handle", out var h) && state.WebUnban(account, h[0])
                    ? Json(200, new JsonObject { ["status"] = "ok" })
                    : Refuse(404, "invalid", "You have not banned this player.");
            })
            .WithName("AccountUnban").WithSummary("Lift the player's ban of another player from its games (form `handle`)")
            .WithDescription("`{status: ok}`; 404 `invalid` for a handle the player has not banned (another player's ban cannot be lifted).")
            .Produces(200, contentType: "application/json");

        group.MapGet("/friends/search", (HttpContext ctx) =>
            {
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var text = ctx.Request.Query["text"].ToString();
                if (text.Trim().Length >= State.FriendSearchMin && !state.SearchAllowed(account))
                    return Refuse(429, "rate_limited", $"At most {State.SearchesPerHour} searches an hour: try again later.");
                return Json(200, state.WebFriendSearch(account, text, site));
            })
            .WithName("AccountFriendSearch").WithSummary("Players whose handle contains the text (query `text`, at least 3 characters)")
            .WithDescription($"`{{hunters: [{{handle, online, avatar}}]}}`, the exact handle first, then by handle, at most {State.FriendSearchLimit}; never the player itself; `online` only true for the player's friends. "
                             + $"A text under {State.FriendSearchMin} characters (spaces around it left out) finds nothing and is not counted; 429 `rate_limited` past {State.SearchesPerHour} searches an hour of the account.")
            .QueryParams(("text", "A part of the handle, any case, at least 3 characters"))
            .Produces(200, contentType: "application/json").Produces(429, contentType: "application/json");

        group.MapGet("/messages", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebConversations(account, site))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountMessages").WithSummary("The player's own conversations: the server's notices and the restored ones")
            .WithDescription("`{conversations: [{handle, preview, ts, unread, avatar}]}`, read only; `ts` is unix seconds; `avatar` is the partner's as in `GET /hunters/<handle>`, null for the server's notices.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/avatars", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.AvatarChoices(account, site))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountAvatars").WithSummary("The player's avatar and the ones to choose from")
            .WithDescription("`{current, ids, locked, imported, face, own}`: `current` the player's avatar as in `GET /hunters/<handle>` (null for none), `ids` the game's avatars the player can choose, in the order of its list (docs/server.md#avatars): the free ones and the store's once the player owns their item, "
                             + "`locked` the store avatars it cannot yet, `[{id, item, name}]` (`item` the store item that unlocks it), `imported` the link of the picture imported from the player's backup, null without one, "
                             + "`face` the 3D head the game draws the player with (the id of the listed avatar of that number; null for an account with none), `own` true while the current picture is the imported one.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/avatar", async (HttpContext ctx) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                var id = form.TryGetValue("id", out var v) ? v[0] : "";
                return (id == "imported" ? state.SetAvatar(account, null) : long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var listed) && state.SetAvatar(account, listed))
                    ? Json(200, state.AvatarChoices(account, site))
                    : Refuse(404, "invalid", "This is none of the avatars you can choose.");
            })
            .WithName("AccountAvatar").WithSummary("Choose the player's avatar (form `id`: a game avatar's id, or `imported`)")
            .WithDescription("The game sees it at its next start (`avatar_url`, `face`, `avatar`, `avatar_id`, and for a listed avatar `gender`; `imported` changes only the picture and keeps a `face` that is a 3D head of the account's gender). 200 like `GET /account/avatars`; 404 `invalid` for an id that is not in the game's list, a store avatar whose item the player does not own, or `imported` without an imported picture.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/messages/thread", (HttpContext ctx) => Account(ctx) is not { } account
                ? Refuse(401, "signed_out", "Sign in first.")
                : state.WebThread(account, ctx.Request.Query["handle"].ToString()) is { } thread
                    ? Json(200, thread)
                    : Refuse(404, "invalid", "This is none of your conversations."))
            .WithName("AccountMessagesThread").WithSummary("One of the player's conversations, newest message first (query `handle`)")
            .WithDescription("`{messages: [{mine, content, ts}]}`, read only (nothing is marked read); 404 `invalid` for a handle that is none of the conversations.")
            .QueryParams(("handle", "The conversation's handle, any case"))
            .Produces(200, contentType: "application/json");

        foreach (var (path, name, add) in new[] { ("/friends/add", "AccountFriendAdd", true), ("/friends/remove", "AccountFriendRemove", false) })
            group.MapPost(path, async (HttpContext ctx) =>
                {
                    if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                    if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                    var form = Py.ParseQs(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync());
                    return form.TryGetValue("handle", out var h) && state.WebFriend(account, h[0], add)
                        ? Json(200, new JsonObject { ["status"] = "ok" })
                        : Refuse(404, "invalid", "This is not another player of this server.");
                })
                .WithName(name).WithSummary(add ? "Ask a player to be friends, or accept their request (form `handle`)" : "Decline or cancel a request, or remove a friend (form `handle`)")
                .WithDescription("`{status: ok}`; 404 `invalid` for the player's own handle or one that has no account here.")
                .Produces(200, contentType: "application/json");

        group.MapGet("/missions", (HttpContext ctx) => Account(ctx) is not { } account
                ? Refuse(401, "signed_out", "Sign in first.")
                : state.WebMissions(account, Lang(ctx)) is { } missions ? Json(200, missions) : Refuse(404, "invalid", "This server has no missions."))
            .WithName("AccountMissions").WithSummary("The player's own missions page")
            .WithDescription("`{missions: [{id, title, desc, state: active|available|completed|locked, objectives, objectives_done, reward, reserve, species, weapon}], groups: [{id, title, desc, favourite, missions}], completed, "
                             + "auto_mission_activate, activate_only_favourite, hide_completed_favourite}`; 404 `invalid` when the catalogue has no missions. Optional query `lang` (`fr`, `pt-BR`): the titles, descriptions and objectives in that language, English where it has none (docs/server.md, Mission texts).")
            .Produces(200, contentType: "application/json");

        group.MapPost("/missions/{call}", async (HttpContext ctx, string call) =>
            {
                if (!OwnPost(ctx)) return Refuse(400, "invalid", "This request is not from a page of this server.");
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                if (!MissionCalls.Contains(call)) return Refuse(404, "invalid", "This is not a missions call.");
                using var body = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(body);
                return state.MissionCall(account, call, body.ToArray()) switch
                {
                    true => Json(200, new JsonObject { ["status"] = "ok" }),
                    false => Refuse(409, "refused", "This mission is not available, or this is not a group."),
                    null => Refuse(404, "invalid", "This server has no missions."),
                };
            })
            .WithName("AccountMissionCall").WithSummary("One of the missions page's writes: activate, activate_all_available, update_automatic_activation, update_activate_only_favourite, update_hide_completed_favourite, update_favourite")
            .WithDescription("The form is the one `POST /v1/Mission/<call>` takes without the token (`mission_id`, the switch's own name, `group_id` and `is_favourite`); `{status: ok}`, 409 `refused` for a mission that is not available or a group id that is no number.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/missions/daily", (HttpContext ctx) => Account(ctx) is not { } account
                ? Refuse(401, "signed_out", "Sign in first.")
                : state.WebDaily(account, Lang(ctx)) is { } daily ? Json(200, daily) : Refuse(404, "invalid", "This server has no daily missions."))
            .WithName("AccountDailyMissions").WithSummary("The player's own daily missions page")
            .WithDescription("`{today, ends_in, frozen, broken, days: [{number, stars, gms}], missions: [{title, desc, objectives, reserve, rewards, done}]}`: the 7 days of the streak's block and the mission of each (empty for a day "
                             + "without one); 404 `invalid` when the server has no daily missions. Optional query `lang`: the texts in that language (`fr`, `pt-BR`), English where it has none.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/missions/daily/activate", (HttpContext ctx) => !OwnPost(ctx) ? Refuse(400, "invalid", "This request is not from a page of this server.")
                : Account(ctx) is not { } account ? Refuse(401, "signed_out", "Sign in first.")
                : state.DailyActivate(account) is true ? Json(200, new JsonObject { ["status"] = "ok" }) : Refuse(404, "invalid", "This server has no daily missions."))
            .WithName("AccountDailyActivate").WithSummary("Start the daily mission streak, as Daily_Mission/activate_user_progress")
            .WithDescription("`{status: ok}`: a frozen streak becomes active at day 1 today; 404 `invalid` when the server has no daily missions.")
            .Produces(200, contentType: "application/json");
    }

    static readonly string[] MissionCalls = ["activate", "activate_all_available", "update_automatic_activation", "update_activate_only_favourite", "update_hide_completed_favourite", "update_favourite"];

    const string TooLarge = "This request is too large: a request is at most 90 MiB, and a larger zip is sent in parts.";
    const string Busy = "An upload of yours is still being processed, or the server is busy. Try again in a minute.";
}
