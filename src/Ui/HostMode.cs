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

    /// <summary>Mock session:out signs out, session:signup signs in an account that has no username yet.</summary>
    public async Task ApplyMockSessionAsync(ISessionService sessions)
    {
        if (Mock == "session:out") await sessions.SignOutAsync();
        else if (Mock == "session:signup") await sessions.SignInAsync("dev@example.com", "000000", register: true);
    }
}
