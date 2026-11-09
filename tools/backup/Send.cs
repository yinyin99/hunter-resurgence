using System.Text.Json;

namespace Backup;

/// <summary>"Send to the community server": signs in with the server's code-link flow (docs/server.md#sign-in: the tool shows a code, the player signs in with Steam in the browser, the tool polls),
/// then uploads a saved zip in parts with that session (POST /account/restore/start, /&lt;id&gt;/&lt;part&gt;, /&lt;id&gt;/finish). Runs only when the player presses the button.</summary>
static class Send
{
    /// <summary>The server's limit for one zip (Uploads.MaxZipBytes): a larger file is not sent at all.</summary>
    public const long MaxZipBytes = 1L << 30;
    /// <summary>The server's limit for one request (Uploads.MaxPartBytes), under a proxy's 100 MB.</summary>
    public const int PartBytes = 90 << 20;
    /// <summary>Tries of one part before the send gives up: a part that fails on the way is sent again.</summary>
    const int Tries = 3;

    static readonly HashSet<string> Kinds = ["review", "already", "taken", "invalid", "busy", "too_large", "signed_out"];

    /// <summary>Kind: the server's status (review, already, taken, invalid, busy, too_large, signed_out) or the tool's own: timeout, error. Message: the server's reason, English, or the network error.</summary>
    public sealed record Result(string Kind, string Message = "");

    /// <summary>showCode gets the code the player compares and the address to open in the browser; sending gets the part being sent and the number of parts, from 1.
    /// Not-ok results are returned, never thrown; cancellation throws.</summary>
    public static async Task<Result> Run(HttpClient http, string server, string zipPath, Action<string, string> showCode, CancellationToken ct, TimeSpan? interval = null, TimeSpan? giveUp = null,
        Action<int, int>? sending = null, int partBytes = PartBytes)
    {
        try
        {
            var size = new FileInfo(zipPath).Length;
            if (size > MaxZipBytes) return new("too_large", "A backup zip is at most 1 GiB.");
            var root = server.TrimEnd('/');

            var (_, link, _) = await Call(http, HttpMethod.Post, root + "/auth/link", null, null, ct);
            if (Str(link, "code") is not { Length: > 0 } code || Str(link, "userCode") is not { } userCode || Str(link, "url") is not { } url
                || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Host != new Uri(root).Host) // the address to open must be the server's own
                return new("error", "The server did not give a sign-in code.");
            showCode(userCode, url);

            var wait = TimeSpan.FromSeconds(link!.TryGetValue("expires", out var ex) && ex.TryGetInt64(out var s) ? s : 300);
            if (giveUp is { } g && g < wait) wait = g;
            var until = DateTime.UtcNow + wait;
            string? cookie = null;
            while (cookie == null)
            {
                if (DateTime.UtcNow >= until) return new("timeout");
                await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct);
                var (status, body, set) = await Call(http, HttpMethod.Get, root + "/auth/link/" + code, null, null, ct);
                if (status == 404) return new("timeout"); // the code ran out on the server
                if (Str(body, "status") == "ok") cookie = set ?? throw new HttpRequestException("the server did not set a session");
            }

            var (_, started, _) = await Call(http, HttpMethod.Post, root + "/account/restore/start", cookie, null, ct);
            if (Str(started, "status") != "started" || Str(started, "id") is not { Length: > 0 } id) return Outcome(started);
            await using var file = File.OpenRead(zipPath);
            var buf = new byte[Math.Min(size, partBytes)];
            var parts = (int)((size + partBytes - 1) / partBytes);
            for (var part = 0; part < parts; part++)
            {
                sending?.Invoke(part + 1, parts);
                var n = await file.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
                for (var tries = 1; ; tries++)
                {
                    Dictionary<string, JsonElement>? reply = null;
                    try
                    {
                        var content = new ByteArrayContent(buf, 0, n);
                        content.Headers.ContentType = new("application/zip");
                        (_, reply, _) = await Call(http, HttpMethod.Post, $"{root}/account/restore/{id}/{part}", cookie, content, ct);
                        if (Str(reply, "status") == "received") break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested && tries < Tries && e is HttpRequestException or TaskCanceledException or IOException) { }
                    if (Str(reply, "status") is not null || tries >= Tries) return Outcome(reply);  // the server's refusal is final; a reply that is not its (a proxy's page) is tried again
                    await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct);
                }
            }
            var (_, done, _) = await Call(http, HttpMethod.Post, $"{root}/account/restore/{id}/finish", cookie, null, ct);
            return Outcome(done);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            return new("error", e.Message);
        }
    }

    static Result Outcome(Dictionary<string, JsonElement>? reply) =>
        Str(reply, "status") is { } kind && Kinds.Contains(kind) ? new(kind, Str(reply, "message") ?? "") : new("error", "The server's reply was not understood.");

    /// <summary>One request; the answer's JSON (null if it is not JSON, e.g. a proxy's page) and the value of the session cookie it set.</summary>
    static async Task<(int Status, Dictionary<string, JsonElement>? Body, string? Cookie)> Call(HttpClient http, HttpMethod method, string url, string? cookie, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Add("X-Requested-With", "backup-tool"); // a state-changing request must show it is not a cross-site form
        if (cookie != null) req.Headers.Add("Cookie", cookie);
        using var res = await http.SendAsync(req, ct);
        var set = res.Headers.TryGetValues("Set-Cookie", out var all) ? all.FirstOrDefault(c => c.StartsWith("hr_session=", StringComparison.Ordinal))?.Split(';')[0] : null;
        Dictionary<string, JsonElement>? body = null;
        try { body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await res.Content.ReadAsStringAsync(ct)); }
        catch (JsonException) { }
        return ((int)res.StatusCode, body, set);
    }

    /// <summary>A string field of a reply, or null when the field is missing or not a string: an odd reply is "not understood", not a crash.</summary>
    static string? Str(Dictionary<string, JsonElement>? d, string k) => d != null && d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
