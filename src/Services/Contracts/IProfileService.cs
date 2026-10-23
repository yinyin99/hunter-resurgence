namespace Resurgence.Services;

/// <summary>The profile header; <see cref="Own"/> is the signed-in player, <see cref="Friend"/> a friend of theirs.</summary>
public sealed record Profile(string Handle, int HunterScore, int Rank, string Membership, bool Online, bool Own, bool Friend);
/// <summary>A progress bar of the ranks tab: <see cref="Level"/> is the rank reached, <see cref="Done"/> of <see cref="Total"/> the way to the next one.</summary>
public sealed record RankBar(string Name, int Level, int Done, int Total);
public sealed record Achievement(string Title, string Description, DateOnly Date);
/// <summary>The "Your progress" widget: score out of the next goal, rank and the newest achievement (null when none).</summary>
public sealed record Progress(int Score, int Max, int Rank, Achievement? Latest);

public interface IProfileService
{
    Task<Profile?> ProfileAsync(string handle);
    /// <param name="kind">overview (the Hunter Score bar first, then the categories), animals, weapons or collectables</param>
    Task<IReadOnlyList<RankBar>> RanksAsync(string handle, string kind);
    Task<Progress> ProgressAsync();
    /// <summary>The launcher opens the single player map while this is 10 or less.</summary>
    Task<int> ExpeditionCountAsync();
}
