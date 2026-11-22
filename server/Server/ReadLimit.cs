// Per-account caps on the reads that could walk the players: profile and hunt reads (ReadAllowed), pictures (PictureAllowed: one gallery page loads
// a picture per tile, so they have their own, larger cap) and the friend search (SearchAllowed).
namespace Resurgence.Server;

public sealed partial class State
{
    public const int ReadsPerMinute = 120, PicturesPerMinute = 600, SearchesPerHour = 30;

    /// <summary>Called with the account when it keeps hitting a cap (at most once an hour per account): the server alerts the review channel.</summary>
    public Action<long>? ReadAlert { get; set; }

    // ponytail: in memory, so a restart forgets the counts; one queue of times per account and cap, pruned as it is read
    enum Cap { Read, Picture, Search }
    readonly Dictionary<(long Account, Cap Cap), Queue<double>> readTimes = [];
    readonly Dictionary<long, double> readAlerted = [];

    /// <summary>Count one profile or hunt read of the account: false, nothing counted, past <see cref="ReadsPerMinute"/> in the last minute
    /// (the route answers 429 `rate_limited`, or the game's empty reply).</summary>
    public bool ReadAllowed(long account) => Take(account, Cap.Read, ReadsPerMinute, 60) || Refused(account);

    /// <summary>Count one read of another player's picture: false, nothing counted, past <see cref="PicturesPerMinute"/> in the last minute.</summary>
    public bool PictureAllowed(long account) => Take(account, Cap.Picture, PicturesPerMinute, 60) || Refused(account);

    /// <summary>Count one friend search of the account: false, nothing counted, past <see cref="SearchesPerHour"/> in the last hour.</summary>
    public bool SearchAllowed(long account) => Take(account, Cap.Search, SearchesPerHour, 3600) || Refused(account);

    bool Take(long account, Cap cap, int max, double seconds)
    {
        lock (readTimes)
        {
            var now = Clock();
            if (!readTimes.TryGetValue((account, cap), out var times))
                readTimes[(account, cap)] = times = new();
            while (times.Count > 0 && now - times.Peek() >= seconds) times.Dequeue();
            if (times.Count >= max) return false;
            times.Enqueue(now);
            return true;
        }
    }

    /// <summary>A refusal: raises <see cref="ReadAlert"/> for the account at most once an hour; always false.</summary>
    bool Refused(long account)
    {
        lock (readTimes)
        {
            var now = Clock();
            if (readAlerted.TryGetValue(account, out var at) && now - at < 3600) return false;
            readAlerted[account] = now;
        }
        ReadAlert?.Invoke(account);
        return false;
    }
}
