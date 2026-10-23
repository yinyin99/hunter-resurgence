namespace Resurgence.Services;

public sealed record Conversation(string Handle, string Last, DateTime When, bool Unread);

public interface IMessagesService
{
    Task<IReadOnlyList<Conversation>> ConversationsAsync();
}
