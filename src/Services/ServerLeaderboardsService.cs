using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The boards of the community server: the hunterscore and the two minigames' (the best scores restored from backups). It has no seasons, so the
/// species, antler and hall of fame boards are none (the live site does not offer them), and the player has a place on the hunterscore board only.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off; a failure of the network is an empty board.</param>
public sealed class ServerLeaderboardsService(HttpClient http, ServerCookie cookie, ILogger<ServerLeaderboardsService> log) : ILeaderboardsService
{
    const string Hunterscore = "hunterscore";
    /// <summary>The boards the server has; the live site offers these only.</summary>
    public static readonly string[] Kinds = [Hunterscore, "lanebandit", "bustthrough"];

    public Task<int> SeasonAsync() => Task.FromResult(0);
    public Task<IReadOnlyList<Board>> BoardsAsync(string kind) => Task.FromResult<IReadOnlyList<Board>>([]);
    public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => Task.FromResult<IReadOnlyList<HallEntry>>([]);

    public async Task<Page<Ranked>> RowsAsync(string kind, int id, int page)
    {
        if (kind == Hunterscore)
            return await GetAsync<ServerBoard>($"{ServerSessionApi.Hunters}?page={page}") is { Rows: not null } b
                ? new Page<Ranked>([.. b.Rows.Select(r => new Ranked(r.Rank, r.Handle, r.Hunterscore))], b.Page, b.Pages, b.Total)
                : new Page<Ranked>([], 1, 1);
        if (kind is "lanebandit" or "bustthrough")
            return await GetAsync<ServerGameBoard>($"{ServerSessionApi.Leaderboards}/{kind}?page={page}") is { Rows: not null } g
                ? new Page<Ranked>([.. g.Rows.Select(r => new Ranked(r.Rank, r.Handle, r.Value))], g.Page, g.Pages, g.Total)
                : new Page<Ranked>([], 1, 1);
        return new Page<Ranked>([], 1, 1);
    }

    public async Task<Ranked?> MyRankAsync(string kind, int id)
    {
        if (kind != Hunterscore) return null;  // the server keeps no place on the other boards
        if (cookie.Value is null) return null;
        return await GetAsync<ServerAccount>(ServerSessionApi.Me) is { Handle.Length: > 0 } me ? new Ranked(me.Rank, me.Handle, me.Hunterscore) : null;
    }

    /// <summary>The JSON of a 200; null for any other answer or none (a 401, an expired cookie, is not worth a warning).</summary>
    async Task<T?> GetAsync<T>(string path) where T : class
    {
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                log.LogWarning("The server answered {Path} with {Status}: an empty board", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): an empty board", path, e.Message);
        }
        return null;
    }
}
