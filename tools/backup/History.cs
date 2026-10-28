using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Backup;

/// <summary>The "history" group: the player's result in the finished competitions the website still lists, written as competition-history.json beside the raw edition replies.</summary>
sealed partial class Engine
{
    const string HistoryFile = "competition-history.json";

    /// <summary>History group only: also follow the list of earlier editions in every edition reply (each id once). That reaches past the rolling window of about a week, at the cost of thousands of requests.</summary>
    public bool OlderEditions;

    async Task History()
    {
        var todo = new Queue<string>();
        var queued = new HashSet<string>(StringComparer.Ordinal); // every edition once, however many lists name it
        void Queue(JsonNode? rows)
        {
            foreach (var r in rows as JsonArray ?? [])
                if (r?["id"]?.ToString() is { Length: > 0 } id && queued.Add(id)) { todo.Enqueue(id); Need(1); }
        }

        var failed = new JsonArray(); // what the file lacks, so it never looks whole when it is not
        void Miss(string request, string reason, int? offset = null, string? edition = null)
        {
            var m = new JsonObject { ["request"] = request };
            if (offset != null) m["offset"] = offset;
            if (edition != null) m["id"] = long.TryParse(edition, out var n) ? n : edition;
            m["reason"] = reason;
            failed.Add(m);
        }

        // the list is a window of the newest finished editions; the server answers an offset past the end with the last page again, so the stop is the total, never an empty page
        // (a request that gave no file is told from a reply of JSON null by Get itself: it returns null only when the request failed)
        Need(1);
        var first = await Get(Q("Page_content/list_previous_competitions", false, ("offset", "0"), ("limit", "20"), ("lang", "en")));
        var page = Json(first);
        Queue(page?["competitions"]);
        var count = Num(page?["total"]);
        if (first == null) { Miss("list_previous_competitions", "no data", offset: 0); log("! history: list page offset 0 failed (the total is unknown)"); }
        else if (count == null && page?["total"] is { } bad) // present but not a whole number: the rest of the list is not asked
        {
            Miss("list_previous_competitions", "total not readable", offset: 0);
            log($"! history: list total not readable: {bad.ToJsonString()}");
        }
        for (int off = 20, total = (int)(count ?? 0); off < total; off += 20)
        {
            Need(1);
            var more = await Get(Q("Page_content/list_previous_competitions", false, ("offset", off.ToString()), ("limit", "20"), ("lang", "en")));
            if (more == null) { Miss("list_previous_competitions", "no data", offset: off); log($"! history: list page offset {off} failed"); continue; }
            Queue(Json(more)?["competitions"]);
        }
        Need(1);
        var league = await Get(Q("Page_content/list_community_league_competitions", false, ("lang", "en")));
        if (league == null) { Miss("list_community_league_competitions", "no data"); log("! history: community league list failed"); }
        Queue(Json(league));
        Sized("history");

        var found = new List<(long Id, JsonObject Edition)>();
        while (todo.Count > 0)
        {
            var id = todo.Dequeue();
            // finished editions never change: a saved one is read back by resume, a failed one is asked again by the next run and does not stop the others
            var body = await Get(Q("Page_content/competitions_new", false, ("id", id), ("lang", "en"), ("entrants_limit", "100")));
            if (body == null) { Miss("competitions_new", "no data", edition: id); log($"! history: edition {id} failed"); continue; }
            if (Json(body) is not JsonObject e) continue;
            if (OlderEditions) Queue(e["competitions"]);
            found.Add((long.TryParse(id, out var n) ? n : 0, Edition(id, e)));
        }

        var editions = found.OrderByDescending(f => f.Id).ThenBy(f => f.Edition["id"]!.ToString(), StringComparer.Ordinal).Select(f => (JsonNode)f.Edition).ToList();
        var notListed = editions.Count(x => x["not_listed"]!.GetValue<bool>());
        var root = new JsonObject
        {
            ["format"] = 1, ["tool_version"] = Version, ["uid"] = uid, ["older_editions"] = OlderEditions, ["complete"] = failed.Count == 0, ["failed"] = failed,
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
