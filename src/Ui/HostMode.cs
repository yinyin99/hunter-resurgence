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
    /// <summary>False on the website with a community server: buying is still the mock's there and would spend a wallet that is not the account's, so the buy buttons and the bank are hidden (docs/website.md#sign-in).</summary>
    public bool Purchases { get; set; } = true;

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
