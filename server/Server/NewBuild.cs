// The current client build talks to one host (the web host), with lower-case paths and JSON request bodies. This turns such
// a request into the one the handlers know, before routing: the RPC calls go to the game server's host and path, every other
// /v1/ call to the API host with the path in the case the handlers use and the JSON body as a form. Nothing else changes: the
// old build's requests are not touched, and the handlers, the replay scope and the recordings see one kind of request.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Resurgence.Server;

public static class NewBuild
{
    /// <summary>The `/v1/` paths of the handlers, any case to the canonical one.</summary>
    static readonly Dictionary<string, string> Known = typeof(Replay).GetFields()
        .Where(f => f.IsLiteral && f.GetRawConstantValue() is string s && s.StartsWith("/v1/", StringComparison.Ordinal))
        .Select(f => (string)f.GetRawConstantValue()!).ToDictionary(s => s, s => s, StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical case of an API path: a handler's, else one the operator's recordings hold, else as sent.</summary>
    public static string Canonical(Replay.Game game, string path) =>
        Known.TryGetValue(path, out var known) ? known : game.RecordedApiPaths.TryGetValue(path, out var recorded) ? recorded : path;

    /// <summary>Where the game web host's request for this path is answered: the host and path the handlers know and whether its
    /// JSON body becomes a form; null: it stays as it is (the pages, the mirror, the launcher's ping).</summary>
    static (string Host, string Path, bool Form)? Map(string path, string gs, string api, Func<string, string> canonical)
    {
        if (path.StartsWith(ProtocolLiterals.WebRpcBase + "/", StringComparison.OrdinalIgnoreCase))
            return (gs, ProtocolLiterals.RpcBase + path[ProtocolLiterals.WebRpcBase.Length..].ToLowerInvariant(), false);  // the body is JSON already
        if (path.StartsWith(State.UPLOAD_PATH, StringComparison.Ordinal) || path.StartsWith(State.PHOTO_PREFIX, StringComparison.Ordinal))
            return (gs, path, false);  // photo links point at the host the game knows
        if (!path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/v1/json/", StringComparison.OrdinalIgnoreCase))
            return null;
        return (api, canonical(path), true);
    }

    public static async Task Rewrite(HttpContext ctx, Replay.Game game)
    {
        if (!string.Equals(ctx.Request.Host.Host, game.GameWeb, StringComparison.OrdinalIgnoreCase)) return;
        var feature = ctx.Features.Get<IHttpRequestFeature>()!;
        var target = feature.RawTarget;
        var q = target.IndexOf('?');
        var (path, query) = q < 0 ? (target, "") : (target[..q], target[q..]);
        if (Map(path, game.Gs, game.Api, p => Canonical(game, p)) is not { } mapped) return;
        var (host, to, form) = mapped;
        if (form && !HttpMethods.IsPut(ctx.Request.Method)) await JsonBodyToForm(ctx.Request);  // the forms are the old POSTs'; a PUT body (a lodge edit) stays JSON
        feature.RawTarget = to + query;
        ctx.Request.Path = to;
        ctx.Request.Host = new HostString(host);
    }

    /// <summary>Recordings of the current build are keyed by the game web host and the path it sent; the handlers look them up
    /// under the host and path of <see cref="Rewrite"/>. Adds the second key of each (never replaces one).</summary>
    public static void Alias(Replay.HarTable table, JsonObject cfg)
    {
        string Host(string key) => ((string)cfg[key]!).ToLowerInvariant();
        var (web, gs, api) = (Host("game_web_host"), Host("gameserver_host"), Host("api_host"));
        foreach (var (key, hit) in table.Entries.ToList())
            if (key.Host == web && Map(key.Path, gs, api, p => Known.GetValueOrDefault(p, p)) is { } mapped)
                table.Entries.TryAdd(key with { Host = mapped.Host, Path = mapped.Path }, hit);
    }

    /// <summary>The handlers read `a=1&amp;b=2` forms (the old build's bodies): a JSON object becomes one, a string as it is and
    /// anything else as its JSON text (`mapping` is a string already).</summary>
    static async Task JsonBodyToForm(HttpRequest request)
    {
        if (!(request.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase) || request.ContentLength is null or 0) return;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();
        try
        {
            if (JsonNode.Parse(body) is JsonObject json)
            {
                body = Encoding.UTF8.GetBytes(string.Join("&", json.Select(kv =>
                    Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : kv.Value?.ToJsonString() ?? ""))));
                request.ContentType = "application/x-www-form-urlencoded";
            }
        }
        catch (JsonException) { }  // not JSON after all: the handlers see it as sent
        request.Body = new MemoryStream(body);
        request.ContentLength = body.Length;
    }
}
