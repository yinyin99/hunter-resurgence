using Resurgence.Services;

namespace Resurgence.Ui;

/// <summary>When the entry points of Discord Linked Roles (the feed's card, the pill on every page, the settings menu item) show: for a signed-in hunter who has not linked a Discord account, where
/// there is a server to link at (docs/website.md#discord-linked-roles). Signed out, or linked: none of them.</summary>
public static class DiscordOffer
{
    public static bool Shown(Session? session, ISessionService sessions) => session is { Handle.Length: > 0, DiscordLinked: false } && sessions.DiscordLinkUrl is not null;
}
