using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The missions page of the signed-in player from the community server (<see cref="ServerAccountApi.Missions"/>): the active and available missions, the packs with their
/// progress and the three switches, and every write of the page. A server that does not answer is an empty page, never the mock's. The daily missions have no server source: the
/// website does not serve their page (<c>HostMode.Live</c>), so they stay the mock's.</summary>
public sealed class ServerMissionsService(HttpClient http, ServerCookie cookie, MockMissionsService mock, ILogger<ServerMissionsService> log) : IMissionsService
{
    public async Task<IReadOnlyList<Mission>> ActiveAsync() => [.. (await RegularAsync()).Missions.Where(m => m.State == "active")];

    public async Task<RegularMissions> RegularAsync()
    {
        if (await GetAsync() is not { } s) return new([], [], 0, 0, 0, 0, false, false, false);
        var all = s.Missions.ToDictionary(m => m.Id, m => new Mission(m.Id, m.Title, m.State, m.Desc, m.Objectives, m.Reward, m.Reserve, m.Species, m.Weapon));
        var packs = s.Groups.Select(g =>
        {
            var list = g.Missions.Where(all.ContainsKey).Select(i => all[i]).ToList();
            return new MissionPack(g.Id, g.Title, list.Count(m => m.State == "completed"), list.Count, g.Favourite, g.Desc, list);
        }).ToList();
        return new([.. all.Values.Where(m => m.State is "active" or "available")], packs, s.Completed, all.Count, packs.Count(p => p.Total > 0 && p.Done == p.Total), packs.Count,
            s.Auto, s.OnlyFavourite, s.HideCompletedFavourite);
    }

    public Task<Result> ActivateAsync(int id) => id switch
    {
        -1 => Task.FromResult(Result.Fail("Mis.Failed")),  // the daily streak is not served
        0 => PostAsync("activate_all_available"),
        _ => PostAsync("activate", ("mission_id", $"{id}")),
    };

    public Task<Result> SetFavouriteAsync(int pack, bool favourite) => PostAsync("update_favourite", ("group_id", $"{pack}"), ("is_favourite", Flag(favourite)));

    /// <summary>As the page of the game: turning one setting on turns the other one off.</summary>
    public async Task<Result> SetAutomaticAsync(string setting, bool on)
    {
        var (call, key, otherCall, otherKey) = setting == "auto"
            ? ("update_automatic_activation", "auto_mission_activate", "update_activate_only_favourite", "activate_only_favourite")
            : ("update_activate_only_favourite", "activate_only_favourite", "update_automatic_activation", "auto_mission_activate");
        var r = await PostAsync(call, (key, Flag(on)));
        return r.Ok && on ? await PostAsync(otherCall, (otherKey, Flag(false))) : r;
    }

    public Task<Result> SetHideCompletedFavouriteAsync(bool hide) => PostAsync("update_hide_completed_favourite", ("hide_completed_favourite", Flag(hide)));

    public Task<DailyCalendar> DailyAsync() => mock.DailyAsync();
    public Task<Result> ResumeStreakAsync() => mock.ResumeStreakAsync();

    static string Flag(bool on) => on ? "true" : "false";

    async Task<Result> PostAsync(string call, params (string Key, string Value)[] form)
    {
        if (cookie.Value is null) return Result.Fail("Mis.Failed");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, $"{ServerAccountApi.Missions}/{call}");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Key, f.Value)));
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result.Success;
            log.LogWarning("The server refused {Call} of the missions ({Status})", call, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Call} of the missions ({Reason})", call, e.Message);
        }
        return Result.Fail("Mis.Failed");
    }

    async Task<ServerMissions?> GetAsync()
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Missions);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerMissions>() is { Missions: not null, Groups: not null } s) return s;
            log.LogWarning("The server answered {Path} with {Status}: no missions", ServerAccountApi.Missions, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no missions", ServerAccountApi.Missions, e.Message);
        }
        return null;
    }
}
