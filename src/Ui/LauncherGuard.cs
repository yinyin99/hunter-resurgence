namespace Resurgence.Ui;

/// <summary>Routes only the launcher serves; the website sends them back to the feed.</summary>
public static class LauncherGuard
{
    static readonly string[] Prefixes = ["dashboard", "launch-map", "servers", "tutorials"];

    public static bool Blocks(string path)
    {
        path = path.Split('?', '#')[0].Trim('/');
        return Prefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
    }
}
