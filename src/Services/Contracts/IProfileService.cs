namespace Resurgence.Services;

/// <summary>The profile header; <see cref="Own"/> is the signed-in player, <see cref="Friend"/> a friend of theirs, <see cref="RequestPending"/> a friend request of the signed-in player that is not answered yet.</summary>
public sealed record Profile(string Handle, int HunterScore, int Rank, string Membership, bool Online, bool Own, bool Friend, bool RequestPending = false);
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

/// <param name="Longest">The longest shot in metres, null when nothing was hit.</param>
public sealed record WeaponStat(string Weapon, string Ammo, int Shots, double Accuracy, int Kills, double KillsPerHit, double? Longest);
public sealed record SpeciesStat(string Name, int Spotted, int Tracked, int Harvested);
public sealed record CollectableStat(string Name, int Collected, int Total, int Max);
public sealed record Totals(IReadOnlyList<WeaponStat> Weapons, IReadOnlyList<SpeciesStat> Animals, IReadOnlyList<CollectableStat> Collectables, double DistanceKm, int HuntSeconds);
/// <summary>A confirmed kill of an expedition; <see cref="Id"/> opens its score sheet.</summary>
public sealed record Kill(long Id, string Species, string Weapon, string Ammo, double DistanceM, double Score, double WeightKg, DateTime When);
/// <param name="End">Null while the expedition is not over.</param>
public sealed record Expedition(long Id, string Reserve, string Lodge, DateTime Start, DateTime? End, IReadOnlyList<WeaponStat> Weapons, IReadOnlyList<SpeciesStat> Animals,
    IReadOnlyList<CollectableStat> Collectables, IReadOnlyList<string> Achievements, double DistanceKm, int Seconds, IReadOnlyList<string> Antlers, IReadOnlyList<Kill> Kills);
public sealed record HistoryRow(long Id, DateTime Start, int Seconds, string Reserve, string Lodge, int Collectables, int Kills);
public sealed record BestRow(string Species, double Score, double Range);
/// <param name="Status">disabled, queued, building, empty (no expeditions) or done</param>
public sealed record PersonalBest(string Status, IReadOnlyList<BestRow> Rows);

public sealed record Photo(long Id, string Animal, double Score);
/// <param name="CompetitionId">Set when the trophy was won in a competition.</param>
public sealed record Trophy(string Name, DateOnly Date, long? CompetitionId, string? Competition);
public sealed record Dog(string Name, string Type, int Level, IReadOnlyList<string> Skills);
public sealed record ProfileFriend(string Handle, int HunterScore);

/// <summary>A line of a scoring sheet; the three values sit in the columns of the sheet kind (null stays empty).</summary>
public sealed record SheetRow(string Label, double? A, double? B, double? C);
public sealed record SheetFact(string Label, double Value);
/// <summary>
/// The scoring sheet of a kill. <see cref="Kind"/> is "score" (the big-game sheet: Facts, Abnormal points, Rows in right, left and difference columns, Sums) or "antler"
/// (Rows in left, right and points columns).
/// </summary>
public sealed record ScoreSheet(string Kind, string Species, IReadOnlyList<SheetFact> Facts, IReadOnlyList<SheetRow> Rows, IReadOnlyList<(double Right, double Left)> Abnormal,
    IReadOnlyList<SheetFact> Sums, string Weapon, bool Scope, string Ammo, double DistanceM, string State, string BodyPart, string Stance, string Platform,
    double WeightKg, string Type, double WoundTime, int Integrity, string ShotLocation, int Shots, DateTime When, long ExpeditionId, double Score, double? HarvestValue);

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
    Task<Totals> TotalsAsync(string handle);
    /// <param name="kind">score or antler</param>
    Task<ScoreSheet?> SheetAsync(string handle, string kind, long id);
    /// <summary>The gallery, 24 photos a page.</summary>
    Task<Page<Photo>> GalleryAsync(string handle, int page);
    /// <summary>The trophies, 24 a page.</summary>
    Task<Page<Trophy>> TrophiesAsync(string handle, int page);
    Task<IReadOnlyList<Dog>> DogsAsync(string handle);
    /// <summary>The friends of a hunter, 20 a page.</summary>
    Task<Page<ProfileFriend>> FriendsAsync(string handle, int page);
    /// <param name="id">Null for the latest expedition.</param>
    Task<Expedition?> ExpeditionAsync(string handle, long? id);
    /// <summary>The expedition list, 40 a page.</summary>
    Task<Page<HistoryRow>> HistoryAsync(string handle, int page);
    /// <param name="season">0 for all time</param>
    Task<PersonalBest> BestAsync(string handle, int season);
    Task<Progress> ProgressAsync();
    /// <summary>The launcher opens the single player map while this is 10 or less.</summary>
    Task<int> ExpeditionCountAsync();
}
