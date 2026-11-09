namespace Resurgence.Services;

public sealed record DiscordMember(string Name, string? Playing);
public sealed record DiscordInfo(int Online, IReadOnlyList<DiscordMember> Members);

public interface INewsService
{
    /// <summary>The Discord widget, or null for none.</summary>
    Task<DiscordInfo?> DiscordAsync();
}
