using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The hunterscore board of the community server; every other board is still the mock's.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off; a failure of the network is an empty board.</param>
public sealed class ServerLeaderboardsService(HttpClient http, ServerCookie cookie, ILogger<ServerLeaderboardsService> log) : ILeaderboardsService
{
    const string Hunterscore = "hunterscore";
    readonly MockLeaderboardsService mock = new();

    public Task<int> SeasonAsync() => mock.SeasonAsync();
    public Task<IReadOnlyList<Board>> BoardsAsync(string kind) => mock.BoardsAsync(kind);
    public Task<IReadOnlyList<HallEntry>> HallAsync(int season) => mock.HallAsync(season);

    public async Task<Page<Ranked>> RowsAsync(string kind, int id, int page)
    {
        if (kind != Hunterscore) return await mock.RowsAsync(kind, id, page);
        var b = await GetAsync<ServerBoard>($"{ServerSessionApi.Hunters}?page={page}");
        return b is null ? new Page<Ranked>([], 1, 1) : new Page<Ranked>([.. b.Rows.Select(r => new Ranked(r.Rank, r.Handle, r.Hunterscore))], b.Page, b.Pages, b.Total);
    }

    public async Task<Ranked?> MyRankAsync(string kind, int id)
    {
        if (kind != Hunterscore) return await mock.MyRankAsync(kind, id);
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
                log.LogWarning("The server answered {Path} with {Status}: no hunterscore board", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no hunterscore board", path, e.Message);
        }
        return null;
    }
}
