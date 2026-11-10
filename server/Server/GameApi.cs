// The game hosts' API: one endpoint per path the player state answers, documented in the "game" OpenAPI document.
// Each handler checks the raw method and path itself (an exact path, or the pattern the routing contract had), like
// the contract it replaces, and returns null to fall through to the replay (Replay.Game.Fallback): a local server's
// recorded reply, a community server's empty one (ReplayScope).
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class GameApi
{
    static readonly Regex Harvest = new(@"\A/game/user/harvest/(\d+)(/taxidermize)?\z", RegexOptions.ECMAScript);
    static readonly Regex ScorePath = new(@"\A/user/[^/]+/score/\z", RegexOptions.ECMAScript);
    static readonly Regex Lodges = new(@"\A(?:/v1)?/game/user/(\d+)/lodges(?:/(\d+))?\z", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
    static readonly Regex LodgeEdit = new(@"\A(?:/v1)?/game/user/lodges/(\d+)(/settings)?\z", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
    static readonly Regex TrophyEdit = new(@"\A(?:/v1)?/game/user/trophies/(\d+)\z", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
    static readonly Regex UserProfile = new(@"\A(?:/v1)?/game/user/(\d+)/profile\z", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
    static readonly Regex UserRanks = new(@"\A(?:/v1)?/game/user/(\d+)/ranks\z", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
    static readonly byte[] IdleHeartbeat = "{\"updates\": [], \"err\": 0}"u8.ToArray();
    static readonly byte[] NoError = "{\"err\": 0}"u8.ToArray();

    public static void MapGame(this WebApplication app, Game game)
    {
        var s = game.State;
        app.Map("/{**path}", Handle(game, _ => new Reply(204, [], [], "sink")))
           .RequireHost([.. game.Sinks]).ExcludeFromDescription();  // telemetry and crash reports

        var gs = Group(app, game.Gs, "Game server (gameserver_host)");
        gs.MapExactPost(game, HEARTBEAT, r => s != null
                ? Own(game, r, a => new(200, Json(), s.HeartbeatReply(a, r.Body), "state"))
                : new(200, Json(), IdleHeartbeat, "stub"))
          .WithName("Heartbeat").WithSummary("Store hunt events, return the queued updates")
          .WithDescription("Every 20–60 s during a hunt. Updates queued by earlier heartbeats come first (trophy score, items, "
                           + "balances, toasts); photo requests get their upload URL at once. Without player state: always "
                           + "`{\"updates\": [], \"err\": 0}`.")
          .Body<HeartbeatRequest>("application/json").Produces<HeartbeatReply>();
        gs.MapExactPost(game, DISCONNECT, r => s is null ? new(200, Json(), NoError, "stub") : Own(game, r, a =>
            {
                s.Disconnect(a, r.Body);
                return new(200, Json(), NoError, "state");
            }))
          .WithName("Disconnect").WithSummary("End the hunt")
          .WithDescription("Closes the open expedition with the session's `distance_travelled`.")
          .Body<DisconnectRequest>("application/json").Produces<ErrReply>();
        if (s != null)
        {
            gs.MapExactPost(game, CONNECT, r => Own(game, r, a =>
                    new(200, game.Table.Entries.TryGetValue(new("POST", game.Gs, CONNECT, null), out var recorded) ? recorded.Headers : Json(), s.ConnectReply(a), "state")))
              .WithName("Connect").WithSummary("Start a hunt session")
              .WithDescription("The connect template (the recorded reply, else the install's sample) with the saved inventory, wallet, skills and the open expedition "
                               + "(opened here after a direct launch); `populations` and `species_ammo` of the last launched "
                               + "reserve (always from the `ethical` list), and `ingame_event` null. Queues the server notice toast.")
              .Body<ConnectRequest>("application/json").Produces(200, contentType: "application/json");
            gs.MapExactPost(game, State.UPLOAD_PATH, r => Own(game, r, a =>
                {
                    var (status, body) = s.UploadReply(a, r.Query, r.Body);
                    return new(status, [("Content-Type", "application/json; charset=UTF-8")], body, "state");
                }))
              .WithName("UploadPhoto").WithSummary("Upload a requested photo (GCS resumable-upload shape)")
              .WithDescription("Target of the `update: 11` URL a heartbeat photo request (event 4) gets. Stores the JPEG in "
                               + "the photos folder; replies like GCS `storage#object`, 404 for an unknown upload id.")
              .QueryParams(("uploadType", "`resumable`"), ("upload_id", "photo id from the update 11 URL"), ("part", "`thumb` for the thumbnail"))
              .Body<byte[]>("image/jpeg").Produces(200, contentType: "application/json").Produces(404, contentType: "application/json");
            var photo = Handle(game, r =>
                r.Method is "GET" or "HEAD" && r.Path.StartsWith(State.PHOTO_PREFIX, StringComparison.Ordinal)
                && s.PhotoFile(r.Path) is { Length: > 0 } f
                    ? new(200, [("Content-Type", "image/jpeg")], File.ReadAllBytes(f), "state")
                    : null);
            gs.MapGet(State.PHOTO_PREFIX + "{**name}", photo)
              .WithName("Photo").WithSummary("A stored photo")
              .WithDescription("Photos uploaded through UploadPhoto, by account id and file name. Public: the link is the access.")
              .PathParams(("name", "`<account id>/<photo id>-<photo_type>[-thumb].jpg`")).Produces(200, contentType: "image/jpeg");
            gs.MapMethods(State.PHOTO_PREFIX + "{**name}", ["HEAD"], photo).ExcludeFromDescription();
        }

        var api = Group(app, game.Api, "Web API (api_host, /v1)");
        if (s != null)
        {
            api.MapExactPost(game, MOVE_MULTIPLE2, r => Own(game, r, a => new(200, JsonAnyOrigin(), s.MoveMultiple2Reply(a, r.Body), "state")))
               .WithName("MoveItems").WithSummary("Move inventory items between slots, clone stacks")
               .WithDescription("Reply `null`, or `{placeholder: new guid}` when stacks were cloned. Plain HTTP in the client.")
               .Body<MoveMultiple2Form>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, BUY, r => Own(game, r, a => new(200, JsonAnyOrigin(), s.BuyReply(a, r.Body), "state")))
               .WithName("Buy").WithSummary("Buy a catalogue item")
               .WithDescription("Charges now (the item's first Application/discounts row if active, rounded half up, as the "
                                + "store shows it); the items (`update: 10`) and the new balance (`update: 15`) come with the "
                                + "next heartbeat. Between hunts the next connect carries the items and the first heartbeat "
                                + "still the balance. `{\"success\": false}` for an unknown item, a missing price, a service or "
                                + "too little currency.")
               .Body<BuyForm>(Form).Produces<BuyReply>();
            api.MapExactPost(game, LAUNCH_START, r => Own(game, r, a =>
                {
                    s.NoteLaunch(a, r.Body);
                    return s.Online ? new(200, JsonAnyOrigin(), Py.DumpBytes(game.Tokens!.Launch(a)), "state") : null;  // offline: the recorded reply
                }))
               .WithName("LaunchStart").WithSummary("Launch a hunt in a reserve")
               .WithDescription("Opens a new expedition at `start` in `reserve` (closing any open one). Reply: `{access_token, refresh_token, "
                                + "session, status}` with fresh tokens of the player; a local server replays the recorded one.")
               .Body<LaunchStartForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, START_SINGLE_PLAYER, r => Own(game, r, a =>
                {
                    s.NoteLaunch(a, r.Body);
                    var (session, network) = (s.Store.CreateToken(a, Lifetimes.Session), 7_000_000_000L + RandomNumberGenerator.GetInt32(1_000_000_000));
                    List<(string, string)> headers = [.. JsonAnyOrigin()];
                    foreach (var path in new[] { "/", START_SINGLE_PLAYER[..(START_SINGLE_PLAYER.LastIndexOf('/') + 1)] })  // the launcher reads them from either path
                        headers.AddRange([("Set-Cookie", $"{ProtocolLiterals.LaunchSessionCookie}={session}; Path={path}; SameSite=lax; Secure"),
                                          ("Set-Cookie", $"{ProtocolLiterals.LaunchNetworkCookie}={network}; Path={path}; SameSite=lax; Secure")]);
                    return new(200, headers, Py.DumpBytes(new JsonObject { ["session_id"] = session, ["network_id"] = network }), "state");
                }))
               .WithName("StartSinglePlayer").WithSummary("Start a single-player session in a reserve")
               .WithDescription("Opens a new expedition at `lodge_id` in `reserve` (closing any open one). Reply `{session_id, network_id}`: the session is the "
                                + "first segment of the launch URL (and the game's `X-Hun-Session`), and it is set as cookies too.")
               .Body<StartSinglePlayerForm>("application/json").Produces(200, contentType: "application/json");
            api.MapExactPost(game, CLIENT_APPLICATION, r => game.Recorded(r) is { Tier: >= 1 } ? null : new(200, JsonAnyOrigin(), s.ClientApplicationReply(), "state"))
               .WithName("ClientApplication").WithSummary("The catalogue the in-game pages read")
               .WithDescription("The recorded reply when the operator has one, else the species, items, reserves and locations of the player state (missions only in a recording).")
               .Body<LangForm>("application/json").Produces(200, contentType: "application/json");
            // the game's JWT, asked for by the game on its own host and by the pages on the API host; a local server replays the recorded one
            Func<Req, Reply?> jwt = r => !s.Online ? null : Own(game, r,
                a => new(200, JsonAnyOrigin(), Py.DumpBytes(new JsonObject { ["token"] = game.Tokens!.Jwt(a) }), "state"), WithJsonToken(r));
            foreach (var (group, name) in new[] { (gs, "GameJwt"), (api, "ApiJwt") })
                group.MapExactPost(game, JWT, jwt).WithName(name).WithSummary("A JWT for the game API v2")
                     .WithDescription("`{token}`: a three-part token that expires in 30 minutes and resolves to the account of `oauth_access_token`.")
                     .Body<TokenForm>("application/json").Produces(200, contentType: "application/json");
            api.MapExactPost(game, OAUTH_TOKEN, r => !s.Online ? null : FormValue(r, "oauth_refresh_token") is { } refresh
                    && refresh.StartsWith(Lifetimes.RefreshPrefix, StringComparison.Ordinal) && s.Store.ResolveToken(refresh) is { } owner
                    ? new(200, JsonAnyOrigin(), Py.DumpBytes(game.Tokens!.Renew(refresh, owner)), "state") : Denied(game, r))
               .WithName("RenewToken").WithSummary("A new access token for a refresh token")
               .WithDescription("`{access_token, refresh_token, expires, user_id, account_id}`; the refresh token is not rotated.")
               .Produces(200, contentType: "application/json");
            gs.MapMethods(ProtocolLiterals.LauncherBase + "/{**rest}", ["GET", "POST"], Handle(game, r => s.Online ? LauncherCall(r) : null))
              .WithName("LauncherCalls").WithSummary("The old launcher's own calls: token, initialise, compatibleHardware, ping")
              .WithDescription("Answered without recordings on a community server; a local server replays them.");
            app.MapAuth(game);
            app.MapAccount(game);
            app.MapHunters(game);
            app.MapCatalog(game);
            api.MapExactPost(game, CHAT_CONVERSATIONS, r => Own(game, r, a => new(200, JsonAnyOrigin(), s.ConversationsReply(a), "state")))
               .WithName("ChatConversations").WithSummary("Inbox threads")
               .WithDescription("The server's thread (non-permitted ammo notices), newest message as preview; `[]` when empty.")
               .Body<TokenForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, CHAT_CONVERSATION, r => Own(game, r, a => new(200, JsonAnyOrigin(), s.ConversationReply(a, r.Body), "state")))
               .WithName("ChatConversation").WithSummary("One thread, newest first; marks it read")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, SKILL_LIST, r => Own(game, r, a => s.SkillListReply(a, r.Body) is { Length: > 0 } reply
                    ? new(200, JsonAnyOrigin(), reply, "state")
                    : null))
               .WithName("SkillList").WithSummary("Skill numbers of a player")
               .WithDescription("The player's own skills from the state (level plus the fraction of its span); for other players a local "
                                + "server replays the recording and a community server sends empty skills.")
               .Body<UserForm>(Form).Produces<SkillNumbers>();
            api.MapExactPost(game, STATISTICS_TOTAL, r => Own(game, r, a => s.StatisticsReply(a, r.Body) is { Length: > 0 } reply
                    ? new(200, JsonAnyOrigin(), reply, "state")
                    : null))
               .WithName("StatisticsTotal").WithSummary("Statistics of a player")
               .WithDescription("The restored statistics plus the closed server hunts (`data`, `distance`, `duration`): the player's own on a local "
                                + "server (other players and unrestored accounts replay the recording), any account's on a community server (an unknown, banned or recorded user id gets empty statistics).")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, ACHIEVEMENT_STATS, r => Own(game, r, a => s.AchievementReply(a, r.Body) is { Length: > 0 } reply
                    ? new(200, JsonAnyOrigin(), reply, "state")
                    : null))
               .WithName("AchievementStats").WithSummary("Achievements of a player with their earn times")
               .WithDescription("The restored achievements and those earned on a community server, with their earn times: the player's own on a local "
                                + "server (other players replay the recording, also for its unrestored accounts), any account's on a community server (`[]` for an unknown, banned or recorded user id).")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, LEADERBOARD_PERSONAL, r => Own(game, r, a => s.PersonalBestReply(a, r.Body) is { Length: > 0 } reply
                    ? new(200, JsonAnyOrigin(), reply, "state")
                    : null))
               .WithName("LeaderboardPersonal").WithSummary("A player's personal bests of a season")
               .WithDescription("`{status, data: [{specie, score, distance}]}` of the `user_id` and `season_no`: the restored reply as saved, and for season 0 (all time) with the best score and the longest range of this server's scored kills "
                                + "where they beat it (any account's on a community server, `{status: done, data: []}` for an unknown, banned or recorded user id); a local server replays the recording for other players.")
               .Body<PersonalForm>(Form).Produces(200, contentType: "application/json");
        }
        api.MapExactPost(game, SPLASH, r => game.Recorded(r) is { Tier: >= 1 } hit
                ? new(hit.Status, hit.Headers, SplashWithNotice(hit.Body), "patch")
                : null)
           .WithName("Splashes").WithSummary("Launcher news splashes")
           .WithDescription("The recorded list with a server notice first.").Produces(200, contentType: "application/json");
        api.MapGet(FEED_DATA, Handle(game, r => r.Method == "GET" && r.Path == FEED_DATA ? FeedData(game, r) : null))
           .WithName("FeedData").WithSummary("Launcher news: `{posts, splashes}`")
           .WithDescription("The recorded reply with the server notice first in `splashes`; without a recording `posts` is empty.").Produces(200, contentType: "application/json");
        if (s != null)
        {
            // Base: the recorded reply on a local server, the endpoint's empty shape on a community server (ReplayScope).
            // `user_id` of another player is that player's hunts, see State.HuntsOf; anyone else's is the base.
            api.MapExactPost(game, PUBLIC_EXPEDITION, r => game.Base(r) is { } hit
                    ? Own(game, r, a => s.HuntsOf(a, r.Body) is { } who ? new(hit.Status, hit.Headers, s.ExpeditionReply(who, hit.Body, r.Body), "state") : null)
                    : null)
               .WithName("LastHunt").WithSummary("One expedition (the latest, or `expedition_id`) with its kills and stats")
               .WithDescription("`stats` stays null while the hunt is open; `expedition_id` picks an older hunt (an unknown id and the "
                                + "time before the first server hunt: a hunt a restore saved, else the recorded reply, a community server: `null`). "
                                + "`user_id` of another player here is that player's hunt (no token of theirs); an unknown or banned one is the base reply.")
               .Body<ExpeditionForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, EXPEDITION_LIST, r => game.Base(r) is { } hit
                    ? Own(game, r, a => s.HuntsOf(a, r.Body) is { } who ? new(hit.Status, hit.Headers, s.ExpeditionListReply(who, hit.Body, r.Body), "state") : null)
                    : null)
               .WithName("Expeditions").WithSummary("Expedition history, newest first")
               .WithDescription("Server expeditions ahead of the hunts a restore saved, else the recorded ones, paged by `offset` and `limit` "
                                + "(a community server: empty). `user_id` of another player here is that player's list; an unknown or banned one is the base reply.")
               .Body<ExpeditionListForm>(Form).Produces(200, contentType: "application/json");
            foreach (var (path, name) in new[] { (ME, "Me"), (POLL, "Poll") })
            {
                Func<Req, Reply?> profile = r => game.Base(r) is not { } hit ? null : Own(game, r, a =>
                    {
                        var reply = (JsonObject)JsonNode.Parse(hit.Body)!;
                        if (path == ME)
                            reply = s.MeReply(a, reply) ?? reply;
                        else
                        {
                            if (s.Online)
                                reply["avatar"] = s.Avatar(a);
                            // the current build's keys, whatever the base reply had
                            reply["campaign_stylesheet"] ??= false;
                            reply["community_campaign_stylesheet"] ??= false;
                            reply["active_campaign"] ??= false;
                            reply["last_hunt_update_ts"] = s.LastHuntUpdate(a);
                        }
                        var wallet = s.Wallet(a);
                        reply["gms"] = wallet["gm"]!.DeepClone();
                        reply["ems"] = wallet["em"]!.DeepClone();
                        reply["unread_messages"] = s.Unread(a);
                        if (s.Online)
                            reply["friend_requests"] = s.FriendRequests(a);
                        return new(hit.Status, hit.Headers, Py.DumpBytes(reply), "state");
                    });
                api.MapExactPost(game, path, profile)
                   .WithName(name).WithSummary(name == "Me" ? "Own profile" : "Launcher poll")
                   .WithDescription("The recorded reply (Me: the account's restored profile instead, when it has one) with `gms`, `ems` and "
                                    + "`unread_messages` from the state; Poll also `last_hunt_update_ts` and the campaign keys. A community server builds them from the account alone "
                                    + "(`friend_requests`: the players who asked it to be friends), "
                                    + "except Me's `rank`: the account's place by hunterscore among the accounts not banned, the recorded player's account left out.")
                   .Body<TokenForm>(Form).Produces(200, contentType: "application/json");
                if (path == ME)  // the in-game page reads it with GET and its JWT
                    api.MapGet(path, Handle(game, r => r.Method == "GET" && r.Path == path ? profile(r) : null)).WithName("MeGet")
                       .WithSummary("Own profile, read with the game API v2 JWT").Produces(200, contentType: "application/json");
            }
            // community server only (a local server replays): the account's items and restored trophies, from the database; another player's item list is the empty reply, its trophy list the public one
            api.MapExactPost(game, INVENTORY_LIST, r => !s.Online ? null : Own(game, r, a => s.IsOwn(a, r.Body) ? new(200, JsonAnyOrigin(), s.InventoryListReply(a), "state") : null))
               .WithName("InventoryList").WithSummary("Owned items with their definitions")
               .WithDescription("`[{item, own: [{id, slot, units, metadata, location}]}]` of the account's items; a placed item's `location` is `{reserve, pos, dir}` (`pos` in metres, `dir` the direction divided by 1000). Another player's `user_id`: `[]`.")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, INVENTORY_DICT, r => !s.Online ? null : Own(game, r, a => s.IsOwn(a, r.Body) ? new(200, JsonAnyOrigin(), s.InventoryDictReply(a), "state") : null))
               .WithName("InventoryDict").WithSummary("Owned items by item id")
               .WithDescription("`{<item id>: [{id, slot, units, metadata, location}]}` of the account's items, the rows of Inventory/list. Another player's `user_id`: `{}`.")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, TROPHY_LIST, r => !s.Online ? null : Own(game, r, a => s.TrophyListReply(a, r.Body) is { } reply ? new(200, JsonAnyOrigin(), reply, "state") : null))
               .WithName("TrophyList").WithSummary("A player's restored trophies, one page")
               .WithDescription("`{total, trophies}` of the `user_id`: the player's own, or any other account's here without its hidden trophies (needs any player's token); the empty reply for an unknown or banned one.")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, GALLERY_LIST, r => !s.Online ? null : Own(game, r, a => s.GalleryListReply(a, r.Body) is { } reply ? new(200, JsonAnyOrigin(), reply, "state") : null))
               .WithName("GalleryList").WithSummary("A player's gallery photos, one page")
               .WithDescription("`{total, photos: [{id, type, url, thumbnail, label, animal}]}` of the `user_id` (the player's own without one) from the photos the game uploaded, newest first, `offset` and `limit` page it; "
                                + "any other account here is listed too (needs any player's token); the empty reply for an unknown or banned one.")
               .Body<UserForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, GALLERY_DELETE, r => !s.Online ? null : Own(game, r, a => new(200, JsonAnyOrigin(),
                    long.TryParse(FormValue(r, "photo_id"), out var id) && s.DeletePhoto(a, id) ? "true"u8.ToArray() : "false"u8.ToArray(), "state")))
               .WithName("GalleryDelete").WithSummary("Delete a photo of the player's own gallery")
               .WithDescription("Form `oauth_access_token`, `photo_id`: `true`, or `false` for an id that is not one of the player's own photos.")
               .Body<TokenForm>(Form).Produces(200, contentType: "application/json");
            // the wallet history (community server only): the restored pages and this server's changes, newest first; another player's `user_id` is the empty reply
            foreach (var (path, name, currency) in new[] { (WALLET_TRANSACTIONS, "WalletTransactions", "em"), (WALLET_SILVER_TRANSACTIONS, "WalletSilverTransactions", "gm") })
                api.MapExactPost(game, path, r => !s.Online ? null : Own(game, r, a => s.WalletListReply(a, currency, r.Body) is { } reply ? new(200, JsonAnyOrigin(), reply, "state") : null))
                   .WithName(name).WithSummary($"The player's {(currency == "em" ? "em$" : "gm$")} history, one page")
                   .WithDescription($"`[{{id, created, type: {{id: C|D, name}}, amount, preBalance, postBalance, summary, mission, items, qty}}]`: {State.WalletPageSize} rows from `offset`, newest first, "
                                    + "the rows a restored backup brought and this server's purchases and rewards; the empty reply for another player's `user_id`.")
                   .Body<TokenForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, WALLET_SPENT, r => !s.Online ? null : Own(game, r, a => s.WalletSpentReply(a, r.Body) is { } reply ? new(200, JsonAnyOrigin(), reply, "state") : null))
               .WithName("WalletSpent").WithSummary("The player's totals spent")
               .WithDescription("`{spent, silver_spent}`: em$ and gm$ paid, restored plus this server's purchases; the empty totals for another player's `user_id`.")
               .Body<TokenForm>(Form).Produces(200, contentType: "application/json");
            // friends (community server only): the Friends table of the Store; a friend counts while both accounts exist here
            api.MapExactPost(game, ME_FRIENDS, r => !s.Online ? null : Own(game, r, a => new(200, JsonAnyOrigin(), s.MeFriendsReply(a), "state"), WithJsonToken(r)))
               .WithName("MeFriends").WithSummary("The account's friends, incoming requests and the user ids it asked")
               .WithDescription("`{friends: [{id, handle, avatar, fullbody_avatar, hostname, hunterscore, online}], friendCount, requests: [same], requestCount, pending: [user id]}`, sorted by handle.")
               .Body<TokenForm>(Form).Produces(200, contentType: "application/json");
            foreach (var (path, name, add) in new[] { (FRIEND_ADD, "FriendAdd", true), (FRIEND_REMOVE, "FriendRemove", false) })
                api.MapExactPost(game, path, r => !s.Online ? null : Own(game, r, a => new(200, JsonAnyOrigin(), s.FriendReply(a, r.Body, add), "state")))
                   .WithName(name).WithSummary(add ? "Ask a player to be friends, or accept their request" : "Decline or cancel a request, or remove a friend")
                   .WithDescription("`true`; `{\"errorCode\": 400}` for the player's own id or one that has no account here.")
                   .Body<FriendForm>(Form).Produces(200, contentType: "application/json");
            api.MapExactPost(game, FRIENDS_LIST, r => !s.Online ? null : new(200, JsonAnyOrigin(), s.FriendsListReply(r.Body), "state"))
               .WithName("FriendsList").WithSummary("Any player's friends, one page, without a token")
               .WithDescription("`{total, friends: [as Me/friends]}`; `limit` 1 to 50 (missing, empty or below 1: all). No `user_id`: `{\"errorCode\": 400}`; "
                                + "an empty, non-positive or unknown one: `null`; a non-numeric value: the handler error.")
               .Body<FriendsListForm>(Form).Produces(200, contentType: "application/json");
            // the public reads (community server only; a local server replays): any account here (State.PublicAccount), no token
            api.MapExactPost(game, PUBLIC_USER, r => !s.Online ? null
                    : new(200, JsonAnyOrigin(), s.PublicAccount(FormValue(r, "hostname") ?? "") is { } a ? Py.DumpBytes(s.PublicUser(a)) : "null"u8.ToArray(), "state"))
               .WithName("PublicUser").WithSummary("A player's public profile, by handle")
               .WithDescription("`{id, handle, hostname, avatar, hunterscore, rank, membership, membershipType, membershipTitle, online, stats: {distance, duration}}` "
                                + "of any account here, without a token; `null` for a handle that is unknown or banned.")
               .Body<HostnameForm>(Form).Produces(200, contentType: "application/json");
            var userProfile = Handle(game, r =>
            {
                var m = UserProfile.Match(r.Path);
                if (!s.Online || r.Method != "GET" || !m.Success) return null;
                return long.TryParse(m.Groups[1].Value, out var id) && s.PublicAccount(id) is { } a
                    ? new(200, [("Content-Type", "application/vnd.game_data.user.profile+json"), .. Cors(r.Header)], s.UserProfileReply(a), "state")
                    : new(404, [("Content-Type", "application/vnd.goa.error"), .. Cors(r.Header)], Encoding.UTF8.GetBytes(ReplayScope.Missing), "state");
            });
            api.MapGet("/v1/game/user/{id}/profile", userProfile).ExcludeFromDescription();  // the current build's path of the same page
            var userRanks = Handle(game, r =>
            {
                var m = UserRanks.Match(r.Path);
                if (!s.Online || r.Method != "GET" || !m.Success) return null;
                return long.TryParse(m.Groups[1].Value, out var id) && s.PublicAccount(id) is { } a
                    ? new(200, [("Content-Type", "application/vnd.game_data.user.rank+json; type=collection"), .. Cors(r.Header)], s.RanksReply(a), "state")
                    : new(404, [("Content-Type", "application/vnd.goa.error"), .. Cors(r.Header)], Encoding.UTF8.GetBytes(ReplayScope.Missing), "state");
            });
            api.MapGet("/v1/game/user/{id}/ranks", userRanks).ExcludeFromDescription();  // the current build's path of the same page

            var apiv2 = Group(app, game.Apiv2, "Game API v2 (apiv2_host)");
            var harvest = Handle(game, r =>
            {
                var m = Harvest.Match(r.Path);
                if (!m.Success || !long.TryParse(m.Groups[1].Value, out var id))
                    return null;
                var taxidermize = m.Groups[2].Success;
                var account = s.Authenticate(r.Tokens());  // a CORS preflight carries no token: with none it is the replay's
                if (r.Method == "OPTIONS")
                    return account is { } owner && Has(s.HarvestReply(owner, id)) ? new(204, Cors(r.Header), [], "state") : null;
                if (account is not { } a)
                    return r.Method is "GET" or "POST" ? Denied(game, r) : null;
                if (r.Method == "GET" && !taxidermize && s.HarvestReply(a, id) is { Length: > 0 } page)
                    return new(200, [("Content-Type", "application/vnd.game.user.trophy_lodge.harvest+json"), .. Cors(r.Header)], page, "state");
                if (r.Method == "POST" && taxidermize && s.Taxidermize(a, id))
                    return new(200, [("Content-Type", "text/plain"), .. Cors(r.Header)], [], "state");
                return null;
            });
            apiv2.MapGet("/game/user/harvest/{id}", harvest)
                 .WithName("Harvest").WithSummary("A harvested trophy (launcher trophy page)")
                 .WithDescription("Stored with the kill's event 3; CORS for the web host.")
                 .PathParams(("id", "harvest id (= kill id)"))
                 .Produces(200, contentType: "application/vnd.game.user.trophy_lodge.harvest+json");
            apiv2.MapPost("/game/user/harvest/{id}/taxidermize", harvest)
                 .WithName("Taxidermize").WithSummary("Taxidermize a harvested trophy")
                 .WithDescription("Marks it taxidermized; no gold charged (taxidermy price not modelled).")
                 .PathParams(("id", "harvest id (= kill id)"))
                 .Produces(200, contentType: "text/plain");
            // CORS preflights and any other method: the same handler (OPTIONS answers for a stored harvest)
            foreach (var pattern in new[] { "/game/user/harvest/{id}", "/game/user/harvest/{id}/taxidermize" })
                apiv2.Map(pattern, harvest).WithOrder(1).ExcludeFromDescription();
            // the account's trophies and the lodges of any account here (community server only); a locked lodge is sent too: the game hides it
            var restored = Handle(game, r =>
            {
                var lodges = Lodges.Match(r.Path);
                if (!s.Online || r.Method != "GET" || (r.Path != "/game/user/trophies" && !lodges.Success))
                    return null;
                if (s.Authenticate(r.Tokens()) is not { } a)
                    return Denied(game, r);
                long? owner = !lodges.Success ? null : s.IsUser(a, lodges.Groups[1].Value) ? a
                    : long.TryParse(lodges.Groups[1].Value, out var userId) ? s.PublicAccount(userId) : null;  // the player's own lodges, else a public account's
                var (body, type) = r.Path == "/game/user/trophies" ? (s.TrophiesReply(a), "application/json")  // ponytail: the trophy list's own media type is not known
                    : owner is not { } o ? (null, "")
                    : !lodges.Groups[2].Success ? (s.LodgesReply(o), "application/vnd.game.user.trophy_lodge.lodge+json; type=collection")
                    : (long.TryParse(lodges.Groups[2].Value, out var lodge) ? s.LodgeReply(o, lodge) : null, "application/vnd.game.user.trophy_lodge.lodge+json");
                return Has(body) ? new(200, [("Content-Type", type), .. Cors(r.Header)], body!, "state") : null;
            });
            apiv2.MapGet("/game/user/trophies", restored).WithName("Trophies").WithSummary("The account's trophies: restored, then harvested here")
                 .Produces(200, contentType: "application/json");
            apiv2.MapGet("/game/user/{id}/lodges", restored).WithName("Lodges").WithSummary("The lodges of any account here, with their slots' trophies")
                 .WithDescription("The lodge rows of a restore, as edited since; each animal slot with a `trophy_id` also has its `trophy_details` (the trophy's row of "
                                  + "`/game/user/trophies`). `locked` is kept: the game hides those lodges from other players. Needs any player's token; an unknown or "
                                  + "banned user id gets the empty reply. Also at `/v1/game/user/<id>/lodges` (the current build).")
                 .PathParams(("id", "user id")).Produces(200, contentType: "application/vnd.game.user.trophy_lodge.lodge+json; type=collection");
            apiv2.MapGet("/game/user/{id}/lodges/{lodge}", restored).WithName("Lodge").WithSummary("One lodge of any account here")
                 .WithDescription("As Lodges; 404 for a lodge the account has not. Also at `/v1/game/user/<id>/lodges/<lodge>` (the current build).")
                 .PathParams(("id", "user id"), ("lodge", "lodge id")).Produces(200, contentType: "application/vnd.game.user.trophy_lodge.lodge+json");
            api.MapGet("/v1/game/user/{id}/lodges", restored).ExcludeFromDescription();  // the current build's paths of the same pages
            api.MapGet("/v1/game/user/{id}/lodges/{lodge}", restored).ExcludeFromDescription();
            // the account's own edits (community server only): the game sends JSON as text/plain and does not read the answer
            var edit = Handle(game, r =>
            {
                var (lodge, trophy) = (LodgeEdit.Match(r.Path), TrophyEdit.Match(r.Path));
                if (!s.Online || r.Method != "PUT" || !(lodge.Success || trophy.Success))
                    return null;
                if (s.Authenticate(r.Tokens()) is not { } a)
                    return Denied(game, r);
                var status = !long.TryParse((lodge.Success ? lodge : trophy).Groups[1].Value, out var id) ? 404
                    : trophy.Success ? s.HideTrophy(a, id, r.Body)
                    : lodge.Groups[2].Success ? s.SaveLodgeSettings(a, id, r.Body) : s.SaveLodge(a, id, r.Body);
                return status == 200 ? new(200, [("Content-Type", "text/plain"), .. Cors(r.Header)], [], "state") : Rejected(game, r, status);
            });
            foreach (var (router, prefix) in new[] { (apiv2, ""), (api, "/v1") })
            {
                var current = prefix.Length > 0;  // the current build's paths are not documented twice
                var save = router.MapPut(prefix + "/game/user/lodges/{id}", edit);
                var settings = router.MapPut(prefix + "/game/user/lodges/{id}/settings", edit);
                var hide = router.MapPut(prefix + "/game/user/trophies/{id}", edit);
                if (current)
                {
                    save.ExcludeFromDescription();
                    settings.ExcludeFromDescription();
                    hide.ExcludeFromDescription();
                    continue;
                }
                save.WithName("LodgeSave").WithSummary("Save the own lodge's slots")
                    .WithDescription("Sets the lodge's `name`, `animal_slots` and `weapon_slots` on its stored rows (a key the body lacks stays); the answer is 200 with no body. "
                                     + "400 for a body that is not as shown, 404 for a lodge the account has not. Also at `/v1/game/user/lodges/<id>` (the current build).")
                    .PathParams(("id", "lodge id")).Body<LodgeSlotsBody>("text/plain").Produces(200, contentType: "text/plain");
                settings.WithName("LodgeSettings").WithSummary("Save the own lodge's name, lock and style")
                    .WithDescription("Statuses as LodgeSave. Also at `/v1/game/user/lodges/<id>/settings` (the current build).")
                    .PathParams(("id", "lodge id")).Body<LodgeSettingsBody>("text/plain").Produces(200, contentType: "text/plain");
                hide.WithName("TrophyHide").WithSummary("Show or hide one of the own trophies")
                    .WithDescription("`{hidden}`: a hidden trophy is left out of the public views (Trophy/list of another player, the profile page); the player's own lists keep it. "
                                     + "Restored rows and harvests made here alike; 404 for a trophy the account has not. Also at `/v1/game/user/trophies/<id>` (the current build).")
                    .PathParams(("id", "trophy id")).Body<TrophyHiddenBody>("text/plain").Produces(200, contentType: "text/plain");
            }
            apiv2.MapGet("/game/user/{id}/profile", userProfile).WithName("UserProfile").WithSummary("A player's profile page header, by user id")
                 .WithDescription("`{background, ribbon, hunterscore, earned_achievements}` of any account here, without a token (`background` and `ribbon` "
                                  + "as its backup had them, else null); 404 for a user id that is unknown or banned. Also at `/v1/game/user/<id>/profile` (the current build).")
                 .PathParams(("id", "user id")).Produces(200, contentType: "application/vnd.game_data.user.profile+json").Produces(404, contentType: "application/vnd.goa.error");
            apiv2.MapGet("/game/user/{id}/ranks", userRanks).WithName("UserRanks").WithSummary("A player's ranks, by user id")
                 .WithDescription("`[{id, category_id, value}]`, one per rank row of the facts file (`category_id` 1 animals, 2 weapons, 3 collectables), `value` the ethical kills "
                                  + "or pickups from the account's statistics (restored plus closed server hunts) as the hunterscore counts them, before its cap; "
                                  + "no token; 404 for a user id that is unknown, banned or the recorded player's. Also at `/v1/game/user/<id>/ranks` (the current build).")
                 .PathParams(("id", "user id")).Produces(200, contentType: "application/vnd.game_data.user.rank+json; type=collection").Produces(404, contentType: "application/vnd.goa.error");

            var web = Group(app, game.Web, "Website (web_host)");
            web.MapGet("/user/{handle}/score/", Handle(game, r =>
                {
                    if (r.Method != "GET" || !ScorePath.IsMatch(r.Path))
                        return null;
                    var killId = Py.ParseQs(r.Query).TryGetValue("id", out var ids) ? ids[0] : "";
                    var handle = Uri.UnescapeDataString(r.Path.Split('/')[2]);  // the page of this player's kill: kill ids are per account
                    var page = Py.IsDigit(killId) && long.TryParse(killId, out var kid) && s.PublicAccount(handle) is { } account
                        ? s.ScorePageReply(account, kid) : null;
                    return Has(page) ? new(200, [("Content-Type", "text/html")], page!, "state") : null;
                }))
               .WithName("ScorePage").WithSummary("Kill details page of a server kill")
               .WithDescription("Shots, weight, wound time, integrity, location and the worked score sheet, rendered as an HTML page.")
               .PathParams(("handle", "player handle (any)")).QueryParams(("id", "kill id"))
               .Produces(200, contentType: "text/html");
        }

        app.MapFallback("/{**path}", Handle(game, _ => null)).ExcludeFromDescription();  // replay, archives, mirror, misses
    }

    const string Form = "application/x-www-form-urlencoded";

    static RouteGroupBuilder Group(WebApplication app, string host, string tag) =>
        app.MapGroup("/").RequireHost(host).WithGroupName("game").WithTags(tag);

    /// <summary>Answer with the handler, or fall back to the replay when it returns null.</summary>
    static Func<HttpContext, Task<Reply>> Handle(Game game, Func<Req, Reply?> handle) => async ctx =>
    {
        var r = await Req.ReadAsync(ctx);
        return handle(r) ?? game.Fallback(r);
    };

    /// <summary>A POST endpoint that answers only exactly this raw path: routing alone also matches other cases, a
    /// trailing slash, percent-encodings and dot segments.</summary>
    static RouteHandlerBuilder MapExactPost(this RouteGroupBuilder group, Game game, string path, Func<Req, Reply?> handle) =>
        group.MapPost(path, Handle(game, r => r.Method == "POST" && r.Path == path ? handle(r) : null));

    /// <summary>Run a state endpoint for the account of the request's token; a token that is unknown, expired or banned
    /// gets the expired-token reply (Denied), unless the state is offline, which answers it as the local account.
    /// tokens: the ones to try instead of the request's own (User/jwt carries its token in a JSON body).</summary>
    static Reply? Own(Game game, Req r, Func<long, Reply?> handle, IEnumerable<string>? tokens = null) =>
        game.State!.Authenticate(tokens ?? r.Tokens()) is { } account ? handle(account) : Denied(game, r);

    /// <summary>The request's tokens and, when the body is JSON, its `oauth_access_token`.</summary>
    static IEnumerable<string> WithJsonToken(Req r)
    {
        foreach (var t in r.Tokens())
            yield return t;
        if ((r.Header("Content-Type") ?? "").Contains("json") && r.Body.Length is > 0 and < 4096)
        {
            JsonNode? body = null;
            try { body = JsonNode.Parse(r.Body); }
            catch (JsonException) { }
            if ((body as JsonObject)?["oauth_access_token"] is JsonValue v && v.TryGetValue<string>(out var token))
                yield return token;
        }
    }

    static string? FormValue(Req r, string name)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(r.Body));
        return form.TryGetValue(name, out var v) ? v[0] : Py.ParseQs(r.Query).TryGetValue(name, out var q) ? q[0] : null;
    }

    /// <summary>The old launcher's own calls to the game server, answered without any recording: nothing in them is
    /// read but their success. The Steam ticket of `token` is not checked (checking it needs a Steam Web API key, which the server does not have).</summary>
    static Reply? LauncherCall(Req r)
    {
        if (r.Path.Length <= ProtocolLiterals.LauncherBase.Length + 1) return null;
        var name = r.Path[(ProtocolLiterals.LauncherBase.Length + 1)..].Split('/')[0];
        var body = name switch
        {
            "token" => """{"err":0,"access_token":"none","refresh_token":"none","expires":0,"user_id":0,"account_id":0,"user_info":{"status":"ok","country":"","state":"","currency":"USD"}}""",
            "initialise" => """{"err":0,"user_handle":"","executable":"","size":0,"package":{"version":1,"current_version":1,"size":0,"size_as_string":"0 B","files":[]},"ftp":{"enabled":0,"ip":"","user_name":"","password":"","port":0,"flags":0}}""",
            "compatibleHardware" => """{"compatible":1,"err":0}""",
            "ping" => """{"err":0}""",
            _ => null,
        };
        return body is null ? null : new(200, Json(), Encoding.ASCII.GetBytes(body), "state");
    }

    /// <summary>splash/feed_data `{posts, splashes}`: the recording's (posts as they were), the server's notice first in the splashes.</summary>
    static Reply FeedData(Game game, Req r)
    {
        JsonObject reply = new() { ["posts"] = new JsonArray(), ["splashes"] = new JsonArray() };
        try { if (game.Recorded(r) is { Tier: >= 1 } hit && JsonNode.Parse(hit.Body) is JsonObject recorded) reply = recorded; }
        catch (JsonException) { }
        reply["splashes"] = JsonNode.Parse(SplashWithNotice(Py.DumpBytes(reply["splashes"] as JsonArray ?? [])));
        return new(200, JsonAnyOrigin(), Py.DumpBytes(reply), "patch");
    }

    /// <summary>The reply for a request without a valid token, in the shape of its host: the web API's error code 5
    /// (token expired, HTTP 200: the web client renews its token and retries), a 401 on the game API v2 and on the game
    /// server. ponytail: the game server's and the game client's own reply to an expired session is not known; err 1 is a guess.</summary>
    static Reply Denied(Game game, Req r) =>
        r.Host == game.Api ? new(200, JsonAnyOrigin(), "{\"errorCode\": 5, \"message\": \"token expired\"}"u8.ToArray(), "denied")
        : r.Host == game.Apiv2 ? new(401, [("Content-Type", "application/vnd.goa.error"), .. Cors(r.Header)],
            "{\"id\":\"server\",\"code\":\"unauthorized\",\"status\":401,\"detail\":\"token expired\"}"u8.ToArray(), "denied")
        : new(401, Json(), "{\"err\": 1}"u8.ToArray(), "denied");

    /// <summary>An edit the state refused (400: the body is not as expected, 404: no such record), in the shape of its host: the web API's
    /// error code (HTTP 200) or a goa error on the game API v2.</summary>
    static Reply Rejected(Game game, Req r, int status) =>
        r.Host == game.Api ? new(200, JsonAnyOrigin(), Encoding.UTF8.GetBytes($"{{\"errorCode\": {status}}}"), "state")
        : new(status, [("Content-Type", "application/vnd.goa.error"), .. Cors(r.Header)],
            Encoding.UTF8.GetBytes(status == 404 ? ReplayScope.Missing : """{"id":"server","code":"bad_request","status":400,"detail":"bad request"}"""), "state");

    // ---- OpenAPI documentation (metadata only: requests are parsed by the handlers, never bound or content-type matched)

    /// <summary>Request body schema of an endpoint, for the OpenAPI document only.</summary>
    public sealed record BodyDoc(Type Type, string ContentType);

    /// <summary>A query or path parameter of an endpoint, for the OpenAPI document only.</summary>
    public sealed record ParamDoc(string Name, bool InPath, string Description);

    static RouteHandlerBuilder Body<T>(this RouteHandlerBuilder b, string contentType) => b.WithMetadata(new BodyDoc(typeof(T), contentType));

    internal static RouteHandlerBuilder QueryParams(this RouteHandlerBuilder b, params (string Name, string Description)[] parameters) =>
        b.WithMetadata([.. parameters.Select(p => new ParamDoc(p.Name, false, p.Description))]);

    static RouteHandlerBuilder PathParams(this RouteHandlerBuilder b, params (string Name, string Description)[] parameters) =>
        b.WithMetadata([.. parameters.Select(p => new ParamDoc(p.Name, true, p.Description))]);

    public sealed record HeartbeatRequest(
        [property: Description("Hunt events since the last heartbeat")] JsonElement[] events,
        [property: Description("Player position")] double xpos, double ypos, double zpos,
        [property: Description("0 stand, 1 crouch, 2 prone")] int pose,
        [property: Description("Game time of day")] double time_of_day, JsonElement weather, JsonElement ping_to_host, JsonElement player_positions);

    public sealed record HeartbeatReply(
        [property: Description("Queued updates: 3 toast, 6 trophy score, 7 collectable score, 10 items, 11 photo upload URL, 15 balance")] JsonElement[] updates,
        int err);

    public sealed record DisconnectRequest(int animals_killed, int animals_spotted, int animals_tracked,
        [property: Description("Metres walked this hunt")] int distance_travelled, int shots_fired, int shots_hit);

    public sealed record ConnectRequest(string client_version,
        [property: Description("Hardware: cpu, gpu, ram, resolution, driver…")] JsonElement g_data,
        [property: Description("1 when the client reports a suspicious DLL")] int suspicious_dll);

    public sealed record ErrReply(int err);

    public sealed record MoveMultiple2Form(
        [property: Description("JSON list `[{item_guid, slot_id}]`; item_guid is a guid or a clonedItems placeholder")] string mapping,
        [property: Description("JSON list `[{item_guid: \"new_item_<ts>\", item_id}]`: stacks to create")] string clonedItems,
        string? oauth_access_token);

    public sealed record BuyForm(long item_id,
        [property: Description("Count (usually 1)")] long amount,
        [property: Description("`GOLD` (em, catalogue price); `SILVER` not modelled")] string wallet,
        string? oauth_access_token);

    public sealed record BuyReply([property: Description("Price charged, absent on failure")] double? price, bool success);

    public sealed record LaunchStartForm(
        [property: Description("Start location id (a lodge)")] long start,
        [property: Description("`LODGE`")] string loctype, long reserve,
        [property: Description("Start time of day, minutes")] long time, long loadout_id, string? oauth_access_token);

    public sealed record UserForm(long user_id, string? oauth_access_token);
    public sealed record PersonalForm(long user_id, long season_no, string? oauth_access_token);
    public sealed record FriendForm(long friend_user_id, string? oauth_access_token);
    public sealed record FriendsListForm(long user_id, long? offset, long? limit);

    public sealed record AnimalSlot(long slot_id, long? trophy_id, long? pose_id, double rotation, [property: Description("The texture's name, \"\" for none")] string? base_texture, bool is_skull);
    public sealed record WeaponSlot(long slot_id, long? weapon_id, double rotation);
    public sealed record LodgeSlotsBody(string name, AnimalSlot[] animal_slots, WeaponSlot[] weapon_slots);
    public sealed record LodgeSettingsBody(string name, bool locked, long? style_id);
    public sealed record TrophyHiddenBody(bool hidden);

    public sealed record HostnameForm([property: Description("The player's handle (any case)")] string hostname);

    public sealed record ExpeditionListForm(long user_id, long limit, long offset);

    public sealed record ExpeditionForm(long user_id, string? oauth_access_token, long? expedition_id);

    public sealed record StartSinglePlayerForm(
        [property: Description("Start time of day, minutes")] long time, long loadout_id,
        [property: Description("Start location id (a lodge)")] long lodge_id, long reserve, string? oauth_access_token);

    public sealed record LangForm(string lang);

    public sealed record TokenForm(string? oauth_access_token);

    public sealed record SkillNumbers(
        [property: Description("Weapon id → level plus fraction")] Dictionary<string, double> weapon,
        [property: Description("By species id")] double[] tracking,
        [property: Description("By species id")] double[] spotting);
}
