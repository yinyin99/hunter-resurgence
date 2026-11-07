using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The news of the community server (<see cref="ServerCatalogApi.News"/>): the original's recent posts and featured slides, the same for everyone, asked with the browser's
/// <c>Host</c> and no cookie. A server that does not answer, or holds no news, is no news, never the mock's. The server has no Discord widget, so there is none.</summary>
public sealed class ServerNewsService(HttpClient http, ServerCookie cookie, ServerCache<ServerNews> cache, ILogger<ServerNewsService> log) : INewsService
{
    public async Task<IReadOnlyList<NewsPost>> RecentAsync() =>
        (await LoadAsync())?.Posts.Select(p => new NewsPost(p.Title, p.Date, p.Slug, p.Excerpt)).ToList() ?? [];

    public async Task<IReadOnlyList<Featured>> FeaturedAsync() =>
        (await LoadAsync())?.Featured.Select(f => new Featured(f.Title, f.Lead, f.LinkText, f.Target, f.Date, f.Image)).ToList() ?? [];

    public Task<DiscordInfo?> DiscordAsync() => Task.FromResult<DiscordInfo?>(null);

    Task<ServerNews?> LoadAsync() => cache.LoadAsync(http, cookie.Host, ServerCatalogApi.News, n => n is { Posts: not null, Featured: not null }, log);
}
