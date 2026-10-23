namespace Resurgence.Services.Mocks;

public sealed class MockMessagesService : IMessagesService
{
    public Task<IReadOnlyList<Conversation>> ConversationsAsync() =>
        Task.FromResult<IReadOnlyList<Conversation>>([new("Ranger_Test", "See you there", new(2026, 1, 3), true), new("Moss_Walker", "Thanks", new(2026, 1, 1), false)]);
}
