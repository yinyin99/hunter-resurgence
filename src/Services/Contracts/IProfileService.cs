namespace Resurgence.Services;

public sealed record Profile(string Handle, int HunterScore, int Rank);

public interface IProfileService
{
    Task<Profile?> ProfileAsync(string handle);
    /// <summary>The launcher opens the single player map while this is 10 or less.</summary>
    Task<int> ExpeditionCountAsync();
}
