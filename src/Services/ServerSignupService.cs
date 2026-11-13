using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The sign-up screens with the community server's avatars in the carousel (the game's list, <see cref="ServerAccountApi.AvatarChoices"/>); the rest of the screens is the mock's. Without a session, or
/// when the server does not answer, the carousel is empty.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerSignupService(HttpClient http, ServerCookie cookie, MockSignupService mock, ILogger<ServerSignupService> log) : ISignupService
{
    public async Task<IReadOnlyList<string>> AvatarsAsync() =>
        [.. ((await ServerAccountService.FetchAsync(http, cookie, log))?.Ids ?? []).Select(id => $"avatar-{id}")];

    public Task<bool> UsernameAvailableAsync(string name) => mock.UsernameAvailableAsync(name);
    public Task<IReadOnlyList<string>> SuggestNamesAsync() => mock.SuggestNamesAsync();
    public Task<string> EmailAsync() => mock.EmailAsync();
    public Task<bool> EmailConfirmedAsync() => mock.EmailConfirmedAsync();
    public Task<Result> ResendEmailAsync() => mock.ResendEmailAsync();
    public Task<Result> ChangeEmailAsync(string email) => mock.ChangeEmailAsync(email);
    public Task<Result> AutoLoginAsync(string id) => mock.AutoLoginAsync(id);
    public Task<Result> ConfirmEmailAsync(string kind, string? code, string? uuid, string? src) => mock.ConfirmEmailAsync(kind, code, uuid, src);
    public Task<Result> SharedDataAsync(string? code) => mock.SharedDataAsync(code);
}
