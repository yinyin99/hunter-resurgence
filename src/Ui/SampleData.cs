namespace Resurgence.Ui;

public enum ServerStatus { Online, Busy, Offline }
public sealed record ServerInfo(string Name, string Address, string Region, int Players, int MaxPlayers, int PingMs, string Version, ServerStatus Status);

// ponytail: sample data until the community server exists; replace with real calls then
// Partial: SampleData.Players.cs (players, trophies, hunts, kills, leaderboards, `Me`) and SampleData.Launcher.cs (launcher-only data) live beside this file.
public static partial class SampleData
{
    public const int WalletCredits = 12_450;
    public const string LauncherVersion = "0.0.0-dev";
    // game install detected by the launcher (sample); GameFound = false shows the "not found" state
    public const string GameFolder = @"C:\Games\SampleGame";
    public const bool GameFound = true;

    public static readonly IReadOnlyList<ServerInfo> Servers =
    [
        new("Pinecrest Community EU", "eu.example.org:7000", "Europe", 142, 500, 38, "1.4.2", ServerStatus.Online),
        new("Pinecrest Community NA", "na.example.org:7000", "North America", 96, 500, 112, "1.4.2", ServerStatus.Online),
        new("Silver Fox Hunters", "fox.example.org:7000", "Europe", 421, 500, 41, "1.4.2", ServerStatus.Busy),
        new("Southern Cross Outfitters", "sc.example.org:7000", "Asia-Pacific", 23, 250, 210, "1.4.1", ServerStatus.Online),
        new("Rio Verde Camp", "rv.example.org:7000", "South America", 0, 250, 0, "1.4.2", ServerStatus.Offline),
    ];
}
