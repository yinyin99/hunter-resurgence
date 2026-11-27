namespace Resurgence.Ui;

/// <summary>Routes the website does not serve: those only the launcher has (back to the feed), and those that need a signed-in player
/// (the sign-in prompt in their place while signed out, in the launcher too).</summary>
public static class LauncherGuard
{
    static readonly string[] LauncherOnly = ["dashboard", "launch-map", "servers", "tutorials"];
    // signed-in players only: the old "V" routes (the inventory lists missions, wallet, messages, settings, dashboard, welcome and more), the inventory, players' data, the store, the
    // open games (the launcher's servers page) and the competitions (the server refuses their reads signed out: docs/website.md#signed-in-players-only)
    static readonly string[] SignedIn = ["missions", "wallet", "bank", "messages", "new-message", "settings", "invite-friends", "welcome", "community-league", "community_league", "steam_callback", "restore",
        "store", "profile", "competitions", "servers", "inventory"];
    // the routes whose data is still the mock's: the website with a community server does not serve them (HostMode.Live). Competitions have no
    // source there: the only recorded ones ended weeks ago (docs/website.md#sign-in). The wallet, the messages and the missions (the daily ones too) are the server's
    // (the wallet's bank tab is the page's to hide). The settings page is the server's for the avatar and the multiplayer bans, its two working items; the store, the inventory, the profiles and the games are the server's
    static readonly string[] MockData = [.. SignedIn.Except(["restore", "wallet", "messages", "missions", "settings", "store", "profile", "servers", "inventory"])];

    public static bool Blocks(string path) => Starts(path, LauncherOnly);

    public static bool NeedsSession(string path) => Starts(path, SignedIn);

    public static bool MockOnly(string path) => Starts(path, MockData);

    static bool Starts(string path, string[] prefixes)
    {
        path = path.Split('?', '#')[0].Trim('/');
        return prefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
    }
}
