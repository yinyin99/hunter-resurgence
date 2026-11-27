// Applies `harvest_gm_bonus` and `store_sales` of settings.json again when the file changes, so the operator schedules a bonus or a sale without a restart (docs/server.md#harvest-gm-bonus, docs/server.md#store-sales).
// It polls the file's time and size instead of a FileSystemWatcher: the server's container has the folder bind-mounted, and an editor that replaces the file (a new inode)
// or a mount that does not carry change notifications would leave a watcher blind, where a stat every few seconds cannot miss it.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class GmBonusWatch(State state, string file, JsonNode? loaded, JsonNode? loadedSales = null)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    public Action<string> Log { get; set; } = Console.WriteLine;

    (DateTime Time, long Length)? seen;  // none yet: the first look reads the file, so an edit made during the start is not missed
    string? applied = loaded?.ToJsonString(), appliedSales = loadedSales?.ToJsonString();
    string? lastError;

    static (DateTime, long)? Stamp(string file) => new FileInfo(file) is { Exists: true } f ? (f.LastWriteTimeUtc, f.Length) : null;

    /// <summary>One look at the file: when it changed, its `harvest_gm_bonus` and its `store_sales` are each checked and, if it is another value than the one in force, applied. A wrong file or a wrong setting is logged
    /// and what is in force stays (it is not read again until the file changes; a good setting beside a wrong one is still applied); any other failure is logged too and the file is read again at the next look.</summary>
    public void Check()
    {
        try
        {
            if (Stamp(file) is not { } stamp || stamp == seen) return;  // gone for a moment (an editor replacing it), or unchanged
            seen = stamp;
            var root = Settings.Read(file);
            Apply("harvest_gm_bonus", "bonuses", () => Settings.ParseHarvestGmBonus(root["harvest_gm_bonus"], (long)state.Clock()), state.LoadHarvestGmBonuses, ref applied);
            Apply("store_sales", "sales", () => Settings.ParseStoreSales(root["store_sales"]), state.LoadStoreSales, ref appliedSales);
            lastError = null;
        }
        catch (InvalidDataException e) when (e.InnerException is null or JsonException)
        {
            Log($"settings: not applied, the bonuses and sales in force stay: {e.Message}");
        }
        catch (Exception e)
        {
            seen = null;  // not the file's fault (a read that failed, the database): the next look tries again
            var line = $"settings: not applied, the bonuses and sales in force stay, tried again at the next look: {e.GetType().Name}: {e.Message}";
            if (line != lastError) Log(line);  // a failure that persists is said once, not at every look
            lastError = line;
        }
    }

    void Apply(string key, string what, Func<JsonNode?> read, Action<JsonNode?> load, ref string? inForce)
    {
        try
        {
            var node = read();
            if (node?.ToJsonString() == inForce) return;  // else another setting changed
            load(node);
            inForce = node?.ToJsonString();
            Log($"{key}: {(node as JsonArray)?.Count ?? 0} read from {file}");
        }
        catch (InvalidDataException e) when (e.InnerException is null or JsonException)
        {
            Log($"{key}: not applied, the {what} in force stay: {e.Message}");
        }
    }

    public async Task Run(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every);
        string? pickError = null;
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                Check();
                try { state.RecordSalePicks(); pickError = null; }  // a period of a random sale that starts is written down at the next look
                catch (Exception e)
                {
                    var line = $"store_sales: the picks were not recorded: {e.GetType().Name}: {e.Message}";
                    if (line != pickError) Log(line);
                    pickError = line;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
