// The account's gallery: the photos the game uploaded (Heartbeat event 4, UploadReply), listed by Gallery/list and the website's gallery tab,
// deleted by their owner, and the picture a kill links. A photo of type SHOT_EVIDENCE is the picture of a shot, not a gallery picture.
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    public const int GalleryPageSize = 24;

    string PhotoUrl(long account, string file, string? site = null) => $"{site ?? baseUrl}{PHOTO_PREFIX}{account}/{file}";

    static string ThumbName(string file) => file[..^".jpg".Length] + "-thumb.jpg";

    /// <summary>The account's gallery pictures, newest first, each `{id, type, url, thumbnail, label, animal}`: an uploaded photo that is not a shot screenshot;
    /// `animal` is the species of the kill with the photo's individual ("" without one), `label` that kill's score ("" without one); `thumbnail` is the
    /// picture's own link when the game sent no thumbnail. The links are on the game's host, or on `site` (the website's) when given. ponytail: the whole gallery is read and each thumbnail looked up on disk per request; page it in SQL if galleries grow past a few hundred photos.</summary>
    List<JsonObject> GalleryRows(long account, string? site = null)
    {
        using var con = Con(account);
        return [.. con.Rows("SELECT p.id, p.photo_type, p.file, k.species, json_extract(h.body, '$.score'), p.saved FROM photos p"
                            + " LEFT JOIN kills k ON k.account_id = $a AND k.individual = p.individual AND p.spawn = " + KillSpawn("k.individual", "k.harvest")
                            + " LEFT JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                            + $" WHERE p.account_id = $a AND p.file IS NOT NULL AND p.photo_type IS NOT {SHOT_EVIDENCE} GROUP BY p.id ORDER BY p.id DESC")
            .Select(r =>
            {
                var file = (string)r[2];
                var url = PhotoUrl(account, file, site);
                var saved = r[5] is string s ? JsonNode.Parse(s) : null;  // a restored picture has no kill here: its saved label and animal
                return new JsonObject
                {
                    ["id"] = (long)r[0], ["type"] = Py.FromSql(r[1]), ["url"] = url,
                    ["thumbnail"] = File.Exists(Path.Combine(AccountPhotos(account), ThumbName(file))) ? PhotoUrl(account, ThumbName(file), site) : url,
                    ["label"] = r[4] is DBNull ? C(saved?["label"]) ?? "" : Py.Str(Py.FromSql(r[4])),
                    ["animal"] = r[3] is DBNull ? C(saved?["animal"]) ?? "" : SpeciesName(Py.FromSql(r[3])) ?? "",
                };
            })];
    }

    /// <summary>Gallery/list of the form's user_id (the player's own without one): one page (`offset`, `limit`) of the gallery, `{total, photos}`; any other
    /// account of this server is listed too (<see cref="PublicAccount(long)"/>); null for anyone else.</summary>
    public byte[]? GalleryListReply(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        long Number(string key) => long.TryParse(Form1(form, key), out var n) && n > 0 ? n : 0;
        if (!IsOwn(account, formBody))
        {
            if (!long.TryParse(Form1(form, "user_id"), out var id) || PublicAccount(id) is not { } other)
                return null;
            account = other;
        }
        var all = GalleryRows(account);
        var page = all.Skip((int)Math.Min(Number("offset"), int.MaxValue));
        var limit = Number("limit");
        return Py.DumpBytes(new JsonObject { ["total"] = all.Count, ["photos"] = new JsonArray((limit > 0 ? page.Take((int)Math.Min(limit, int.MaxValue)) : page).Cast<JsonNode?>().ToArray()) });
    }

    /// <summary>What a profile page's gallery tab shows of an account: one page of its gallery, `{page, pages, total, rows: [{id, type, url, thumbnail, label, animal}]}`.</summary>
    public JsonObject PublicGallery(long account, int page, string site)
    {
        var all = GalleryRows(account, site);
        var pages = Math.Max(1, (all.Count + GalleryPageSize - 1) / GalleryPageSize);
        page = Math.Clamp(page, 1, pages);
        return new JsonObject
        {
            ["page"] = page, ["pages"] = pages, ["total"] = all.Count,
            ["rows"] = new JsonArray([.. all.Skip((page - 1) * GalleryPageSize).Take(GalleryPageSize).Select(r => (JsonNode?)r)]),
        };
    }

    /// <summary>Delete the account's photo (its row and files): true, false for an id that is not one of the account's photos.</summary>
    public bool DeletePhoto(long account, long photoId)
    {
        using var con = Con(account);
        con.Begin();
        var file = con.Scalar("SELECT file FROM photos WHERE account_id = $a AND id = ?", photoId) as string;
        var found = con.Exec("DELETE FROM photos WHERE account_id = $a AND id = ?", photoId) > 0;
        con.Commit();
        if (file is not null)
            foreach (var name in new[] { file, ThumbName(file) })
                File.Delete(Path.Combine(AccountPhotos(account), name));
        return found;
    }

    /// <summary>The account's gallery as a zip of its pictures. ponytail: built in memory; stream it if galleries grow past a few hundred MiB.</summary>
    public byte[] GalleryZip(long account)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var row in GalleryRows(account))
            {
                var name = Path.GetFileName((string)row["url"]!);
                if (PhotoFile($"{PHOTO_PREFIX}{account}/{name}") is { } path)
                    zip.CreateEntryFromFile(path, name, CompressionLevel.NoCompression);  // a JPEG does not compress
            }
        return ms.ToArray();
    }

    /// <summary>SQL of the spawn (event 7) of a kill's animal: the latest of its individual before the kill's event 3, as <see cref="KillHits"/> bounds its hits.</summary>
    static string KillSpawn(string individual, string harvest) =>
        $"(SELECT MAX(s.id) FROM events s WHERE s.account_id = $a AND s.event = 7 AND s.individual = {individual}"
        + $" AND s.id < (SELECT MAX(t.id) FROM events t WHERE t.account_id = $a AND t.event = 3 AND t.body = {harvest}))";

    /// <summary>The link of the picture of a kill, among the photos asked for while its animal was the individual: its shot screenshot, else the newest other photo; null without one.</summary>
    string? KillPhoto(Db con, long account, object? individual, string harvest) =>
        con.Scalar($"SELECT file FROM photos WHERE account_id = $a AND file IS NOT NULL AND individual = ?1 AND spawn = {KillSpawn("?1", "?2")}"
                   + $" ORDER BY photo_type IS {SHOT_EVIDENCE} DESC, id DESC LIMIT 1", individual, harvest) is string file
            ? PhotoUrl(account, file) : null;
}
