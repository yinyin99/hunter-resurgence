using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The profile header of a real player from the community server (handle, hunterscore, rank); a handle the
/// server does not know is a fictional hunter of the mock pages. Every tab is still the mock's.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerProfileService(HttpClient http, ServerCookie cookie, ISessionService sessions, MockProfileService mock,
    ILogger<ServerProfileService> log) : IProfileService
{
    public async Task<Profile?> ProfileAsync(string handle)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return await mock.ProfileAsync(handle) is { } fictional ? fictional with { Own = false } : null;  // a handle typed by hand is never the signed-in player's
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerHunter>() is { } h)
            {
                var own = (await sessions.CurrentAsync())?.Handle is { } me && string.Equals(me, h.Handle, StringComparison.OrdinalIgnoreCase);
                var p = await mock.ProfileAsync(h.Handle);
                return p! with { Handle = h.Handle, HunterScore = h.Hunterscore, Rank = h.Rank, Own = own, Online = false, Membership = "", Friend = false, RequestPending = false, Real = true };
            }
            log.LogWarning("The server answered {Path} with {Status}: no profile", ServerSessionApi.Hunters, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no profile", ServerSessionApi.Hunters, e.Message);
        }
        return null;
    }

    public Task<IReadOnlyList<RankBar>> RanksAsync(string handle, string kind) => mock.RanksAsync(handle, kind);
    public Task<AchievementSummary> AchievementSummaryAsync(string handle) => mock.AchievementSummaryAsync(handle);
    public Task<IReadOnlyList<Milestone>> MilestonesAsync(string handle, string kind) => mock.MilestonesAsync(handle, kind);
    public Task<IReadOnlyList<Challenge>> ChallengesAsync(string handle) => mock.ChallengesAsync(handle);
    public Task<IReadOnlyList<Skill>> SkillsAsync(string handle, string kind) => mock.SkillsAsync(handle, kind);
    public Task<Totals> TotalsAsync(string handle) => mock.TotalsAsync(handle);
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
