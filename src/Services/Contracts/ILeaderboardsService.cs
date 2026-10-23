namespace Resurgence.Services;

public sealed record LeaderRow(int Rank, string Handle, double Value);

public interface ILeaderboardsService
{
    /// <param name="board">score, range, hunterscore, halloffame, lanebandit, bustthrough or antlers</param>
    Task<IReadOnlyList<LeaderRow>> RowsAsync(string board, int offset);
}
