namespace Resurgence.Services;

public sealed record Friend(string Handle, bool Online);

public interface IFriendsService
{
    Task<IReadOnlyList<Friend>> FriendsAsync();
}
