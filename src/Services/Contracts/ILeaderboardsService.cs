namespace Resurgence.Services;

/// <param name="Sheet">Id of the kill (or antler) sheet the value links to; 0 for the boards without one.</param>
/// <param name="Avatar">The slot or link of the hunter's picture (<see cref="Avatars.Slot"/>).</param>
/// <param name="Weapon">Name of the weapon of the kill; "" when unknown or not on the board.</param>
/// <param name="When">Time of the kill; null when the board shows none.</param>
public sealed record Ranked(int Rank, string Handle, double Value, long Sheet = 0, string Avatar = Avatars.Default, string Weapon = "", DateTimeOffset? When = null);

/// <summary>A season of the species boards: 1 is the restored harvests (up to the community server's start), later ones the server's. <see cref="End"/> is null for the current one. Season 0, all time, is none of these.</summary>
public sealed record Season(int No, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>One group of an overview: a species (or an antler type) and its best players.</summary>
public sealed record Board(int Id, string Name, IReadOnlyList<Ranked> Top);

/// <summary>Winner of a past season for one species: best score and best range.</summary>
public sealed record HallEntry(string Species, Ranked? Score, Ranked? Range);

/// <summary>A reserve's row of the hunting statistics: the hours hunted there by every hunter, gm$ earned per hour and per harvest at the rewards' base (no bonus), harvests per hour and the number of hunters behind the row.</summary>
public sealed record ReserveHunting(string Reserve, double Hours, double GmPerHour, double GmPerHarvest, double HarvestsPerHour, int Players, double ComparableGmPerHour, double ComparableGmPerHarvest);

/// <summary>A species' row of the hunting statistics, with the reserve where it earns most gm$ per hour (null when none).</summary>
public sealed record SpeciesHunting(string Species, double Hours, double GmPerHour, double GmPerHarvest, double HarvestsPerHour, int Players, double ComparableGmPerHour, double ComparableGmPerHarvest, string? BestReserve = null);

/// <summary>The server-wide hunting statistics: figures of all hunters together, never a player's name; a row built from too few hunters is left out by the server.</summary>
public sealed record HuntingStats(IReadOnlyList<ReserveHunting> Reserves, IReadOnlyList<SpeciesHunting> Species, IReadOnlyList<HuntingEvent> Events);

/// <summary>A bonus event inside the period the hunting statistics cover (the paid gm$ include it, the comparable ones do not); the multiplier is null when it has none.</summary>
public sealed record HuntingEvent(string Name, DateTimeOffset Start, DateTimeOffset End, double? Multiplier);

/// <summary>A board that could not be read: the server did not answer (in time) or answered an error. An empty board is an answer, not this.</summary>
public sealed class BoardUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Species boards and the hall of fame answer for a season: 0 all time, else a <see cref="Season"/>'s number. The other boards ignore it.</summary>
/// <remarks>Every method but <see cref="MyRankAsync"/> throws <see cref="BoardUnavailableException"/> when the board cannot be read.</remarks>
public interface ILeaderboardsService
{
    /// <summary>The current season number and the seasons (0, all time, is not listed).</summary>
    Task<(int Current, IReadOnlyList<Season> Seasons)> SeasonsAsync();
    /// <param name="kind">score, range, antlers-single or antlers-double</param>
    Task<IReadOnlyList<Board>> BoardsAsync(string kind, int season);
    /// <param name="kind">score, range, hunterscore, lanebandit, bustthrough, antlers-single or antlers-double</param>
    /// <param name="id">species or antler group</param>
    Task<Page<Ranked>> RowsAsync(string kind, int id, int page, int season);
    /// <summary>The player's own place; null when not ranked.</summary>
    Task<Ranked?> MyRankAsync(string kind, int id, int season);
    /// <summary>The best of each species of an ended season; none for all time and for the current season.</summary>
    Task<IReadOnlyList<HallEntry>> HallAsync(int season);
    /// <summary>The server-wide hunting statistics; empty when the server has none.</summary>
    Task<HuntingStats> HuntingAsync();
}
