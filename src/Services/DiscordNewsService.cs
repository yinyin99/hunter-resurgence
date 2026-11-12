using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

public sealed record DiscordInvite(DiscordInvite.Ref? Guild)
{
    public sealed record Ref(string? Id);
}

public sealed record DiscordWidget(string? Id);

/// <summary>The feed's Discord widget is Discord's own iframe of the server behind the <c>link_discord</c> invite. The invite gives the server's id; the widget
/// is shown only when the server's owner turned it on (Server Settings, Widget), which its public <c>widget.json</c> tells. Each reply is shared by every visitor
/// (<see cref="ServerCache{T}"/>); no invite, a widget that is off or a Discord that does not answer is no widget.</summary>
/// <param name="http">No base address: the requests are Discord's absolute addresses.</param>
/// <param name="invite">The <c>link_discord</c> link (https://discord.gg/&lt;code&gt;), null for none.</param>
public sealed class DiscordNewsService(HttpClient http, string? invite, ServerCache<DiscordInvite> invites, ServerCache<DiscordWidget> widgets, ILogger<DiscordNewsService> log) : INewsService
{
    readonly string? code = Uri.TryCreate(invite, UriKind.Absolute, out var uri) && uri.Segments[^1].Trim('/') is { Length: > 0 } c ? Uri.EscapeDataString(c) : null;

    public async Task<string?> DiscordGuildAsync()
    {
        if (code is null) return null;
        if (await invites.LoadAsync(http, null, $"https://discord.com/api/v10/invites/{code}", i => i.Guild?.Id is { Length: > 0 }, log) is not { Guild.Id: { } guild }) return null;
        var w = await widgets.LoadAsync(http, null, $"https://discord.com/api/v10/guilds/{Uri.EscapeDataString(guild)}/widget.json", w => w.Id == guild, log);
        return w is null ? null : guild;
    }
}
