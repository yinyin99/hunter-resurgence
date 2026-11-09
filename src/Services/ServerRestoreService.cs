using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The player's backup and data on the community server behind the same domain, for the browser whose cookie <see cref="ServerCookie"/> holds.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off, and a timeout long enough for an upload (the sign-in's is 5 s).</param>
public sealed class ServerRestoreService(HttpClient http, ServerCookie cookie, ILogger<ServerRestoreService> log) : IRestoreService
{
    sealed record StatusJson(long? RestoredUserId, KeptZip[]? Review, RestoreDecision[]? Decisions);

    sealed record ResultJson(string? Status, string? Message, string? Report, string? Id);

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = cookie.Request(method, path);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
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
            return json is null ? null : new RestoreStatus(json.RestoredUserId, json.Review ?? [], json.Decisions ?? []);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.Restore, e.Message);
            return null;
        }
    }

    /// <summary>The zip in parts of <see cref="ServerAccountApi.PartBytes"/>, each request under a proxy's limit: start, the parts in order, finish. Each part is
    /// streamed from the browser's file as it comes, never held whole.</summary>
    public async Task<RestoreResult> UploadAsync(Stream zip, long length)
    {
        if (cookie.Value is null) return new("signed_out", "Sign in first.");
        try
        {
            var (started, id) = await PostAsync(ServerAccountApi.Restore + "/start", null);
            if (started.Status != "started" || id is not { Length: > 0 }) return started;
            for (var (part, sent) = (0, 0L); sent < length; part++, sent += ServerAccountApi.PartBytes)
            {
                var (received, _) = await PostAsync($"{ServerAccountApi.Restore}/{id}/{part}", new Part(zip, Math.Min(ServerAccountApi.PartBytes, length - sent)));
                if (received.Status != "received") return received;
            }
            return (await PostAsync($"{ServerAccountApi.Restore}/{id}/finish", null)).Result;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            log.LogWarning("The server did not take the upload ({Reason})", e.Message);
            return new("unavailable", "The server did not answer.");
        }
    }

    async Task<(RestoreResult Result, string? Id)> PostAsync(string path, HttpContent? content)
    {
        using var request = Request(HttpMethod.Post, path);
        request.Content = content;
        if (content is not null) content.Headers.ContentType = new("application/zip");
        using var response = await http.SendAsync(request);
        ResultJson? json = null;
        try { json = await response.Content.ReadFromJsonAsync<ResultJson>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { }  // the server's own limits answer without JSON
        if (json?.Status is { Length: > 0 } status) return (new(status, json.Message ?? "", json.Report), json.Id);
        return (response.StatusCode switch
        {
            HttpStatusCode.RequestEntityTooLarge => new("too_large", "This file is too large."),
            HttpStatusCode.TooManyRequests => new("busy", "Try again in a minute."),
            HttpStatusCode.Unauthorized => new("signed_out", "Sign in first."),
            _ => new("unavailable", "The server did not answer."),
        }, null);
    }

    /// <summary>The next <paramref name="length"/> bytes of the zip as one request's body.</summary>
    sealed class Part(Stream zip, long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buf = new byte[81920];
            for (var left = length; left > 0;)
            {
                var n = await zip.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)));
                if (n == 0) throw new IOException("the file ended before its size");
                await stream.WriteAsync(buf.AsMemory(0, n));
                left -= n;
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
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
