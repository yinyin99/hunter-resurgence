// A message to the operator's chat when something waits for them: a Discord-style webhook (setting review_webhook, https only).
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class Alerts
{
    public static HttpClient Shared { get; set; } = new() { Timeout = TimeSpan.FromSeconds(10) };  // tests swap it
    static int badUrlLogged;

    /// <summary>Post `{"content": text}` to the review webhook on a background task. No setting: nothing. A bad URL or a failed post is logged, never thrown.
    /// `http` is for tests.</summary>
    public static void Review(JsonObject cfg, string text, HttpClient? http = null) => Send(cfg, "review_webhook", text, http);

    /// <summary>The same for the sign-up webhook (setting signup_webhook): new accounts and claims restored at once.</summary>
    public static void Signup(JsonObject cfg, string text, HttpClient? http = null) => Send(cfg, "signup_webhook", text, http);

    /// <summary>A handle for an alert: only plain name characters, at most 32 (it may be the archive's own text).</summary>
    public static string Name(string handle)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(handle, @"[^A-Za-z0-9_.-]", "");
        return s.Length > 32 ? s[..32] : s;
    }

    static void Send(JsonObject cfg, string key, string text, HttpClient? http)
    {
        if ((string?)cfg[key] is not { Length: > 0 } url) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            if (Interlocked.Exchange(ref badUrlLogged, 1) == 0) Console.Error.WriteLine($"{key}: not an https URL, no alerts are sent");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var reply = await (http ?? Shared).PostAsJsonAsync(uri, new { content = text });
                reply.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{key}: alert not sent: {e.GetType().Name}: {e.Message}");
            }
        });
    }

    /// <summary>The account's verified Steam id and its profile link for an alert (the link in angle brackets: Discord shows no preview).</summary>
    public static string Steam(string? steamId64) =>
        steamId64 is { Length: > 0 } s ? $"steam id {s} <https://steamcommunity.com/profiles/{s}>" : "no steam id";
}
