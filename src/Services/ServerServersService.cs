using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The launch map of the community server: the reserves, their starts and the tutorials are its catalogue (<see cref="ServerCatalogApi.Reserves"/>), the loadouts are the player's
/// (<see cref="ServerAccountApi.Loadouts"/>), and a start asks the server to open the hunt (<see cref="ServerLaunchApi.Launch"/>) and hands the launch URL it answers to
/// <see cref="ILauncherBridge.StartGame"/>. The open multiplayer games are the server's list (<see cref="ServerCatalogApi.Games"/>); hosting and joining one are starts with <c>mp=host</c> and <c>mp=join</c>, and a
/// refused join fails with the message of its reason. A server that does not answer is an empty map and no games, never the mock's.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
/// <param name="cookie">The catalogue is the same for everyone and is asked without the session cookie; the loadouts and a start carry it.</param>
/// <param name="settings">The game's language (<see cref="IGameSettingsService.LanguageAsync"/>) goes with every start, as the form's <c>lang</c>.</param>
public sealed class ServerServersService(HttpClient http, ServerCookie cookie, ServerCache<ServerLaunchCatalogue> cache, ILauncherBridge bridge, IGameSettingsService settings, ILogger<ServerServersService> log) : IServersService
{
    const string Failed = "Launch.Failed";

    public async Task<IReadOnlyList<Reserve>> ReservesAsync() => [.. (await LoadAsync()).Reserves.Select(r => new Reserve(r.Id, r.Name))];

    public async Task<IReadOnlyList<MapMarker>> MarkersAsync(int reserve) => [.. await StartsAsync(reserve), .. await PlacedAsync(reserve)];

    async Task<IEnumerable<MapMarker>> StartsAsync(int reserve) =>
        (await LoadAsync()).Reserves.Where(r => r.Id == reserve).SelectMany(r => r.Starts).Select(s => Marker(s)).OfType<MapMarker>();

    /// <summary>The starts of the game's reserve and the host's placed items there (<see cref="ServerCatalogApi.GamePlaced"/>): the host's tents can be picked as the start (<see cref="JoinAsync"/>), its other items are shown only (<see cref="MapMarker.Only"/>). Without markers when the server does not give them, with the message key of
    /// the reason: the 403's <c>why</c> (<see cref="ServerPlacedRefusal"/>; a password it has was not given or is wrong, <see cref="ServerCatalogApi.GamePassword"/>, percent-encoded), else the generic <c>Launch.Failed</c> (the game is gone, the player is signed out, the server does not answer).</summary>
    public async Task<JoinMap> JoinMarkersAsync(long server, int reserve, string? password)
    {
        if (cookie.Value is null) return new(null, Failed);
        var path = ServerCatalogApi.GamePlaced(server);
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            if (password is { Length: > 0 }) request.Headers.Add(ServerCatalogApi.GamePassword, Uri.EscapeDataString(password));
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerPlaced>() is { Items: not null } placed)
                return new([.. await StartsAsync(reserve), .. placed.Items.Where(i => i.Reserve == reserve && i.Id is > 0 and <= int.MaxValue).Select(Placed).OfType<MapMarker>().Select(m => m.Kind == MarkerKind.Tent ? m : m with { Only = true })]);
            log.LogWarning("The server answered {Path} with {Status}: no map of the game", path, (int)response.StatusCode);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && await response.Content.ReadFromJsonAsync<ServerPlacedRefusal>() is { Why: { } why } && PlacedRefusals.TryGetValue(why, out var key))
                return new(null, key);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no map of the game", path, e.Message);
        }
        return new(null, Failed);
    }

    /// <summary>The 403's reasons (<see cref="ServerPlacedRefusal"/>) and the message each has, the join's own where it has one.</summary>
    static readonly Dictionary<string, string> PlacedRefusals = new()
    {
        ["password"] = "Join.WrongPassword", ["full"] = "Join.Full", ["friends"] = "Join.NotAllowed", ["hunterscore"] = "Join.NotAllowed", ["starting"] = "Join.HostNotReady", ["network"] = "Join.HostNetwork", ["banned"] = "Join.Banned",
    };

    /// <summary>The open games of the server (<see cref="ServerCatalogApi.Games"/>, for a signed-in player: the browser's host and the session cookie); none when signed out (the server is not asked) or when it does not answer. A player is placed on the map where the server says (<c>mapX</c>, <c>mapY</c>), and not when it does not.</summary>
    public async Task<IReadOnlyList<GameServer>> ServersAsync()
    {
        if (cookie.Value is null) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerCatalogApi.Games);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerGame[]>() is { } games)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return [.. games.Where(g => g.Reserve is not null).Select(g => Game(g, now))];
            }
            log.LogWarning("The server answered {Path} with {Status}: no games", ServerCatalogApi.Games, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no games", ServerCatalogApi.Games, e.Message);
        }
        return [];
    }

    /// <summary>The host is the game's own handle; the game's age is the time since it was created. A host can set any Hunter Score limit: it is kept within 0 to 99999, and no limit is that whole range, as the list shows it. A player's place is the server's percent of the map, as sent.</summary>
    static GameServer Game(ServerGame g, long now) => new(g.Id, g.Name ?? "", g.Description ?? "", Whole(g.Reserve!.Id), g.Clients?.Length ?? 0, Whole(g.Max), g.Handle ?? "", g.Password, g.Country ?? "", Distance.Short,
        Whole(now - g.Started), Whole(g.Min ?? 0, 99999), Whole(g.MaxScore ?? 99999, 99999),
        [.. (g.Clients ?? []).Select(c => new GamePlayer(c.Handle ?? "", Whole(c.Hunterscore), c.MapX, c.MapY, Avatars.Slot(c.Avatar)))], g.Why, g.Event, g.EventDiffers);

    static int Whole(long n, int max = int.MaxValue) => (int)Math.Clamp(n, 0, max);

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

    /// <summary>The player's own placed items in a reserve (<see cref="ServerAccountApi.Placed"/>); none when signed out or when the server does not answer.</summary>
    async Task<MapMarker[]> PlacedAsync(int reserve)
    {
        if (cookie.Value is null) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Placed);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerPlaced>() is { Items: not null } placed)
                return [.. placed.Items.Where(i => i.Reserve == reserve && i.Id is > 0 and <= int.MaxValue).Select(Placed).OfType<MapMarker>()];
            log.LogWarning("The server answered {Path} with {Status}: no placed items", ServerAccountApi.Placed, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no placed items", ServerAccountApi.Placed, e.Message);
        }
        return [];
    }

    /// <summary>A placed item's marker id is its negated guid, which no location number is.</summary>
    static MapMarker? Placed(ServerPlacedItem i) => i.Kind switch
    {
        "tent" => new MapMarker(-(int)i.Id, MarkerKind.Tent, i.Name, i.X, i.Y),
        "caller" => new MapMarker(-(int)i.Id, MarkerKind.Caller, i.Name, i.X, i.Y),
        "groundblind" => new MapMarker(-(int)i.Id, MarkerKind.GroundBlind, i.Name, i.X, i.Y),
        "treestand" => new MapMarker(-(int)i.Id, MarkerKind.TreeStand, i.Name, i.X, i.Y),
        "tripod" => new MapMarker(-(int)i.Id, MarkerKind.Tripod, i.Name, i.X, i.Y),
        "deployabletower" => new MapMarker(-(int)i.Id, MarkerKind.DeployableTower, i.Name, i.X, i.Y),
        "climbing" => new MapMarker(-(int)i.Id, MarkerKind.Climbing, i.Name, i.X, i.Y),
        _ => null,
    };

    public Task<Result> StartSingleAsync(int reserve, int marker, int minutes, int loadout) =>
        LaunchAsync([("reserve", $"{reserve}"), Start(marker), ("time", $"{minutes}"), ("loadout", $"{loadout}")]);

    public Task<Result> StartTutorialAsync(int id) => LaunchAsync([("tutorial", $"{id}")]);

    public Task<Result> CreateAsync(NewGame game, int reserve, int marker, int minutes, int loadout)
    {
        List<(string, string)> form = [("mp", "host"), ("reserve", $"{reserve}"), Start(marker), ("time", $"{minutes}"), ("loadout", $"{loadout}"),
            ("name", game.Name), ("description", game.Description), ("public_game", game.FriendsOnly ? "0" : "1"), ("free_players", "1"), ("max_players", $"{game.MaxPlayers}")];
        if (game.MinScore is { } min && game.MaxScore is { } max) form.AddRange([("hs_min", $"{min}"), ("hs_max", $"{max}")]);
        if (game.Password is { Length: > 0 } password) form.Add(("password", password));
        return LaunchAsync([.. form]);
    }

    /// <summary>The game has its reserve and start time, so a join sends neither. A tent marker of a join map (a negative id, the negated guid) is a tent of the game's host: <c>host_tent</c> says so, and the server checks it.</summary>
    public Task<Result> JoinAsync(long server, int marker, string? password, int loadout)
    {
        List<(string, string)> form = [("mp", "join"), ("network_id", $"{server}"), Start(marker), ("loadout", $"{loadout}")];
        if (marker < 0) form.Add(("host_tent", "1"));
        if (password is { Length: > 0 }) form.Add(("password", password));
        return LaunchAsync([.. form]);
    }

    static readonly Dictionary<string, string> Refusals = new() { ["password"] = "Join.WrongPassword", ["full"] = "Join.Full", ["not_allowed"] = "Join.NotAllowed", ["host_not_ready"] = "Join.HostNotReady", ["host_network"] = "Join.HostNetwork", ["banned"] = "Join.Banned" };

    /// <summary>A placed tent's marker id is its negated guid (<see cref="Placed"/>).</summary>
    static (string, string) Start(int marker) => marker < 0 ? ("tent", $"{-marker}") : ("lodge", $"{marker}");

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
            request.Content = new FormUrlEncodedContent(form.Append(("lang", await settings.LanguageAsync())).Select(f => KeyValuePair.Create(f.Item1, f.Item2)));  // the game's language rides on every start
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerLaunch>() is { Url.Length: > 0 } launch)
                // off the UI thread: the launcher may wait for Steam to sign in
                return await Task.Run(() => bridge.StartGame(launch.Url));
            log.LogWarning("The server refused {Path} ({Status})", ServerLaunchApi.Launch, (int)response.StatusCode);
            // a refused join says why; any other refusal is the generic failure
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && await response.Content.ReadFromJsonAsync<ServerRefusal>() is { Error: { } why } && Refusals.TryGetValue(why, out var key))
                return Result.Fail(key);
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
