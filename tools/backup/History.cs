using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Backup;

/// <summary>The "history" group: the player's result in the finished competitions the website still lists, written as competition-history.json beside the raw edition replies.</summary>
sealed partial class Engine
{
    const string HistoryFile = "competition-history.json";

    async Task History()
    {
        var todo = new Queue<string>();
        var queued = new HashSet<string>(StringComparer.Ordinal); // every edition once, however many lists name it
        void Queue(JsonNode? rows)
        {
            foreach (var r in rows as JsonArray ?? [])
                if (r?["id"]?.ToString() is { Length: > 0 } id && queued.Add(id)) { todo.Enqueue(id); Need(1); }
        }

        // the list is a window of the newest finished editions; the server answers an offset past the end with the last page again, so the stop is the total, never an empty page
        Need(1);
        var page = Json(await Get(Q("Page_content/list_previous_competitions", false, ("offset", "0"), ("limit", "20"), ("lang", "en"))));
        Queue(page?["competitions"]);
        for (int off = 20, total = Int(page?["total"]) ?? 0; off < total; off += 20)
        {
            Need(1);
            Queue(Json(await Get(Q("Page_content/list_previous_competitions", false, ("offset", off.ToString()), ("limit", "20"), ("lang", "en"))))?["competitions"]);
        }
        Need(1);
        Queue(Json(await Get(Q("Page_content/list_community_league_competitions", false, ("lang", "en")))));
        Sized("history");

        var found = new List<(long Id, JsonObject Edition)>();
        while (todo.Count > 0)
        {
            var id = todo.Dequeue();
            // finished editions never change: a saved one is read back by resume, a failed one is asked again by the next run and does not stop the others
            if (Json(await Get(Q("Page_content/competitions_new", false, ("id", id), ("lang", "en"), ("entrants_limit", "100")))) is not JsonObject e) continue;
            found.Add((long.TryParse(id, out var n) ? n : 0, Edition(id, e)));
        }

        var editions = found.OrderByDescending(f => f.Id).ThenBy(f => f.Edition["id"]!.ToString(), StringComparer.Ordinal).Select(f => (JsonNode)f.Edition).ToList();
        var notListed = editions.Count(x => x["not_listed"]!.GetValue<bool>());
        var root = new JsonObject
        {
            ["format"] = 1, ["tool_version"] = Version, ["uid"] = uid,
            ["totals"] = new JsonObject { ["editions"] = editions.Count, ["listed"] = editions.Count - notListed, ["not_listed"] = notListed },
            ["editions"] = new JsonArray([.. editions]),
        };
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(SetupJson));
        WriteAtomic(Path.Combine(dir, HistoryFile), bytes);
        lock (files) files[HistoryFile] = new(HistoryFile, "derived", [], null, Hex(SHA256.HashData(bytes)), DateTime.UtcNow, null, true);
        Interlocked.Increment(ref saved);
        log($"competition history: {editions.Count} edition(s), your result in {editions.Count - notListed}");
    }

    /// <summary>One edition: its type and dates as sent, and the player's row, found by user id. The page returns only the best 100, so a low rank is missing: not_listed, not a guess.</summary>
    JsonObject Edition(string requestedId, JsonObject reply)
    {
        var info = (reply["info"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var own = (reply["entrants"] as JsonArray)?.FirstOrDefault(x => x?["user"]?["id"]?.ToString() == uid);
        var e = new JsonObject
        {
            ["id"] = info?["id"]?.DeepClone() ?? requestedId, ["type_id"] = info?["type"]?["id"]?.DeepClone(), ["type_name"] = info?["type"]?["name"]?.DeepClone(),
            ["start"] = info?["start"]?.DeepClone(), ["end"] = info?["end"]?.DeepClone(), ["not_listed"] = own == null,
        };
        if (own != null) (e["position"], e["points"]) = (own["position"]?.DeepClone(), own["points"]?.DeepClone());
        return e;
    }
}
