// Whether a player's Steam account owns the game (docs/server.md#steam-licence): the launcher says it next to its "Install with Steam" button. Steam Web API IPlayerService/GetOwnedGames,
// with the operator's key, at most once a day per account; only the yes/no is kept, in memory.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

/// <param name="wait">The longest a caller waits for Steam (default 2 seconds, <see cref="DefaultWait"/>): /auth/me is read with a 5 second limit by the website and the launcher, past it they take the player as signed out. A caller that gives up gets `unknown`; the call goes on and fills the cache for the next one.</param>
public sealed class SteamLicence(HttpClient http, string key, string appId, Func<DateTimeOffset>? clock = null, TimeSpan? wait = null)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);
    public const string Owned = "owned", NotOwned = "not_owned", Unknown = "unknown";
    static readonly TimeSpan Every = TimeSpan.FromDays(1);

    readonly TimeSpan wait = wait ?? DefaultWait;
    int warned;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    // ponytail: in memory, so a restart asks again (once per account); a table if restarts ever make that many calls. The Lazy makes the call once for requests that arrive together
    readonly ConcurrentDictionary<long, (DateTimeOffset At, Lazy<Task<string>> Answer)> cache = new();

    /// <summary>On when both `steam_web_api_key` and `steam_app_id` are set (they go together, <see cref="Settings"/>), else null: no call is ever made.</summary>
    public static SteamLicence? FromCfg(JsonObject cfg, HttpClient http) =>
        (string?)cfg["steam_web_api_key"] is { } key && (string?)cfg["steam_app_id"] is { } appId ? new(http, key, appId) : null;

    /// <summary>owned, not_owned or unknown for the account: unknown without a Steam id, when Steam does not answer and when the player's game details are private.</summary>
    public async Task<string> OfAsync(long account, string? steamId)
    {
        if (steamId is null) return Unknown;
        var at = now();
        var entry = cache.AddOrUpdate(account, _ => (at, Ask(steamId)), (_, old) => at - old.At < Every ? old : (at, Ask(steamId)));
        try { return await entry.Answer.Value.WaitAsync(wait); }
        catch (TimeoutException) { return Unknown; }  // Steam is slow: this request goes on without it, the call finishes for the next one
    }

    Lazy<Task<string>> Ask(string steamId) => new(() => FetchAsync(steamId));

    async Task<string> FetchAsync(string steamId)
    {
        try
        {
            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(key)}&steamid={Uri.EscapeDataString(steamId)}&include_played_free_games=1&appids_filter%5B0%5D={Uri.EscapeDataString(appId)}&format=json";
            using var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                if (Interlocked.Exchange(ref warned, 1) == 0) Console.Error.WriteLine($"steam licence: Steam answered {(int)response.StatusCode}; check steam_web_api_key (said once)");  // the status only: the address holds the key
                return Unknown;
            }
            // game details that are private: an empty response; a public library without the game: game_count 0
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync())?["response"] is not JsonObject r) return Unknown;
            if (r["games"] is JsonArray games) return games.Any(g => g?["appid"]?.ToString() == appId) ? Owned : NotOwned;
            return r["game_count"] is JsonValue n && n.TryGetValue<long>(out var count) && count == 0 ? NotOwned : Unknown;
        }
        catch (Exception) { return Unknown; }  // whatever went wrong (a cancelled or failed call, odd JSON): never the message, it can carry the URL, which holds the key; and never a failed task in the cache
    }
}
