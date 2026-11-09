using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The profile of a player of the community server: the header from <c>GET /hunters/&lt;handle&gt;</c>, and the tabs in <see cref="Tabs"/>. A handle the server
/// does not know, or an answer that fails, is no profile (the page is "Page not found"), never a hunter of the mock pages.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerProfileService(HttpClient http, ServerCookie cookie, ISessionService sessions, MockProfileService mock,
    ILogger<ServerProfileService> log) : IProfileService
{
    /// <summary>The profile tabs that have a server source (the keys of <c>HunterProfile</c>'s tabs, and <c>expedition</c>, <c>score</c> and <c>antler</c>
    /// for the hunt and score sheet pages). The ticket that gives a tab its source reads it from the server below and adds its key here; the others stay hidden.</summary>
    public static readonly IReadOnlySet<string> Tabs = new HashSet<string> { "skills", "statistics", "trophies", "dogs" };

    public async Task<Profile?> ProfileAsync(string handle)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerProfile>() is { } h)
            {
                var own = (await sessions.CurrentAsync())?.Handle is { } me && string.Equals(me, h.Handle, StringComparison.OrdinalIgnoreCase);
                return new(h.Handle, h.Hunterscore, h.Rank, h.Membership ?? "", Online: false, own, Friend: false, Tabs: Tabs, Avatar: AvatarSlot(h.Avatar));
            }
            log.LogWarning("The server answered {Path} with {Status}: no profile", ServerSessionApi.Hunters, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no profile", ServerSessionApi.Hunters, e.Message);
        }
        return null;
    }

    /// <summary>The slot of a game avatar picture (<c>…/&lt;id&gt;.png</c> → <c>avatar-&lt;id&gt;</c>, docs/images.md); <c>avatar-default</c> for any other URL.</summary>
    public static string AvatarSlot(string? url) =>
        Path.GetFileNameWithoutExtension(url) is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? "avatar-" + id : "avatar-default";

    public Task<IReadOnlyList<RankBar>> RanksAsync(string handle, string kind) => mock.RanksAsync(handle, kind);
    public Task<AchievementSummary> AchievementSummaryAsync(string handle) => mock.AchievementSummaryAsync(handle);
    public Task<IReadOnlyList<Milestone>> MilestonesAsync(string handle, string kind) => mock.MilestonesAsync(handle, kind);
    public Task<IReadOnlyList<Challenge>> ChallengesAsync(string handle) => mock.ChallengesAsync(handle);
    /// <summary><c>GET /hunters/&lt;handle&gt;/skills</c>: the species with their spotting and tracking rings, or the weapons grouped by their category with a shooting ring. A failed answer is no skills.</summary>
    public async Task<IReadOnlyList<Skill>> SkillsAsync(string handle, string kind)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.SkillsSuffix}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerSkills>() is { } s)
                return kind == "weapons"
                    ? [.. (s.Weapons ?? []).Select(w => new Skill(w.Name, w.Group, [Ring("Shooting", w.Value)]))]
                    : [.. (s.Species ?? []).Select(x => new Skill(x.Name, null, [Ring("Spotting", x.Spotting), Ring("Tracking", x.Tracking ?? 1)]))];
            log.LogWarning("The server answered {Path} with {Status}: no skills", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no skills", path, e.Message);
        }
        return [];
    }

    /// <summary>A skill number (level plus the fraction of its span) as a ring: its level and the percent of the span done.</summary>
    static SkillRing Ring(string label, double value) => new(label, (int)value, (int)Math.Floor(Math.Round((value - Math.Floor(value)) * 100, 6)));
    /// <summary>The lifetime statistics from <c>GET /hunters/&lt;handle&gt;/statistics</c> (JSON of <see cref="Totals"/>); empty ones when the server does not answer.</summary>
    public async Task<Totals> TotalsAsync(string handle)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}/statistics";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<Totals>() is { Weapons: not null, Animals: not null, Collectables: not null } totals) return totals;
            log.LogWarning("The server answered {Path} with {Status}: empty statistics", ServerSessionApi.Hunters + "/<handle>/statistics", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): empty statistics", ServerSessionApi.Hunters + "/<handle>/statistics", e.Message);
        }
        return new([], [], [], 0, 0);
    }
    public Task<ScoreSheet?> SheetAsync(string handle, string kind, long id) => mock.SheetAsync(handle, kind, id);
    public Task<Page<Photo>> GalleryAsync(string handle, int page) => mock.GalleryAsync(handle, page);
    public Task<Result> DeleteGalleryImageAsync(long id) => mock.DeleteGalleryImageAsync(id);
    public Task<byte[]> GalleryZipAsync(string handle) => mock.GalleryZipAsync(handle);
    /// <summary><c>GET /hunters/&lt;handle&gt;/trophies?page=n</c>: one page of 24. A failed answer is an empty page.</summary>
    public async Task<Page<Trophy>> TrophiesAsync(string handle, int page)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.TrophiesSuffix}?page={page}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerTrophies>() is { } t)
                return new([.. (t.Rows ?? []).Select(r => new Trophy(r.Name, DateOnly.TryParse(r.Date, out var day) ? day : default, null, null))], t.Page, t.Pages, t.Total);
            log.LogWarning("The server answered {Path} with {Status}: no trophies", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no trophies", path, e.Message);
        }
        return new([], 1, 1);
    }
    /// <summary><c>GET /hunters/&lt;handle&gt;/dogs</c>: each owned dog with its level and skills. A failed answer is no dogs.</summary>
    public async Task<IReadOnlyList<Dog>> DogsAsync(string handle)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.DogsSuffix}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerDogs>() is { } d)
                return [.. (d.Dogs ?? []).Select(x => new Dog(x.Name, x.Type, x.Level, x.Percent, [.. (x.Skills ?? []).Select(s => new DogSkill(s.Title, "", s.Level))], x.Trophies ?? []))];
            log.LogWarning("The server answered {Path} with {Status}: no dogs", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no dogs", path, e.Message);
        }
        return [];
    }
    public Task<Page<ProfileFriend>> FriendsAsync(string handle, int page) => mock.FriendsAsync(handle, page);
    public Task<Expedition?> ExpeditionAsync(string handle, long? id) => mock.ExpeditionAsync(handle, id);
    public Task<Page<HistoryRow>> HistoryAsync(string handle, int page) => mock.HistoryAsync(handle, page);
    public Task<PersonalBest> BestAsync(string handle, int season) => mock.BestAsync(handle, season);
    public Task<Progress> ProgressAsync() => mock.ProgressAsync();
    public Task<int> ExpeditionCountAsync() => mock.ExpeditionCountAsync();
}
