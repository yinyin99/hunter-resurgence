using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>Asks the community server whether this is the account's first restore offer (<see cref="ServerAccountApi.RestoreOffer"/>); the server remembers it with the account.</summary>
public sealed class ServerRestoreOfferService(HttpClient http, ServerCookie cookie, ILogger<ServerRestoreOfferService> log) : IRestoreOfferService
{
    public async Task<bool> FirstAsync()
    {
        if (cookie.Value is null) return false;
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.RestoreOffer);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerRestoreOffer>() is { } invite) return invite.First;
            log.LogWarning("The server answered the restore offer with {Status}: none", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer the restore offer ({Reason}): none", e.Message);
        }
        return false;
    }
}
