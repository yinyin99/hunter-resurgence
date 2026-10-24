namespace Resurgence.Services.Mocks;

/// <summary>
/// The friends of the signed-in hunter, kept per circuit (the website) or per run (the launcher). Mock "friends:requests" starts with three requests,
/// "friends:incoming" lets one arrive five seconds after the start.
/// </summary>
public sealed class MockFriendsService(string? mock = null) : IFriendsService
{
    const string Own = "Hunter01";
    static readonly string[] Others = ["Hunter03", "Hunter04", "Pine_Walker", "River_Test"];

    readonly List<string> friends = ["Ranger_Test", "Moss_Walker", "Hunter02"];
    readonly List<string> requests = mock == "friends:requests" ? ["Quiet_Buck", "Grey_Falcon", "North_Oak"] : [];
    readonly List<string> pending = [];
    readonly DateTime started = DateTime.UtcNow;
    bool arrived;

    public Task<IReadOnlyList<Friend>> FriendsAsync() => Task.FromResult<IReadOnlyList<Friend>>(friends.Select(h => new Friend(h, h == "Ranger_Test")).ToList());

    // everyone the mock knows, whether a friend or not, and oneself, whom a search never lists
    static readonly string[] Everyone = ["Ranger_Test", "Moss_Walker", "Hunter01", "Hunter02", .. Others];

    public Task<IReadOnlyList<Friend>> SearchAsync(string text) =>
        Task.FromResult<IReadOnlyList<Friend>>(Everyone.Where(h => h != Own && h.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(h => new Friend(h, false)).ToList());

    public Task<IReadOnlyList<string>> RequestsAsync()
    {
        if (mock == "friends:incoming" && !arrived && DateTime.UtcNow - started > TimeSpan.FromSeconds(5)) { arrived = true; requests.Add("Pine_Walker"); }
        return Task.FromResult<IReadOnlyList<string>>(requests.ToList());
    }

    public Task<IReadOnlyList<string>> PendingAsync() => Task.FromResult<IReadOnlyList<string>>(pending.ToList());

    public Task<Result> SendRequestAsync(string handle)
    {
        if (!friends.Contains(handle) && !pending.Contains(handle)) pending.Add(handle);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> AcceptAsync(string handle)
    {
        if (requests.Remove(handle) && !friends.Contains(handle)) friends.Add(handle);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> DeclineAsync(string handle)
    {
        requests.Remove(handle);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> RemoveAsync(string handle)
    {
        friends.Remove(handle);
        return Task.FromResult(Result.Success);
    }
}
