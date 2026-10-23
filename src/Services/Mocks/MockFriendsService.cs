namespace Resurgence.Services.Mocks;

public sealed class MockFriendsService : IFriendsService
{
    public Task<IReadOnlyList<Friend>> FriendsAsync() =>
        Task.FromResult<IReadOnlyList<Friend>>([new("Ranger_Test", true), new("Moss_Walker", false), new("Hunter02", false)]);
}
