namespace Resurgence.Services.Mocks;

public sealed class MockFriendsService : IFriendsService
{
    static readonly Friend[] Friends = [new("Ranger_Test", true), new("Moss_Walker", false), new("Hunter02", false)];
    static readonly string[] Others = ["Hunter03", "Hunter04", "Pine_Walker", "River_Test"];

    public Task<IReadOnlyList<Friend>> FriendsAsync() => Task.FromResult<IReadOnlyList<Friend>>(Friends);

    public Task<IReadOnlyList<Friend>> SearchAsync(string text) =>
        Task.FromResult<IReadOnlyList<Friend>>(Friends.Select(f => f.Handle).Concat(Others).Where(h => h.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(h => new Friend(h, false)).ToList());
}
