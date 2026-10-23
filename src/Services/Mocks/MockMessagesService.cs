namespace Resurgence.Services.Mocks;

public sealed class MockMessagesService : IMessagesService
{
    bool onlyFriends;

    public Task<IReadOnlyList<Conversation>> ConversationsAsync() =>
        Task.FromResult<IReadOnlyList<Conversation>>([new("Moss_Walker", "Hey there. I heard you had a...", new(2026, 1, 3), false), new("Ranger_Test", "See you there", new(2026, 1, 1), false)]);

    public Task<IReadOnlyList<Message>> ThreadAsync(string handle) =>
        Task.FromResult<IReadOnlyList<Message>>([new(false, "Most recent message.", TimeSpan.FromMinutes(1)), new(true, "A reply in the same thread.", TimeSpan.FromHours(1)), new(false, "Placeholder text used only to show the thread layout.", TimeSpan.FromDays(1))]);

    // nothing is sent in the mock; hunters named like the first part of "nobody" do not exist and one cannot write to oneself
    public Task<Result> SendAsync(string handle, string text) =>
        Task.FromResult(handle.StartsWith("nobody", StringComparison.OrdinalIgnoreCase) ? Result.Fail("Msg.NoUser") : handle == "Hunter01" ? Result.Fail("Msg.Self") : Result.Success);

    public Task<bool> OnlyFriendsAsync() => Task.FromResult(onlyFriends);

    public Task SetOnlyFriendsAsync(bool on) { onlyFriends = on; return Task.CompletedTask; }
}
