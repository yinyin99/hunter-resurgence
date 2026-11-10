// Restore layer B, pictures: the gallery pictures, kill photos, score sheet pages and avatar and background pictures of a backup zip are unpacked into the
// account's photo folder (`photos/<account>/`, the folder of the photos taken here, served at the same links), and a restored hunt's kill is linked to its
// picture and its saved score sheet page. Layer A only names them (<see cref="MediaFile"/>); the bytes are read from the zip here.
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>What a restore puts on disk once its rows are committed: Files (stored name, entry of the zip), Stale (the gallery files of the pictures an earlier restore unpacked) and the report line.</summary>
    internal sealed record Unpacking(List<(string Name, string Entry)> Files, List<string> Stale, string Report);

    // a saved file is stored under its path in the zip with every `/` and other odd character as `_` (`kills/score_5.html` is `kills_score_5.html`): Importer.CleanName

    static readonly string[] StoredFolders = ["kills", "images"];

    /// <summary>Inside the restore's transaction: delete the account's restored gallery rows, and add one per saved `Gallery/list` entry whose picture the zip holds (oldest first, so the
    /// newest has the highest id; its `type`, `label` and `animal` as saved). The kill photos, score pages and images need no row: they are named by their zip path.
    /// Nothing is written to disk here: <see cref="Unpack"/> does that once the transaction has committed.</summary>
    internal Unpacking PreparePictures(Db con, Restored read)
    {
        var stale = con.Rows("SELECT file FROM photos WHERE account_id = $a AND saved IS NOT NULL AND file IS NOT NULL").Select(r => (string)r[0]).ToList();
        con.Exec("DELETE FROM photos WHERE account_id = $a AND saved IS NOT NULL");
        var media = new Dictionary<string, MediaFile>(StringComparer.Ordinal);
        foreach (var m in read.Media) media.TryAdd(m.Path, m);
        var files = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int listed = 0, gallery = 0;
        static string Text(JsonNode? n) => Py.IsStr(n) ? (string)n! : Py.IsNum(n) ? Py.Str(n) : "";
        static long Page(string key) => Regex.Match(key, @"__offset=(\d+)").Groups[1] is { Length: > 0 } g && long.TryParse(g.Value, out var n) ? n : 0;
        foreach (var p in read.Model.Extras.Where(e => e.Key.StartsWith("Gallery_list", StringComparison.Ordinal) && e.Value?["photos"] is JsonArray)
                     .OrderBy(e => Page(e.Key)).SelectMany(e => e.Value!["photos"]!.AsArray().OfType<JsonObject>())
                     .Where(p => Py.IsStr(p["url"]) && seen.Add((string)p["url"]!)).Reverse())
        {
            listed++;
            if (Importer.MediaPath("photos", (string)p["url"]!) is not { } path || !media.TryGetValue(path, out var file))
                continue;
            long? type = Py.IsNum(p["type"]) ? Py.L(p["type"]) : Py.IsStr(p["type"]) && long.TryParse((string)p["type"]!, out var t) ? t : null;
            var id = con.Insert("INSERT INTO photos (account_id, photo_type, saved) VALUES ($a, ?, ?)", type, Py.Dumps(new JsonObject { ["label"] = Text(p["label"]), ["animal"] = Text(p["animal"]) }));
            var name = $"{id}-{type ?? 0}.jpg";
            con.Exec("UPDATE photos SET file = ? WHERE account_id = $a AND id = ?", name, id);
            files.Add((name, file.Entry));
            gallery++;
        }
        var others = media.Values.Where(m => StoredFolders.Any(f => m.Path.StartsWith(f + "/", StringComparison.Ordinal))).ToList();
        files.AddRange(others.Select(m => (Importer.CleanName(m.Path), m.Entry)));
        var report = $"pictures stored: gallery {gallery}, kill photos and score pages {others.Count(m => m.Path.StartsWith("kills/", StringComparison.Ordinal))}, "
                     + $"avatar and background pictures {others.Count(m => m.Path.StartsWith("images/", StringComparison.Ordinal))}"
                     + (listed > gallery ? $"; {listed - gallery} gallery entr{(listed - gallery == 1 ? "y" : "ies")} without a picture in the zip" : "");
        return new(files, stale, report);
    }

    /// <summary>After the restore's commit: remove the pictures of the restore before (the stale gallery files, every kill and image file), then read each stored file from the zip into the account's folder.
    /// The zip's bytes were counted against the caps when it was read (a picture at most <see cref="Limits.EntryBytes"/>, all of it <see cref="Limits.TotalBytes"/>); they are copied from the entry as they are, never held whole.</summary>
    internal void Unpack(long account, string zipPath, Unpacking u)
    {
        var dir = AccountPhotos(account);
        if (Directory.Exists(dir))
        {
            foreach (var name in u.Stale.SelectMany(f => new[] { f, ThumbName(f) }))
                File.Delete(Path.Combine(dir, name));
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => StoredFolders.Any(s => Path.GetFileName(f).StartsWith(s + "_", StringComparison.Ordinal))))
                File.Delete(f);
        }
        if (u.Files.Count == 0)
            return;
        Directory.CreateDirectory(dir);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var (name, entry) in u.Files)
        {
            using var from = (zip.GetEntry(entry) ?? throw new InvalidDataException($"{entry} is not in the zip")).Open();
            using var to = File.Create(Path.Combine(dir, name));
            from.CopyTo(to);
        }
    }

    /// <summary>The saved score sheet page of a restored kill (`kills/score_&lt;id&gt;.html` of the zip) in the account's folder; null without one.</summary>
    string? SavedPage(long account, long killId)
    {
        var file = Path.Combine(AccountPhotos(account), Importer.CleanName($"kills/score_{killId}.html"));
        return File.Exists(file) ? file : null;
    }

    /// <summary>The page the game's score sheet link of a restored kill opens: the page its backup saved, as saved; null for a kill made here (<see cref="ScorePageReply"/> renders that) or without a saved page.
    /// The page comes from a zip: the caller sends it with a sandbox policy, so no script in it runs.</summary>
    public byte[]? SavedScorePage(long account, long killId) =>
        SavedPage(account, killId) is { } file && LoadKill(account, killId) is null ? File.ReadAllBytes(file) : null;

    /// <summary>The saved hunt with each kill's `photo` (a link on the official host) replaced by the link of the stored picture, where the zip had it.</summary>
    JsonNode LinkPhotos(long account, JsonNode hunt)
    {
        foreach (var info in ((hunt as JsonObject)?["kills"] as JsonArray ?? []).OfType<JsonObject>().Select(k => k["kill"] as JsonObject))
            if (info is not null && Py.IsStr(info["photo"]) && Importer.MediaPath("kills", (string)info["photo"]!) is { } path
                && File.Exists(Path.Combine(AccountPhotos(account), Importer.CleanName(path))))
                info["photo"] = PhotoUrl(account, Importer.CleanName(path));
        return hunt;
    }

    JsonObject? RestoredKill(long account, long killId)
    {
        using var con = Con(account);
        return PlayerRows.RestoredKill(con, killId);
    }
}
