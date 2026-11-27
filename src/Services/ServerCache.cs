using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>A public reply of the community server (the store, the news), shared by every visitor: the website asks again only after <see cref="Lifetime"/>, and after a
/// failure not before <see cref="Pause"/> (a page reads it several times, and a server that hangs would cost a time-out each).</summary>
public sealed class ServerCache<T> where T : class
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(10);

    sealed record Held(DateTime Expires, T Value);

    volatile Held? held;
    long failedAt;  // ticks of the last failure, 0 for none

    public T? Get(DateTime now) => held is { } h && now < h.Expires ? h.Value : null;

    public bool Paused(DateTime now) => Volatile.Read(ref failedAt) is var t && t != 0 && now - new DateTime(t, DateTimeKind.Utc) < Pause;

    /// <summary>Keeps <paramref name="value"/> for <see cref="Lifetime"/>, or until <paramref name="until"/> when that is sooner (the end of a sale the reply shows: after it the server charges the full price), but at least <see cref="Pause"/>.</summary>
    public void Set(DateTime now, T value, DateTimeOffset? until = null)
    {
        var expires = now + Lifetime;
        if (until is { } u) expires = new[] { expires, new[] { u.UtcDateTime, now + Pause }.Max() }.Min();
        held = new Held(expires, value);
        Volatile.Write(ref failedAt, 0);
    }

    public void Fail(DateTime now) => Volatile.Write(ref failedAt, now.Ticks);

    /// <summary>The held reply, else GET <paramref name="path"/> with the browser's <c>Host</c> (the server picks its site by it) and its session <paramref name="cookie"/> when given (a reply for
    /// signed-in players: the caller asks only with one). Null when the server does not
    /// answer, answers an error, or sends a reply that is not <paramref name="complete"/> (it would break every page until it expires): the caller shows nothing, never the mock.</summary>
    public async Task<T?> LoadAsync(HttpClient http, string? host, string path, Func<T, bool> complete, ILogger log, string? cookie = null, Func<T, DateTimeOffset?>? until = null)
    {
        var now = DateTime.UtcNow;
        if (Get(now) is { } value) return value;
        if (Paused(now)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (host is not null) request.Headers.Host = host;
            if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"{ServerSessionApi.Cookie}={cookie}");
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                if (await response.Content.ReadFromJsonAsync<T>() is { } reply && complete(reply))
                {
                    Set(now, reply, until?.Invoke(reply));
                    return reply;
                }
                log.LogWarning("The server answered {Path} with an incomplete reply: nothing is shown", path);
            }
            else log.LogWarning("The server answered {Path} with {Status}: nothing is shown", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): nothing is shown", path, e.Message);
        }
        Fail(now);
        return null;
    }
}
