using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's seasonal events setting and the running event from the community server (<see cref="ServerAccountApi.Events"/>). Without an answer
/// there is no event and the player plays, so the normal pictures show.</summary>
public sealed class ServerSeasonalEventsService(HttpClient http, ServerCookie cookie, ILogger<ServerSeasonalEventsService> log) : ISeasonalEventsService
{
    static readonly SeasonalEvents None = new(true, null, []);

    public async Task<SeasonalEvents> GetAsync()
    {
        if (cookie.Value is null) return None;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Events);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerEvents>() is { } s) return new(s.Play, s.Event?.Name, s.Event?.Reserves ?? []);
            log.LogWarning("The server answered the seasonal events with {Status}: no event", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer the seasonal events ({Reason}): no event", e.Message);
        }
        return None;
    }

    public async Task<EventPanelData?> PanelAsync()
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.EventPanel);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerEventPanel>() is { Name: not null } p)
                return new(p.Title ?? p.Name, DateTimeOffset.FromUnixTimeSeconds(p.Start), DateTimeOffset.FromUnixTimeSeconds(p.End),
                    [.. (p.Community ?? []).Select(c => new CommunityMission(c.Title, c.Progress, c.Target, c.Gm, c.Em))],
                    [.. (p.Contributors ?? []).Select(c => new Contributor(c.Handle, c.Count))],
                    [.. (p.Missions ?? []).Select(m => new EventMission(new(m.Id, m.Title, m.State, "", [.. m.Objectives.Select(o => o.Title)], m.Reward, "", "", ""), m.Objectives.Count(o => o.Done)))],
                    [.. (p.Items ?? []).Select(i => new EventItem(i.Id, i.Name))]);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer the event panel ({Reason}): no panel", e.Message);
        }
        return null;
    }

    public async Task<Result> SetAsync(bool play)
    {
        if (cookie.Value is null) return Result.Fail("Events.Failed");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.Events);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("play", play ? "1" : "0")]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result.Success;
            log.LogWarning("The server refused the seasonal events setting ({Status})", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer the seasonal events setting ({Reason})", e.Message);
        }
        return Result.Fail("Events.Failed");
    }
}
