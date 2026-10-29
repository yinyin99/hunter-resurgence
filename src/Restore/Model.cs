using System.Text.Json.Nodes;

namespace Resurgence.Restore;

/// <summary>The server's own item catalogue (never one taken from an upload): true when the item has units, a count.</summary>
public interface IItemCatalogue
{
    bool HasUnits(int itemId);
}

/// <summary>A player as read from a backup zip, free of any storage or server type.
/// Extras: every saved JSON reply that is not fully mapped to a typed member (keyed by its path in the zip), plus rows that could not be mapped.</summary>
public sealed record PlayerModel(Identity Identity, Profile Profile, AccountDefaults Account, Wallet? Wallet, IReadOnlyList<ItemRow> Items, SkillSet? Skills,
    StatsBlock? Stats, IReadOnlyList<AchievementRow> Achievements, IReadOnlyList<RawRow> Trophies, IReadOnlyList<RawRow> Lodges,
    IReadOnlyDictionary<string, JsonNode?> Extras);

/// <summary>Mode: "my-account" or "public". Format: 1 for a manifest without one (every release before format 2). ClaimHash is a hint only: the key is per PC, not per account.</summary>
public sealed record Identity(string Uid, string? Handle, string? SteamId64, string Mode, int Format, string? ToolVersion, string ClaimHash, DateTime? StartedUtc, DateTime? CompletedUtc);

public sealed record Profile(long Id, string? Handle, string? Hostname, int Gender, int Hunterscore, string? AvatarUrl, string? MembershipType, int MembershipInt,
    string? Uuid, string? Locale, string? Timezone, string? Country, long? MemberSince, int UserGroup, int? Rank);

/// <summary>Fields of the game's login reply that no web reply holds: the importer's defaults. Avatar ids and the account id are assigned by the server.</summary>
public sealed record AccountDefaults(bool FacebookEnabled, int ShareOnFacebook, string? FacebookId, string? Email, int EmailVerified, int HasPassword,
    int AccountState, int FirstTime, int Face, string? SteamId, int UseImperial, int UserGroup);

public sealed record Wallet(double Em, double Gm);

/// <summary>Integer millimetres.</summary>
public sealed record Vec3I(int X, int Y, int Z);

/// <summary>Units: the saved value only when the catalogue item has units, else null. WebReserve: location.reserve (from 1).</summary>
public sealed record ItemRow(long Guid, int ItemId, int Slot, int? Units, JsonObject Metadata, Vec3I? Position, Vec3I? Direction, int? WebReserve);

/// <summary>Values as Skill/list sends them (level plus progress); Floor() gives the levels the game connects with.</summary>
public sealed record SkillSet(IReadOnlyDictionary<int, double> Weapon, double[] Tracking, double[] Spotting)
{
    public SkillSet Floor() => new(Weapon.ToDictionary(w => w.Key, w => Math.Floor(w.Value)), [.. Tracking.Select(Math.Floor)], [.. Spotting.Select(Math.Floor)]);
}

/// <summary>UserStats: the four data sub-objects of Statistics/total (collectables, weapon, animal, daily_missions).</summary>
public sealed record StatsBlock(JsonObject UserStats, double Distance, double Duration, int? BustThroughScore, int? LaneBanditScore);

/// <summary>Triggers: threshold to unix time (the earn times).</summary>
public sealed record AchievementRow(int Id, int CategoryId, int GroupId, JsonNode? Value, JsonObject Triggers);

/// <summary>A reply row carried as sent. Source: the endpoint it came from; Id: its id (a lodge reply or harvest: the id in the path).</summary>
public sealed record RawRow(string Source, string Id, JsonNode? Node);

/// <summary>What was read. Complete: "yes" (completed_utc present), "no", or "unknown" (a zip from before format 2 without it).
/// UnitsRuleUsed: "catalogue", or "zero-only" when no catalogue was given (0 reads as null, every other value is kept).</summary>
public sealed record Report(string Mode, IReadOnlyList<string> Restored, IReadOnlyList<string> Unsupported, IReadOnlyList<string> Missing, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failed, IReadOnlyList<string> Rejected, string Complete, string UnitsRuleUsed, bool MembershipInferred)
{
    public string ToText()
    {
        var w = new System.Text.StringBuilder();
        w.AppendLine($"mode: {Mode}").AppendLine($"complete: {Complete}").AppendLine($"units rule: {UnitsRuleUsed}");
        if (MembershipInferred) w.AppendLine("membership: inferred");
        foreach (var (title, lines) in new[] { ("restored", Restored), ("unsupported", Unsupported), ("missing", Missing), ("warnings", Warnings), ("failed", Failed), ("rejected", Rejected) })
        {
            w.AppendLine($"{title}: {lines.Count}");
            foreach (var l in lines) w.AppendLine("  " + l);
        }
        return w.ToString();
    }
}

/// <summary>Restore result: the model and the report. The same zip always gives the same of both.</summary>
public sealed record Restored(PlayerModel Model, Report Report);

/// <summary>The zip is refused as a whole; the message says why.</summary>
public sealed class RestoreException(string message) : Exception(message);
