// HTTP glue for the game hosts: the request as handlers see it (Req), the reply they build (Reply, written byte for
// byte) and the request log (one console line per request, the miss log).
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Resurgence.Server;

/// <summary>A game request: method, lowercase host without port, raw path and query (not percent-decoded: recorded
/// replies are keyed by the raw URL), headers and body.</summary>
public sealed record Req(string Method, string Host, string Path, string Query, Func<string, string?> Header, byte[] Body, bool IsHttps)
{
    /// <summary>The request of this context, read once (the body is buffered).</summary>
    public static async Task<Req> ReadAsync(HttpContext ctx)
    {
        if (ctx.Items[typeof(Req)] is Req read)
            return read;
        using var body = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(body);
        var host = ctx.Request.Headers.Host.ToString().ToLowerInvariant();
        var colon = host.LastIndexOf(':');
        if (colon >= 0) host = host[..colon];
        var target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget;
        var hash = target.IndexOf('#');
        if (hash >= 0) target = target[..hash];
        var q = target.IndexOf('?');
        var headers = ctx.Request.Headers;
        var req = new Req(ctx.Request.Method, host, q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..],
                          k => headers.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null, body.ToArray(), ctx.Request.IsHttps);
        ctx.Items[typeof(Req)] = req;
        return req;
    }
}

/// <summary>A game reply: status, headers in order, body, and where it came from (state, stub, har, patch, archive,
/// mirror, weak, sink, miss) for the log. 204 has no Content-Length; HEAD gets the headers only.</summary>
public sealed record Reply(int Status, List<(string Name, string Value)> Headers, byte[] Body, string Source) : IResult
{
    public async Task ExecuteAsync(HttpContext ctx)
    {
        var req = await Req.ReadAsync(ctx);
        ctx.RequestServices.GetRequiredService<RequestLog>().Write(req, this);
        var response = ctx.Response;
        response.StatusCode = Status;
        foreach (var (name, value) in Headers)
            response.Headers.Append(name, value);
        // Swagger UI on localhost calls the game hosts cross-origin; the client never sends a localhost origin
        if (ServerApp.IsLocalOrigin(req.Header("Origin")) && !response.Headers.ContainsKey("Access-Control-Allow-Origin"))
            response.Headers.AccessControlAllowOrigin = req.Header("Origin");
        if (Status == 204)
            return;
        response.ContentLength = Body.Length;
        if (!HttpMethods.IsHead(req.Method))
            await response.Body.WriteAsync(Body);
    }
}

/// <summary>"source status method host+path" per game request on the console, and per miss one miss-log line
/// (keys only, never values). Telemetry and crash reports (the "sink" replies) are kept whole, bodies cut at 64 KB, in
/// telemetry.jsonl: trackPlayerDeath's death_type and trackShoot's organ data are only there.</summary>
public sealed class RequestLog(bool console, string? missesPath, string? telemetryPath = null)
{
    readonly Lock gate = new();

    /// <summary>Every reply as the handlers built it, before it is written (coverage and tests read these).</summary>
    public event Action<Req, Reply>? Replied;

    public void Write(Req r, Reply reply)
    {
        Replied?.Invoke(r, reply);
        JsonObject? line = null;
        if (reply.Source == "miss" && missesPath != null && !ServerApp.IsLocalOrigin(r.Header("Origin")))  // Swagger UI is not the client
            line = new JsonObject
            {
                ["t"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'"),
                ["scheme"] = r.IsHttps ? "https" : "http",
                ["method"] = r.Method,
                ["host"] = r.Host,
                ["path"] = r.Path,
                ["query_keys"] = new JsonArray(Py.ParseQs(r.Query).Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()),
                ["body_keys"] = new JsonArray(Replay.BodyKeys(r.Header("Content-Type") ?? "", r.Body).Select(k => (JsonNode?)k).ToArray()),
                ["bytes"] = r.Body.Length,
                ["content_type"] = r.Header("Content-Type"),
            };
        JsonObject? tele = reply.Source == "sink" && telemetryPath != null
            ? new JsonObject
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'"),
                ["host"] = r.Host,
                ["method"] = r.Method,
                ["path"] = r.Path,
                ["body"] = Encoding.UTF8.GetString(r.Body, 0, Math.Min(r.Body.Length, 64 * 1024)),
            }
            : null;
        if (!console && line is null && tele is null)
            return;
        lock (gate)
        {
            if (console)
                Console.WriteLine($"{reply.Source,-6} {reply.Status} {r.Method} {r.Host}{r.Path}");
            if (line != null)  // text mode: OS line ending
                File.AppendAllText(missesPath!, Py.Dumps(line) + Environment.NewLine, new UTF8Encoding(false));
            if (tele != null)
                File.AppendAllText(telemetryPath!, Py.Dumps(tele) + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    /// <summary>The logged misses, oldest first; read under the log's lock so a miss being appended can't collide.</summary>
    public string[] Misses()
    {
        lock (gate)
            return missesPath is not null && File.Exists(missesPath) ? File.ReadAllLines(missesPath) : [];
    }
}
