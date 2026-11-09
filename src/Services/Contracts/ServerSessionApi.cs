namespace Resurgence.Services;

/// <summary>What the community server serves on the website's own host (docs/website.md#sign-in): the cookie and its paths. Sign-in is a browser navigation to <see cref="SteamLogin"/>.</summary>
public static class ServerSessionApi
{
    public const string Cookie = "hr_session";
    /// <summary>GET with the cookie: 200 <see cref="ServerAccount"/>, or 401 when signed out.</summary>
    public const string Me = "/auth/me";
    /// <summary>POST with the cookie and <c>X-Requested-With: XMLHttpRequest</c>: 204, the session is deleted.</summary>
    public const string SignOut = "/auth/signout";
    /// <summary>Takes <c>?next=</c>, a local path to come back to.</summary>
    public const string SteamLogin = "/auth/steam/login";
    /// <summary>GET <c>?page=n</c>: 200 <see cref="ServerBoard"/>; GET <c>/hunters/&lt;handle&gt;</c>: 200 <see cref="ServerProfile"/> or 404; GET <c>/hunters/&lt;handle&gt;/statistics</c>: 200 <see cref="Totals"/> or 404. Public: no cookie needed.</summary>
    public const string Hunters = "/hunters";
    /// <summary>GET <c>/hunters/&lt;handle&gt;/skills</c>: 200 <see cref="ServerSkills"/> or 404. Public: no cookie needed.</summary>
    public const string SkillsSuffix = "/skills";
    /// <summary>GET <c>/hunters/&lt;handle&gt;/dogs</c>: 200 <see cref="ServerDogs"/> or 404. Public: no cookie needed.</summary>
    public const string DogsSuffix = "/dogs";
    /// <summary>GET <c>/hunters/&lt;handle&gt;/trophies?page=n</c>: 200 <see cref="ServerTrophies"/> or 404. Public: no cookie needed.</summary>
    public const string TrophiesSuffix = "/trophies";
    /// <summary>GET <c>/hunters/&lt;handle&gt;/ranks</c>: 200 <see cref="ServerRanks"/> or 404. Public: no cookie needed.</summary>
    public const string RanksSuffix = "/ranks";
    /// <summary>GET <c>/hunters/&lt;handle&gt;/friends?page=n</c>: 200 <see cref="ServerFriendsPage"/> or 404. Public: no cookie needed.</summary>
    public const string FriendsSuffix = "/friends";
    /// <summary>GET <c>/leaderboards/&lt;lanebandit|bustthrough&gt;?page=n</c>: 200 <see cref="ServerGameBoard"/>. Public: no cookie needed.</summary>
    public const string Leaderboards = "/leaderboards";
}

/// <summary>The JSON of <see cref="ServerSessionApi.Me"/>: <c>ems</c> and <c>gms</c> are the wallet.</summary>
public sealed record ServerAccount(long UserId, string Handle, int Hunterscore = 0, int Rank = 0, int Ems = 0, int Gms = 0);

/// <summary>A hunter on the hunterscore board of <see cref="ServerSessionApi.Hunters"/>.</summary>
public sealed record ServerHunter(int Rank, string Handle, int Hunterscore);

/// <summary>A hunter's profile header of <see cref="ServerSessionApi.Hunters"/>: <c>membership</c> is its title ("" without one), <c>avatar</c> the URL of its picture.</summary>
public sealed record ServerProfile(string Handle, int Hunterscore, int Rank, string? Membership, string? Avatar);

/// <summary>One page of the hunterscore board of <see cref="ServerSessionApi.Hunters"/>.</summary>
public sealed record ServerBoard(int Page, int Pages, int Total, ServerHunter[] Rows);

/// <summary>A hunter's best score on a minigame board of <see cref="ServerSessionApi.Leaderboards"/>.</summary>
public sealed record ServerGameRow(int Rank, string Handle, long Value);

/// <summary>One page of a minigame board of <see cref="ServerSessionApi.Leaderboards"/>.</summary>
public sealed record ServerGameBoard(int Page, int Pages, int Total, ServerGameRow[] Rows);

/// <summary>A hunter's skills of <see cref="ServerSessionApi.SkillsSuffix"/>: every number is a level plus the fraction of its span (2.5: level 2, 50%).</summary>
public sealed record ServerSkills(ServerSpeciesSkill[] Species, ServerWeaponSkill[] Weapons);

public sealed record ServerSpeciesSkill(string Name, double Spotting, double? Tracking);

public sealed record ServerWeaponSkill(string Name, string? Group, double Value);

/// <summary>One page of a hunter's trophies of <see cref="ServerSessionApi.TrophiesSuffix"/>.</summary>
public sealed record ServerTrophies(int Page, int Pages, int Total, ServerTrophy[] Rows);

/// <param name="Date">The day, yyyy-MM-dd; empty when the trophy has none.</param>
public sealed record ServerTrophy(string Name, string Date);

/// <summary>A hunter's dogs of <see cref="ServerSessionApi.DogsSuffix"/>: <c>Percent</c> is the way to the next level.</summary>
public sealed record ServerDogs(ServerDog[] Dogs);

public sealed record ServerDog(string Name, string Type, int Level, int Percent, ServerDogSkill[] Skills, string[] Trophies);

public sealed record ServerDogSkill(string Title, int Level);

/// <summary>A hunter's ranks and milestones of <see cref="ServerSessionApi.RanksSuffix"/>: <c>Cap</c> is the most a rank row adds to the hunterscore.</summary>
public sealed record ServerRanks(long Cap, ServerRank[] Ranks, ServerMilestone[] Milestones);

/// <param name="Category">animals, weapons or collectables.</param>
/// <param name="Value">The count of ethical kills or pickups, before the cap.</param>
public sealed record ServerRank(long Id, string Category, string Name, long Value);

/// <param name="Category">animals, weapons, exploration or day-mission.</param>
/// <param name="Steps">The thresholds; <c>Value</c> the counter they are reached by.</param>
public sealed record ServerMilestone(string Category, string Title, long[] Steps, long Value);

/// <summary>One page of a hunter's friends of <see cref="ServerSessionApi.FriendsSuffix"/>.</summary>
public sealed record ServerFriendsPage(int Page, int Pages, int Total, ServerFriendRow[] Rows);

public sealed record ServerFriendRow(string Handle, int Hunterscore);

/// <summary>A player of <see cref="ServerAccountApi.Friends"/>: <c>Online</c> while a hunt of theirs is open.</summary>
public sealed record ServerPlayer(string Handle, bool Online);

/// <summary>The signed-in player's friends of <see cref="ServerAccountApi.Friends"/>: <c>Requests</c> asked them to be friends, <c>Pending</c> were asked by them.</summary>
public sealed record ServerFriends(ServerPlayer[] Friends, ServerPlayer[] Requests, ServerPlayer[] Pending);

/// <summary>The players of <see cref="ServerAccountApi.FriendSearch"/>.</summary>
public sealed record ServerPlayers(ServerPlayer[] Hunters);
