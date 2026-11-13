namespace Resurgence.Services;

/// <summary>What the pages draw for the avatar a community server names (docs/website.md#avatars): a slot name (<c>avatar-&lt;id&gt;</c> for an avatar of the game's list, docs/images.md), or the link of a
/// picture the player imported from a backup (an <c>&lt;img&gt;</c>, see <c>AssetSlot</c>); the default avatar for anything else.</summary>
public static class Avatars
{
    public const string Default = "avatar-default";

    /// <summary>The server's avatar URL (<c>…/&lt;id&gt;.png</c> of the game's list, or a link on its <c>/photos/</c>) as what a page draws; <see cref="Default"/> for null or any other address.</summary>
    public static string Slot(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return Default;
        if (u.Scheme == Uri.UriSchemeHttps && u.AbsolutePath.StartsWith("/photos/", StringComparison.Ordinal)) return u.AbsoluteUri;
        var file = Path.GetFileName(u.AbsolutePath);
        return file.EndsWith(".png", StringComparison.Ordinal) && file[..^4] is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? "avatar-" + id : Default;
    }

    /// <summary>The server's id of an avatar choice (<c>avatar-&lt;id&gt;</c>) the other way: the id; null for the imported picture (a link) or anything else.</summary>
    public static string? IdOf(string slot) => slot.StartsWith("avatar-", StringComparison.Ordinal) && slot[7..] is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id : null;
}
