namespace Resurgence.Services;

/// <summary>The game install the launcher uses, how it was found and the other installs it saw. Source: "manual", "steam-option", "steam-library" or null.
/// Gone: the folder chosen by hand that no longer holds the game (the automatic install is used then); null otherwise.</summary>
public sealed record GameInstallInfo(string? Folder, string? Source, IReadOnlyList<string> Others, string? Gone = null);
