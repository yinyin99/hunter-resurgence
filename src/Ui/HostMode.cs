namespace Resurgence.Ui;

/// <summary>Which host renders the pages: the launcher window (launcher mode) or the website (web mode). The website sets it per request.</summary>
public sealed class HostMode
{
    public bool Launcher { get; set; }
    /// <summary>Development website only: enables the ?alert= preview used to compare the alert dialog.</summary>
    public bool Dev { get; set; }
}
