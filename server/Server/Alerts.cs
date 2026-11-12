// A message to the operator's chat when something waits for them: a Discord-style webhook (setting review_webhook, https only).
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class Alerts
{
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };
    static int badUrlLogged;

    /// <summary>Post `{"content": text}` to the review webhook on a background task. No setting: nothing. A bad URL or a failed post is logged, never thrown.
    /// `http` is for tests.</summary>
    public static void Review(JsonObject cfg, string text, HttpClient? http = null)
    {
        if ((string?)cfg["review_webhook"] is not { Length: > 0 } url) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            if (Interlocked.Exchange(ref badUrlLogged, 1) == 0) Console.Error.WriteLine("review_webhook: not an https URL, no alerts are sent");
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
                Console.Error.WriteLine($"review_webhook: alert not sent: {e.GetType().Name}: {e.Message}");
            }
        });
    }
}
