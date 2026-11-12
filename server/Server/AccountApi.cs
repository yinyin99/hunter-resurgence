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
        var group = app.MapGroup("/account").RequireHost(game.Web).WithGroupName("game").WithTags("Account (web_host, session cookie)");

        long? Account(HttpContext ctx) => SignIn.CookieOf(ctx.Request.Headers.Cookie.ToString(), SignIn.SessionCookie) is { } c ? state.Store.ResolveWebSession(c) : null;

        group.MapGet("/restore", (HttpContext ctx) =>
            {
                if (Account(ctx) is not { } account) return Refuse(401, "signed_out", "Sign in first.");
                var (restored, review, decisions) = uploads.Status(account);
                return Json(200, new JsonObject
                {
                    ["restoredUserId"] = restored,
                    ["review"] = new JsonArray([.. review.Select(x => (JsonNode?)new JsonObject { ["ts"] = x.Ts, ["note"] = x.Note })]),
                    ["decisions"] = new JsonArray([.. decisions.Select(x => (JsonNode?)new JsonObject
                    {
                        ["ts"] = x.Ts, ["kind"] = x.Kind, ["userId"] = x.UserId, ["handle"] = x.Handle, ["handleKept"] = x.HandleKept, ["reason"] = x.Reason,
                    })]),
                });
            })
            .WithName("AccountRestoreStatus").WithSummary("What became of the player's uploads")
            .WithDescription("`restoredUserId`: the backup's user id the account restored (once), or null; `review`: the zips kept for the operator, each with its time and note "
                             + "(the reason of a refused approve); `decisions`: what the operator decided, each `{ts, kind: restored|rejected, userId, handle, handleKept, reason}`.");

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
                var o = claims.Claim(account, One("name"), One("old_name") == "1", One("confirm") == "overwrite");
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
                if (uploads.Start(account) is not { } id) return Logged(account, new(429, "busy", Busy));
                try
                {
                    var part = await uploads.Part(account, id, 0, ctx.Request.Body);
                    return Logged(account, part.Kind == "received" ? uploads.Finish(account, id) : part);
                }
                finally
                {
                    uploads.Drop(id);
                }
            }))
            .WithName("AccountRestore").WithSummary("Upload a backup zip in one request (raw body)")
            .WithDescription("The zip is the request body (at most 90 MiB; a larger zip is sent in parts, below), one upload at a time per account. A readable zip is kept for an operator "
                             + "and never restored automatically (`server review approve <id>`). "
                             + "Answers `{status, message}`: status `review`, `already`, `taken`, `invalid`, `busy` or `too_large`.")
            .Produces(200, contentType: "application/json");

        group.MapPost("/restore/start", Task<Reply> (HttpContext ctx) => Upload(ctx, account => Task.FromResult(uploads.Start(account) is { } id
                ? Json(200, new JsonObject { ["status"] = "started", ["id"] = id, ["partBytes"] = Uploads.MaxPartBytes })
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

        group.MapPost("/restore/{id}/finish", (HttpContext ctx, string id) => Upload(ctx, account => Task.FromResult(Logged(account, uploads.Finish(account, id)))))
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
                var gms = One("wallet", "GOLD") switch { "GOLD" => false, "SILVER" => true, _ => (bool?)null };
                if (gms is null) return Refuse(400, "invalid", "The wallet is GOLD (em$) or SILVER (gm$).");
                return state.WebBuy(account, id, One("bundle", "0") == "1", count, gms.Value) is { } balance
                    ? Json(200, new JsonObject { ["status"] = "bought", [gms.Value ? "gms" : "ems"] = balance })
                    : Refuse(409, "refused", $"This is not for sale, you do not have enough {(gms.Value ? "gm$" : "em$")}, or you own as many as allowed.");
            })
            .WithName("AccountBuy").WithSummary("Buy from the store with em$ or gm$ (form `id`, `count`, `wallet`, `bundle=1` for a bundle)")
            .WithDescription("The item or bundle of `GET /catalog/store` at its price: `wallet` `GOLD` (the default) pays the em$ price, `SILVER` the gm$ price (items only, never discounted). "
                             + "Answers `{status: bought, ems}` or `{status: bought, gms}` (the new balance of the wallet paid), 400 `invalid` for another wallet, "
                             + "or 409 `refused` for something not for sale, a balance too low or an own limit reached. The game gets the items with its next heartbeat or connect.")
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

        group.MapGet("/events", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.EventStatus(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountEvents").WithSummary("The player's seasonal events setting and the event that runs now")
            .WithDescription("`{play, event}`: `play` is whether the player plays seasonal events (on by default), `event` the event that runs now as `{name, define, reserves}` (the game's reserve numbers, "
                             + "empty for every reserve), or null outside its window or with none set. The launcher shows the event's pictures only while `play` is true.")
            .Produces(200, contentType: "application/json");

        group.MapGet("/events/panel", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.EventPanel(account) ?? new JsonObject { ["event"] = null })
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountEventPanel").WithSummary("The running event's panel: community missions, the player's event missions, store items")
            .WithDescription("For a player who plays seasonal events while an event runs: `{name, title, start, end, community, contributors, missions, items}` (`start` and `end` unix seconds; `community` `[{id, title, progress, target, gm, em}]` "
                             + "with the divided target, `contributors` the top 10 `[{handle, count}]`, `missions` the player's `[{id, title, state, objectives: [{title, done}], reward}]`, `items` `[{id, name}]`). Otherwise `{event: null}`.")
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

        group.MapGet("/friends", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebFriends(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountFriends").WithSummary("The player's friends, the players who asked to be friends and those the player asked")
            .WithDescription("`{friends, requests, pending}`, each a list of `{handle, online}` by handle (upper case, ordinal).")
            .Produces(200, contentType: "application/json");

        group.MapGet("/friends/search", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebFriendSearch(account, ctx.Request.Query["text"].ToString()))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountFriendSearch").WithSummary("Players whose handle contains the text (query `text`)")
            .WithDescription($"`{{hunters: [{{handle, online}}]}}`, by handle, at most {State.FriendSearchLimit}; never the player itself.")
            .QueryParams(("text", "A part of the handle, any case"))
            .Produces(200, contentType: "application/json");

        group.MapGet("/messages", (HttpContext ctx) => Account(ctx) is { } account
                ? Json(200, state.WebConversations(account))
                : Refuse(401, "signed_out", "Sign in first."))
            .WithName("AccountMessages").WithSummary("The player's own conversations: the server's notices and the restored ones")
            .WithDescription("`{conversations: [{handle, preview, ts, unread}]}`, read only; `ts` is unix seconds.")
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
                : state.WebMissions(account) is { } missions ? Json(200, missions) : Refuse(404, "invalid", "This server has no missions."))
            .WithName("AccountMissions").WithSummary("The player's own missions page")
            .WithDescription("`{missions: [{id, title, desc, state: active|available|completed|locked, objectives, reward, reserve, species, weapon}], groups: [{id, title, desc, favourite, missions}], completed, "
                             + "auto_mission_activate, activate_only_favourite, hide_completed_favourite}`; 404 `invalid` when the catalogue has no missions.")
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
                : state.WebDaily(account) is { } daily ? Json(200, daily) : Refuse(404, "invalid", "This server has no daily missions."))
            .WithName("AccountDailyMissions").WithSummary("The player's own daily missions page")
            .WithDescription("`{today, ends_in, frozen, broken, days: [{number, stars, gms}], missions: [{title, desc, objectives, reserve, rewards, done}]}`: the 7 days of the streak's block and the mission of each (empty for a day "
                             + "without one); 404 `invalid` when the server has no daily missions.")
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
