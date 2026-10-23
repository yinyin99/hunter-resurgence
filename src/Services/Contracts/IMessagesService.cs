namespace Resurgence.Services;

public sealed record Conversation(string Handle, string Last, DateTime When, bool Unread);

/// <summary>One message of a thread, newest first; <see cref="Ago"/> is how long ago it was sent.</summary>
public sealed record Message(bool Mine, string Text, TimeSpan Ago);

public interface IMessagesService
{
    Task<IReadOnlyList<Conversation>> ConversationsAsync();
    Task<IReadOnlyList<Message>> ThreadAsync(string handle);
    /// <summary>Sends a message; the error key is Msg.NoUser, Msg.Self or Msg.Failed.</summary>
    Task<Result> SendAsync(string handle, string text);
    Task<bool> OnlyFriendsAsync();
    Task SetOnlyFriendsAsync(bool on);
}
