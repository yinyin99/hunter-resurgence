using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Backup;

/// <summary>"Send to the community server": signs in by the native-app flow (RFC 8252 loopback redirect, RFC 7636 PKCE; docs/server.md, "The loopback sign-in": the tool listens once on 127.0.0.1, the player signs in with Steam in the browser,
/// the browser comes back to the tool with a one-time code, which only this tool can trade for the session), then uploads a saved zip in parts with that session (POST /account/restore/start, /&lt;id&gt;/&lt;part&gt;, /&lt;id&gt;/finish). Runs only when the player presses the button.</summary>
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

    /// <summary>open gets the address to open in the player's browser (the sign-in), giveUp is how long the player has to sign in there (5 minutes); sending gets the part being sent and the number of parts, from 1.
    /// Not-ok results are returned, never thrown; cancellation throws.</summary>
    public static async Task<Result> Run(HttpClient http, string server, string zipPath, Action<string> open, CancellationToken ct, TimeSpan? interval = null, TimeSpan? giveUp = null,
        Action<int, int>? sending = null, int partBytes = PartBytes)
    {
        try
        {
            var size = new FileInfo(zipPath).Length;
            if (size > MaxZipBytes) return new("too_large", "A backup zip is at most 1 GiB.");
            var root = server.TrimEnd('/');

            var cookie = await SignIn(http, root, open, giveUp ?? TimeSpan.FromMinutes(5), ct);
            if (cookie == null) return new("timeout");

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
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or IOException or SocketException or UnauthorizedAccessException or UriFormatException)
        {
            return new("error", e.Message);
        }
    }

    /// <summary>The session cookie of the player who signed in in the browser, or null when the time ran out. The server redirects the browser to 127.0.0.1 on our port with the one-time code and our state;
    /// the code is worth nothing without the verifier, which never leaves this PC except in the exchange.</summary>
    static async Task<string?> SignIn(HttpClient http, string root, Action<string> open, TimeSpan wait, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(wait);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            open($"{root}/auth/app/start?challenge={challenge}&port={((IPEndPoint)listener.LocalEndpoint).Port}&state={state}");

            var code = await WaitForBrowser(listener, state, limit.Token);
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["verifier"] = verifier });
            var (_, _, set) = await Call(http, HttpMethod.Post, root + "/auth/app/exchange", null, form, ct);
            return set ?? throw new HttpRequestException("the server did not sign the tool in");
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested) { return null; }
        finally { listener.Stop(); }
    }

    /// <summary>The browser's way back: serves 127.0.0.1 until a request carries our <paramref name="state"/> and a code (any other request, a stray one or a page that guesses the port, gets a refusal and changes nothing).</summary>
    static async Task<string> WaitForBrowser(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            string? code = null;
            try
            {
                var stream = client.GetStream();
                var buf = new byte[4096];
                var n = 0;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        while (n < buf.Length && !Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n", StringComparison.Ordinal))
                            if (await stream.ReadAsync(buf.AsMemory(n), limit.Token) is var read and > 0) n += read; else break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // a silent connection (a browser's spare one)
                }
                var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
                if (line is ["GET", var target, _] && Uri.TryCreate("http://127.0.0.1" + target, UriKind.Absolute, out var url) && url.AbsolutePath == "/")
                {
                    var q = HttpUtility.ParseQueryString(url.Query);
                    if (q["state"] is { } given && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(state))) code = q["code"];
                }
                var (status, text) = code is { Length: > 0 } ? ("200 OK", "Signed in, you can close this tab.") : ("400 Bad Request", "This is not the sign-in the app started.");
                var page = Encoding.UTF8.GetBytes($"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Hunter Resurgence</title></head><body><p>{text}</p></body></html>");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {page.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n"), ct);
                await stream.WriteAsync(page, ct);
            }
            catch (IOException) { } // a connection that dropped before the answer (a probe, an aborted request): the wait goes on, unless the request already gave the code
            if (code is { Length: > 0 }) return code;
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
