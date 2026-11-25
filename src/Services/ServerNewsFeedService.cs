using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The community server's public <see cref="Path"/>: the posts that show now (docs/server.md#news). The server caches them for a minute.</summary>
/// <param name="http">Base address is the server; a failure of the network is no posts.</param>
/// <param name="cookie">Only its host is used: the path is public but the server answers it on the website's host only.</param>
/// <param name="origin">What a picture's path is added to to make its address: the launcher's server address; "" on the website, whose pages reach the pictures on their own domain.</param>
public sealed class ServerNewsFeedService(HttpClient http, ServerCookie cookie, string origin, ILogger<ServerNewsFeedService> log) : INewsFeedService
{
    /// <summary>GET with <c>?lang=</c>: 200 <c>{"posts": [{id, published, pinned, title, body, link, linkText, picture}]}</c>, no sign-in.</summary>
    public const string Path = "/news";

    sealed record Feed(List<Post>? Posts);

    sealed record Post(long Id, long Published, bool Pinned, string? Title, string? Body, string? Link, string? LinkText, string? Picture);

    public async Task<IReadOnlyList<NewsPost>> PostsAsync(string language)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Path}?lang={Uri.EscapeDataString(language)}");
            if (cookie.Host is not null) request.Headers.Host = cookie.Host;
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<Feed>() is { Posts: { } posts })
                return [.. posts.Where(p => p is { Id: > 0, Title.Length: > 0, Body.Length: > 0 }).Select(p => new NewsPost(p.Id, DateTimeOffset.FromUnixTimeSeconds(p.Published), p.Pinned, p.Title!, p.Body!,
                    p.Link is { Length: > 0 } && p.LinkText is { Length: > 0 } ? p.Link : null, p.LinkText, p.Picture is { } picture ? picture.StartsWith("/news/", StringComparison.Ordinal) ? origin + picture : picture.StartsWith(NewsPost.SlotPrefix, StringComparison.Ordinal) && picture.Length > NewsPost.SlotPrefix.Length ? picture : null : null))];
            log.LogWarning("The server answered {Path} with {Status} or an unusable reply: no posts", Path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or ArgumentOutOfRangeException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no posts", Path, e.Message);
        }
        return [];
    }
}
