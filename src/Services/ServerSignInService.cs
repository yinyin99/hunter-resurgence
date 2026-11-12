using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>Reads the sign-in in progress from the community server with this browser's login cookie (docs/website.md#sign-in).</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off (the cookies are the player's, sent by hand).</param>
public sealed class ServerSignInService(HttpClient http, ServerCookie cookie, ILogger<ServerSignInService> log) : ISignInService
{
    public async Task<SignInStep?> PendingAsync()
    {
        if (cookie.Login is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerSessionApi.Pending);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SignInStep>() : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerSessionApi.Pending, e.Message);
            return null;
        }
    }
}
