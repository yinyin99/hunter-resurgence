namespace Resurgence.Services;

public sealed record Friend(string Handle, bool Online);

public interface IFriendsService
{
    Task<IReadOnlyList<Friend>> FriendsAsync();
    /// <summary>Hunters whose handle contains the text (the friends dropdown searches from 3 characters).</summary>
    Task<IReadOnlyList<Friend>> SearchAsync(string text);
}
