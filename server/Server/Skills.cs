// Hunter skills: the XP curve behind `Skill/list` numbers and the XP heartbeat events earn
// (XP curve and heartbeat XP rules). XP here includes the 0.1 every skill starts with (a fresh skill reads 1.007).
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class Skills
{
    public const double FRESH_XP = 0.1;
    public const int MAX_LEVEL = 20;
    public const double SPOT_XP = 2.0;  // event 11: spotting of the animal's species
    public const double HIT_XP = 2.0;  // event 1 with permitted ammo: the weapon's family; kills and misses earn nothing
    public const double GROUND_BIRD = 0.5;  // py_data animal\species.json experience.WEAPON {"": 0.5, "FLYING": 1}, set on the BIRDS_AG species
    public static readonly double[] TRACK_XP = [0.8, 1.2, 1.6, 0.4];  // event 12: the n-th clue on one animal in a hunt; 0.2 from the 5th on

    /// <summary>XP at which `level` starts: level L spans 15 * L XP.</summary>
    public static double Threshold(long level) => 7.5 * level * (level - 1);

    /// <summary>Skill/list number: level plus the fraction of its span, 3 decimals; int 20 from Threshold(20) on.</summary>
    public static JsonNode Value(double xp)
    {
        long level = 1;
        while (level < MAX_LEVEL && xp >= Threshold(level + 1)) level++;
        return level == MAX_LEVEL ? JsonValue.Create((long)MAX_LEVEL) : JsonValue.Create(Py.Round(level + (xp - Threshold(level)) / (15 * level), 3));
    }

    /// <summary>Inverse of Value() (to 0.1 XP; above level ~10 the 3 decimals blur it by up to 0.15).</summary>
    public static double Xp(double number)
    {
        var level = Math.Min((long)number, MAX_LEVEL);
        return Py.Round(Threshold(level) + (number - level) * 15 * level, 1);
    }

    /// <summary>Weapon XP of one permitted hit.</summary>
    public static double HitXp(long species, JsonNode? animalState)
    {
        var groundBird = (int)species == species && Scoring.BIRDS_AG.Contains((int)species) && !Scoring.InFlying(animalState);
        return HIT_XP * (groundBird ? GROUND_BIRD : 1);
    }

    /// <summary>Tracking XP of the n-th clue (event 12) on one animal in one hunt.</summary>
    public static double TrackXp(long n)
    {
        if (n > TRACK_XP.Length) return 0.2;
        return TRACK_XP[n - 1 < 0 ? n - 1 + TRACK_XP.Length : n - 1];  // negative index counts from the end, like the list
    }

    /// <summary>Level-up toast text, e.g. "Mule Deer Tracking is now level 2".</summary>
    public static string LevelUpText(string kind, string? name, long level)
    {
        var skill = kind == "weapon" ? name ?? "None" : $"{name ?? "None"} {(kind.Length == 0 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant())}";
        return $"{skill}: level {level} reached";
    }
}
