using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The account of the signed-in player: the avatar is the community server's (<see cref="ServerAccountApi.AvatarChoices"/>: the game's avatars to choose from, the one imported from the player's backup while
/// they have it, and the choice, <see cref="ServerAccountApi.SetAvatar"/>); the rest of the settings is still the mock's, which the live site does not offer (<c>HostMode.Live</c>). Without a session, or when the server
/// does not answer, the avatar is the default one, there is nothing to choose and a choice fails.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerAccountService(HttpClient http, ServerCookie cookie, MockAccountService mock, ILogger<ServerAccountService> log) : IAccountService
{
    public event Action? Changed;

    public async Task<Account> AccountAsync() => await mock.AccountAsync() with { Avatar = Avatars.Slot((await FetchAsync(http, cookie, log))?.Current) };

    /// <summary>The game's avatars in the order of its list, then the imported picture when the player has one; the avatar-<c>id</c> slots, the picture's link for the imported one.</summary>
    public async Task<IReadOnlyList<AvatarChoice>> AvatarsAsync()
    {
        if (await FetchAsync(http, cookie, log) is not { } a) return [];
        var choices = (a.Ids ?? []).Select(id => new AvatarChoice($"avatar-{id}", $"{id}", false)).ToList();
        if (a.Imported is not null) choices.Add(new(Avatars.Slot(a.Imported), "", false));
        return choices;
    }

    public async Task<Result> SetAvatarAsync(string id)
    {
        if (cookie.Value is null) return Result.Fail("Store.Oops");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.SetAvatar);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("id", Avatars.IdOf(id) ?? "imported")]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                Changed?.Invoke();
                return Result.Success;
            }
            log.LogWarning("The server refused {Path} ({Status})", ServerAccountApi.SetAvatar, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.SetAvatar, e.Message);
        }
        return Result.Fail("Store.Oops");
    }

    /// <summary>The player's avatar and choices; null without a session, or when the server does not answer.</summary>
    internal static async Task<ServerAvatars?> FetchAsync(HttpClient http, ServerCookie cookie, ILogger log)
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.AvatarChoices);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<ServerAvatars>();
            log.LogWarning("The server answered {Path} with {Status}: no avatars", ServerAccountApi.AvatarChoices, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no avatars", ServerAccountApi.AvatarChoices, e.Message);
        }
        return null;
    }

    public Task<LocaleChoices> LocaleChoicesAsync() => mock.LocaleChoicesAsync();
    public Task<Result> SaveLocaleAsync(string language, string country, string timeZone) => mock.SaveLocaleAsync(language, country, timeZone);
    public Task<Result> RequestEmailChangeAsync(string email) => mock.RequestEmailChangeAsync(email);
    public Task<Result> ResendConfirmationAsync() => mock.ResendConfirmationAsync();
    public Task<Result> ConfirmEmailChangeAsync(string email, string code) => mock.ConfirmEmailChangeAsync(email, code);
    public Task<IReadOnlyList<BannedPlayer>> BansAsync() => mock.BansAsync();
    public Task<Result> UnbanAsync(int id) => mock.UnbanAsync(id);
    public Task<Headers> HeadersAsync() => mock.HeadersAsync();
    public Task<Result> SaveHeaderAsync(string selected) => mock.SaveHeaderAsync(selected);
    public Task<Result> DisconnectSteamAsync() => mock.DisconnectSteamAsync();
    public Task<Recruiting> RecruitingAsync() => mock.RecruitingAsync();
}
