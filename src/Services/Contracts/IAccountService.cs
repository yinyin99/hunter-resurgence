namespace Resurgence.Services;

public sealed record Account(string Avatar);
/// <summary>An avatar picture of the settings; a locked one is unlocked by buying the store item <c>Item</c>. <c>InGame</c>: while the player's own picture is selected, the listed avatar whose head the game draws the player with.</summary>
public sealed record AvatarChoice(string Id, string Name, bool Locked, long? Item = null, bool InGame = false);

/// <summary>A line of the recruit rewards: an item with a store page, or plain text such as 50 Em$.</summary>
public sealed record RewardItem(string Text, int? ItemId);
public sealed record RewardRow(string Purchase, IReadOnlyList<RewardItem> YouGet, IReadOnlyList<RewardItem> OneTime);
public sealed record Recruit(string Handle, DateOnly Joined, string Reward);
/// <summary>The recruit page: the player's referral link, the reward table and the friends who joined.</summary>
public sealed record Recruiting(string Link, IReadOnlyList<RewardRow> Rewards, IReadOnlyList<Recruit> Recruits);
/// <summary>A player the signed-in player banned from their multiplayer games (the game's Ban button): the avatar slot or link (<see cref="Avatars.Slot"/>) and the day of the ban.</summary>
public sealed record GameBan(string Handle, DateOnly Date, string Avatar = Avatars.Default);

public interface IAccountService
{
    /// <summary>Raised when the avatar or the units change (the bar shows the avatar, the pages the units).</summary>
    event Action? Changed;
    Task<Account> AccountAsync();
    Task<IReadOnlyList<AvatarChoice>> AvatarsAsync();
    Task<Result> SetAvatarAsync(string id);
    /// <summary>Saves the player's units on the account (the game's and the pages'): imperial (yards, pounds) or metric; raises <see cref="Changed"/> so the pages read them again (<see cref="Session.Imperial"/>).</summary>
    Task<Result> SetImperialAsync(bool imperial);
    Task<Recruiting> RecruitingAsync();
    /// <summary>The players the signed-in player banned from their games, the latest first; none without a session or when the server does not answer.</summary>
    Task<IReadOnlyList<GameBan>> GameBansAsync();
    /// <summary>Lifts the signed-in player's ban of the player, who can join their games again.</summary>
    Task<Result> UnbanAsync(string handle);
}
