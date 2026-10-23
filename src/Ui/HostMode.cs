namespace Resurgence.Ui;

/// <summary>Which host renders the pages: the launcher window (launcher mode) or the website (web mode). The website sets it per request.</summary>
public sealed class HostMode
{
    public bool Launcher { get; set; }
}
