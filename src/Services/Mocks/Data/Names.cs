namespace Resurgence.Services.Mocks;

/// <summary>Fictional players and real animal names (public) the list mocks are built from.</summary>
static class Names
{
    // ids 19, 23 and 28 are those of the old pages
    public static readonly (int Id, string Name)[] Species =
    [
        (19, "Alpine Ibex"), (28, "American Black Duck"), (23, "Arctic Fox"), (54, "Axis Deer"), (46, "Banteng"), (39, "Bighorn Sheep"), (24, "Bison"), (9, "Black Bear"),
        (6, "Blacktail Deer (Typical)"), (43, "Bobcat"), (13, "Brown Bear"), (16, "Canada Goose"), (17, "Cottontail Rabbit"), (4, "Coyote"), (41, "Dall Sheep"),
        (49, "Eurasian Lynx"), (20, "European Rabbit"), (55, "Fallow Deer"), (40, "Feral Goat"), (5, "Feral Hog"), (29, "Gadwall"), (36, "Grey Wolf"), (37, "Grizzly Bear"),
        (47, "Magpie Goose"), (15, "Mallard"), (8, "Moose"), (1, "Mule Deer (Typical)"), (27, "Northern Pintail"), (7, "Pheasant"), (30, "Polar Bear"), (52, "Puma"),
        (12, "Red Deer"), (14, "Red Fox"), (18, "Reindeer"), (34, "Rock Ptarmigan"), (11, "Roe Deer"), (2, "Turkey"), (0, "Whitetail Deer (Typical)"), (57, "Wood Grouse"),
        (21, "Red Kangaroo"), (38, "Rocky Mountain Elk"), (3, "Roosevelt Elk"), (44, "Rusa Deer"), (45, "Sambar Deer"), (26, "Sitka Deer"), (51, "Snow Goose"), (25, "Snowshoe Hare"),
        (42, "Water Buffalo"), (35, "White-tailed Ptarmigan"), (10, "Wild Boar"), (33, "Willow Ptarmigan"),
    ];

    static readonly string[] First = ["Quiet", "Wild", "Grey", "Silent", "North", "Moss", "Pine", "River"];
    static readonly string[] Second = ["Buck", "Falcon", "Fox", "Oak", "Wind", "Walker", "Ranger", "Hawk"];

    /// <summary>The n-th fictional handle (64 distinct names, then numbered).</summary>
    public static string Handle(int n) => $"{First[n % 8]}{Second[n / 8 % 8]}{(n >= 64 ? "_" + n / 64 : "")}";
}
