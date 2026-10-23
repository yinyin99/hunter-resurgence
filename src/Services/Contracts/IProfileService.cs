namespace Resurgence.Services;

public sealed record Profile(string Handle, int HunterScore, int Rank);
public sealed record Achievement(string Title, string Description, DateOnly Date);
/// <summary>The "Your progress" widget: score out of the next goal, rank and the newest achievement (null when none).</summary>
public sealed record Progress(int Score, int Max, int Rank, Achievement? Latest);

public interface IProfileService
{
    Task<Profile?> ProfileAsync(string handle);
    Task<Progress> ProgressAsync();
    /// <summary>The launcher opens the single player map while this is 10 or less.</summary>
    Task<int> ExpeditionCountAsync();
}
