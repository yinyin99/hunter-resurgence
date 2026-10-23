namespace Resurgence.Services;

public sealed record Ranked(int Rank, string Handle, double Value);

/// <summary>One group of an overview: a species (or an antler type) and its best players.</summary>
public sealed record Board(int Id, string Name, IReadOnlyList<Ranked> Top);

/// <summary>Winner of a past season for one species: best score and best range.</summary>
public sealed record HallEntry(string Species, Ranked Score, Ranked Range);

public interface ILeaderboardsService
{
    /// <summary>The current season number.</summary>
    Task<int> SeasonAsync();
    /// <param name="kind">score, range, antlers-single or antlers-double</param>
    Task<IReadOnlyList<Board>> BoardsAsync(string kind);
    /// <param name="kind">score, range, hunterscore, lanebandit, bustthrough, antlers-single or antlers-double</param>
    /// <param name="id">species or antler group; 0 for the boards without groups</param>
    Task<Page<Ranked>> RowsAsync(string kind, int id, int page);
    /// <summary>The player's own place; null when not ranked.</summary>
    Task<Ranked?> MyRankAsync(string kind, int id);
    Task<IReadOnlyList<HallEntry>> HallAsync(int season);
}
