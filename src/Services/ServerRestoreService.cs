using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The player's backup and data on the community server behind the same domain, for the browser whose cookie <see cref="ServerCookie"/> holds.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off, and a timeout long enough for an upload (the sign-in's is 5 s).</param>
public sealed class ServerRestoreService(HttpClient http, ServerCookie cookie, ILogger<ServerRestoreService> log) : IRestoreService
{
    sealed record StatusJson(long? RestoredUserId, JsonElement[]? Review);

    sealed record ResultJson(string? Status, string? Message, string? Report);

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", $"{ServerSessionApi.Cookie}={cookie.Value}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (cookie.Host is not null) request.Headers.Host = cookie.Host;
        return request;
    }

    public async Task<RestoreStatus?> StatusAsync()
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = Request(HttpMethod.Get, ServerAccountApi.Restore);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadFromJsonAsync<StatusJson>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return json is null ? null : new RestoreStatus(json.RestoredUserId, json.Review?.Length ?? 0);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.Restore, e.Message);
            return null;
        }
    }

    public async Task<RestoreResult> UploadAsync(Stream zip, long length)
    {
        if (cookie.Value is null) return new("signed_out", "Sign in first.");
        try
        {
            using var request = Request(HttpMethod.Post, ServerAccountApi.Restore);
            request.Content = new StreamContent(zip);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            request.Content.Headers.ContentLength = length;
            using var response = await http.SendAsync(request);
            ResultJson? json = null;
            try { json = await response.Content.ReadFromJsonAsync<ResultJson>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { }  // the server's own limits answer without JSON
            if (json?.Status is { Length: > 0 } status) return new(status, json.Message ?? "", json.Report);
            return response.StatusCode switch
            {
                HttpStatusCode.RequestEntityTooLarge => new("too_large", "This file is too large."),
                HttpStatusCode.TooManyRequests => new("busy", "Try again in a minute."),
                HttpStatusCode.Unauthorized => new("signed_out", "Sign in first."),
                _ => new("unavailable", "The server did not answer."),
            };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not take the upload ({Reason})", e.Message);
            return new("unavailable", "The server did not answer.");
        }
    }

    public async Task<bool> DeleteAsync()
    {
        if (cookie.Value is null) return false;
        try
        {
            using var request = Request(HttpMethod.Post, ServerAccountApi.Delete);
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("confirm", "delete")]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) cookie.Value = null;
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.Delete, e.Message);
            return false;
        }
    }
}
