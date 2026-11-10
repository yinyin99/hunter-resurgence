using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The profile of a player of the community server: the header from <c>GET /hunters/&lt;handle&gt;</c>, and the tabs in <see cref="Tabs"/>. A handle the server
/// does not know, or an answer that fails, is no profile (the page is "Page not found"), never a hunter of the mock pages.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerProfileService(HttpClient http, ServerCookie cookie, ISessionService sessions, MockProfileService mock,
    ILogger<ServerProfileService> log, IFriendsService? friends = null) : IProfileService
{
    /// <summary>The profile tabs that have a server source (the keys of <c>HunterProfile</c>'s tabs, and <c>expedition</c>, <c>score</c> and <c>antler</c>
    /// for the hunt and score sheet pages). A tab left out of this set stays hidden on the live site; today every tab has a source.</summary>
    public static readonly IReadOnlySet<string> Tabs = new HashSet<string> { "skills", "statistics", "trophies", "dogs", "friends", "achievements", "ranks", "gallery", "expedition", "score", "antler" };

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
                // whether the signed-in player is a friend of this hunter, or has asked it to be (the header's request button)
                var (friend, pending) = !own && cookie.Value is not null && friends is not null
                    ? ((await friends.FriendsAsync()).Any(f => string.Equals(f.Handle, h.Handle, StringComparison.OrdinalIgnoreCase)), (await friends.PendingAsync()).Contains(h.Handle, StringComparer.OrdinalIgnoreCase))
                    : (false, false);
                return new(h.Handle, h.Hunterscore, h.Rank, h.Membership ?? "", Online: false, own, friend, pending, Tabs: Tabs, Avatar: AvatarSlot(h.Avatar));
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

    /// <summary><c>GET /hunters/&lt;handle&gt;/ranks</c>: for "overview" the hunterscore and the three categories as bars of what the rows add up to (each row counts to the cap), else
    /// the rows of the kind (animals, weapons or collectables) by name, each with its count (up to the cap) as level and as the way to the cap. A failed answer is no bars.</summary>
    public async Task<IReadOnlyList<RankBar>> RanksAsync(string handle, string kind)
    {
        if (await GetRanksAsync(handle) is not { } r) return [];
        RankBar Bar(string name, IEnumerable<ServerRank> rows) => new(name, 0, Count(rows.Sum(x => Math.Min(x.Value, r.Cap))), Count(rows.Count() * r.Cap));
        if (kind != "overview")
            return [.. r.Ranks.Where(x => x.Category == kind).OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new RankBar(x.Name, Count(Math.Min(x.Value, r.Cap)), Count(Math.Min(x.Value, r.Cap)), Count(r.Cap)))];
        return [Bar("Hunter Score", r.Ranks), Bar("Animals", r.Ranks.Where(x => x.Category == "animals")),
                Bar("Weapons", r.Ranks.Where(x => x.Category == "weapons")), Bar("Collectables", r.Ranks.Where(x => x.Category == "collectables"))];
    }

    static int Count(long n) => (int)Math.Clamp(n, 0, int.MaxValue);

    async Task<ServerRanks?> GetRanksAsync(string handle)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.RanksSuffix}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerRanks>() is { Ranks: not null, Milestones: not null } r) return r;
            log.LogWarning("The server answered {Path} with {Status}: no ranks", ServerSessionApi.Hunters + "/<handle>" + ServerSessionApi.RanksSuffix, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no ranks", ServerSessionApi.Hunters + "/<handle>" + ServerSessionApi.RanksSuffix, e.Message);
        }
        return null;
    }
    /// <summary><c>GET /hunters/&lt;handle&gt;/achievements</c>: the categories' progress, the number unlocked and the latest. A failed answer is an empty summary.</summary>
    public async Task<AchievementSummary> AchievementSummaryAsync(string handle)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.AchievementsSuffix}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerAchievements>() is { } a)
                return new([.. (a.Categories ?? []).Select(c => new RankBar(c.Name, c.Level, c.Done, c.Total))], a.Unlocked,
                    [.. (a.Recent ?? []).Select(r => new Achievement(r.Title, r.Description, DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", out var d) ? d : default))]);
            log.LogWarning("The server answered {Path} with {Status}: no achievements", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no achievements", path, e.Message);
        }
        return new([], 0, []);
    }
    /// <summary>The milestones of a kind (animals, weapons, exploration or day-mission) from <c>GET /hunters/&lt;handle&gt;/ranks</c>: the steps reached of each row's thresholds. A failed answer is none.</summary>
    public async Task<IReadOnlyList<Milestone>> MilestonesAsync(string handle, string kind) => await GetRanksAsync(handle) is { } r
        ? [.. r.Milestones.Where(m => m.Category == kind).Select(m => new Milestone(m.Title, m.Steps.Count(s => m.Value >= s), [.. m.Steps.Select(Count)], Count(m.Value)))]
        : [];
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
                    ? [.. (s.Weapons ?? []).Select(w => new Skill(w.Name, w.Group, [Ring("Shooting", w.Value)], null, w.Id))]
                    : [.. (s.Species ?? []).Select(x => new Skill(x.Name, null, [Ring("Spotting", x.Spotting), Ring("Tracking", x.Tracking ?? 1)], x.Image))];
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
    /// <summary><c>GET /hunters/&lt;handle&gt;/score/&lt;kill&gt;</c>: the score sheet of a kill (the server decides its <paramref name="kind"/>, score or antler); null when the kill has none or the server does not answer.</summary>
    public async Task<ScoreSheet?> SheetAsync(string handle, string kind, long id) =>
        await GetAsync<ServerSheet>($"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.ScoreSuffix}/{id}", "score sheet") is { } s
            ? new(s.Kind, s.Species, s.Facts ?? [], s.Rows ?? [], [.. (s.Abnormal ?? []).Select(a => (a.Right, a.Left))], s.Sums ?? [], s.Weapon, s.Scope, s.Ammo, s.DistanceM, s.State, s.BodyPart, s.Stance, s.Platform,
                s.WeightKg, s.Type, s.WoundTime, s.Integrity, s.ShotLocation, s.Shots, Utc(s.When), s.ExpeditionId, s.Score, s.HarvestValue)
            : null;
    /// <summary><c>GET /hunters/&lt;handle&gt;/gallery?page=n</c>: one page of 24 photos, each with its small picture. A failed answer is an empty page.</summary>
    public async Task<Page<Photo>> GalleryAsync(string handle, int page)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.GallerySuffix}?page={page}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerGalleryPage>() is { } g)
                return new([.. (g.Rows ?? []).Select(r => new Photo(r.Id, r.Animal, double.TryParse(r.Label, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score) ? score : 0, r.Url, r.Thumbnail))], g.Page, g.Pages, g.Total);
            log.LogWarning("The server answered {Path} with {Status}: no photos", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no photos", path, e.Message);
        }
        return new([], 1, 1);
    }
    /// <summary><c>POST /account/gallery/delete</c> as the signed-in player: a photo of their own gallery. Fails without a session or for a photo that is not theirs.</summary>
    public async Task<Result> DeleteGalleryImageAsync(long id)
    {
        if (cookie.Value is null) return Result.Fail("Mis.Failed");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.GalleryDelete);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result.Success;
            log.LogWarning("The server refused {Path} ({Status})", ServerAccountApi.GalleryDelete, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.GalleryDelete, e.Message);
        }
        return Result.Fail("Mis.Failed");
    }
    /// <summary><c>GET /account/gallery/zip</c> as the signed-in player: their own gallery as a zip. Empty when the server does not answer (the handle is not asked: only the own gallery is downloaded).</summary>
    public async Task<byte[]> GalleryZipAsync(string handle)
    {
        if (cookie.Value is null) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.GalleryZip);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync();
            log.LogWarning("The server answered {Path} with {Status}: no gallery file", ServerAccountApi.GalleryZip, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no gallery file", ServerAccountApi.GalleryZip, e.Message);
        }
        return [];
    }
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
    /// <summary><c>GET /hunters/&lt;handle&gt;/friends?page=n</c>: one page of 20. A failed answer is an empty page.</summary>
    public async Task<Page<ProfileFriend>> FriendsAsync(string handle, int page)
    {
        var path = $"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.FriendsSuffix}?page={page}";
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerFriendsPage>() is { } f)
                return new([.. (f.Rows ?? []).Select(r => new ProfileFriend(r.Handle, r.Hunterscore))], f.Page, f.Pages, f.Total);
            log.LogWarning("The server answered {Path} with {Status}: no friends", path, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no friends", path, e.Message);
        }
        return new([], 1, 1);
    }
    /// <summary><c>GET /hunters/&lt;handle&gt;/hunts/&lt;id|latest&gt;</c>: a hunt with its statistics and kills; null when the server does not know it or does not answer. A hunt that is open has no statistics yet.</summary>
    public async Task<Expedition?> ExpeditionAsync(string handle, long? id) =>
        await GetAsync<ServerHunt>($"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.HuntsSuffix}/{(id?.ToString() ?? "latest")}", "hunt") is { } h
            ? new(h.Id, h.Reserve, h.Lodge, Utc(h.Start), h.End is { } end ? Utc(end) : null, h.Weapons ?? [], h.Animals ?? [], [], [], h.DistanceKm, (int)Math.Clamp(h.Seconds, 0, int.MaxValue), [],
                [.. (h.Kills ?? []).Select(k => new Kill(k.Id, k.Species, k.Weapon, k.Ammo, k.DistanceM, k.Score, k.WeightKg, Utc(k.When), null, k.Sheet))])
            : null;
    /// <summary><c>GET /hunters/&lt;handle&gt;/hunts?page=n</c>: one page of 40. A failed answer is an empty page.</summary>
    public async Task<Page<HistoryRow>> HistoryAsync(string handle, int page) =>
        await GetAsync<ServerHunts>($"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.HuntsSuffix}?page={page}", "hunts") is { } h
            ? new([.. (h.Rows ?? []).Select(r => new HistoryRow(r.Id, Utc(r.Start), (int)Math.Clamp(r.Seconds, 0, int.MaxValue), r.Reserve, r.Lodge, r.Collectables, r.Kills))], h.Page, h.Pages, h.Total)
            : new([], 1, 1);

    static DateTime Utc(long seconds) => DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, -62135596800, 253402300799)).UtcDateTime;

    /// <summary>One answer of the server as its JSON, null when it is not a 200 with a body or does not come; the reason is logged.</summary>
    async Task<T?> GetAsync<T>(string path, string what) where T : class
    {
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<T>() is { } body) return body;
            log.LogWarning("The server answered {Path} with {Status}: no {What}", path, (int)response.StatusCode, what);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no {What}", path, e.Message, what);
        }
        return null;
    }
    /// <summary><c>GET /hunters/&lt;handle&gt;/best</c>: the best score of each species, all time (the server has no seasons, so <paramref name="season"/> is not read). No rows, or a failed answer, is "empty".</summary>
    public async Task<PersonalBest> BestAsync(string handle, int season) =>
        await GetAsync<ServerBest>($"{ServerSessionApi.Hunters}/{Uri.EscapeDataString(handle)}{ServerSessionApi.BestSuffix}", "personal bests") is { Rows.Length: > 0 } b
            ? new("done", [.. b.Rows.Select(r => new BestRow(r.Species, r.Score, r.Range, r.Weapon, Utc(r.Ts), r.Kill))])
            : new("empty", []);
    public Task<Progress> ProgressAsync() => mock.ProgressAsync();
    public Task<int> ExpeditionCountAsync() => mock.ExpeditionCountAsync();
}
