namespace Resurgence.Services;

/// <param name="Sheet">Id of the kill (or antler) sheet the value links to; 0 for the boards without one.</param>
/// <param name="Avatar">The slot or link of the hunter's picture (<see cref="Avatars.Slot"/>).</param>
/// <param name="Weapon">Name of the weapon of the kill; "" when unknown or not on the board.</param>
/// <param name="When">Time of the kill; null when the board shows none.</param>
public sealed record Ranked(int Rank, string Handle, double Value, int Sheet = 0, string Avatar = Avatars.Default, string Weapon = "", DateTimeOffset? When = null);

/// <summary>A season of the species boards: 1 is the restored harvests (up to the community server's start), later ones the server's. <see cref="End"/> is null for the current one. Season 0, all time, is none of these.</summary>
public sealed record Season(int No, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>One group of an overview: a species (or an antler type) and its best players.</summary>
public sealed record Board(int Id, string Name, IReadOnlyList<Ranked> Top);

/// <summary>Winner of a past season for one species: best score and best range.</summary>
public sealed record HallEntry(string Species, Ranked Score, Ranked Range);

/// <summary>Species boards and the hall of fame answer for a season: 0 all time, else a <see cref="Season"/>'s number. The other boards ignore it.</summary>
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
}
