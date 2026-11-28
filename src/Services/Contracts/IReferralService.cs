namespace Resurgence.Services;

/// <summary>An item of a referral reward, named by the server from its catalogue.</summary>
public sealed record ReferralItem(int Id, string Name, int Count);

/// <summary>What a referral reward gives: gm$, em$ and items. The server sends none (null) for a reward the operator has not set yet: "to be announced".</summary>
public sealed record ReferralReward(int Gm, int Em, ReferralItem[]? Items)
{
    public bool Any => Gm > 0 || Em > 0 || Items is { Length: > 0 };
}

/// <summary>What a recruit has to do for the recruiter to be rewarded: <see cref="Hours"/> played and <see cref="Score"/> HunterScore gained, both counted from the moment the code was entered.</summary>
public sealed record ReferralQualification(double Hours, int Score);

/// <param name="Hours">Played since the code was entered.</param>
/// <param name="Score">HunterScore gained since the code was entered.</param>
/// <param name="QualifiedAt">Unix seconds; null until <see cref="Qualified"/>.</param>
public sealed record ReferralRecruit(string Handle, double Hours, int Score, bool Qualified, long? QualifiedAt, bool DiscordLinked, bool Paid);

/// <summary>A milestone: reaching <see cref="Recruits"/> pays <see cref="Reward"/> once (null: to be announced).</summary>
public sealed record ReferralTier(int Recruits, ReferralReward? Reward, bool Reached);

public sealed record ReferralDiscord(int LinkedCount, ReferralTier[] Milestones);

/// <summary>The signed-in player's referral page (<see cref="ServerAccountApi.Referral"/>).</summary>
/// <param name="Link">The invitation link, <c>/join/&lt;code&gt;</c> on the website's host.</param>
/// <param name="CanEnter">A code can still be entered; <see cref="EnterUntil"/> (unix seconds) ends the window, null when it cannot.</param>
/// <param name="EnteredCode">The code the player entered and the handle of the recruiter it belongs to; null before.</param>
/// <param name="Recruits">The server's order: not qualified first, closest to qualifying first, then the qualified, newest first.</param>
/// <param name="RecruitReward">What a player gets for entering a code (null: to be announced).</param>
public sealed record Referral(string Code, string Link, bool CanEnter, long? EnterUntil, string? EnteredCode, string? Recruiter, ReferralQualification Qualification,
    ReferralRecruit[] Recruits, int QualifiedCount, ReferralTier[] Tiers, ReferralDiscord Discord, ReferralReward? RecruitReward);

/// <param name="Recruiter">The handle of the code's owner.</param>
/// <param name="Reward">What the player got at once; null: to be announced.</param>
public sealed record ReferralEntered(string Recruiter, ReferralReward? Reward);

/// <summary>The referral code, the progress of the recruits and entering another player's code (#929).</summary>
public interface IReferralService
{
    /// <summary>The signed-in player's referral page; null signed out or when the server does not answer. Never throws.</summary>
    Task<Referral?> GetAsync();

    /// <summary>Enters <paramref name="code"/> (case, spaces and dashes do not matter). The error is a text key: <c>Referral.Error.</c> and the server's reason <c>unknown</c>, <c>own</c>, <c>loop</c>, <c>already</c>,
    /// <c>expired</c> or <c>banned</c>; <c>Referral.Error.failed</c> for an answer that does not come.</summary>
    Task<Result<ReferralEntered>> EnterAsync(string code);
}
