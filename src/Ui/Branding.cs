using System.Text.Json;

namespace Resurgence.Ui;

/// <summary>
/// The game's name and its external links, read from the untracked settings.json (game_name, game_full, link_news, link_forum, ...).
/// A missing file, a missing key or a placeholder (&lt;...&gt;) gives "the game" and no link, so the repository never needs the real values.
/// </summary>
public sealed class Branding
{
    readonly Dictionary<string, string> values;

    public Branding(Dictionary<string, string>? values = null) => this.values = values ?? [];

    public static Branding Load(string path)
    {
        try { return new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 && !v.Contains('<') ? v : null;

    public string GameName => Get("game_name") ?? "the game";
    public string GameFull => Get("game_full") ?? GameName;

    /// <summary>External link by short key (news, forum, support, ...); null when not configured.</summary>
    public string? Link(string key) => Get("link_" + key);

    /// <summary>This branding with link_<paramref name="key"/> set to <paramref name="url"/>, which wins over settings.json; an empty or missing url keeps the current link.</summary>
    public Branding WithLink(string key, string? url) => string.IsNullOrEmpty(url) ? this : new(new Dictionary<string, string>(values) { ["link_" + key] = url });

    /// <summary>{game} is the short name, {GAME} the same in capitals, {game_full} the name with its edition.</summary>
    public string Expand(string text) => text.Replace("{game_full}", GameFull).Replace("{game}", GameName).Replace("{GAME}", GameName.ToUpperInvariant());
}
