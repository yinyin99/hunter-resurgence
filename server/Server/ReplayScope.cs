// Which game endpoints may be answered from the operator's recordings. A recording holds one real player's replies: on a
// community server (offline_mode false) only the endpoints that are the same for everyone may be replayed. Everything
// else on the game hosts is per player: it is answered from the database, or with the empty reply of its shape listed
// here, or (an endpoint this table does not know) with the host's miss reply. The table is the only place that decides.
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public static class ReplayScope
{
    /// <summary>host: api, apiv2, gs (game server) or web; path: a case-insensitive regex matching the whole path. A global
    /// rule has no `Empty`; a per-player rule's `Empty` is the body of its empty reply (with Status and ContentType).</summary>
    public sealed record Rule(string Host, string Path, string? Empty = null, int Status = 200, string ContentType = "application/json")
    {
        public bool Global => Empty is null;
        internal readonly Regex Pattern = new($@"\A(?:{Path})\z", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
    }

    // the empty shape of Me/me: the keys the client reads, with the value kind of each (State.MeReply fills the account's own)
    const string MeShape = """
        {"id": 0, "uuid": "", "handle": "", "hostname": "", "avatar": "", "gender": 0, "locale": "", "timezone": "", "continent": "",
         "country": "", "rank": 0, "hunterscore": 0, "ems": 0, "gms": 0, "membershipType": "", "membershipTitle": "", "memberSince": 0,
         "membershipExpires": 0, "licenseAccepted": 0, "user_group": 0, "user_services": {}, "items": {}, "worlditems": [],
         "records": {}, "friend_requests": 0, "unread_messages": 0}
        """;

    const string Stats = """{"duration": 0, "distance": 0, "data": {"collectables": {}, "weapon": {}, "animal": {}, "daily_missions": {}}}""";
    const string HandlerError = """{"error": 1, "code": "handlerError"}""";
    const string Missing = """{"id":"server","code":"not_found","status":404,"detail":"not found"}""";
    const string Nothing = """{"id": 0, "define": "", "name": "", "scoretype": 0, "total": 0, "position": null, "records": []}""";

    static Rule Global(string host, string path) => new(host, path);
    static Rule Own(string path, string empty) => new("api", "/v1/" + path, empty);

    /// <summary>Every endpoint of the game hosts that a recording could answer. A request that matches no rule on a game
    /// host is per player too (default deny), so a new endpoint is never replayed until it is listed global here.</summary>
    public static readonly IReadOnlyList<Rule> Rules =
    [
        // ---- GLOBAL: the same reply for every player
        Global("api", "/v1/Application/(application|discounts|client_application)"),  // the catalogue (the in-game pages' share of it) and the discounts
        Global("api", "/v1/Store/products2"),
        Global("api", "/v1/Splash/(list_active|feed_data)"),  // the splash list and the launcher news (the server's notice is put first)
        Global("api", "/v1/(Campaign|Community_Campaign)/get_active_stylesheet"),
        Global("api", "/v1/Membership/get_type"),  // what each membership type gives
        Global("api", "/v1/Membership_Bundle/get_active"),  // the bundles on sale
        Global("api", "/v1/Avatar/getAvatars"),  // every avatar is unlocked for everyone
        Global("api", "/v1/Challenge/get_challenges"),  // the challenge definitions
        Global("api", "/v1/Sidebar_Counter/getTemplate"),
        Global("gs", ProtocolLiterals.RpcBase + "/ping"),  // the client's ping
        Global("gs", "/"),
        Global("web", "/(static|content|dist|feed|media)/.*"),  // the website's files, its news feed and the pictures
        Global("web", "/(client|launcher)"),  // the in-game pages' shell and the launcher's page (a community server answers the second itself)
        Global("web", "/v1/json/emotelauncher/ping"),

        // ---- PER PLAYER, answered from the database where the state has a handler (State.*Reply); otherwise the empty reply
        Own("Me/me", MeShape),
        Own("Me/poll", """{"counter": 0, "unread_messages": 0, "avatar": "", "ems": 0, "gms": 0, "friend_requests": 0, "campaign_stylesheet": false, "community_campaign_stylesheet": false, "active_campaign": false, "last_hunt_update_ts": 0}"""),
        Own("Me/friends", """{"friends": [], "requests": [], "pending": [], "requestCount": 0, "friendCount": 0}"""),
        Own("Me/accept_license", "true"),  // the license is accepted on every new account
        Own("User/friendslist", """{"total": 0, "friends": []}"""),
        Own("Friend/add", "false"),
        Own("Network/list", "[]"),  // the live multiplayer games: this server has none
        Own("Expedition/list", """{"total": 0, "has_more": false, "expeditions": []}"""),
        Own("Expedition/animal", "null"),
        Own("Public_user/expedition", "null"),
        Own("Public_user/getByHostname", "null"),
        Own("Inventory/list", "[]"),
        Own("Inventory/dict", "{}"),
        Own("Inventory/refillBarrel", """{"success": false}"""),
        Own("Skill/list", """{"weapon": {}, "tracking": [], "spotting": []}"""),
        Own("Statistics/total", Stats),
        Own("Achievement/stats", "[]"),
        Own("Trophy/list", """{"total": 0, "trophies": []}"""),
        Own("Gallery/list", """{"photos": [], "total": 0}"""),
        Own("Chat/get_message_settings", """{"receive_private_messages": 0}"""),
        Own("Chat/message", HandlerError),
        Own("Launch/tutorial", HandlerError),  // the tutorial's tokens are the recorded player's: none is handed out
        Own("Wallet/(bank_transactions|silver_transactions|transactions)", "[]"),
        Own("Wallet/spent", """{"spent": 0.0, "silver_spent": 0.0}"""),
        Own("Mission/missions", """{"active": [], "completed": [], "available": [], "states": {}}"""),  // missions are the operator's to decide: none yet
        Own("Game/user/loadout", "[]"),  // the loadouts: none stored
        Own("Mission/activate", "false"),
        Own("Daily_Mission/get_daily_missions_calendar",
            """{"is_frozen": 1, "broken_streak_info": {"has_broken_streak": false, "streak_larger": 0}, "calendar": []}"""),
        Own("Daily_Mission/activate_user_progress", """{"success": false}"""),
        Own("Challenge/get_user_challenges", "{}"),
        Own("Leaderboard/personal", """{"status": "done", "data": []}"""),
        Own("Leaderboard/current_personal", "[]"),
        Own("Competition/join", "false"),  // competitions are the operator's to decide: none yet
        Own("Page_content/(competition_states|list_competitions|leaderboards_all)", "[]"),
        Own("Page_content/competitions_new", """{"info": [], "entrants_total": 0, "entrants": [], "competitions": [], "competitions_total": 0}"""),
        Own("Page_content/hunterscores", """{"total": 0, "records": []}"""),
        Own("Page_content/leaderboard_details", Nothing),
        Own("Page_content/(leaderboards_hall_of_fame|list_community_league_competitions|competition_community_league_states)", "[]"),
        Own("Page_content/list_previous_competitions", """{"total": 0, "competitions": []}"""),
        Own("Page_content/minigames_list", """{"total": 0, "position": null, "records": []}"""),
        Own("Recruit/get_recruits", "[]"),
        new("apiv2", "/game/user/backgrounds", "[]"),
        new("apiv2", "/game/user/leaderboards/antlers/(single|double)/all", "[]"),
        new("apiv2", "/game/user/loadout", "[]", ContentType: "application/vnd.game_data.user.loadout+json; type=collection"),
        new("apiv2", @"/game/user/\d+/profile", """{"earned_achievements": 0, "hunterscore": 0}""", ContentType: "application/vnd.game_data.user.profile+json"),
        new("apiv2", @"/game/user/\d+/ranks", "[]", ContentType: "application/vnd.game_data.user.rank+json; type=collection"),
        new("apiv2", @"/game/user/harvest/\d+(/taxidermize)?", Missing, 404, "application/vnd.goa.error"),
        new("apiv2", "/game/user/trophies", "[]"),
        new("apiv2", @"/game/user/\d+/lodges", "[]", ContentType: "application/vnd.game.user.trophy_lodge.lodge+json; type=collection"),
        new("apiv2", @"/game/user/\d+/lodges/\d+", Missing, 404, "application/vnd.goa.error"),
    ];

    /// <summary>The rule of a request: the first that matches its host and path, or null. The website's `/v1/` paths are the
    /// web API's (any case).</summary>
    public static Rule? Find(Replay.Game game, Req r)
    {
        var host = r.Host == game.Api ? "api" : r.Host == game.Apiv2 ? "apiv2" : r.Host == game.Gs ? "gs" : r.Host == game.Web || r.Host == game.GameWeb ? "web" : null;
        if (host is null) return null;
        if (host == "web" && r.Path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) && Rules.FirstOrDefault(x => x.Host == "api" && x.Pattern.IsMatch(r.Path)) is { } api)
            return api;
        return Rules.FirstOrDefault(x => x.Host == host && x.Pattern.IsMatch(r.Path));
    }

    /// <summary>On a community server, is this request one whose recorded reply must not be sent? True for every request
    /// of a game host that is not a global rule and not a CORS preflight (no body, no data); false on the asset hosts.</summary>
    public static bool Denied(Replay.Game game, Req r) =>
        game.State is { Online: true } && r.Method != "OPTIONS"
        && (r.Host == game.Api || r.Host == game.Apiv2 || r.Host == game.Gs || r.Host == game.Web || r.Host == game.GameWeb) && Find(game, r) is not { Global: true };
}
