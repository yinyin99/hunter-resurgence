namespace Resurgence.Services;

public sealed record Account(string Email, string Avatar, string Language, string Country, string TimeZone, bool EmailConfirmed = true);
/// <summary>An avatar picture of the settings; a locked one is bought in the store.</summary>
public sealed record AvatarChoice(string Id, string Name, bool Locked);
public sealed record BannedPlayer(int Id, string Handle);
public sealed record HeaderImage(string Id, bool Unlocked);
/// <summary>A group of profile header pictures; <see cref="Name"/> is Animals, Weapons, Exploration, Events or Daily Missions.</summary>
public sealed record HeaderGroup(string Name, IReadOnlyList<HeaderImage> Images);
/// <param name="Selected">none, recent or a picture id</param>
public sealed record Headers(string Selected, IReadOnlyList<HeaderGroup> Groups);
public sealed record LocaleChoices(IReadOnlyList<string> Countries, IReadOnlyList<string> TimeZones);

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
    Task<LocaleChoices> LocaleChoicesAsync();
    Task<Result> SaveLocaleAsync(string language, string country, string timeZone);
    /// <summary>Sends a confirmation code to the new address; the error key is Validation.Email.Invalid or Set.EmailTaken.</summary>
    Task<Result> RequestEmailChangeAsync(string email);
    /// <summary>Sends the confirmation e-mail of the address that is not confirmed yet.</summary>
    Task<Result> ResendConfirmationAsync();
    /// <summary>Changes the address once the code from the e-mail is right; the error key is Validation.Code.Invalid.</summary>
    Task<Result> ConfirmEmailChangeAsync(string email, string code);
    Task<IReadOnlyList<BannedPlayer>> BansAsync();
    Task<Result> UnbanAsync(int id);
    Task<Headers> HeadersAsync();
    Task<Result> SaveHeaderAsync(string selected);
    /// <summary>Disconnects the Steam account; the player is signed out and the launcher closes.</summary>
    Task<Result> DisconnectSteamAsync();
    Task<Recruiting> RecruitingAsync();
}
