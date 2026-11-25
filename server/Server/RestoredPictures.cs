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
    /// <summary>What a restore puts on disk once its rows are committed: Files (stored name, entry, and whether the entry is in the zip of fetched pictures (<see cref="PictureLinks"/>) instead of the backup zip),
    /// Stale (the gallery files of the pictures an earlier restore unpacked), the report line, and Replace: a restore replaces the account's earlier pictures, `import pictures` only adds what is missing.</summary>
    internal sealed record Unpacking(List<(string Name, string Entry, bool Linked)> Files, List<string> Stale, string Report, bool Replace);

    // a saved file is stored under its path in the zip with every `/` and other odd character as `_` (`kills/score_5.html` is `kills_score_5.html`): Importer.CleanName

    static readonly string[] StoredFolders = ["kills", "images"];

    /// <summary>The saved `Gallery/list` and `Gallery/list_all` entries with a link, once per link, oldest first (so the newest gets the highest id). An entry is an object with the details
    /// (`type`, `label`, `animal`, `kill_id`) or, in a `Gallery/list_all` of another save format, a plain link; a link no detailed entry names comes after the detailed ones (it is the older).</summary>
    static List<JsonObject> GalleryEntries(Restored read)
    {
        static long Page(string key) => Regex.Match(key, @"__offset=(\d+)").Groups[1] is { Length: > 0 } g && long.TryParse(g.Value, out var n) ? n : 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = read.Model.Extras.Where(e => e.Key.StartsWith("Gallery_list", StringComparison.Ordinal) && e.Value?["photos"] is JsonArray)
                      .OrderBy(e => Page(e.Key)).SelectMany(e => e.Value!["photos"]!.AsArray()).ToList();
        return [.. all.OfType<JsonObject>().Concat(all.Where(Py.IsStr).Select(n => new JsonObject { ["url"] = (string)n! }))
                     .Where(p => Py.IsStr(p["url"]) && seen.Add((string)p["url"]!)).Reverse()];
    }

    static Dictionary<string, MediaFile> MediaByPath(Restored read)
    {
        var media = new Dictionary<string, MediaFile>(StringComparer.Ordinal);
        foreach (var m in read.Media) media.TryAdd(m.Path, m);
        return media;
    }

    /// <summary>The zip's picture of a gallery link; a zip of backup tool 0.9.0 held a listed gallery picture only under `kills/`.</summary>
    static MediaFile? ZipPicture(Dictionary<string, MediaFile> media, string url) =>
        Importer.MediaPath("photos", url) is { } path && (media.TryGetValue(path, out var file) || media.TryGetValue("kills/" + path["photos/".Length..], out file)) ? file : null;

    /// <summary>The player's own avatar when the zip lacks it (<see cref="UploadedAvatarLink"/>), then the links of the gallery entries whose picture the zip lacks: what <see cref="PictureLinks"/> fetches.
    /// The avatar comes first: a save with hundreds of gallery links can use up the fetch's time budget, and what is last is what is skipped.</summary>
    internal List<string> MissingPictures(Restored read)
    {
        var media = MediaByPath(read);
        List<string> missing = [.. GalleryEntries(read).Select(p => (string)p["url"]!).Where(url => ZipPicture(media, url) is null)];
        if (UploadedAvatarLink(read) is { } avatar) missing.Insert(0, avatar);
        return missing;
    }

    /// <summary>The SHA-256 of what is not stored yet and of what the account has, read before the restore's transaction opens (the files can be many and large): Have, the hashes of the account's
    /// pictures that stay (with `replace`, those of an earlier restore go); Entries, the hash of every gallery picture the backup zip or the fetched pictures hold, by entry name.</summary>
    internal sealed record PictureHashes(HashSet<string> Have, Dictionary<string, string> Entries);

    internal PictureHashes HashPictures(Restored read, long account, string? zipPath, string? linksPath, bool replace)
    {
        var dir = AccountPhotos(account);
        HashSet<string> have;
        using (var con = Con(account))
            have = con.Rows("SELECT file FROM photos WHERE account_id = $a AND file IS NOT NULL" + (replace ? " AND saved IS NULL" : ""))
                .Select(r => Path.Combine(dir, (string)r[0])).Where(File.Exists).Select(Restorer.Sha256).ToHashSet(StringComparer.Ordinal);
        var media = MediaByPath(read);
        using var zip = zipPath is not null && File.Exists(zipPath) ? ZipFile.OpenRead(zipPath) : null;
        using var linked = linksPath is not null && File.Exists(linksPath) ? ZipFile.OpenRead(linksPath) : null;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in GalleryEntries(read).Select(p => (string)p["url"]!))
        {
            var inZip = ZipPicture(media, url);
            var entry = inZip?.Entry ?? PictureLinks.EntryName(url);
            if ((inZip is not null ? zip : linked)?.GetEntry(entry) is not { } found) continue;
            using var content = found.Open();
            entries[entry] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        }
        if (linked is not null && UploadedAvatarLink(read) is { } avatar && linked.GetEntry(PictureLinks.EntryName(avatar)) is { } picture)
        {
            using var content = picture.Open();
            entries[picture.Name] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        }
        return new(have, entries);
    }

    /// <summary>Inside the restore's transaction: with `replace`, delete the account's restored gallery rows; then add one per saved gallery entry whose picture the zip holds, or the fetched pictures
    /// hold under its link (oldest first; its `type`, `label`, `animal` and `kill_id` as saved). A picture the account already has (the same file hash, from <see cref="HashPictures"/>) is not added again.
    /// The kill photos, score pages and images need no row: they are named by their zip path.
    /// Nothing is written to disk here: <see cref="Unpack"/> does that once the transaction has committed. `keepFiles`: a stored file the account's folder has already is not written again (an archive claim over a backup restore).</summary>
    internal Unpacking PreparePictures(Db con, Restored read, PictureHashes hashes, bool replace, bool keepFiles = false)
    {
        List<string> stale = replace ? con.Rows("SELECT file FROM photos WHERE account_id = $a AND saved IS NOT NULL AND file IS NOT NULL").Select(r => (string)r[0]).ToList() : [];
        if (replace) con.Exec("DELETE FROM photos WHERE account_id = $a AND saved IS NOT NULL");
        var have = hashes.Have;
        var media = MediaByPath(read);
        var files = new List<(string, string, bool)>();
        int missing = 0, fetched = 0, gallery = 0, already = 0;
        foreach (var p in GalleryEntries(read))
        {
            var url = (string)p["url"]!;
            var inZip = ZipPicture(media, url);
            if (inZip is null) missing++;
            var entry = inZip?.Entry ?? PictureLinks.EntryName(url);
            if (!hashes.Entries.TryGetValue(entry, out var hash)) continue;
            if (inZip is null) fetched++;
            if (!have.Add(hash))
            {
                already++;
                continue;
            }
            long? type = Py.IsNum(p["type"]) ? Py.L(p["type"]) : Py.IsStr(p["type"]) && long.TryParse((string)p["type"]!, out var t) ? t : null;
            var saved = SavedDetails(p);
            var id = con.Insert("INSERT INTO photos (account_id, photo_type, saved) VALUES ($a, ?, ?)", type, Py.Dumps(saved));
            var name = PhotoName(con.Account!.Value, id, $"{type ?? 0}");
            con.Exec("UPDATE photos SET file = ? WHERE account_id = $a AND id = ?", name, id);
            files.Add((name, entry, inZip is null));
            gallery++;
        }
        var others = media.Values.Where(m => StoredFolders.Any(f => m.Path.StartsWith(f + "/", StringComparison.Ordinal))).ToList();
        files.AddRange(others.Select(m => (Importer.CleanName(m.Path), m.Entry, false)));
        if (ImportedAvatarOf(read) is { } avatar) files.Add((ImportedAvatar, avatar.Entry, false));  // the player's own picture, again at its stable link
        else if (UploadedAvatarLink(read) is { } link && hashes.Entries.ContainsKey(PictureLinks.EntryName(link))) files.Add((ImportedAvatar, PictureLinks.EntryName(link), true));  // or the one fetched for it
        var report = $"pictures stored: gallery {gallery}, kill photos and score pages {others.Count(m => m.Path.StartsWith("kills/", StringComparison.Ordinal))}, "
                     + $"avatar and background pictures {others.Count(m => m.Path.StartsWith("images/", StringComparison.Ordinal))}"
                     + (missing > 0 ? $"; pictures from links: {fetched} of {missing}" : "")
                     + (already > 0 ? $"; {already} gallery picture{(already == 1 ? "" : "s")} already in the gallery" : "");
        if (keepFiles) files.RemoveAll(f => File.Exists(Path.Combine(AccountPhotos(con.Account!.Value), f.Item1)));
        return new(files, stale, report, replace);
    }

    /// <summary>What a gallery entry keeps in `photos.saved`: `animal` the species, `label` the entry's label else its kill's score (as saved, text), `kill_id` the entry's else its kill's id. In a real `Gallery/list` the entry's
    /// `animal` is the kill, `{scoreType, species_id, score, id, species}` (null for a camera photo) and its `label` null; another save format has the species as text.</summary>
    static JsonObject SavedDetails(JsonObject p)
    {
        static string Text(JsonNode? n) => Py.IsStr(n) ? (string)n! : Py.IsNum(n) ? Py.Str(n) : "";
        var kill = p["animal"] as JsonObject;
        var saved = new JsonObject { ["label"] = Text(p["label"]) is { Length: > 0 } label ? label : Text(kill?["score"]), ["animal"] = kill is null ? Text(p["animal"]) : Text(kill["species"]) };
        if ((Text(p["kill_id"]) is { Length: > 0 } id ? id : Text(kill?["id"])) is { Length: > 0 } killId) saved["kill_id"] = killId;
        return saved;
    }

    /// <summary>`admin gallery-details`: for each account that has kept zips (`restores/&lt;account&gt;/*.zip`, newest first; `only` limits it to one), set the details of every restored gallery picture (`photos.saved`: species, score, kill id)
    /// from the zip's entry for the same picture (the same file hash). It reads the zips and the pictures already on disk, and neither fetches, adds nor removes a picture. One line per account: id, handle, rows updated of rows restored.</summary>
    public List<string> SweepGalleryDetails(string dataDir, long? only)
    {
        var lines = new List<string>();
        var root = Path.Combine(dataDir, "restores");
        foreach (var dir in Directory.Exists(root) ? Directory.GetDirectories(root).Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            if (!long.TryParse(Path.GetFileName(dir), out var account) || (only is not null && only != account) || !Store.AccountExists(account)) continue;
            var zips = Directory.GetFiles(dir, "*.zip").Where(f => !f.EndsWith(".pictures.zip", StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            Dictionary<string, long> rows = [];  // the file hash of each restored picture the account has, and its row
            using (var con = Con(account))
                foreach (var r in con.Rows("SELECT id, file FROM photos WHERE account_id = $a AND saved IS NOT NULL AND file IS NOT NULL"))
                    if (Path.Combine(AccountPhotos(account), (string)r[1]) is var file && File.Exists(file)) rows.TryAdd(Restorer.Sha256(file), (long)r[0]);
            var total = rows.Count;
            var updates = new List<(long Id, string Saved)>();
            (string Path, JsonNode? Body)? profile = null;  // the saved v2 profile (`game_user_<uid>_profile.json`: background and ribbon) the account lacks, from the newest zip that has it
            bool needProfile;
            using (var con = Con(account))
                needProfile = RestoredProfile(con) is null;
            foreach (var zip in zips.TakeWhile(_ => rows.Count > 0 || (needProfile && profile is null)))
            {
                var read = Importer.Read(zip, Catalogue);
                var linked = Restorer.PicturesZip(zip);
                var hashes = HashPictures(read, account, zip, File.Exists(linked) ? linked : null, replace: false);
                var media = MediaByPath(read);
                foreach (var p in GalleryEntries(read))
                    if (hashes.Entries.TryGetValue(ZipPicture(media, (string)p["url"]!)?.Entry ?? PictureLinks.EntryName((string)p["url"]!), out var hash) && rows.Remove(hash, out var id))
                        updates.Add((id, Py.Dumps(SavedDetails(p))));
                if (needProfile && profile is null && read.Model.Extras.FirstOrDefault(e => Regex.IsMatch(e.Key, @"^game_user_\d+_profile\.json$")) is { Key: not null } saved)
                    profile = (saved.Key, saved.Value);
            }
            using (var con = Con(account))
            {
                con.Begin();
                foreach (var (id, saved) in updates) con.Exec("UPDATE photos SET saved = ? WHERE account_id = $a AND id = ?", saved, id);
                if (profile is { } p) PlayerRows.SetExtra(con, p.Path, p.Body);
                con.Commit();
            }
            lines.Add($"{account} {(string)Store.AccountJson(account)["handle"]!} {updates.Count} of {total} gallery pictures updated" + (profile is null ? "" : ", profile background restored"));
        }
        return lines;
    }

    /// <summary>After the restore's commit: with Replace, remove the pictures of the restore before (the stale gallery files, every kill and image file); then read each stored file from the backup zip, or from
    /// the zip of fetched pictures beside it (<see cref="Restorer.PicturesZip"/>), into the account's folder; without Replace nothing of the account's is removed.
    /// The zip's bytes were counted against the caps when it was read (a picture at most <see cref="Limits.EntryBytes"/>, all of it <see cref="Limits.TotalBytes"/>); they are copied from the entry as they are, never held whole.</summary>
    internal void Unpack(long account, string? zipPath, string? linksPath, Unpacking u)
    {
        var dir = AccountPhotos(account);
        if (u.Replace && Directory.Exists(dir))
        {
            foreach (var name in u.Stale.SelectMany(f => new[] { f, ThumbName(f) }))
                File.Delete(Path.Combine(dir, name));
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => StoredFolders.Any(s => Path.GetFileName(f).StartsWith(s + "_", StringComparison.Ordinal))))
                File.Delete(f);
            File.Delete(Path.Combine(dir, ImportedAvatar));
        }
        if (u.Files.Count == 0)
            return;
        Directory.CreateDirectory(dir);
        using var zip = u.Files.Any(f => !f.Linked) ? ZipFile.OpenRead(zipPath!) : null;
        using var linked = u.Files.Any(f => f.Linked) ? ZipFile.OpenRead(linksPath!) : null;
        foreach (var (name, entry, link) in u.Files)
        {
            var target = Path.Combine(dir, name);
            using var from = ((link ? linked! : zip!).GetEntry(entry) ?? throw new InvalidDataException($"{entry} is not in the zip")).Open();
            using var to = File.Create(target);
            from.CopyTo(to);
        }
    }

    /// <summary>A restore that adopted another user id: move the files of `photos/<from>/` into `photos/<to>/`, replacing a file of the same name there, and remove the old folder.
    /// The rows hold file names only, so the links (built from the account id) follow.</summary>
    public void MovePhotos(long from, long to)
    {
        var old = AccountPhotos(from);
        if (!Directory.Exists(old)) return;
        var dir = AccountPhotos(to);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(old))
            File.Move(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        Directory.Delete(old);
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

    /// <summary>The saved hunt with each kill's `photo` (a link on the official host) replaced by the link of the stored picture, where the zip had it; else by the link of the restored trophy shot (type 2) whose saved
    /// `kill_id` is the kill's id (the newest). A kill with neither keeps its photo as saved.</summary>
    JsonNode LinkPhotos(Db con, long account, JsonNode hunt)
    {
        var shots = new Dictionary<string, string>(StringComparer.Ordinal);  // kill id -> file of its newest trophy shot
        foreach (var r in con.Rows("SELECT json_extract(saved, '$.kill_id'), file FROM photos WHERE account_id = $a AND saved IS NOT NULL AND file IS NOT NULL AND photo_type = 2 ORDER BY id DESC"))
            if (r[0] is not DBNull) shots.TryAdd(Py.Str(Py.FromSql(r[0])), (string)r[1]);
        foreach (var k in ((hunt as JsonObject)?["kills"] as JsonArray ?? []).OfType<JsonObject>())
            if (k["kill"] is JsonObject info)
                if (Py.IsStr(info["photo"]) && Importer.MediaPath("kills", (string)info["photo"]!) is { } path
                    && File.Exists(Path.Combine(AccountPhotos(account), Importer.CleanName(path))))
                    info["photo"] = PhotoUrl(account, Importer.CleanName(path));
                else if (Py.IsNum(k["id"]) && shots.TryGetValue(Py.Str(k["id"]), out var file)) info["photo"] = PhotoUrl(account, file);
        return hunt;
    }

    JsonObject? RestoredKill(long account, long killId)
    {
        using var con = Con(account);
        return PlayerRows.RestoredKill(con, killId);
    }
}
