using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The launch map of the community server: the reserves, their starts and the tutorials are its catalogue (<see cref="ServerCatalogApi.Reserves"/>), the loadouts are the player's
/// (<see cref="ServerAccountApi.Loadouts"/>), and a start asks the server to open the hunt (<see cref="ServerLaunchApi.Launch"/>) and hands the launch address it answers to
/// <see cref="ILauncherBridge.StartGame"/>. The server has no multiplayer: no game is listed and creating or joining one fails. A server that does not answer is an empty map, never the mock's.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
/// <param name="cookie">The catalogue is the same for everyone and is asked without the session cookie; the loadouts and a start carry it.</param>
public sealed class ServerServersService(HttpClient http, ServerCookie cookie, ServerCache<ServerLaunchCatalogue> cache, ILauncherBridge bridge, ILogger<ServerServersService> log) : IServersService
{
    const string Failed = "Launch.Failed";

    public async Task<IReadOnlyList<Reserve>> ReservesAsync() => [.. (await LoadAsync()).Reserves.Select(r => new Reserve(r.Id, r.Name))];

    public async Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve) =>
        [.. (await LoadAsync()).Reserves.Where(r => r.Id == reserve).SelectMany(r => r.Starts).Select(s => Marker(s)).OfType<MapMarker>()];

    public Task<IReadOnlyList<GameServer>> ServersAsync() => Task.FromResult<IReadOnlyList<GameServer>>([]);

    public async Task<IReadOnlyList<Tutorial>> TutorialsAsync() => [.. (await LoadAsync()).Tutorials.Select(t => new Tutorial(t.Id, t.Name, "", t.Reserve))];

    public async Task<IReadOnlyList<Loadout>> LoadoutsAsync()
    {
        if (cookie.Value is null) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Loadouts);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerLoadout[]>() is { } loadouts)
                return [.. loadouts.Select(l => new Loadout(l.Id, l.Name))];
            log.LogWarning("The server answered {Path} with {Status}: no loadouts", ServerAccountApi.Loadouts, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no loadouts", ServerAccountApi.Loadouts, e.Message);
        }
        return [];
    }

    public Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout) =>
        LaunchAsync([("reserve", $"{reserve}"), ("lodge", $"{marker}"), ("time", $"{minutes}"), ("loadout", $"{loadout}")]);

    public Task<Result> StartTutorialAsync(int id) => LaunchAsync([("tutorial", $"{id}")]);

    public Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout) => Task.FromResult(Result.Fail(Failed));

    public Task<Result> JoinAsync(int server, int marker, string? password, int loadout) => Task.FromResult(Result.Fail(Failed));

    static MapMarker? Marker(ServerStart s) => s.Kind switch
    {
        "lodge" => new MapMarker(s.Id, MarkerKind.Lodge, s.Name, s.X, s.Y),
        "campsite" => new MapMarker(s.Id, MarkerKind.Campsite, s.Name, s.X, s.Y),
        "tower" => new MapMarker(s.Id, MarkerKind.Tower, s.Name, s.X, s.Y),
        "interest" => new MapMarker(s.Id, MarkerKind.Interest, s.Name, s.X, s.Y),
        _ => null,
    };

    async Task<Result> LaunchAsync((string, string)[] form)
    {
        if (cookie.Value is null) return Result.Fail(Failed);
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerLaunchApi.Launch);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Item1, f.Item2)));
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerLaunch>() is { Url.Length: > 0 } launch)
            {
                bridge.StartGame(launch.Url);
                return Result.Success;
            }
            log.LogWarning("The server refused {Path} ({Status})", ServerLaunchApi.Launch, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerLaunchApi.Launch, e.Message);
        }
        return Result.Fail(Failed);
    }

    async Task<ServerLaunchCatalogue> LoadAsync() =>
        await cache.LoadAsync(http, cookie.Host, ServerCatalogApi.Reserves, c => c is { Reserves: not null, Tutorials: not null } && c.Reserves.All(r => r.Starts is not null), log)
        ?? new([], []);
}
