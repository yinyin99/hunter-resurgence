namespace Resurgence.Services;

public interface INewsService
{
    /// <summary>The id of the Discord server whose widget the feed shows (Discord's own iframe), or null for none.</summary>
    Task<string?> DiscordGuildAsync();
}
