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
    public static readonly IReadOnlySet<string> Tabs = new HashSet<string> { "statistics" };

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
    public Task<IReadOnlyList<Skill>> SkillsAsync(string handle, string kind) => mock.SkillsAsync(handle, kind);
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
    public Task<Page<Trophy>> TrophiesAsync(string handle, int page) => mock.TrophiesAsync(handle, page);
    public Task<IReadOnlyList<Dog>> DogsAsync(string handle) => mock.DogsAsync(handle);
    public Task<Page<ProfileFriend>> FriendsAsync(string handle, int page) => mock.FriendsAsync(handle, page);
    public Task<Expedition?> ExpeditionAsync(string handle, long? id) => mock.ExpeditionAsync(handle, id);
    public Task<Page<HistoryRow>> HistoryAsync(string handle, int page) => mock.HistoryAsync(handle, page);
    public Task<PersonalBest> BestAsync(string handle, int season) => mock.BestAsync(handle, season);
    public Task<Progress> ProgressAsync() => mock.ProgressAsync();
    public Task<int> ExpeditionCountAsync() => mock.ExpeditionCountAsync();
}
