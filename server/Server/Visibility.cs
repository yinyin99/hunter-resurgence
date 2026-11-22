// Who a player may see of the others (docs/server.md#who-sees-whom): nobody can list every player, so a board shows its top 100 and, to a signed-in player,
// his own place with the 50 above and the 50 below it; a player sees himself, his friends and whoever a board shows him.
namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The places of a board every signed-in player sees, and how far above and below his own place he also sees.</summary>
    public const int BoardTop = 100, BoardAround = 50;

    /// <summary>Whether the place <paramref name="rank"/> of a board is shown to a player whose own place there is <paramref name="mine"/> (0: not on it): the top 100, and the 50 above and below his.</summary>
    public static bool OnView(long rank, long mine) => rank <= BoardTop || mine > 0 && Math.Abs(rank - mine) <= BoardAround;

    /// <summary>The rows of a board (best first, 0-based) a player sees beyond its top 100: those of his window (<see cref="OnView"/>) past the top, as <c>Skip(From).Take(Count)</c>; Count 0 when he is not on it (<paramref name="mine"/> 0) or within the top 50.</summary>
    public static (int From, int Count) Window(long mine)
    {
        var from = (int)Math.Max(BoardTop, mine - 1 - BoardAround);
        return (from, mine <= 0 ? 0 : (int)Math.Max(0, mine + BoardAround - from));
    }

    /// <summary>Whether <paramref name="caller"/> may see <paramref name="other"/> (account ids): himself, his friends, and whoever a board shows him (<see cref="OnView"/>):
    /// the hunterscore board, a species board (score or range) of any season or all time, a minigame's board. Never an account that is on no board (unknown, banned or the recorded player's).
    /// ponytail: walks every kept species board and reads both minigame boards (every account's statistics) when the cheaper checks fail; keep each player's places per board if profile reads get slow.</summary>
    public bool CanSee(long caller, long other)
    {
        if (caller == other) return true;
        if (Store.Listed(other, LocalAccount) is null) return false;
        if (Store.FriendsOf(caller, LocalAccount).Friends.Contains(other)) return true;
        if (OnView(Store.Rank(other, LocalAccount), Store.Rank(caller, LocalAccount))) return true;
        foreach (var season in (long[])[0, .. Store.Seasons().Select(s => s.No)])
            foreach (var range in (bool[])[false, true])
                foreach (var board in Boards(range, season).Values)
                    if (Shows(board.Select(r => r.Account), caller, other)) return true;
        return Store.GameIds.Keys.Any(kind => Shows(Store.GameBoard(kind, LocalAccount).Select(r => r.Id), caller, other));
    }

    /// <summary>Whether a board (its accounts, best first) shows <paramref name="other"/> to <paramref name="caller"/>.</summary>
    static bool Shows(IEnumerable<long> board, long caller, long other)
    {
        long rank = 0, mine = 0, theirs = 0;
        foreach (var id in board)
        {
            rank++;
            if (id == caller) mine = rank;
            else if (id == other) theirs = rank;
        }
        return theirs > 0 && OnView(theirs, mine);
    }
}
