namespace Resurgence.Services;

/// <summary>The profile header; <see cref="Own"/> is the signed-in player, <see cref="Friend"/> a friend of theirs.</summary>
public sealed record Profile(string Handle, int HunterScore, int Rank, string Membership, bool Online, bool Own, bool Friend);
/// <summary>A progress bar of the ranks tab: <see cref="Level"/> is the rank reached, <see cref="Done"/> of <see cref="Total"/> the way to the next one.</summary>
public sealed record RankBar(string Name, int Level, int Done, int Total);
public sealed record Achievement(string Title, string Description, DateOnly Date);
/// <summary>The "Your progress" widget: score out of the next goal, rank and the newest achievement (null when none).</summary>
public sealed record Progress(int Score, int Max, int Rank, Achievement? Latest);

public sealed record Challenge(string Title, string Description);
public sealed record AchievementSummary(IReadOnlyList<RankBar> Categories, int Unlocked, IReadOnlyList<Achievement> Recent);
/// <summary>A row of milestones: <see cref="Steps"/> are the goals, <see cref="Value"/> the player's count, <see cref="Done"/> of the steps reached.</summary>
public sealed record Milestone(string Title, int Done, IReadOnlyList<int> Steps, int Value);
/// <summary>A round gauge: Spotting, Tracking or Shooting, its level and the percent to the next level.</summary>
public sealed record SkillRing(string Label, int Level, int Percent);
/// <summary>A skill card; weapon skills have a <see cref="Group"/> (the weapon category).</summary>
public sealed record Skill(string Name, string? Group, IReadOnlyList<SkillRing> Rings);

public interface IProfileService
{
    Task<Profile?> ProfileAsync(string handle);
    /// <param name="kind">overview (the Hunter Score bar first, then the categories), animals, weapons or collectables</param>
    Task<IReadOnlyList<RankBar>> RanksAsync(string handle, string kind);
    Task<AchievementSummary> AchievementSummaryAsync(string handle);
    /// <param name="kind">animals, weapons, exploration or day-mission</param>
    Task<IReadOnlyList<Milestone>> MilestonesAsync(string handle, string kind);
    Task<IReadOnlyList<Challenge>> ChallengesAsync(string handle);
    /// <param name="kind">species or weapons</param>
    Task<IReadOnlyList<Skill>> SkillsAsync(string handle, string kind);
    Task<Progress> ProgressAsync();
    /// <summary>The launcher opens the single player map while this is 10 or less.</summary>
    Task<int> ExpeditionCountAsync();
}
