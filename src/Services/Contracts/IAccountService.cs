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

public interface IAccountService
{
    /// <summary>Raised when the avatar changes (the bar shows it).</summary>
    event Action? Changed;
    Task<Account> AccountAsync();
    Task<IReadOnlyList<AvatarChoice>> AvatarsAsync();
    Task<Result> SetAvatarAsync(string id);
    Task<Recruiting> RecruitingAsync();
}
