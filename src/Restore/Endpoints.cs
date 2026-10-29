using System.Text.RegularExpressions;

namespace Resurgence.Restore;

/// <summary>File name back to endpoint, for a file the manifest gives no usable endpoint for. The backup tool names a /v1 reply
/// Area_name[__k=v]... and an apiv2 reply by its whole path with "/" as "_"; names such as Page_content or Public_user hold "_" themselves,
/// so the left part is looked up in a closed list instead of being split.</summary>
static partial class Endpoints
{
    static readonly string[] V1 =
    [
        "Me/me", "Me/friends", "Public_user/getByHostname", "Public_user/expedition", "Inventory/dict", "Inventory/list", "Skill/list", "Statistics/total",
        "Achievement/stats", "Challenge/get_user_challenges", "Leaderboard/personal", "Leaderboard/current_personal", "Page_content/minigames_list", "Trophy/list",
        "Expedition/list", "Mission/missions", "Daily_Mission/get_daily_missions_calendar", "Wallet/transactions", "Wallet/silver_transactions",
        "Wallet/bank_transactions", "Wallet/spent", "Page_content/competition_states", "Page_content/competition_community_league_states",
        "Page_content/list_previous_competitions", "Page_content/list_community_league_competitions", "Page_content/competitions_new",
        "Chat/get_message_settings", "Chat/conversations", "Chat/conversation", "Recruit/get_recruits", "User/friendslist", "Gallery/list",
        "Campaign/get_active_campaign", "Community_Campaign/get_active_community_campaign", "Application/application", "Membership_Bundle/get_active",
    ];

    static readonly Dictionary<string, string> ByName = V1.ToDictionary(p => p.Replace('/', '_'), p => "/v1/" + p, StringComparer.Ordinal);

    [GeneratedRegex(@"^game_user_(?:\d+_(?:profile|ranks|lodges(?:_\d+)?)|trophies|harvest_\d+|loadout|backgrounds|leaderboards_antlers_(?:single|double)_(?:all|\d+_personal_\d+))$")]
    private static partial Regex V2();

    public static (string Endpoint, Dictionary<string, string> Params)? FromName(string path)
    {
        if (!path.EndsWith(".json", StringComparison.Ordinal) || path.Contains('/')) return null;
        var name = path[..^".json".Length];
        if (V2().IsMatch(name)) return ("/" + name.Replace('_', '/'), []);
        var cut = name.IndexOf("__", StringComparison.Ordinal);
        if (!ByName.TryGetValue(cut < 0 ? name : name[..cut], out var ep)) return null;
        var ps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in cut < 0 ? [] : name[(cut + 2)..].Split("__"))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) return null;
            ps[kv[..eq]] = kv[(eq + 1)..];
        }
        return (ep, ps);
    }
}
