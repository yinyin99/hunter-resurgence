// Applies `harvest_gm_bonus` of settings.json again when the file changes, so the operator schedules a bonus without a restart (docs/server.md#harvest-gm-bonus).
// It polls the file's time and size instead of a FileSystemWatcher: the server's container has the folder bind-mounted, and an editor that replaces the file (a new inode)
// or a mount that does not carry change notifications would leave a watcher blind, where a stat every few seconds cannot miss it.
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class GmBonusWatch(State state, string file, JsonNode? loaded)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    public Action<string> Log { get; set; } = Console.WriteLine;

    (DateTime Time, long Length)? seen = Stamp(file);
    string? applied = loaded?.ToJsonString();

    static (DateTime, long)? Stamp(string file) => new FileInfo(file) is { Exists: true } f ? (f.LastWriteTimeUtc, f.Length) : null;

    /// <summary>One look at the file: when it changed, its `harvest_gm_bonus` is checked and, if it is another list than the one in force, applied. A wrong file is logged and the bonuses in force stay.</summary>
    public void Check()
    {
        if (Stamp(file) is not { } stamp || stamp == seen) return;  // gone for a moment (an editor replacing it), or unchanged
        seen = stamp;
        try
        {
            var read = Settings.ReadHarvestGmBonus(file);
            if (read?.ToJsonString() == applied) return;  // another setting changed
            state.LoadHarvestGmBonuses(read);
            applied = read?.ToJsonString();
            Log($"harvest_gm_bonus: {read?.Count ?? 0} read from {file}");
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Log($"harvest_gm_bonus: not applied, the bonuses in force stay: {e.Message}");
        }
    }

    public async Task Run(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every);
        try { while (await timer.WaitForNextTickAsync(stop)) Check(); }
        catch (OperationCanceledException) { }
    }
}
