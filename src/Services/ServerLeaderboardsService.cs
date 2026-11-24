using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The boards of the community server: the species boards by season and their hall of fame, the hunterscore and the two minigames' (the best scores, restored or played).
/// It has no antler boards (the live site does not offer them).</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off; a failure of the network is <see cref="BoardUnavailableException"/> (the launcher gives it time: the server may be computing).</param>
public sealed class ServerLeaderboardsService(HttpClient http, ServerCookie cookie, ILogger<ServerLeaderboardsService> log) : ILeaderboardsService
{
    const string Hunterscore = "hunterscore";
    /// <summary>The boards the server has, in the order of the tabs; the live site offers these only. <c>hunting</c> is the hunting statistics, a page of the boards' strip.</summary>
    public static readonly string[] Kinds = ["score", "range", Hunterscore, "halloffame", "lanebandit", "bustthrough", "hunting"];

    static bool Species(string kind) => kind is "score" or "range";

    static Ranked Row(ServerRow r) => new(r.Rank, r.Handle, r.Value, r.Kill, Avatars.Slot(r.Avatar), r.WeaponName ?? "", r.Ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.Ts) : null);

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

    public async Task<HuntingStats> HuntingAsync() =>
        await GetAsync<HuntingStats>(ServerSessionApi.HuntingStatistics) is { Reserves: not null, Species: not null } h ? h : new([], []);

    public async Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season)
    {
        if (kind == Hunterscore)
        {
            static Ranked Hunter(ServerHunter r) => new(r.Rank, r.Handle, r.Hunterscore, Avatar: Avatars.Slot(r.Avatar));
            return await GetAsync<ServerBoard>($"{ServerSessionApi.Hunters}?page={page}") is { Rows: not null } b
                ? new Page<Ranked>([.. b.Rows.Select(Hunter)], b.Page, b.Pages, Around: [.. (b.Around ?? []).Select(Hunter)])
                : new Page<Ranked>([], 1, 1);
        }
        if (kind is "lanebandit" or "bustthrough")
        {
            static Ranked Game(ServerGameRow r) => new(r.Rank, r.Handle, r.Value, Avatar: Avatars.Slot(r.Avatar));
            return await GetAsync<ServerGameBoard>($"{ServerSessionApi.Leaderboards}/{kind}?page={page}") is { Rows: not null } g
                ? new Page<Ranked>([.. g.Rows.Select(Game)], g.Page, g.Pages, Around: [.. (g.Around ?? []).Select(Game)])
                : new Page<Ranked>([], 1, 1);
        }
        if (Species(kind) && await GetAsync<ServerRowsPage>($"{ServerSessionApi.Leaderboards}/{kind}/{id}?season={season}&page={page}") is { Rows: not null } s)
            return new Page<Ranked>([.. s.Rows.Select(Row)], s.Page, s.Pages, Around: [.. (s.Around ?? []).Select(Row)]);
        return new Page<Ranked>([], 1, 1);
    }

    public async Task<Ranked?> MyRankAsync(string kind, int id, int season)
    {
        if (cookie.Value is null) return null;
        if (kind == Hunterscore)
            return await GetAsync<ServerAccount>(ServerSessionApi.Me, mine: true) is { Handle.Length: > 0 } me ? new Ranked(me.Rank, me.Handle, me.Hunterscore, Avatar: Avatars.Slot(me.Avatar)) : null;
        // the server keeps no place on the minigame boards
        return Species(kind) && await GetAsync<ServerRow>($"{ServerSessionApi.Leaderboards}/{kind}/{id}/me?season={season}", mine: true) is { Handle.Length: > 0 } r ? Row(r) : null;
    }

    /// <summary>The JSON of a 200; null for a 401 or a 404 (signed out, not ranked: answers). Any other answer, or none, throws <see cref="BoardUnavailableException"/>,
    /// except for <paramref name="mine"/>, the player's own place: a place that cannot be read is no place.</summary>
    async Task<T?> GetAsync<T>(string path, bool mine = false) where T : class
    {
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"the server answered {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("A board request failed ({Reason})", e.Message);
            return mine ? null : throw new BoardUnavailableException(e.Message, e);
        }
    }
}
