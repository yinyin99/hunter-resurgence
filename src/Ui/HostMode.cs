using Resurgence.Services;

namespace Resurgence.Ui;

/// <summary>Which host renders the pages: the launcher window (launcher mode) or the website (web mode). The website sets it per request.</summary>
public sealed class HostMode
{
    public bool Launcher { get; set; }
    /// <summary>Development website only: enables the ?alert= preview used to compare the alert dialog.</summary>
    public bool Dev { get; set; }
    /// <summary>Development website only: mock state from ?mock= (session:out shows the public site).</summary>
    public string? Mock { get; set; }
    /// <summary>True on the website with a community server: what would show a signed-in player mock data is hidden (the pages of <see cref="LauncherGuard.MockOnly"/>, their links,
    /// the status bar's friends, messages and wallet links, a real player's profile tabs, the bank), and the store buys from the server with em$ only (docs/website.md#sign-in).</summary>
    public bool Live { get; set; }

    /// <summary>The picture slot of a store item or bundle. With a community server the ids are the original's, and the stand-in pictures in the operator's folder are named after the mock's
    /// fictional ids (<c>item-1227</c> is a hat there), so a real item must not find one: it has its own names, empty until its picture is mapped (docs/images.md).</summary>
    public string ItemSlot(int id) => Live ? $"store-item-{id}" : $"item-{id}";

    public string BundleSlot(int id) => Live ? $"store-bundle-{id}" : $"bundle-{id}";

    /// <summary>The picture slot of a feed slide: the live site's slides are the original's and are named after the picture they showed (a new recording with a new picture is a
    /// new slot), never the mock's stand-in pictures, which are named after the target.</summary>
    public string SlideSlot(Resurgence.Services.Featured slide) => Live ? $"splash-{slide.Image ?? "none"}" : $"news-{slide.Target}";

    /// <summary>How often the status bar and the messages page read the server again: 30 seconds as the old interface; a development hook that lets something arrive shortens it to 2.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Mock is "poll:fast" or "friends:incoming" or "messages:incoming" ? 2 : 30);

    /// <summary>Mock session:out signs out, session:signup signs in an account that has no username yet, eula an account that has not accepted the agreements.</summary>
    public async Task ApplyMockSessionAsync(ISessionService sessions)
    {
        if (Mock == "session:out") await sessions.SignOutAsync();
        else if (Mock == "session:signup") await sessions.SignInAsync("dev@example.com", "000000", register: true);
        else if (Mock == "eula" && sessions is Resurgence.Services.Mocks.MockSessionService mock) mock.RequireLicense();
    }
}
