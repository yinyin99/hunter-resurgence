using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's trophy lodges (<see cref="ServerAccountApi.Lodges"/>) and feeders (<see cref="ServerAccountApi.Feeders"/>) from the community server. Without a session, or when the server does not answer, there are none and a change fails.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerGameManagementService(HttpClient http, ServerCookie cookie, ILogger<ServerGameManagementService> log) : IGameManagementService
{
    static readonly FeederList NoFeeders = new([], 0, new(0, 0));

    public async Task<IReadOnlyList<LodgeRow>> LodgesAsync() => (await GetAsync<LodgeList>(ServerAccountApi.Lodges))?.Lodges ?? [];

    public async Task<FeederList> FeedersAsync() => await GetAsync<FeederList>(ServerAccountApi.Feeders) ?? NoFeeders;

    public async Task<Result> SaveLodgeAsync(LodgeRow lodge, string name, bool locked) =>
        (await SendAsync<object>(HttpMethod.Put, $"{ServerAccountApi.Lodges}/{Uri.EscapeDataString(lodge.Id)}/settings", new { name, locked, style_id = lodge.StyleId }, "GM.SaveFailed")).Ok
            ? Result.Success : Result.Fail("GM.SaveFailed");

    public Task<Result<RefillReply>> RefillAsync(string id, string currency, int maxAmount) =>
        SendAsync<RefillReply>(HttpMethod.Post, $"{ServerAccountApi.Feeders}/{Uri.EscapeDataString(id)}/refill", new { currency, max_amount = maxAmount }, "GM.RefillFailed");

    async Task<T?> GetAsync<T>(string path) where T : class
    {
        if (cookie.Value is not { } sent) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            log.LogWarning("The server answered {Path} with {Status}: nothing", path, (int)response.StatusCode);
            cookie.Refused(response, sent);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): nothing", path, e.Message);
        }
        return null;
    }

    async Task<Result<T>> SendAsync<T>(HttpMethod method, string path, object body, string failed) where T : class
    {
        if (cookie.Value is null) return Result<T>.Fail(failed);
        try
        {
            using var request = cookie.Request(method, path);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result<T>.Success(typeof(T) == typeof(object) ? null! : await response.Content.ReadFromJsonAsync<T>() ?? throw new System.Text.Json.JsonException("empty"));
            log.LogWarning("The server refused {Path} ({Status})", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", path, e.Message);
        }
        return Result<T>.Fail(failed);
    }
}
