namespace Resurgence.Services.Mocks;

/// <summary>The project's three first posts, in English, newest first: the mock website and the launcher without a server show them.</summary>
public sealed class MockNewsFeedService : INewsFeedService
{
    static readonly IReadOnlyList<NewsPost> Posts =
    [
        new(3, new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero), false, "Launcher 0.31: Game check and \"Report a problem\" feature added",
            "The launcher now checks your game before every hunt: version, files, folder, Steam and setup. If something is wrong, it tells you how to fix it. If the game is not found, choose its folder in Settings, Game Folder. If the game won't start, Settings, Report a problem sends us your logs.",
            "link:source", "Follow the project on GitHub", null),
        new(2, new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), false, "The official servers have closed",
            "The official servers closed on 11 November 2026. The backup tool saved your profile: inventory, trophies, statistics and more. The community server is open now.",
            "link:backup", "Download the backup tool", null),
        new(1, new(2026, 11, 9, 11, 0, 0, TimeSpan.Zero), false, "Hunter Resurgence is in development",
            "The community server is being built in the open. Accounts, restoring your backup and this website are taking shape, and the server opens to the public soon.",
            "link:source", "Follow the project on GitHub", null),
    ];

    public Task<IReadOnlyList<NewsPost>> PostsAsync(string language) => Task.FromResult(Posts);
}
