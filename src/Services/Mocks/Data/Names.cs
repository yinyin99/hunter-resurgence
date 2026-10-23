namespace Resurgence.Services.Mocks;

/// <summary>Fictional players and real animal names (public) the list mocks are built from.</summary>
static class Names
{
    // ids 19, 23 and 28 are those of the old pages
    public static readonly (int Id, string Name)[] Species =
    [
        (19, "Alpine Ibex"), (28, "American Black Duck"), (23, "Arctic Fox"), (4, "Axis Deer"), (5, "Banteng"), (6, "Bighorn Sheep"), (7, "Bison"), (8, "Black Bear"),
        (9, "Blacktail Deer (Typical)"), (10, "Bobcat"), (11, "Brown Bear"), (12, "Canada Goose"), (13, "Cottontail Rabbit"), (14, "Coyote"), (15, "Dall Sheep"),
        (16, "Eurasian Lynx"), (17, "European Rabbit"), (18, "Fallow Deer"), (20, "Feral Goat"), (21, "Feral Hog"), (22, "Gadwall"), (24, "Grey Wolf"), (25, "Grizzly Bear"),
        (26, "Magpie Goose"), (27, "Mallard"), (29, "Moose"), (30, "Mule Deer (Typical)"), (31, "Northern Pintail"), (32, "Pheasant"), (33, "Polar Bear"), (34, "Puma"),
        (35, "Red Deer"), (36, "Red Fox"), (37, "Reindeer"), (38, "Rock Ptarmigan"), (39, "Roe Deer"), (40, "Turkey"), (41, "Whitetail Deer (Typical)"), (42, "Wood Grouse"),
        (43, "Red Kangaroo"), (44, "Rocky Mountain Elk"), (45, "Roosevelt Elk"), (46, "Rusa Deer"), (47, "Sambar Deer"), (48, "Sitka Deer"), (49, "Snow Goose"), (50, "Snowshoe Hare"),
        (51, "Water Buffalo"), (52, "White-tailed Ptarmigan"), (53, "Wild Boar"), (54, "Willow Ptarmigan"),
    ];

    static readonly string[] First = ["Quiet", "Wild", "Grey", "Silent", "North", "Moss", "Pine", "River"];
    static readonly string[] Second = ["Buck", "Falcon", "Fox", "Oak", "Wind", "Walker", "Ranger", "Hawk"];

    /// <summary>The n-th fictional handle (64 distinct names, then numbered).</summary>
    public static string Handle(int n) => $"{First[n % 8]}{Second[n / 8 % 8]}{(n >= 64 ? "_" + n / 64 : "")}";
}
