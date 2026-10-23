namespace Resurgence.Ui;

public sealed record PlayerProfile(string Handle, DateOnly MemberSince, bool Online, int Hunts, int Kills, int Trophies, double DistanceKm, int BestScore);
public sealed record Trophy(int Id, int HuntId, string Species, int Score, double WeightKg, string Weapon, string Reserve, DateOnly Date); // Id = the kill's id
public sealed record KillRecord(int Id, int HuntId, string Species, string Gender, double WeightKg, int Score, int DistanceM, string Weapon, string Ammo, string Placement, bool Harvested, bool Ethical);
public sealed record HuntRecord(int Id, string Handle, DateOnly Date, string Reserve, int DurationMin, double DistanceKm, IReadOnlyList<KillRecord> Kills)
{
    public int BestScore => Kills.Count == 0 ? 0 : Kills.Max(k => k.Score);
}
public sealed record LeaderboardRow(string Handle, int Score, int Kills);

// ponytail: sample data until the community server exists; replace with real calls then
public static partial class SampleData
{
    // properties instead of fields: the data builds lazily on first use, so no dependence on static-initializer order across the partial files
    public static PlayerProfile Me => PlayerStore.Players[0];
    public static IReadOnlyList<PlayerProfile> Players => PlayerStore.Players;
    public static IReadOnlyList<string> SpeciesNames => PlayerStore.SpeciesNames;
    public static IReadOnlyList<string> WeaponClasses { get; } = ["Rifle", "Shotgun", "Handgun", "Bow"];
    public static IReadOnlyList<string> Seasons { get; } = ["Season 3", "Season 2", "All time"];

    public static PlayerProfile? FindPlayer(string handle) => PlayerStore.Players.FirstOrDefault(p => p.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<HuntRecord> HuntsOf(string handle) => PlayerStore.Hunts.Where(h => h.Handle == handle).OrderByDescending(h => h.Date).ToList();
    public static IReadOnlyList<Trophy> TrophiesOf(string handle) => PlayerStore.Trophies.Where(t => PlayerStore.Hunts.First(h => h.Id == t.HuntId).Handle == handle).ToList();
    public static HuntRecord? FindHunt(int id) => PlayerStore.Hunts.FirstOrDefault(h => h.Id == id);
    public static KillRecord? FindKill(int id) => PlayerStore.Hunts.SelectMany(h => h.Kills).FirstOrDefault(k => k.Id == id);

    /// <summary>Ranked rows; every filter rescales each hunter's score a deterministic amount so the order changes visibly. Null filter = no filter.</summary>
    public static IReadOnlyList<LeaderboardRow> Leaderboard(string? species, string? weaponClass, string? season) =>
        PlayerStore.Board.Select(b =>
        {
            var f = PlayerStore.Tweak(b.Handle, species, .15, .45)
                  * PlayerStore.Tweak(b.Handle, weaponClass, .3, .8)
                  * (season == "All time" ? 2.4 : PlayerStore.Tweak(b.Handle, season, .6, 1));
            return new LeaderboardRow(b.Handle, (int)(b.Score * f), Math.Max(1, (int)Math.Round(b.Kills * f)));
        }).OrderByDescending(r => r.Score).ThenBy(r => r.Handle).ToList();

    // Field order matters here: Players is built last and fills Hunts/Trophies.
    static class PlayerStore
    {
        sealed record Beast(string Name, double MinKg, double MaxKg, int MaxScore, string[] Classes);
        sealed record Gun(string Name, string Class, string[] Ammo);

        static readonly Beast[] Beasts =
        [
            new("Whitetail Deer", 55, 120, 210, ["Rifle", "Shotgun", "Bow"]),
            new("Moose", 380, 620, 260, ["Rifle", "Bow"]),
            new("Elk", 230, 450, 240, ["Rifle", "Bow"]),
            new("Wild Boar", 70, 190, 190, ["Rifle", "Shotgun", "Handgun", "Bow"]),
            new("Black Bear", 90, 270, 230, ["Rifle", "Handgun", "Bow"]),
            new("Pronghorn", 40, 65, 170, ["Rifle", "Bow"]),
            new("Coyote", 11, 22, 130, ["Rifle", "Shotgun", "Handgun"]),
            new("Red Fox", 4, 9, 120, ["Rifle", "Shotgun", "Handgun"]),
            new("Mallard", 1, 1.4, 100, ["Shotgun", "Bow"]),
            new("Canada Goose", 3, 6.5, 110, ["Shotgun"]),
        ];

        static readonly Gun[] Guns =
        [
            new(".270 bolt-action rifle", "Rifle", ["Soft point", "Match grade"]),
            new(".308 bolt-action rifle", "Rifle", ["Soft point", "Full jacket"]),
            new("12-gauge pump shotgun", "Shotgun", ["Birdshot", "Buckshot", "Slug"]),
            new(".44 revolver", "Handgun", ["Hollow point"]),
            new("Recurve bow", "Bow", ["Broadhead"]),
            new("Compound bow", "Bow", ["Broadhead", "Carbon broadhead"]),
        ];

        static readonly string[] Reserves = ["Pinecrest Valley", "Cedar Ridge", "Willow Marsh", "Stonebrook Highlands", "Amber Prairie"];

        // repeated entries weight the pick: most shots are clean
        static readonly (string Placement, bool Ethical)[] Placements =
        [
            ("Heart", true), ("Heart", true), ("Heart", true), ("Lungs", true), ("Lungs", true), ("Lungs", true),
            ("Neck", true), ("Shoulder", true), ("Liver", false), ("Gut", false),
        ];

        // Me first (Players[0]); score and kills are the base for the leaderboard
        public static readonly (string Handle, int Score, int Kills)[] Board =
        [
            ("SampleHunter", 7420, 96), ("NightOwl", 9840, 131), ("FernWalker", 9410, 127), ("MossyOak", 8975, 119),
            ("RiverStone", 8630, 112), ("TundraWolf", 8190, 108), ("QuietPine", 7280, 94), ("AmberFox", 6955, 90),
            ("CedarSong", 6710, 87), ("GreyHeron", 6340, 83), ("BrookTrout", 6020, 79), ("WillowWisp", 5780, 75),
            ("StagRunner", 5410, 71), ("IronBark", 5120, 66), ("DawnPatrol", 4790, 61), ("LoneCoyote", 4430, 58),
            ("FieldNotes", 4015, 52), ("SilverBirch", 3660, 47), ("HollowLog", 3190, 41), ("PrairieWind", 2740, 35),
        ];

        public static readonly IReadOnlyList<string> SpeciesNames = Beasts.Select(b => b.Name).ToArray();
        public static readonly List<HuntRecord> Hunts = [];
        public static readonly List<Trophy> Trophies = [];
        public static readonly List<PlayerProfile> Players = Build();

        // string.GetHashCode is randomized per process; samples must be stable
        static int Hash(string s)
        {
            var h = 17;
            foreach (var c in s) h = unchecked(h * 31 + c);
            return h & int.MaxValue;
        }

        public static double Tweak(string handle, string? key, double lo, double hi) =>
            key is null ? 1 : lo + Hash(handle + key) % 1000 / 1000.0 * (hi - lo);

        static List<PlayerProfile> Build()
        {
            var players = new List<PlayerProfile>();
            int huntId = 0, killId = 0;
            foreach (var (handle, _, _) in Board)
            {
                var me = handle == "SampleHunter";
                var seed = Hash(handle);
                var r = new Random(seed);
                var date = new DateOnly(2026, 10, 20).AddDays(me ? 0 : -(seed % 6));
                var mine = new List<HuntRecord>();
                for (int i = 0, n = me ? 10 : 2 + seed % 4; i < n; i++)
                {
                    var id = ++huntId;
                    var kills = new List<KillRecord>();
                    for (int k = 0, m = 2 + r.Next(4); k < m; k++) kills.Add(NewKill(r, ++killId, id));
                    mine.Add(new(id, handle, date, Reserves[r.Next(Reserves.Length)], 25 + r.Next(116), Math.Round(2 + r.NextDouble() * 9, 1), kills));
                    date = date.AddDays(-(2 + r.Next(5)));
                }
                Hunts.AddRange(mine);

                var top = mine.SelectMany(h => h.Kills.Select(k => (h, k))).OrderByDescending(x => x.k.Score).Take(me ? 12 : 1 + seed % 4).ToList();
                Trophies.AddRange(top.Select(x => new Trophy(x.k.Id, x.h.Id, x.k.Species, x.k.Score, x.k.WeightKg, x.k.Weapon, x.h.Reserve, x.h.Date)));

                players.Add(new(handle, me ? new DateOnly(2025, 3, 14) : new DateOnly(2024, 1, 1).AddDays(seed % 600), me || seed % 3 == 0,
                    mine.Count, mine.Sum(h => h.Kills.Count), top.Count, Math.Round(mine.Sum(h => h.DistanceKm), 1), mine.Max(h => h.BestScore)));
            }
            return players;
        }

        static KillRecord NewKill(Random r, int id, int huntId)
        {
            var b = Beasts[r.Next(Beasts.Length)];
            var usable = Guns.Where(g => b.Classes.Contains(g.Class)).ToArray();
            var gun = usable[r.Next(usable.Length)];
            var q = Math.Pow(r.NextDouble(), .7); // 0..1, leans high
            var (placement, ethical) = Placements[r.Next(Placements.Length)];
            var (lo, hi) = gun.Class switch { "Rifle" => (60, 320), "Bow" => (12, 45), "Handgun" => (10, 60), _ => (15, 70) };
            return new(id, huntId, b.Name, r.Next(2) == 0 ? "Male" : "Female", Math.Round(b.MinKg + (b.MaxKg - b.MinKg) * q, 1),
                (int)(b.MaxScore * (.35 + .65 * q) * (ethical ? 1 : .6)), r.Next(lo, hi + 1), gun.Name, gun.Ammo[r.Next(gun.Ammo.Length)],
                placement, ethical || r.Next(2) == 0, ethical);
        }
    }
}
