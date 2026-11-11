namespace Resurgence.Services;

/// <param name="Avatar">The picture's address, null for the <c>avatar-discord</c> slot.</param>
public sealed record DiscordMember(string Name, string? Playing, string? Avatar = null);
public sealed record DiscordInfo(int Online, IReadOnlyList<DiscordMember> Members);

public interface INewsService
{
    /// <summary>The Discord widget, or null for none.</summary>
    Task<DiscordInfo?> DiscordAsync();
}
