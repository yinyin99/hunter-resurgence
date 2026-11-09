using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The friends of the signed-in player of the community server (<see cref="ServerAccountApi.Friends"/>): the list, the requests to answer, the requests sent, the search
/// and the changes. The server keeps no time for a request, so the requests come by handle. Without a session, or when the server does not answer, there are no friends and a
/// change fails.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerFriendsService(HttpClient http, ServerCookie cookie, ILogger<ServerFriendsService> log) : IFriendsService
{
    public async Task<IReadOnlyList<Friend>> FriendsAsync() => [.. (await ListAsync())?.Friends.Select(ToFriend) ?? []];

    public async Task<IReadOnlyList<Friend>> SearchAsync(string text) =>
        [.. (await GetAsync<ServerPlayers>($"{ServerAccountApi.FriendSearch}?text={Uri.EscapeDataString(text)}"))?.Hunters?.Select(ToFriend) ?? []];

    public async Task<IReadOnlyList<string>> RequestsAsync() => [.. (await ListAsync())?.Requests.Select(p => p.Handle) ?? []];

    public async Task<IReadOnlyList<string>> PendingAsync() => [.. (await ListAsync())?.Pending.Select(p => p.Handle) ?? []];

    // the server asks, or accepts the other's open request: one call for both
    public Task<Result> SendRequestAsync(string handle) => ChangeAsync("add", handle);
    public Task<Result> AcceptAsync(string handle) => ChangeAsync("add", handle);
    public Task<Result> DeclineAsync(string handle) => ChangeAsync("remove", handle);
    public Task<Result> RemoveAsync(string handle) => ChangeAsync("remove", handle);

    static Friend ToFriend(ServerPlayer p) => new(p.Handle, p.Online);

    async Task<ServerFriends?> ListAsync() => await GetAsync<ServerFriends>(ServerAccountApi.Friends) is { Friends: not null, Requests: not null, Pending: not null } f ? f : null;

    async Task<T?> GetAsync<T>(string path) where T : class
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            log.LogWarning("The server answered {Path} with {Status}: no friends", path.Split('?')[0], (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no friends", path.Split('?')[0], e.Message);
        }
        return null;
    }

    async Task<Result> ChangeAsync(string action, string handle)
    {
        var path = $"{ServerAccountApi.Friends}/{action}";
        if (cookie.Value is null) return Result.Fail("Store.Oops");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, path);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("handle", handle)]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result.Success;
            log.LogWarning("The server refused {Path} ({Status})", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", path, e.Message);
        }
        return Result.Fail("Store.Oops");
    }
}
