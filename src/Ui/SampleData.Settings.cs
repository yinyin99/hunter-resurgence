namespace Resurgence.Ui;

/// <summary>One group of a backup preview; Name is a key suffix (Restore.Group.{Name}).</summary>
public sealed record RestoreGroup(string Name, int Count);

// ponytail: sample data until the community server exists; replace with real calls then
public static partial class SampleData
{
    // preview of what a backup zip would restore
    public static readonly IReadOnlyList<RestoreGroup> RestoreGroups =
    [
        new("Inventory", 86),
        new("Trophies", 41),
        new("Hunts", 312),
        new("Statistics", 24),
        new("Skills", 18),
        new("Missions", 57),
        new("Wallet", 8_320),
        new("Friends", 9),
    ];

    // video memory the game settings page pretends to detect
    public const int VideoMemoryMb = 4096;
}
