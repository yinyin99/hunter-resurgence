namespace Resurgence.Ui;

/// <summary>Routes the website does not serve: those only the launcher has, and those that need a signed-in player. Both go back to the feed.</summary>
public static class LauncherGuard
{
    static readonly string[] LauncherOnly = ["dashboard", "launch-map", "servers", "tutorials"];
    // the old "V" routes: signed-in players only (the inventory lists missions, wallet, messages, settings, dashboard, welcome and more)
    static readonly string[] SignedIn = ["missions", "wallet", "bank", "messages", "new-message", "settings", "game-management", "invite-friends", "welcome", "community-league", "restore"];
    // the signed-in routes whose data is still the mock's: the website with a community server does not serve them (HostMode.Live)
    static readonly string[] MockData = [.. SignedIn.Except(["restore"])];

    public static bool Blocks(string path) => Starts(path, LauncherOnly);

    public static bool NeedsSession(string path) => Starts(path, SignedIn);

    public static bool MockOnly(string path) => Starts(path, MockData);

    static bool Starts(string path, string[] prefixes)
    {
        path = path.Split('?', '#')[0].Trim('/');
        return prefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
    }
}
