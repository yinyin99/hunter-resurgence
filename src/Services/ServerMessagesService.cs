using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's own conversations on the community server (<see cref="ServerAccountApi.Messages"/>): the server's notices and the restored ones, read only. Without a session,
/// or when the server does not answer, there are none; a message cannot be sent and nothing is marked read.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerMessagesService(HttpClient http, ServerCookie cookie, ILogger<ServerMessagesService> log) : IMessagesService
{
    public async Task<IReadOnlyList<Conversation>> ConversationsAsync() =>
        [.. (await GetAsync<ServerConversations>(ServerAccountApi.Messages))?.Conversations?.Select(c => new Conversation(c.Handle, c.Preview, When(c.Ts), c.Unread)) ?? []];

    public async Task<IReadOnlyList<Message>> ThreadAsync(string handle) =>
        [.. (await GetAsync<ServerThread>(ServerAccountApi.MessagesThread, $"?handle={Uri.EscapeDataString(handle)}"))?.Messages?.Select(m => new Message(m.Mine, m.Content, DateTime.UtcNow - When(m.Ts))) ?? []];

    public Task MarkReadAsync(string handle) => Task.CompletedTask;

    public Task<Result> SendAsync(string handle, string text) => Task.FromResult(Result.Fail("Msg.Failed"));

    public Task<bool> OnlyFriendsAsync() => Task.FromResult(false);

    public Task SetOnlyFriendsAsync(bool on) => Task.CompletedTask;

    static DateTime When(long ts) => DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(ts, 0, 253402300799)).UtcDateTime;

    /// <param name="query">Holds the player's input: sent, never logged.</param>
    async Task<T?> GetAsync<T>(string endpoint, string query = "") where T : class
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, endpoint + query);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            log.LogWarning("The server answered {Path} with {Status}: no messages", endpoint, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no messages", endpoint, e.Message);
        }
        return null;
    }
}
