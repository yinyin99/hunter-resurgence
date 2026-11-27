// Applies `harvest_gm_bonus` of settings.json again when the file changes, so the operator schedules a bonus without a restart (docs/server.md#harvest-gm-bonus).
// It polls the file's time and size instead of a FileSystemWatcher: the server's container has the folder bind-mounted, and an editor that replaces the file (a new inode)
// or a mount that does not carry change notifications would leave a watcher blind, where a stat every few seconds cannot miss it.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class GmBonusWatch(State state, string file, JsonNode? loaded)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    public Action<string> Log { get; set; } = Console.WriteLine;

    (DateTime Time, long Length)? seen;  // none yet: the first look reads the file, so an edit made during the start is not missed
    string? applied = loaded?.ToJsonString();
    string? lastError;

    static (DateTime, long)? Stamp(string file) => new FileInfo(file) is { Exists: true } f ? (f.LastWriteTimeUtc, f.Length) : null;

    /// <summary>One look at the file: when it changed, its `harvest_gm_bonus` is checked and, if it is another list than the one in force, applied. A wrong file is logged and the bonuses in force stay
    /// (it is not read again until it changes); any other failure is logged too and the file is read again at the next look.</summary>
    public void Check()
    {
        try
        {
            if (Stamp(file) is not { } stamp || stamp == seen) return;  // gone for a moment (an editor replacing it), or unchanged
            seen = stamp;
            var read = Settings.ReadHarvestGmBonus(file, (long)state.Clock());
            if (read?.ToJsonString() != applied)  // else another setting changed
            {
                state.LoadHarvestGmBonuses(read);
                applied = read?.ToJsonString();
                Log($"harvest_gm_bonus: {read?.Count ?? 0} read from {file}");
            }
            lastError = null;
        }
        catch (InvalidDataException e) when (e.InnerException is null or JsonException)
        {
            Log($"harvest_gm_bonus: not applied, the bonuses in force stay: {e.Message}");
        }
        catch (Exception e)
        {
            seen = null;  // not the file's fault (a read that failed, the database): the next look tries again
            var line = $"harvest_gm_bonus: not applied, the bonuses in force stay, tried again at the next look: {e.GetType().Name}: {e.Message}";
            if (line != lastError) Log(line);  // a failure that persists is said once, not at every look
            lastError = line;
        }
    }

    public async Task Run(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every);
        try { while (await timer.WaitForNextTickAsync(stop)) Check(); }
        catch (OperationCanceledException) { }
    }
}
