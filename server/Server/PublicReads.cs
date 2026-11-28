// What a community server's account shows of itself where no restore filled the rows: Me/me from the account, Inventory/list
// from the items table. A local server never calls these (it answers from its recordings).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>Does the request ask for this account's own data: no `user_id`, or its own? A local server has one account, so
    /// always. The owner-only areas check this; a public area reads the account of <see cref="PublicAccount(long)"/>.</summary>
    public bool IsOwn(long account, byte[] formBody)
    {
        var userId = Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "user_id");
        return offline || userId == "" || IsUser(account, userId);
    }

    public bool IsUser(long account, string userId) => userId == Py.Str(Account(account)["user_id"]);

    /// <summary>The account a public read shows (a profile page, in the game and on the website), by user id or by handle (any
    /// case): an account of this server that is not banned and is not the recorded player's (LocalAccount: no player here owns
    /// it, so a backup nobody restored onto their account is never shown); else null. A local server has one account: always
    /// it. Read the account's rows with `Con(account)` (Store.Open, the per-account `$a` rule).</summary>
    public long? PublicAccount(long userId) => offline ? LocalAccount : Store.Listed(userId, LocalAccount);

    /// <inheritdoc cref="PublicAccount(long)"/>
    public long? PublicAccount(string handle) => offline ? LocalAccount : Store.Listed(handle, LocalAccount);

    /// <summary>The account of another player's `user_id` in a read of <paramref name="caller"/>: <see cref="PublicAccount(long)"/> when the caller is within his read limit
    /// (<see cref="ReadAllowed"/>: every read of another user id counts, known, seen or not, so walking ids is slow and raises the alert; his own reads are not counted) and may see it
    /// (<see cref="CanSee"/>: himself, his friends, whoever a board shows him); else null, so walking user ids finds nothing.</summary>
    public long? SeenAccount(long caller, long userId)
    {
        var other = PublicAccount(userId);
        if (other == caller) return other;
        return ReadAllowed(caller) && other is { } o && CanSee(caller, o) ? o : null;
    }

    /// <summary>Public_user/getByHostname of an account: who it is (`membership` as its connect `account` has it, the type and
    /// title as its Me/me does), its hunterscore and rank, `online` while a hunt of it is open and alive (shown to the account itself and its friends only:
    /// <paramref name="viewer"/>; false for anyone else), and the distance and duration of its statistics (restored base plus the server hunts, the open one so far).</summary>
    public JsonObject PublicUser(long account, long? viewer = null)
    {
        using var con = Con(account);
        var me = MeOf(con, account);
        var stats = RestoredReads.StatisticsTotal(con);
        return new JsonObject
        {
            ["id"] = account, ["handle"] = C(me["handle"]), ["hostname"] = C(me["hostname"]), ["avatar"] = C(me["avatar"]),
            ["hunterscore"] = C(me["hunterscore"]), ["rank"] = Store.Rank(account, LocalAccount),
            ["membership"] = C(G(Account(account), "membership", 0)), ["membershipType"] = C(me["membershipType"]), ["membershipTitle"] = C(me["membershipTitle"]),
            ["online"] = viewer is { } v && (v == account || Store.FriendsOf(v, LocalAccount).Friends.Contains(account)) && HuntAlive(con),
            ["stats"] = new JsonObject { ["distance"] = C(stats["distance"]), ["duration"] = C(stats["duration"]) },
        };
    }

    /// <summary>Is a hunt of the connection's account open and had a heartbeat in the last HEARTBEAT_TIMEOUT.</summary>
    bool HuntAlive(Db con) =>
        con.Scalar("SELECT 1 FROM expeditions WHERE account_id = $a AND end_ts IS NULL AND COALESCE(last_seen, start_ts) >= ?", Clock() - HEARTBEAT_TIMEOUT) is not null;

    /// <summary>Seconds <see cref="OnlineCounts"/> is kept (and the `max-age` of `GET /stats/online`).</summary>
    public const int OnlineCacheSeconds = 30;

    readonly object onlineGate = new();
    (double At, long Players, long Games)? onlineCounts;

    /// <summary>`GET /stats/online`: how many accounts are in a hunt with a heartbeat in the last HEARTBEAT_TIMEOUT (the `online` of <see cref="PublicUser(long)"/>; never a banned
    /// account or the recorded player's) and how many multiplayer sessions are open (the list of `Network/list`). Counted at most once in <see cref="OnlineCacheSeconds"/>.
    /// ponytail: expeditions is a per-player table, so each account is read on its own connection (Db refuses a cross-account read), as in
    /// <see cref="Store.GameBoard"/>; one query per account every 30 s at most. Keep a last-heartbeat map in memory if the accounts grow into the tens of thousands.</summary>
    public (long Players, long Games) OnlineCounts()
    {
        lock (onlineGate)
        {
            var now = Clock();
            if (onlineCounts is { } kept && now >= kept.At && now - kept.At < OnlineCacheSeconds) return (kept.Players, kept.Games);
            List<long> accounts;
            using (var con = Store.Open())
                accounts = [.. con.Rows("SELECT id FROM accounts WHERE banned = 0 AND id IS NOT ?", LocalAccount).Select(r => (long)r[0])];
            var players = accounts.LongCount(id =>
            {
                using var con = Store.Open(id);
                return HuntAlive(con);
            });
            var fresh = (At: now, Players: players, Games: (long)games.Open(now).Count);
            onlineCounts = fresh;
            return (fresh.Players, fresh.Games);
        }
    }

    /// <summary>Friend entries of accounts, as Me/friends and User/friendslist send them, sorted by `handle` (upper case, ordinal); `online` only where <paramref name="online"/> says
    /// the reader may see it (his friends), else false.</summary>
    List<JsonNode?> FriendEntries(IEnumerable<long> accounts, Func<long, bool> online) => [.. accounts.Select(account =>
    {
        using var con = Con(account);
        var me = MeOf(con, account);
        return (JsonNode?)new JsonObject
        {
            ["id"] = account, ["handle"] = C(me["handle"]), ["avatar"] = C(me["avatar"]), ["fullbody_avatar"] = C(me["fullbody_avatar"]) ?? 0,
            ["hostname"] = C(me["hostname"]), ["hunterscore"] = C(me["hunterscore"]), ["online"] = online(account) && HuntAlive(con),
        };
    }).OrderBy(e => Py.Str(e!["handle"]).ToUpperInvariant(), StringComparer.Ordinal)];

    static bool Friend(long _) => true;
    static bool Stranger(long _) => false;

    /// <summary>Me/friends: the account's friends, the players who asked it (`requests`, the same entries, never online) and the user ids it asked (`pending`).</summary>
    public byte[] MeFriendsReply(long account)
    {
        var (friends, requests, pending) = Store.FriendsOf(account, LocalAccount);
        return Py.DumpBytes(new JsonObject
        {
            ["friends"] = new JsonArray([.. FriendEntries(friends, Friend)]), ["friendCount"] = friends.Count,
            ["requests"] = new JsonArray([.. FriendEntries(requests, Stranger)]), ["requestCount"] = requests.Count,
            ["pending"] = new JsonArray([.. pending.Select(id => (JsonNode?)id)]),
        });
    }

    /// <summary>How many players asked the account to be friends: Me/poll's and Me/me's `friend_requests`.</summary>
    public long FriendRequests(long account) => Store.FriendsOf(account, LocalAccount).Requests.Count;

    /// <summary>Friend/add (ask, or accept the other's request) or Friend/remove (decline, cancel, remove) of `friend_user_id`: `true`; `{"errorCode": 400}` for the account's own id
    /// and for any id other than a player of its friends, its requests either way, its multiplayer session or one it may see (<see cref="SeenAccount"/>): asking any id would name
    /// the player in the website's list of the requests sent, so walking ids would list the handles.</summary>
    public byte[] FriendReply(long account, byte[] formBody, bool add)
    {
        if (!long.TryParse(Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "friend_user_id"), out var id) || id == account)
            return """{"errorCode": 400}"""u8.ToArray();
        var (friends, requests, pending) = Store.FriendsOf(account, LocalAccount);
        var other = PublicAccount(id) is { } o && (friends.Contains(o) || requests.Contains(o) || pending.Contains(o) || InSessionWith(account, o)) ? o : SeenAccount(account, id);
        if (other is not { } known)
            return """{"errorCode": 400}"""u8.ToArray();
        if (add) Store.AddFriend(account, known);
        else Store.RemoveFriend(account, known);
        return "true"u8.ToArray();
    }

    /// <summary>Are the two accounts in the same open multiplayer session (a player met in a hunt can be asked to be friends).</summary>
    bool InSessionWith(long a, long b) => games.Open(Clock()).Any(g => g.Members.Any(m => m.Account == a) && g.Members.Any(m => m.Account == b));

    /// <summary>User/friendslist of `user_id` for the caller's own account only (a player sees only his own friends): `{total, friends}`, one page by `offset` and `limit` (1 to 50;
    /// missing, empty or below 1: every friend). Another player's `user_id`: the empty list. No `user_id`: `{"errorCode": 400}`; an empty or non-positive one: `null`;
    /// a non-numeric value: the handler error.</summary>
    public byte[] FriendsListReply(long caller, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody), keepBlank: true);
        string? Value(string key) => form.TryGetValue(key, out var v) ? v[0] : null;
        var (user, offset, limit) = (Value("user_id"), Value("offset"), Value("limit"));
        if (new[] { user, offset, limit }.Any(v => v is { Length: > 0 } && !long.TryParse(v, out _)))
            return """{"error": 1, "code": "handlerError"}"""u8.ToArray();
        if (user is null)
            return """{"errorCode": 400}"""u8.ToArray();
        if (!long.TryParse(user, out var id) || id <= 0)
            return "null"u8.ToArray();
        if (!IsUser(caller, user))
            return """{"total": 0, "friends": []}"""u8.ToArray();
        var all = FriendEntries(Store.FriendsOf(caller, LocalAccount).Friends, Friend);
        var page = all.Skip(long.TryParse(offset, out var o) && o > 0 ? (int)Math.Min(o, int.MaxValue) : 0);
        return Py.DumpBytes(new JsonObject
        {
            ["total"] = all.Count,
            ["friends"] = new JsonArray([.. long.TryParse(limit, out var l) && l > 0 ? page.Take((int)Math.Min(l, 50)) : page]),
        });
    }

    public const int FriendsPageSize = 20;
    public const int FriendSearchLimit = 10, FriendSearchMin = 3;

    /// <summary>The signed-in player's own friends tab, `GET /hunters/&lt;handle&gt;/friends` of his own handle: one page of his friends by handle,
    /// `{page, pages, total, rows: [{handle, hunterscore, avatar}]}` (`avatar` as <see cref="AvatarUrl"/>, on `site`).</summary>
    public JsonObject PublicFriends(long account, int page, string site)
    {
        var all = FriendEntries(Store.FriendsOf(account, LocalAccount).Friends, Friend);
        var pages = Math.Max(1, (all.Count + FriendsPageSize - 1) / FriendsPageSize);
        page = Math.Clamp(page, 1, pages);
        return new JsonObject
        {
            ["page"] = page, ["pages"] = pages, ["total"] = all.Count,
            ["rows"] = new JsonArray([.. all.Skip((page - 1) * FriendsPageSize).Take(FriendsPageSize).Select(e => (JsonNode?)new JsonObject { ["handle"] = C(e!["handle"]), ["hunterscore"] = C(e["hunterscore"]), ["avatar"] = AvatarUrl(Py.L(e["id"]), site) })]),
        };
    }

    JsonArray Hunters(IEnumerable<JsonNode?> entries, string site) => new([.. entries.Select(e => (JsonNode?)new JsonObject { ["handle"] = C(e!["handle"]), ["online"] = C(e["online"]), ["avatar"] = AvatarUrl(Py.L(e["id"]), site) })]);

    /// <summary>The signed-in website player's friends, `GET /account/friends`: `{friends: [{handle, online, avatar}], requests: [same], pending: [same]}`,
    /// each by handle (`requests` asked the account, `pending` were asked by it; only friends show `online`); `avatar` as <see cref="AvatarUrl"/>, on `site`.</summary>
    public JsonObject WebFriends(long account, string site)
    {
        var (friends, requests, pending) = Store.FriendsOf(account, LocalAccount);
        return new JsonObject { ["friends"] = Hunters(FriendEntries(friends, Friend), site), ["requests"] = Hunters(FriendEntries(requests, Stranger), site), ["pending"] = Hunters(FriendEntries(pending, Stranger), site) };
    }

    /// <summary>`GET /account/friends/search?text=`: at most <see cref="FriendSearchLimit"/> players whose handle contains the text (at least <see cref="FriendSearchMin"/> characters,
    /// else none), the exact handle first, never the account itself, `{hunters: [{handle, online, avatar}]}`; `online` only for the account's friends.</summary>
    public JsonObject WebFriendSearch(long account, string text, string site)
    {
        text = text.Trim();
        if (text.Length < FriendSearchMin) return new() { ["hunters"] = new JsonArray() };
        var friends = Store.FriendsOf(account, LocalAccount).Friends;
        var found = FriendEntries(Store.SearchHandles(text, account, LocalAccount, FriendSearchLimit), friends.Contains)
            .OrderBy(e => !string.Equals(Py.Str(e!["handle"]), text, StringComparison.OrdinalIgnoreCase));  // stable: the exact handle first, the rest by handle
        return new() { ["hunters"] = Hunters(found, site) };
    }

    /// <summary>`POST /account/friends/add` (ask, or accept the other's request) or `/remove` (decline, cancel, remove) of a player by handle (any case);
    /// false, and nothing changed, for the account's own handle or one that is not a player here.</summary>
    public bool WebFriend(long account, string handle, bool add)
    {
        if (PublicAccount(handle) is not { } other || other == account) return false;
        if (add) Store.AddFriend(account, other);
        else Store.RemoveFriend(account, other);
        return true;
    }

    /// <summary>User/ban_player of `banned_user_id`: the account bans that player from its games (<see cref="Games.Why"/> `banned`), once; `true`. `{"errorCode": 400}`, and nothing stored, for the account's own id
    /// and any id that is not a player of its multiplayer session or one it may see (<see cref="SeenAccount"/>, as for <see cref="FriendReply"/>: banning any id would name the player in the website's list of bans).</summary>
    public byte[] BanPlayerReply(long account, byte[] formBody)
    {
        if (!long.TryParse(Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "banned_user_id"), out var id) || id == account)
            return """{"errorCode": 400}"""u8.ToArray();
        var other = PublicAccount(id) is { } o && InSessionWith(account, o) ? o : SeenAccount(account, id);
        if (other is not { } known) return """{"errorCode": 400}"""u8.ToArray();
        Store.BanFromGames(account, known);
        games.Kick(account, known);  // the game's own kick follows, but the server must not keep counting him
        return "true"u8.ToArray();
    }

    /// <summary>`GET /account/bans`: `{bans: [{handle, avatar, ts}]}`, the players the account banned from its games, the latest first (`avatar` as <see cref="AvatarUrl"/>, on `site`; `ts` Unix seconds).</summary>
    public JsonObject WebBans(long account, string site) => new()
    {
        ["bans"] = new JsonArray([.. Store.GameBansOf(account).Select(b => (JsonNode?)new JsonObject { ["handle"] = Store.HandleOf(b.Account), ["avatar"] = AvatarUrl(b.Account, site), ["ts"] = b.Ts })]),
    };

    /// <summary>`POST /account/bans/unban` of a player by handle: lifts the account's own ban of it; false when it banned no such player.</summary>
    public bool WebUnban(long account, string handle) => Store.AccountByHandle(handle) is { } other && Store.UnbanFromGames(account, other);

    /// <summary>The restored `/game/user/&lt;uid&gt;/profile` reply, by whatever user id the zip or the archive named: the account may keep an id of its own (a restore into an existing account, a claim).</summary>
    JsonNode? RestoredProfile(Db con) =>
        con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path LIKE 'game!_user!_%!_profile.json' ESCAPE '!'") is string s ? JsonNode.Parse(s) : null;

    /// <summary>The `background` of an account's restored `/game/user/&lt;id&gt;/profile` reply, the header picture the player chose: a string, null when it has none.</summary>
    public JsonNode? ProfileBackground(long account)
    {
        using var con = Con(account);
        return RestoredProfile(con)?["background"] is { } b && Py.IsStr(b) ? C(b) : null;
    }

    /// <summary>The collector ribbon of a share of the achievement points earned (in percent), the original's rule: 90 and more turkey, 75 fox, 50 bear, 25 elk, 5 moose, below 5 none (a file name such as `moose.png`).</summary>
    public static string? RibbonOf(double percent) =>
        percent >= 90 ? "turkey.png" : percent >= 75 ? "fox.png" : percent >= 50 ? "bear.png" : percent >= 25 ? "elk.png" : percent >= 5 ? "moose.png" : null;

    /// <summary>How many of all the thresholds of the connect's achievements the account has earned (those with an earn time), and how many there are.</summary>
    (int Done, int Total) EarnedThresholds(long account)
    {
        using var con = Con(account);
        var times = con.Rows("SELECT id, triggers FROM achievements WHERE account_id = $a").ToDictionary(r => Convert.ToInt64(r[0]), r => Parse(r[1]));
        int done = 0, total = 0;
        foreach (var def in Arr(G(connect, "achievements", null)).OfType<JsonObject>())
        {
            if (!TryKey(def["id"], out var id))
                continue;
            foreach (var t in Arr(def["triggers"]).Where(Py.IsNum))
            {
                total++;
                if (times.GetValueOrDefault(id)?[Py.Str(t)] is { } time && Py.IsNum(time) && Py.D(time) > 0)
                    done++;
            }
        }
        return (done, total);
    }

    /// <summary>The collector ribbon the profile shows under the picture: <see cref="RibbonOf"/> the account's earned thresholds of all the thresholds of the connect's achievements
    /// (a string, null below 5 %); with no achievements in the connect the `ribbon` of its restored `/game/user/&lt;id&gt;/profile` reply, null when it has none.</summary>
    public JsonNode? ProfileRibbon(long account)
    {
        var (done, total) = EarnedThresholds(account);
        if (total > 0)
            return RibbonOf(100.0 * done / total);
        using var con = Con(account);
        return RestoredProfile(con)?["ribbon"] is { } r && Py.IsStr(r) ? C(r) : null;
    }

    /// <summary>/game/user/&lt;id&gt;/profile of an account: `background` of its restored reply (null without one), `ribbon` of <see cref="ProfileRibbon"/>, its
    /// hunterscore and how many achievement thresholds it has earned (those with an earn time).</summary>
    public byte[] UserProfileReply(long account)
    {
        var ribbon = ProfileRibbon(account);
        using var con = Con(account);
        var restored = RestoredProfile(con);
        return Py.DumpBytes(new JsonObject
        {
            ["background"] = C(restored?["background"]), ["ribbon"] = ribbon, ["hunterscore"] = C(MeOf(con, account)["hunterscore"]),
            ["earned_achievements"] = (long)con.Scalar("SELECT COUNT(*) FROM achievements, json_each(triggers) WHERE account_id = $a AND json_each.value > 0")!,
        });
    }

    /// <summary>The statistics tab of an account, `GET /hunters/&lt;handle&gt;/statistics`: `weapons` (one row per weapon and ammo: their item ids `weaponId` and `ammoId`, null when the key is not a number, shots,
    /// accuracy % of shots, kills, kills per hit %, longest hit in metres or null), `animals` (spotted, tracked, harvested per species),
    /// `collectables` (always empty: the server has no names for them), `distanceKm` and `huntSeconds`; names from the catalogue (a deer's typical and non-typical rows apart, typical first),
    /// "#id" where it has none. Restored base plus server hunts (the open one so far), like Statistics/total. `distanceKmHere` is the distance of the server hunts alone,
    /// `huntSecondsHere` the part played on this server (<see cref="HuntSecondsHere"/>).</summary>
    public JsonObject StatisticsPage(long account)
    {
        JsonNode stats;
        double hereM;
        long here;
        using (var con = Con(account))
        {
            stats = RestoredReads.StatisticsTotal(con);
            hereM = DistanceHere(con);
            here = HuntSecondsHere(con);
        }
        var page = StatisticsView(stats);
        page["distanceKmHere"] = hereM / 1000;
        page["huntSecondsHere"] = here;
        return page;
    }

    /// <summary>The metres the account walked on this server since <see cref="PlaytimeSince"/>, the closed hunts <see cref="HuntSecondsHere"/> counts: each one's `distance` (the game's own, or the measured one set at its close).
    /// The open hunt adds 0 until it closes, as in the total.</summary>
    double DistanceHere(Db con) =>
        Convert.ToDouble(con.Scalar("SELECT COALESCE(SUM(COALESCE(distance, 0)), 0) FROM expeditions WHERE account_id = $a AND end_ts IS NOT NULL AND start_ts >= ?", PlaytimeSince));

    /// <summary>The tab's rows of a Statistics/total shaped node (<c>data</c> an object): a hunt's stats are one too.</summary>
    JsonObject StatisticsView(JsonNode stats)
    {
        var total = stats.AsObject();
        static double N(JsonNode? o, string key) => o is JsonObject x && x[key] is { } n && Py.IsNum(n) ? Py.D(n) : 0;  // a restored backup is the player's: any member can be of another type
        static int Count(double d) => (int)Math.Clamp(d, 0, int.MaxValue);  // and a counter any size: the website holds ints
        static JsonNode? Key(string id) => long.TryParse(id, out var n) ? JsonValue.Create(n) : null;  // the catalogues are keyed by number
        string Weapon(string id) => Py.Str(Lookup(items, Key(id))?["name"] ?? id);
        var weapons = new List<(string Name, JsonObject Row)>();
        foreach (var (w, ammo) in total["data"]!["weapon"] as JsonObject ?? [])
            foreach (var (a, c) in ammo as JsonObject ?? [])
            {
                double hits = N(c, "hits"), shots = hits + N(c, "misses"), mm = N(c, "distance");
                weapons.Add((Weapon(w), new JsonObject
                {
                    ["weapon"] = Weapon(w), ["weaponId"] = Key(w), ["ammo"] = Weapon(a), ["ammoId"] = Key(a), ["shots"] = Count(shots), ["accuracy"] = shots > 0 ? Math.Round(hits / shots * 100, 1) : 0,
                    ["kills"] = Count(N(c, "kills")), ["killsPerHit"] = hits > 0 ? Math.Round(N(c, "kills") / hits * 100, 1) : 0, ["longest"] = mm > 0 ? mm / 1000 : null,
                }));
            }
        var animals = new List<(string Name, JsonObject Row)>();
        foreach (var (sp, c) in total["data"]!["animal"] as JsonObject ?? [])
        {
            var name = Key(sp) is { } id ? BoardName(id) : sp;
            animals.Add((name, new JsonObject { ["name"] = name, ["spotted"] = Count(N(c, "spots")), ["tracked"] = Count(N(c, "tracks")), ["harvested"] = Count(N(c, "kills")) }));
        }
        return new JsonObject
        {
            ["weapons"] = new JsonArray([.. weapons.OrderBy(w => w.Name, StringComparer.Ordinal).Select(w => (JsonNode?)w.Row)]),
            ["animals"] = new JsonArray([.. animals.OrderBy(a => a.Name.Split(" (")[0], StringComparer.Ordinal).ThenBy(a => a.Name.EndsWith("(Non-Typical)", StringComparison.Ordinal)).Select(a => (JsonNode?)a.Row)]),
            ["collectables"] = new JsonArray(), ["distanceKm"] = N(total, "distance") / 1000, ["huntSeconds"] = Count(N(total, "duration")),
        };
    }

    static double Num(JsonNode? n) => n is JsonValue v && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : 0;

    /// <summary>Leaderboard/personal for the form's user_id and season_no: on a community server the account of a player the caller may see (<see cref="SeenAccount"/>; null for anyone else), else the player's own.</summary>
    public byte[]? PersonalBestReply(long account, byte[] formBody)
    {
        var season = long.TryParse(Form1(Py.ParseQs(Encoding.UTF8.GetString(formBody)), "season_no"), out var n) ? n : 0;
        return PublicReply(account, formBody, con => PersonalBest(con, season));
    }

    /// <summary>The personal bests tab of an account, `GET /hunters/&lt;handle&gt;/best?season=`: `rows` one per species of the bests of the season (0: all time), `[{species, score, range, weapon, ts, kill}]`
    /// of the species' best score: its name, score, the range of that kill in metres, the weapon's name ("" when unknown), the time of the kill (Unix seconds) and its id.</summary>
    public JsonObject PublicBest(long account, long season = 0)
    {
        JsonObject all;
        using (var con = Con(account))
            all = PersonalBest(con, season);
        return new JsonObject
        {
            ["rows"] = new JsonArray([.. all["data"]!.AsArray().OfType<JsonObject>().Where(r => r["score"] is JsonObject && Py.IsNum(r["specie"])).Select(r =>
            {
                var s = r["score"]!;
                return (JsonNode?)new JsonObject
                {
                    ["species"] = BoardName(Py.L(r["specie"])), ["score"] = Num(s["score"]), ["range"] = Num(s["distance"]) / 1000,
                    ["weapon"] = Lookup(items, s["weapon_id"])?["name"] is { } w && Py.IsStr(w) ? C(w) : "", ["ts"] = (long)Num(s["confirm_ts"]), ["kill"] = (long)Num(s["animal_id"]),
                };
            })]),
        };
    }

    /// <summary>The reply nodes a restore stored in `trophies` or `lodges` for a source, as stored (none: empty).</summary>
    List<JsonNode?> Restored(long account, string table, string source, string? id = null)
    {
        using var con = Con(account);
        return Restored(con, table, source, id);
    }

    /// <summary>The same on a connection the caller has open for the account (a scan of every account reads each one once).</summary>
    static List<JsonNode?> Restored(Db con, string table, string source, string? id = null) =>
        [.. con.Rows($"SELECT node FROM {table} WHERE account_id = $a AND source = ?{(id is null ? "" : " AND id = ?")} ORDER BY n",
                     id is null ? [source] : [source, id]).Select(r => JsonNode.Parse((string)r[0]))];

    /// <summary>One restored record as the reply it was read from; null when the account has none with this id.</summary>
    public byte[]? RowReply(long account, string table, string source, string id) =>
        Restored(account, table, source, id).FirstOrDefault() is { } node ? Py.DumpBytes(node) : null;

    static readonly string[] TrophyFields = ["species", "species_define", "score", "score_type", "is_skull", "gender", "weight", "scale", "reserve_id", "timestamp",
                                             "texture_id", "base_texture", "fur_texture", "antlers_seed", "antlers_score_percentage", "holed"];

    /// <summary>A trophy row for each animal slot of the restored lodges that carries `trophy_details`, in the restored shape: a public restore has no `/game/user/trophies` (the game sends it
    /// to its owner only), but its lodges name the animals they hold.</summary>
    List<JsonObject> MountedRows(long account)
    {
        using var con = Con(account);
        return MountedRows(con);
    }

    static List<JsonObject> MountedRows(Db con) =>
        [.. Restored(con, "lodges", LodgeOne).Concat(Restored(con, "lodges", LodgeList)).OfType<JsonObject>()
            .SelectMany(l => (l["animal_slots"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(s => TrophyId(s["trophy_id"]) is not null && s["trophy_details"] is JsonObject)
            .Select(s =>
            {
                var row = new JsonObject { ["id"] = TrophyId(s["trophy_id"]), ["hidden"] = false };
                foreach (var field in TrophyFields)
                    row[field] = C(s["trophy_details"]![field]);
                return row;
            })];

    /// <summary>Give each animal of <see cref="MountedRows"/> that has no `/game/user/trophies` row one, in the caller's transaction: a lodge's slots are rewritten without their `trophy_details` when the game
    /// saves the lodge, so the animal is kept as a row before that.</summary>
    void StoreMounted(Db con, long account)
    {
        var listed = con.Rows("SELECT id FROM trophies WHERE account_id = $a AND source = '/game/user/trophies'").Select(r => (string)r[0]).ToHashSet();
        foreach (var row in MountedRows(account))
            if (listed.Add(Py.Str(row["id"])))
                PlayerRows.AddTrophy(con, "/game/user/trophies", Py.Str(row["id"]), row);
    }

    /// <summary>The account's trophies as `/game/user/trophies` sends them, newest first (<see cref="Newest"/>): the restored rows, then the animals mounted in the restored lodges that have no row (<see cref="MountedRows"/>), then one row per harvest made on this
    /// server that is taxidermized or whose grace window is open (<see cref="InEditor"/>), in the restored shape (`hidden` as the player set it), each with the lodge slot that holds it now (<see cref="Mount"/>).
    /// A new kill id is above every restored id (see Heartbeat). <paramref name="all"/>: every harvest, for the `trophy_details` of a lodge slot (a harvest that is mounted stays shown in its lodge).</summary>
    List<JsonNode?> TrophyRows(long account, bool all = false)
    {
        var rows = Restored(account, "trophies", "/game/user/trophies");
        var listed = rows.Select(r => Py.Str(r?["id"])).ToHashSet();
        rows.AddRange(MountedRows(account).Where(r => listed.Add(Py.Str(r["id"]))));
        using var con = Con(account);
        var grace = Store.TaxidermyGrace();
        foreach (var r in con.Rows("SELECT kill_id, body, hidden FROM harvests WHERE account_id = $a ORDER BY kill_id"))
        {
            var body = JsonNode.Parse((string)r[1])!;
            if (body is not JsonObject page || !all && !InEditor(page, grace)) continue;
            AsTypical(page);
            var row = new JsonObject { ["id"] = (long)r[0], ["hidden"] = (long)r[2] != 0 };
            foreach (var field in TrophyFields)
                row[field] = C(body[field]);
            rows.Add(row);
        }
        Mount(account, rows);
        return Newest(rows, "timestamp");
    }

    /// <summary>Rows newest first by the Unix seconds or ISO text in <paramref name="field"/>; rows without a date go last, equal dates keep their order.</summary>
    static List<JsonNode?> Newest(IEnumerable<JsonNode?> rows, string field)
    {
        static double At(JsonNode? v) =>
            Py.IsNum(v) ? (Day(v) is { } n ? n : double.MinValue) : Py.IsStr(v) && DateTimeOffset.TryParse(Py.Str(v), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeSeconds() : double.MinValue;
        return [.. rows.OrderByDescending(r => At(r?[field]))];
    }

    static bool NotHidden(JsonNode? row) => !Py.Truthy(row?["hidden"]);

    /// <summary>`/game/user/trophies`: the restored rows, the animals mounted in the restored lodges and the harvests made here.</summary>
    public byte[] TrophiesReply(long account) => Py.DumpBytes(new JsonArray(TrophyRows(account).ToArray()));

    /// <summary>Trophy/list for the form's user_id: one page (`offset`, `limit`) of the restored trophies (rows of their own kind) of the player's own
    /// account or, on a community server, of another account the player may see without its hidden ones (<see cref="SeenAccount"/>);
    /// null for anyone else.</summary>
    public byte[]? TrophyListReply(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        long Number(string key) => long.TryParse(Form1(form, key), out var n) && n > 0 ? n : 0;
        var own = IsOwn(account, formBody);
        if (!own)
        {
            if (!long.TryParse(Form1(form, "user_id"), out var id) || SeenAccount(account, id) is not { } other)
                return null;
            account = other;
        }
        var all = Restored(account, "trophies", "/v1/Trophy/list");
        if (!own)
            all = [.. all.Where(NotHidden)];
        var page = all.Skip((int)Math.Min(Number("offset"), int.MaxValue));
        var limit = Number("limit");
        return Py.DumpBytes(new JsonObject { ["total"] = all.Count, ["trophies"] = new JsonArray((limit > 0 ? page.Take((int)Math.Min(limit, int.MaxValue)) : page).ToArray()) });
    }

    public const int TrophiesPageSize = 24;

    /// <summary>What a profile page's trophies tab shows of an account: one page, newest first, of its competition trophies (the restored `Trophy/list` rows) without the hidden ones,
    /// `{page, pages, total, rows: [{name, date, competitionId, competition, image}]}`: the trophy's name, the day it was won (UTC, from `ts`; "" without one), the competition's id
    /// and name (null without them) and its picture's file name without the extension (null without a picture).</summary>
    public JsonObject PublicTrophies(long account, int page)
    {
        var all = Newest(Restored(account, "trophies", "/v1/Trophy/list").Where(NotHidden), "ts");
        var pages = Math.Max(1, (all.Count + TrophiesPageSize - 1) / TrophiesPageSize);
        page = Math.Clamp(page, 1, pages);
        static JsonNode? Text(JsonNode? n) => Py.IsStr(n) ? C(n) : null;
        return new JsonObject
        {
            ["page"] = page, ["pages"] = pages, ["total"] = all.Count,
            ["rows"] = new JsonArray([.. all.Skip((page - 1) * TrophiesPageSize).Take(TrophiesPageSize).Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = Text(t?["name"]) ?? "", ["date"] = TrophyDay(t?["ts"]),
                ["competitionId"] = Py.IsNum(t?["competition_id"]) && Num(t!["competition_id"]) is >= 0 and <= 9007199254740991 and var cid ? JsonValue.Create((long)cid) : null, ["competition"] = Text(t?["competition_name"]),
                ["image"] = Py.IsStr(t?["image"]) && Path.GetFileNameWithoutExtension(Py.Str(t!["image"])) is { Length: > 0 } file ? file : null,
            })]),
        };
    }

    /// <summary>The day (UTC, yyyy-MM-dd) of a trophy row's `ts` (Unix seconds); "" for anything else.</summary>
    static string TrophyDay(JsonNode? ts) =>
        Day(ts) is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at).ToString("yyyy-MM-dd") : "";

    /// <summary>The Unix seconds of a numeric date, null when it is not a number or outside 1970..9999.</summary>
    static double? Day(JsonNode? v) => Py.IsNum(v) && Num(v) is > 0 and <= 253402300799 and var n ? n : null;

    /// <summary>The account's Me/me as it stands: the restored profile, else what a new account has (no recorded value).</summary>
    JsonObject MeOf(Db con, long account)
    {
        if (RestoredReads.Me(con) is { } restored)
            return restored;
        var a = Account(account);
        return new JsonObject
        {
            ["id"] = C(a["user_id"]), ["uuid"] = "", ["handle"] = C(a["handle"]), ["hostname"] = C(a["hostname"]) ?? Py.Str(a["handle"]).ToLowerInvariant(),
            ["avatar"] = C(G(a, "avatar_url", "")), ["gender"] = C(G(a, "gender", 0)), ["locale"] = "", ["timezone"] = "",
            ["continent"] = "", ["country"] = "", ["hunterscore"] = C(G(a, "hunterscore", 0)),
            ["membershipType"] = "freeplay", ["membershipTitle"] = "Hunter", ["memberSince"] = 0,  // ponytail: no join date shown
            ["membershipExpires"] = 0, ["licenseAccepted"] = 1, ["user_group"] = 1, ["user_services"] = new JsonObject(),
            ["items"] = new JsonObject(), ["worlditems"] = new JsonArray(), ["records"] = new JsonObject(), ["friend_requests"] = 0,
        };
    }

    /// <summary>The picture of an account: Me/poll sends it.</summary>
    public JsonNode? Avatar(long account)
    {
        using var con = Con(account);
        return C(MeOf(con, account)["avatar"]);
    }

    /// <summary>The in-game pages' catalogue: the keys they read of the application catalogue (species with the permitted ammo of
    /// each, items, reserves, locations; the missions only when the catalogue has them, their texts in the language `lang` names).</summary>
    public byte[] ClientApplicationReply(string? lang = null)
    {
        var reply = new JsonObject();
        foreach (var key in (string[])["items", "locale", "locations", "mission_groups", "missions", "reserves", "season", "species"])
            reply[key] = C(application[key]) ?? key switch { "locale" => JsonValue.Create("en_us"), "season" => JsonValue.Create(0), _ => new JsonArray() };
        if (Online) reply["season"] = Store.Seasons().Last().No;  // the current leaderboard season, not the recorded one
        foreach (var sp in reply["species"]!.AsArray().OfType<JsonObject>())
            if (sp["ethicalAmmo"] is null && Py.L(sp["id"]) is >= 0 and var id && id < ethicalAmmo.Count)
                sp["ethicalAmmo"] = C(ethicalAmmo[(int)id]);
        foreach (var dependencies in (reply["missions"] as JsonArray ?? []).OfType<JsonObject>().Select(m => m["dependencies"]).OfType<JsonArray>())  // the in-game page lists a locked mission's prerequisites from these: the ones the server ignores must not show (#1012)
            for (var i = dependencies.Count - 1; i >= 0; i--)
                if (TryKey(dependencies[i], out _) && Beginner.Contains(Py.L(dependencies[i])))
                    dependencies.RemoveAt(i);
        Texts.Apply(reply, lang);  // the missions' texts in the game's language
        return Py.DumpBytes(reply);
    }

    /// <summary>When the account last hunted (the end of its newest expedition, else the last heartbeat): Me/poll's `last_hunt_update_ts`, 0 before a first hunt.</summary>
    public double LastHuntUpdate(long account)
    {
        using var con = Con(account);
        return Convert.ToDouble(con.Scalar("SELECT COALESCE(MAX(COALESCE(end_ts, last_seen, start_ts)), 0) FROM expeditions WHERE account_id = $a"));
    }

    /// <summary>The slot of a placed item.</summary>
    const long PLACED_SLOT = 23;

    /// <summary>The account's item rows by item id, as (item id, owned rows) in the shape of Inventory/list and Inventory/dict:
    /// `{id, slot, units, metadata, location}`.</summary>
    List<(long ItemId, JsonNode?[] Own)> OwnRows(long account)
    {
        List<object[]> rows;
        var now = Clock();
        using (var con = Con(account))
            rows = con.Rows($"SELECT item_id, guid, slot, units, metadata FROM items WHERE account_id = $a AND {Shown} ORDER BY item_id, guid");
        return [.. rows.GroupBy(r => (long)r[0]).Select(g => (g.Key, g.Select(r =>
        {
            var metadata = Parse(r[4]);
            Feeders.Refresh(metadata, now);
            JsonNode? Take(string key)
            {
                metadata.TryGetPropertyValue(key, out var value);
                metadata.Remove(key);
                return value;
            }
            // millimetres and x 1000 in the row, both divided by 1000 in the location
            static JsonNode? Scaled(JsonNode? v) => v is JsonObject o
                ? new JsonObject { ["x"] = Py.D(o["x"]) / 1000, ["y"] = Py.D(o["y"]) / 1000, ["z"] = Py.D(o["z"]) / 1000 } : null;
            var (pos, dir, reserve) = (Take(POSITION), Take(DIRECTION), Take(RESERVE));
            Take(CLONE);
            Take(HIDDEN);
            JsonNode? location = null;
            if (r[2] is PLACED_SLOT && pos is not null)
            {
                location = new JsonObject { ["reserve"] = reserve, ["pos"] = Scaled(pos) };
                if (dir is not null) location["dir"] = Scaled(dir);
            }
            return (JsonNode?)new JsonObject { ["slot"] = Py.FromSql(r[2]), ["units"] = Py.FromSql(r[3]), ["metadata"] = metadata, ["id"] = Py.FromSql(r[1]), ["location"] = location };
        }).ToArray()))];
    }

    /// <summary>Inventory/list of the account: a row per owned item with its definition and the owned rows. A placed item
    /// (slot 23) has `location` `{reserve, pos: {x, y, z}, dir: {x, y, z}}` (`dir` only with a direction), else null.</summary>
    public byte[] InventoryListReply(long account) =>
        Py.DumpBytes(new JsonArray(OwnRows(account).Select(g => (JsonNode?)new JsonObject
        {
            ["item"] = items.TryGetValue(g.ItemId, out var def) ? C(def) : new JsonObject { ["id"] = g.ItemId },
            ["own"] = new JsonArray(g.Own),
        }).ToArray()));

    /// <summary>Inventory/dict of the account: `{<item id>: [{id, slot, units, metadata, location}]}`.</summary>
    public byte[] InventoryDictReply(long account)
    {
        var dict = new JsonObject();
        foreach (var (itemId, own) in OwnRows(account))
            dict[itemId.ToString()] = new JsonArray(own);
        return Py.DumpBytes(dict);
    }
}
