using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The community server's public <see cref="Path"/>: the counts of hunters in a hunt and of open multiplayer games. The server caches them for 30 seconds.</summary>
/// <param name="http">Base address is the server; a failure of the network is none.</param>
/// <param name="cookie">Only its host is used: the path is public but the server answers it on the website's host only.</param>
public sealed class ServerOnlineService(HttpClient http, ServerCookie cookie, ILogger<ServerOnlineService> log) : IOnlineService
{
    /// <summary>GET: 200 <see cref="OnlineCounts"/> as <c>{"players": n, "games": n}</c>, no sign-in.</summary>
    public const string Path = "/stats/online";

    public async Task<OnlineCounts?> NowAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path);
            if (cookie.Host is not null) request.Headers.Host = cookie.Host;
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<OnlineCounts>() is { Players: >= 0, Games: >= 0 } now) return now;
            log.LogWarning("The server answered {Path} with {Status} or an unusable reply: no counts", Path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no counts", Path, e.Message);
        }
        return null;
    }
}
