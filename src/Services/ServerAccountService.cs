using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The account of the signed-in player: the avatar is the community server's (<see cref="ServerAccountApi.AvatarChoices"/>: the game's avatars to choose from, the one imported from the player's backup while
/// they have it, and the choice, <see cref="ServerAccountApi.SetAvatar"/>); the recruit page is still the mock's. Without a session, or when the server
/// does not answer, the avatar is the default one, there is nothing to choose and a choice fails.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerAccountService(HttpClient http, ServerCookie cookie, MockAccountService mock, ILogger<ServerAccountService> log) : IAccountService
{
    public event Action? Changed;

    public async Task<Account> AccountAsync() => new(Avatars.Slot((await FetchAsync(http, cookie, log))?.Current));

    /// <summary>The game's avatars the player can choose in the order of its list (while the imported picture is selected, the one whose id is the head the game uses is <c>InGame</c>), then the imported picture when the player has one, then the store's avatars it has not unlocked (locked, with the store item that unlocks one); the avatar-<c>id</c> slots, the picture's link for the imported one.</summary>
    public async Task<IReadOnlyList<AvatarChoice>> AvatarsAsync()
    {
        if (await FetchAsync(http, cookie, log) is not { } a) return [];
        var choices = (a.Ids ?? []).Select(id => new AvatarChoice($"avatar-{id}", $"{id}", false, InGame: a.Own && a.Face == id)).ToList();
        if (a.Imported is not null) choices.Add(new(Avatars.Slot(a.Imported), "", false));
        choices.AddRange((a.Locked ?? []).Select(l => new AvatarChoice($"avatar-{l.Id}", l.Name ?? $"{l.Id}", true, l.Item)));
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

    public Task<Recruiting> RecruitingAsync() => mock.RecruitingAsync();
}
