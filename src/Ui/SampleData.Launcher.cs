namespace Resurgence.Ui;

public sealed record NewsItem(string Title, DateOnly Date, string Excerpt);
public sealed record ReserveInfo(string Name, string Description);
public sealed record OpenGame(string Host, string Reserve, int Players, int MaxPlayers, bool Locked);
public sealed record StoreItem(string Category, string Name, string Description, int Price, bool Owned = false);
/// <summary>Daily mission; done when Progress reaches Goal.</summary>
public sealed record MissionInfo(string Title, string Description, int Progress, int Goal, int Credits, string? Item = null, bool Claimed = false);
/// <summary>Mission chain; Current = completed steps (index of the current step), done when it equals Steps.Count.</summary>
public sealed record MissionChain(string Title, string Description, IReadOnlyList<string> Steps, int Current, int Credits, string? Item = null, bool Claimed = false);
public enum FriendActivity { InHunt, InLauncher, Offline }
/// <summary>Reserve is set while InHunt; LastSeenDays while Offline.</summary>
public sealed record FriendInfo(string Handle, FriendActivity Activity, string? Reserve = null, int LastSeenDays = 0);

// ponytail: sample data until the community server exists; replace with real calls then
public static partial class SampleData
{
    public static readonly IReadOnlyList<NewsItem> News =
    [
        new("Winter hunting season opens", new(2026, 10, 14), "Snow arrives on Cedar Ridge and Granite Basin, with new tracks and quieter herds."),
        new("Server maintenance on Saturday", new(2026, 10, 9), "Community servers restart at 04:00 UTC. Expect about twenty minutes of downtime."),
        new("Launcher 0.1 preview notes", new(2026, 10, 2), "A new play screen, a store, daily missions and a friends list are coming together."),
        new("Weekend event: marsh waterfowl", new(2026, 9, 25), "Double credits for waterfowl on Willow Marsh until Sunday evening."),
    ];

    public static readonly IReadOnlyList<ReserveInfo> Reserves =
    [
        new("Pinecrest Valley", "Rolling pine forest with open meadows and shallow creeks."),
        new("Cedar Ridge", "Steep slopes and thick cover, famous for early morning fog."),
        new("Willow Marsh", "Wetlands, reed beds and long shots across still water."),
        new("Granite Basin", "Rocky bowl with sparse trees and wide sight lines."),
        new("Amber Prairie", "Golden grassland where herds roam in the open."),
        new("Frostline Tundra", "Cold, windswept and quiet. Dress for it."),
    ];

    public static readonly IReadOnlyList<OpenGame> OpenGames =
    [
        new("NorthernOtter", "Pinecrest Valley", 2, 4, false),
        new("QuietBirch", "Cedar Ridge", 3, 4, true),
        new("MarshWalker", "Willow Marsh", 1, 6, false),
        new("RidgeRunner42", "Granite Basin", 4, 4, false),
        new("FoxglovePilot", "Amber Prairie", 2, 8, true),
    ];

    public static readonly IReadOnlyList<string> StoreCategories = ["weapons", "ammo", "optics", "gear", "licences", "equipment"];

    public static readonly IReadOnlyList<StoreItem> StoreItems =
    [
        new("weapons", "Bolt-action rifle", "Reliable and accurate at range.", 3200, true),
        new("weapons", "Lever-action carbine", "Quick handling for dense cover.", 2400),
        new("weapons", "Pump shotgun", "Heavy hitter for close work.", 1900),
        new("weapons", "Break-action shotgun", "Two shots, light and simple.", 1500),
        new("weapons", "Recurve bow", "Silent and demanding.", 1200),
        new("weapons", "Compound bow", "More power, easier draw.", 2800),
        new("weapons", "Magnum rifle", "For the biggest game only.", 6500),
        new("ammo", "Soft-point rounds", "Box of 20. Expands on impact.", 120, true),
        new("ammo", "Full-metal rounds", "Box of 20. Deep penetration.", 100),
        new("ammo", "Buckshot shells", "Box of 25. Wide pattern.", 90),
        new("ammo", "Bird shot shells", "Box of 25. For waterfowl.", 80),
        new("ammo", "Field arrows", "Set of 12. Everyday arrows.", 150),
        new("ammo", "Broadhead arrows", "Set of 6. Cuts clean.", 210),
        new("optics", "Fixed 4x scope", "Simple and sturdy.", 700),
        new("optics", "Variable 3-9x scope", "A good all-rounder.", 1400, true),
        new("optics", "Long-range 6-24x scope", "Built for patient shooters.", 3100),
        new("optics", "Red-dot sight", "Fast target pickup.", 900),
        new("optics", "Rangefinder", "Distance at a glance.", 1100),
        new("optics", "Compact binoculars", "Find the herd first.", 600),
        new("optics", "Thermal monocular", "See warm bodies in the dark.", 8200),
        new("gear", "Camo jacket", "Breaks up your outline.", 450),
        new("gear", "Insulated boots", "Warm feet, quiet steps.", 380, true),
        new("gear", "Rain poncho", "Stays dry in the marsh.", 220),
        new("gear", "Hunting backpack", "Room for a full day out.", 520),
        new("gear", "Hearing protection", "Muffles shots, keeps footsteps.", 340),
        new("gear", "Field knife", "For after the hunt.", 260),
        new("licences", "Whitetail deer licence", "Hunt whitetail deer on any reserve.", 800),
        new("licences", "Moose licence", "Large game permit.", 1600),
        new("licences", "Wild boar licence", "Open season, all year.", 700, true),
        new("licences", "Waterfowl licence", "Ducks and geese.", 650),
        new("licences", "Black bear licence", "Seasonal permit.", 1800),
        new("licences", "Pronghorn licence", "Plains game permit.", 1300),
        new("equipment", "Ground blind", "A quick hide for any spot.", 900),
        new("equipment", "Tree stand", "A high seat with a view.", 1100),
        new("equipment", "Deer call", "Draws curious animals in.", 240, true),
        new("equipment", "Duck decoys", "Set of 6 floating decoys.", 310),
        new("equipment", "Scent spray", "Hides your trail from the wind.", 280),
        new("equipment", "Trail camera", "Watch a spot while you are away.", 1250),
        new("equipment", "Game cart", "Carry a heavy harvest back.", 750),
    ];

    public static readonly IReadOnlyList<MissionInfo> DailyMissions =
    [
        new("Clean shot", "Harvest 3 animals with a single shot each.", 3, 3, 400),
        new("Early riser", "Start a hunt before sunrise.", 1, 1, 250, Claimed: true),
        new("Long way out", "Track an animal for more than 300 metres.", 180, 300, 500),
        new("Marsh patrol", "Spend 20 minutes on Willow Marsh.", 7, 20, 300, "Duck decoys"),
    ];

    public static readonly IReadOnlyList<MissionChain> MissionChains =
    [
        new("Master of the ridge", "A season of tracking on Cedar Ridge.",
            ["Scout the ridge", "Find a feeding ground", "Harvest a trophy buck", "Return at dusk"], 2, 2500, "Camo jacket"),
        new("First steps", "Everything a new hunter should try once.",
            ["Take a first shot", "Use a call", "Harvest an animal"], 3, 800),
        new("Waterfowl season", "Follow the flocks across the marsh.",
            ["Set up decoys", "Harvest 5 birds", "Harvest a rare species"], 0, 1500, "Waterfowl licence"),
    ];

    public static readonly IReadOnlyList<FriendInfo> Friends =
    [
        new("NorthernOtter", FriendActivity.InHunt, "Pinecrest Valley"),
        new("QuietBirch", FriendActivity.InHunt, "Cedar Ridge"),
        new("MarshWalker", FriendActivity.InLauncher),
        new("RidgeRunner42", FriendActivity.Offline, LastSeenDays: 2),
        new("FoxglovePilot", FriendActivity.Offline, LastSeenDays: 0),
        new("AmberTracker", FriendActivity.Offline, LastSeenDays: 11),
    ];

    public static readonly IReadOnlyList<string> IncomingRequests = ["WillowWren", "GraniteGuide"];
    public static readonly IReadOnlyList<string> OutgoingRequests = ["TundraTom"];
}
