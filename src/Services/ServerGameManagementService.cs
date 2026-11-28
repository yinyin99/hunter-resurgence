using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's trophy lodges (<see cref="ServerAccountApi.Lodges"/>) and feeders (<see cref="ServerAccountApi.Feeders"/>) from the community server. Without a session, or when the server does not answer, there are none and a change fails.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerGameManagementService(HttpClient http, ServerCookie cookie, ILogger<ServerGameManagementService> log) : IGameManagementService
{
    static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);
    static readonly FeederList NoFeeders = new([], 0, new(0, 0));

    public async Task<IReadOnlyList<LodgeRow>> LodgesAsync() => (await GetAsync<LodgeList>(ServerAccountApi.Lodges))?.Lodges ?? [];

    public async Task<FeederList> FeedersAsync() => await GetAsync<FeederList>(ServerAccountApi.Feeders) ?? NoFeeders;

    public async Task<Result> SaveLodgeAsync(LodgeRow lodge, string name, bool locked) =>
        await SendAsync(HttpMethod.Put, $"{ServerAccountApi.Lodges}/{Uri.EscapeDataString(lodge.Id)}/settings", new { name, locked, style_id = lodge.StyleId }) is not null ? Result.Success : Result.Fail("GM.SaveFailed");

    public async Task<Result<RefillReply>> RefillAsync(string id, string currency, int maxAmount)
    {
        try
        {
            if (await SendAsync(HttpMethod.Post, $"{ServerAccountApi.Feeders}/{Uri.EscapeDataString(id)}/refill", new { currency, max_amount = maxAmount }) is { } json
                && System.Text.Json.JsonSerializer.Deserialize<RefillReply>(json, Web) is { Success: true } reply)
                return Result<RefillReply>.Success(reply);
        }
        catch (System.Text.Json.JsonException e)
        {
            log.LogWarning("The server's refill reply was not readable ({Reason})", e.Message);
        }
        return Result<RefillReply>.Fail("GM.RefillFailed");
    }

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

    /// <summary>A JSON change as the signed-in player: the reply's body when the server accepted it, else null (signed out, refused, no answer).</summary>
    async Task<string?> SendAsync(HttpMethod method, string path, object body)
    {
        if (cookie.Value is not { } sent) return null;
        try
        {
            using var request = cookie.Request(method, path);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
            log.LogWarning("The server refused {Path} ({Status})", path, (int)response.StatusCode);
            cookie.Refused(response, sent);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", path, e.Message);
        }
        return null;
    }
}
