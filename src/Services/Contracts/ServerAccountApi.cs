namespace Resurgence.Services;

/// <summary>What the community server serves under <c>/account/</c> to the website's own server (the reverse proxy routes only <c>/account/restore/*</c>, for the backup tool): the session cookie of <see cref="ServerSessionApi"/> authenticates all of them.</summary>
public static class ServerAccountApi
{
    /// <summary>GET: <c>{restoredUserId, review: [{ts, note}]}</c>. POST with <c>X-Requested-With</c> to <c>/start</c>: <c>{status: started, id}</c>, then each part of at most
    /// <see cref="PartBytes"/> to <c>/&lt;id&gt;/&lt;part&gt;</c> (<c>{status: received}</c>), then <c>/&lt;id&gt;/finish</c>: <c>{status, message, report}</c>.</summary>
    public const string Restore = "/account/restore";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>confirm=delete</c>: deletes the account and all its data.</summary>
    public const string Delete = "/account/delete";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>id</c>, <c>count</c>, <c>bundle=1</c> for a bundle: buys with em$ at the store's price; 200 <c>{status, ems}</c>,
    /// or 409 for something not for sale or a balance too low.</summary>
    public const string Buy = "/account/buy";
    /// <summary>GET with the cookie: 200 <see cref="ServerFriends"/> (the friends, the requests and the pending requests of the player); POST with <c>X-Requested-With</c> to
    /// <c>/add</c> (asks, or accepts the other's request) or <c>/remove</c> (declines, cancels or removes) and the form <c>handle</c>: 200, or 404 for a handle that is not another player.</summary>
    public const string Friends = "/account/friends";
    /// <summary>GET with the cookie and <c>?text=</c>: 200 <see cref="ServerPlayers"/>, the players whose handle contains the text.</summary>
    public const string FriendSearch = "/account/friends/search";
    /// <summary>GET with the cookie: 200 the player's missions (<see cref="ServerMissions"/>), or 404 for a server without missions; POST with <c>X-Requested-With</c> to <c>/&lt;call&gt;</c>
    /// (<c>activate</c> and <c>mission_id</c>, <c>activate_all_available</c>, <c>update_automatic_activation</c> and <c>auto_mission_activate</c>, <c>update_activate_only_favourite</c> and
    /// <c>activate_only_favourite</c>, <c>update_hide_completed_favourite</c> and <c>hide_completed_favourite</c> as <c>true</c> or <c>false</c>, <c>update_favourite</c> with <c>group_id</c> and <c>is_favourite</c>): 200, or 409 for a mission that is not available.</summary>
    public const string Missions = "/account/missions";
    /// <summary>GET with the cookie: 200 the player's daily missions page (<see cref="ServerDaily"/>), or 404 for a server without daily missions; POST with <c>X-Requested-With</c> to <c>/activate</c>:
    /// 200 once the streak is active (a frozen one starts at day 1).</summary>
    public const string DailyMissions = "/account/missions/daily";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>id</c>: deletes a photo of the player's own gallery; 200, or 404 for one that is not theirs.</summary>
    public const string GalleryDelete = "/account/gallery/delete";
    /// <summary>GET with the cookie: 200 with the player's own gallery as a zip of its pictures.</summary>
    public const string GalleryZip = "/account/gallery/zip";
    /// <summary>GET with the cookie: 200 <see cref="ServerConversations"/>, the player's own conversations (the server's notices and the restored ones), read only.</summary>
    public const string Messages = "/account/messages";
    /// <summary>GET with the cookie and <c>?handle=</c>: 200 <see cref="ServerThread"/>, one of the conversations newest first, or 404 for a handle that is none of them.</summary>
    public const string MessagesThread = "/account/messages/thread";
    /// <summary>GET with the cookie: <c>/ems</c> and <c>/gms</c> (query <c>offset</c>) 200 a page of <see cref="ServerWalletRow"/>, newest first, 50 at most; <c>/spent</c> 200 <see cref="ServerSpent"/>.</summary>
    public const string Wallet = "/account/wallet";
    public const long MaxZipBytes = 1L << 30;
    /// <summary>One request's body at most, under a proxy's 100 MB.</summary>
    public const int PartBytes = 90 << 20;
}

/// <summary>A conversation of <see cref="ServerAccountApi.Messages"/>: <c>Ts</c> is unix seconds.</summary>
public sealed record ServerConversation(string Handle, string Preview, long Ts, bool Unread);

public sealed record ServerConversations(ServerConversation[] Conversations);

/// <summary>A message of <see cref="ServerAccountApi.MessagesThread"/>: <c>Mine</c> when the player wrote it.</summary>
public sealed record ServerMessage(bool Mine, string Content, long Ts);

public sealed record ServerThread(ServerMessage[] Messages);

/// <summary>A mission of <see cref="ServerAccountApi.Missions"/>: <c>State</c> is active, available, completed or locked; <c>Reserve</c>, <c>Species</c> and <c>Weapon</c> are "" for none.</summary>
public sealed record ServerMission(int Id, string Title, string Desc, string State, string[] Objectives, string Reward, string Reserve, string Species, string Weapon);

/// <summary>A pack of <see cref="ServerAccountApi.Missions"/>: <c>Missions</c> are mission ids in display order.</summary>
public sealed record ServerMissionGroup(int Id, string Title, string Desc, bool Favourite, int[] Missions);

/// <summary>A day of the daily calendar: its number (1 to 7), the difficulty id (1 easy, 2 medium, 3 hard) as <c>Stars</c> and its reward in gm$.</summary>
public sealed record ServerDailyDay(int Number, int Stars, int Gms);

/// <summary>The mission of a day of <see cref="ServerDaily"/>: empty (no <c>Title</c>) for a day that has none; <c>Done</c> once every objective is finished.</summary>
public sealed record ServerDailyMission(string Title, string Desc, string[] Objectives, string Reserve, string[] Rewards, bool Done);

/// <summary>The daily missions page: <c>Today</c> is the number of today's day, <c>EndsIn</c> the seconds until the day ends, <c>Frozen</c> true until the player activates the streak;
/// <c>Missions</c> are in the order of <c>Days</c>.</summary>
public sealed record ServerDaily(int Today, [property: System.Text.Json.Serialization.JsonPropertyName("ends_in")] int EndsIn, bool Frozen, ServerDailyDay[] Days, ServerDailyMission[] Missions);

public sealed record ServerMissions(ServerMission[] Missions, ServerMissionGroup[] Groups, int Completed,
    [property: System.Text.Json.Serialization.JsonPropertyName("auto_mission_activate")] bool Auto,
    [property: System.Text.Json.Serialization.JsonPropertyName("activate_only_favourite")] bool OnlyFavourite,
    [property: System.Text.Json.Serialization.JsonPropertyName("hide_completed_favourite")] bool HideCompletedFavourite);
