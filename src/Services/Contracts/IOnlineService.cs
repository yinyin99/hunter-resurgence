namespace Resurgence.Services;

/// <summary>How many hunters are in a hunt and how many multiplayer games are open right now: the community server's counts, no handles (docs/server.md).</summary>
public sealed record OnlineCounts(int Players, int Games);

/// <summary>The "online now" line of the main page (docs/website.md).</summary>
public interface IOnlineService
{
    /// <summary>The counts, or null when the server cannot be reached or does not answer them; the line is then hidden. Never throws.</summary>
    Task<OnlineCounts?> NowAsync();
}
