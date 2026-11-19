using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The boards of the community server: the species boards by season and their hall of fame, the hunterscore and the two minigames' (the best scores, restored or played).
/// It has no antler boards (the live site does not offer them).</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off; a failure of the network is an empty board.</param>
public sealed class ServerLeaderboardsService(HttpClient http, ServerCookie cookie, ILogger<ServerLeaderboardsService> log) : ILeaderboardsService
{
    const string Hunterscore = "hunterscore";
    /// <summary>The boards the server has, in the order of the tabs; the live site offers these only.</summary>
    public static readonly string[] Kinds = ["score", "range", Hunterscore, "halloffame", "lanebandit", "bustthrough"];

    static bool Species(string kind) => kind is "score" or "range";

    static Ranked Row(ServerRow r) => new(r.Rank, r.Handle, r.Value, (int)r.Kill, Avatars.Slot(r.Avatar), r.WeaponName ?? "", r.Ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.Ts) : null);

    public async Task<(int Current, IReadOnlyList<Season> Seasons)> SeasonsAsync() =>
        await GetAsync<ServerSeasons>($"{ServerSessionApi.Leaderboards}/seasons") is { Seasons: not null } s
            ? (s.Current, [.. s.Seasons.Select(x => new Season(x.No, DateTimeOffset.FromUnixTimeSeconds(x.Start ?? 0), x.End is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : null))])
            : (0, []);

    public async Task<IReadOnlyList<Board>> BoardsAsync(string kind, int season) =>
        Species(kind) && await GetAsync<ServerSpeciesBoards>($"{ServerSessionApi.Leaderboards}/{kind}?season={season}") is { Boards: not null } b
            ? [.. b.Boards.Select(x => new Board(x.Species, x.Name, [.. x.Top.Select(Row)]))]
            : [];

    public async Task<IReadOnlyList<HallEntry>> HallAsync(int season) =>
        await GetAsync<ServerHall>($"{ServerSessionApi.Leaderboards}/halloffame?season={season}") is { Rows: not null } h
            ? [.. h.Rows.Select(x => new HallEntry(x.Species, x.Score is { } s ? Row(s) : null, x.Range is { } r ? Row(r) : null))]
            : [];

    public async Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season)
    {
        if (kind == Hunterscore)
            return await GetAsync<ServerBoard>($"{ServerSessionApi.Hunters}?page={page}") is { Rows: not null } b
                ? new Page<Ranked>([.. b.Rows.Select(r => new Ranked(r.Rank, r.Handle, r.Hunterscore, Avatar: Avatars.Slot(r.Avatar)))], b.Page, b.Pages, b.Total)
                : new Page<Ranked>([], 1, 1);
        if (kind is "lanebandit" or "bustthrough")
            return await GetAsync<ServerGameBoard>($"{ServerSessionApi.Leaderboards}/{kind}?page={page}") is { Rows: not null } g
                ? new Page<Ranked>([.. g.Rows.Select(r => new Ranked(r.Rank, r.Handle, r.Value, Avatar: Avatars.Slot(r.Avatar)))], g.Page, g.Pages, g.Total)
                : new Page<Ranked>([], 1, 1);
        if (Species(kind) && await GetAsync<ServerRowsPage>($"{ServerSessionApi.Leaderboards}/{kind}/{id}?season={season}&page={page}") is { Rows: not null } s)
            return new Page<Ranked>([.. s.Rows.Select(Row)], s.Page, s.Pages, s.Total);
        return new Page<Ranked>([], 1, 1);
    }

    public async Task<Ranked?> MyRankAsync(string kind, int id, int season)
    {
        if (cookie.Value is null) return null;
        if (kind == Hunterscore)
            return await GetAsync<ServerAccount>(ServerSessionApi.Me) is { Handle.Length: > 0 } me ? new Ranked(me.Rank, me.Handle, me.Hunterscore, Avatar: Avatars.Slot(me.Avatar)) : null;
        // the server keeps no place on the minigame boards
        return Species(kind) && await GetAsync<ServerRow>($"{ServerSessionApi.Leaderboards}/{kind}/{id}/me?season={season}") is { Handle.Length: > 0 } r ? Row(r) : null;
    }

    /// <summary>The JSON of a 200; null for any other answer or none (a 401, an expired cookie, and a 404, not ranked, are not worth a warning).</summary>
    async Task<T?> GetAsync<T>(string path) where T : class
    {
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            if (response.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound))
                log.LogWarning("The server answered {Path} with {Status}: an empty board", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): an empty board", path, e.Message);
        }
        return null;
    }
}
