namespace Resurgence.Services;

public sealed record Friend(string Handle, bool Online);

public interface IFriendsService
{
    Task<IReadOnlyList<Friend>> FriendsAsync();
    /// <summary>Hunters whose handle contains the text, never the signed-in hunter (the friends dropdown searches from 3 characters).</summary>
    Task<IReadOnlyList<Friend>> SearchAsync(string text);
    /// <summary>Hunters who asked to be friends, oldest first.</summary>
    Task<IReadOnlyList<string>> RequestsAsync();
    /// <summary>Hunters the signed-in hunter has asked and who have not answered yet.</summary>
    Task<IReadOnlyList<string>> PendingAsync();
    Task<Result> SendRequestAsync(string handle);
    /// <summary>The hunter becomes a friend and the request is gone.</summary>
    Task<Result> AcceptAsync(string handle);
    Task<Result> DeclineAsync(string handle);
    Task<Result> RemoveAsync(string handle);
}
