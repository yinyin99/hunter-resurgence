using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's referral page and the entering of a code on the community server (<see cref="ServerAccountApi.Referral"/>). Without a session, or when the server does not answer, there is no page.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerReferralService(HttpClient http, ServerCookie cookie, ILogger<ServerReferralService> log) : IReferralService
{
    /// <summary>The reasons the server refuses a code with (409 <c>status</c>); each has a text <c>Referral.Error.&lt;reason&gt;</c>.</summary>
    static readonly string[] Reasons = ["unknown", "own", "loop", "already", "expired", "banned"];

    sealed record Refusal(string? Status);

    public async Task<Referral?> GetAsync()
    {
        if (cookie.Value is not { } sent) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Referral);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<Referral>();
            log.LogWarning("The server answered {Path} with {Status}: no referral page", ServerAccountApi.Referral, (int)response.StatusCode);
            cookie.Refused(response, sent);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no referral page", ServerAccountApi.Referral, e.Message);
        }
        return null;
    }

    /// <param name="code">Holds the player's input: sent, never logged.</param>
    public async Task<Result<ReferralEntered>> EnterAsync(string code)
    {
        if (cookie.Value is not { } sent) return Result<ReferralEntered>.Fail("Referral.Error.failed");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.ReferralCode);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = JsonContent.Create(new { code });
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ReferralEntered>() is { } entered) return Result<ReferralEntered>.Success(entered);
            // a refused code is the player's to hear about, not a warning
            if (response.StatusCode == HttpStatusCode.Conflict && (await response.Content.ReadFromJsonAsync<Refusal>())?.Status is { } reason && Reasons.Contains(reason))
                return Result<ReferralEntered>.Fail("Referral.Error." + reason);
            log.LogWarning("The server refused {Path} ({Status})", ServerAccountApi.ReferralCode, (int)response.StatusCode);
            cookie.Refused(response, sent);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.ReferralCode, e.Message);
        }
        return Result<ReferralEntered>.Fail("Referral.Error.failed");
    }
}
