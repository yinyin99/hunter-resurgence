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

    /// <summary>The account's Steam profile as a Discord link, or `none`. The link is in angle brackets: Discord shows no preview.</summary>
    public static string Steam(string? steamId64) =>
        steamId64 is { Length: > 0 } s ? $"[profile](<https://steamcommunity.com/profiles/{s}>)" : "none";

    /// <summary>One alert in the shared layout: a bold title with the kind's emoji, a `label: value` line per field (a null value is left out), then the command to run in a code span (if any).</summary>
    public static string Text(string emoji, string title, string? run, params (string Label, object? Value)[] fields)
    {
        var text = new System.Text.StringBuilder($"{emoji} **{title}**");
        foreach (var (label, value) in fields)
            if (value is not null) text.Append($"\n{label}: {value}");
        if (run is not null) text.Append($"\nrun: `{run}`");
        return text.ToString();
    }

    /// <summary>A handle in a code span, so a `_` in it is not read as formatting. Only plain name characters (see <see cref="Name"/>).</summary>
    public static string Handle(string handle) => Name(handle) is { Length: > 0 } n ? $"`{n}`" : "none";

    /// <summary>A zip's size for an alert: KiB below 1 MiB, else MiB with one decimal.</summary>
    static string Size(long bytes) => bytes < 1 << 20 ? $"{(bytes + 1023) / 1024} KiB" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.0} MiB");

    /// <summary>A problem report arrived (review channel): who sent it, its reference, what started it and its size. `trigger` is manual, short, exitcode or nowindow.</summary>
    public static void ProblemReport(JsonObject cfg, long account, string handle, string reference, string trigger, long bytes, HttpClient? http = null) =>
        Review(cfg, Text("\U0001FA7A", "Problem report", null,
            ("handle", Handle(handle)), ("account", account), ("reference", $"`{Name(reference)}`"), ("trigger", Name(trigger)), ("size", Size(bytes))), http);
}
