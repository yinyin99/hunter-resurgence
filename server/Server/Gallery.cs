// The account's gallery: the photos the game uploaded (Heartbeat event 4, UploadReply), listed by Gallery/list and the website's gallery tab,
// deleted by their owner, and the picture a kill links. Only a camera photo and a trophy shot (GalleryTypes), and a picture a restore unpacked (a saved gallery entry,
// whatever its saved type), are gallery pictures; the game's other photos (its automatic screenshots, the avatar picture) are stored and never listed or linked, and every file name carries a token nobody can guess.
// On the website's host a picture is served to a signed-in player only (<see cref="WebPhoto"/>): its owner gets the original, anyone else a display-size copy.
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using ImageConfiguration = SixLabors.ImageSharp.Configuration;
using DecoderOptions = SixLabors.ImageSharp.Formats.DecoderOptions;
using Picture = SixLabors.ImageSharp.Image;

namespace Resurgence.Server;

public sealed partial class State
{
    public const int GalleryPageSize = 24;

    const string GalleryTypes = "1, 2";  // photo_type of a camera photo and of a trophy shot
    const long TrophyShot = 2;

    string PhotoUrl(long account, string file, string? site = null) => $"{site ?? baseUrl}{PHOTO_PREFIX}{account}/{file}";

    static string ThumbName(string file) => file[..^".jpg".Length] + "-thumb.jpg";

    /// <summary>The stored name of a photo, `&lt;id&gt;-&lt;photo_type&gt;-&lt;token&gt;[-&lt;part&gt;].jpg`. The token is the first 32 hex digits of an HMAC-SHA256 of the account and the photo id,
    /// keyed with the server's secret: nobody can guess a link from the sequential ids, and a picture and its thumbnail (uploaded in either order) share it without any stored state.</summary>
    public string PhotoName(long account, long photoId, string type, string part = "") =>
        $"{photoId}-{type}-{Convert.ToHexStringLower(HMACSHA256.HashData(Store.JwtKey, Encoding.ASCII.GetBytes($"photo:{account}:{photoId}")))[..32]}{(part != "" ? "-" + part : "")}.jpg";

    static readonly Regex OldPhotoName = new(@"\A[0-9]+-([0-9]+)\.jpg\z", RegexOptions.ECMAScript);

    /// <summary>Once, at start: every photo stored as `&lt;id&gt;-&lt;photo_type&gt;.jpg` (the names before <see cref="PhotoName"/>, which anyone could guess) is renamed to its new name,
    /// its thumbnail with it, and its row follows; a file that is gone just updates the row. Files of every type are renamed (the automatic screenshots stay stored, only unlinked). A second start changes nothing.</summary>
    public void MigratePhotoNames()
    {
        List<long> accounts;
        using (var all = Store.Open())
            accounts = [.. all.Rows("SELECT id FROM accounts").Select(r => (long)r[0])];
        foreach (var account in accounts)
        {
            var dir = AccountPhotos(account);
            using var con = Con(account);
            foreach (var r in con.Rows("SELECT id, file FROM photos WHERE account_id = $a AND file IS NOT NULL").ToList())
            {
                var old = (string)r[1];
                if (OldPhotoName.Match(old) is not { Success: true } m)
                    continue;
                var name = PhotoName(account, (long)r[0], m.Groups[1].Value);
                foreach (var (from, to) in new[] { (old, name), (ThumbName(old), ThumbName(name)) })
                    if (File.Exists(Path.Combine(dir, from)))
                        File.Move(Path.Combine(dir, from), Path.Combine(dir, to), overwrite: true);
                con.Exec("UPDATE photos SET file = ? WHERE account_id = $a AND id = ?", name, (long)r[0]);
            }
        }
    }

    /// <summary>The account's gallery pictures, newest first, each `{id, type, url, thumbnail, label, animal}`: an uploaded camera photo or trophy shot (<see cref="GalleryTypes"/>), or a picture a restore unpacked;
    /// `animal` is the species of the kill with the photo's individual ("" without one), `label` that kill's score ("" without one); `thumbnail` is the
    /// picture's own link when the game sent no thumbnail; with `kills`, `kill` is the id of the kill whose score sheet page the website has (<see cref="PublicSheet"/>), else null. The links are on the game's host, or on `site` (the website's) when given. ponytail: the whole gallery is read and each thumbnail looked up on disk per request; page it in SQL if galleries grow past a few hundred photos.</summary>
    List<JsonObject> GalleryRows(long account, string? site = null, bool kills = false)
    {
        using var con = Con(account);
        return [.. con.Rows("SELECT p.id, p.photo_type, p.file, k.species, json_extract(h.body, '$.score'), p.saved, k.kill_id FROM photos p"
                            + " LEFT JOIN kills k ON k.account_id = $a AND k.individual = p.individual AND p.spawn = " + KillSpawn("k.individual", "k.harvest")
                            + " LEFT JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                            + $" WHERE p.account_id = $a AND p.file IS NOT NULL AND (p.photo_type IN ({GalleryTypes}) OR p.saved IS NOT NULL) GROUP BY p.id ORDER BY p.id DESC")
            .Select(r =>
            {
                var file = (string)r[2];
                var url = PhotoUrl(account, file, site);
                var saved = r[5] is string s ? JsonNode.Parse(s) : null;  // a restored picture has no kill here: its saved label and animal
                var row = new JsonObject
                {
                    ["id"] = (long)r[0], ["type"] = Py.FromSql(r[1]), ["url"] = url,
                    ["thumbnail"] = File.Exists(Path.Combine(AccountPhotos(account), ThumbName(file))) ? PhotoUrl(account, ThumbName(file), site) : url,
                    ["label"] = r[4] is DBNull ? C(saved?["label"]) ?? "" : Py.Str(Py.FromSql(r[4])),
                    ["animal"] = r[3] is DBNull ? C(saved?["animal"]) ?? "" : SpeciesName(Py.FromSql(r[3])) ?? "",
                };
                if (kills)
                    row["kill"] = r[6] is not DBNull ? (long?)(long)r[6] : long.TryParse((string?)saved?["kill_id"], out var kill) && SavedPage(account, kill) is not null ? kill : (long?)null;
                return row;
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

    /// <summary>What a profile page's gallery tab shows of an account to `viewer`: one page of its gallery, `{page, pages, total, rows: [{id, type, url, thumbnail, label, animal, kill, share}]}`; `url` and `thumbnail` are both
    /// the viewer's link of the picture (<see cref="ViewLink"/>): the original for its owner, the display-size copy for anyone else. `share` (the owner's rows only) is the bare link of the display-size copy: it shows to a signed-in player, never the original.</summary>
    public JsonObject PublicGallery(long account, int page, string site, long viewer)
    {
        var all = GalleryRows(account, site, kills: true);
        var pages = Math.Max(1, (all.Count + GalleryPageSize - 1) / GalleryPageSize);
        page = Math.Clamp(page, 1, pages);
        var rows = all.Skip((page - 1) * GalleryPageSize).Take(GalleryPageSize).ToList();
        foreach (var r in rows)  // the game's square crop is not the tile: the page scales the picture, as for a restored one
        {
            var file = Path.GetFileName((string)r["url"]!);
            var link = ViewLink(viewer, account, file, site);
            r["url"] = link;
            r["thumbnail"] = link;
            if (viewer == account) r["share"] = PhotoUrl(account, DisplayName(account, file), site);
        }
        return new JsonObject { ["page"] = page, ["pages"] = pages, ["total"] = all.Count, ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)r)]) };
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
            {
                File.Delete(Path.Combine(AccountPhotos(account), name));
                if (File.Exists(DisplayPath(account, name))) File.Delete(DisplayPath(account, name));  // its folder may not exist: no copy was ever made
            }
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

    /// <summary>The longest side of the copy of a picture another player is served: the lightbox's size.</summary>
    public const int DisplaySize = 1280;
    const long MaxPixels = 50_000_000;  // a picture larger than this is never decoded
    static readonly ImageConfiguration JpegOnly = new(new JpegConfigurationModule());

    /// <summary>The website's name of the display-size copy of a picture: `display-&lt;32 hex digits&gt;.jpg`, an HMAC-SHA256 of the picture keyed with the server's secret, so a display link never tells the original's name
    /// (the game's host serves the original by its name, without a session).</summary>
    string DisplayName(long account, string file) =>
        "display-" + Convert.ToHexStringLower(HMACSHA256.HashData(Store.JwtKey, Encoding.UTF8.GetBytes($"photo-display:{account}/{file}")))[..32] + ".jpg";

    /// <summary>The website's link of a picture for `viewer`: the original for its owner, else the display-size copy (<see cref="DisplayName"/>). Its key (<see cref="ViewKey"/>) names the viewer and holds until the end of
    /// tomorrow (UTC), so the launcher's pages, which send no cookie, load it, and a bare link answers 401.</summary>
    string ViewLink(long viewer, long account, string file, string site)
    {
        var until = ((long)Clock() / 86400 + 2) * 86400;
        var name = viewer == account ? file : DisplayName(account, file);
        return PhotoUrl(account, name, site) + $"?viewer={viewer}&until={until}&key={ViewKey(viewer, $"{account}/{name}", until)}";
    }

    /// <summary>The first 32 hex digits of an HMAC-SHA256 of the viewer, the picture (`&lt;account&gt;/&lt;name&gt;`) and the link's end, keyed with the server's secret.</summary>
    string ViewKey(long viewer, string name, long until) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Store.JwtKey, Encoding.UTF8.GetBytes($"photo-view:{viewer}:{name}:{until}")))[..32];

    /// <summary>The viewer a picture link names (<see cref="ViewLink"/>): null for a missing, wrong or expired key.</summary>
    public long? LinkViewer(string name, string? viewer, string? until, string? key) =>
        long.TryParse(viewer, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && long.TryParse(until, NumberStyles.None, CultureInfo.InvariantCulture, out var u) && u > Clock()
        && key is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ViewKey(v, name, u)), Encoding.ASCII.GetBytes(key))
            ? v : null;

    /// <summary>GET of the website's `/photos/&lt;account&gt;/&lt;name&gt;` by `viewer` (the session's account, else the one the link names): the status and the file to send. 401 without a viewer. A stored file (the original)
    /// goes to its owner only, 403 for anyone else; a display name (<see cref="DisplayName"/>) of a gallery picture is its display-size copy (<see cref="DisplayCopy"/>), and another player's read of it counts in
    /// <see cref="ReadAllowed"/> (429 past it). The imported avatar stays everyone's. 404 for no such picture.</summary>
    public (int Status, string? File) WebPhoto(string name, long? viewer)
    {
        var file = PhotoFile(PHOTO_PREFIX + name);
        if (file is not null && Path.GetFileName(file) == ImportedAvatar) return (200, file);
        if (viewer is not { } v) return (401, null);
        var slash = name.IndexOf('/');
        if (slash < 0 || !long.TryParse(name[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var owner)) return (404, null);
        if (file is not null) return owner == v ? (200, file) : (403, null);
        if (DisplayedPicture(owner, name[(slash + 1)..]) is not { } original) return (404, null);
        if (owner != v && !ReadAllowed(v)) return (429, null);
        return DisplayCopy(owner, original) is { } copy ? (200, copy) : (404, null);
    }

    /// <summary>The stored file of the gallery picture whose display name is `name`; null for none. ponytail: one HMAC per gallery picture per request; keep a name column if galleries grow past a few thousand.</summary>
    string? DisplayedPicture(long account, string name)
    {
        if (!name.StartsWith("display-", StringComparison.Ordinal)) return null;
        using var con = Con(account);
        return con.Rows($"SELECT file FROM photos WHERE account_id = $a AND file IS NOT NULL AND (photo_type IN ({GalleryTypes}) OR saved IS NOT NULL)")
            .Select(r => (string)r[0]).FirstOrDefault(f => DisplayName(account, f) == name) is { } file ? PhotoFile($"{PHOTO_PREFIX}{account}/{file}") : null;
    }

    string DisplayDir(long account) => Path.Combine(photosDir, "display", account.ToString(CultureInfo.InvariantCulture));

    string DisplayPath(long account, string name) => Path.Combine(DisplayDir(account), name);

    /// <summary>Remove every display-size copy of the account (a restore replaced or moved its pictures): they are made again when asked for.</summary>
    void DropDisplayCopies(long account)
    {
        if (Directory.Exists(DisplayDir(account))) Directory.Delete(DisplayDir(account), recursive: true);
    }

    /// <summary>The display-size copy of a stored picture: decoded as a JPEG without its metadata, its longest side at most <see cref="DisplaySize"/>, and encoded again; kept in `photos/display/&lt;account&gt;/` under the
    /// original's name and made again when the original is newer. Null for a file that is no JPEG this reads.</summary>
    string? DisplayCopy(long account, string file)
    {
        var copy = DisplayPath(account, Path.GetFileName(file));
        if (File.Exists(copy) && File.GetLastWriteTimeUtc(copy) >= File.GetLastWriteTimeUtc(file)) return copy;
        Directory.CreateDirectory(DisplayDir(account));
        var part = $"{copy}.{Guid.NewGuid():N}.part";
        try
        {
            var info = Picture.Identify(Decoding(null), file);
            if ((long)info.Width * info.Height > MaxPixels) return null;
            var large = info.Width > DisplaySize || info.Height > DisplaySize;
            using var picture = Picture.Load(Decoding(large ? new(DisplaySize, DisplaySize) : null), file);  // a JPEG decodes at a smaller scale
            if (picture.Width > DisplaySize || picture.Height > DisplaySize)  // the decoder leaves it when one side already fits the box
                picture.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new(DisplaySize, DisplaySize) }));
            using (var to = File.Create(part))
                picture.Save(to, new JpegEncoder { Quality = 85 });
            File.Move(part, copy, overwrite: true);
            return copy;
        }
        catch (Exception e) when (e is SixLabors.ImageSharp.ImageFormatException or SixLabors.ImageSharp.Memory.InvalidMemoryOperationException or IOException)
        {
            return null;
        }
        finally
        {
            File.Delete(part);
        }
    }

    static DecoderOptions Decoding(SixLabors.ImageSharp.Size? target) => new() { Configuration = JpegOnly, MaxFrames = 1, SkipMetadata = true, TargetSize = target };

    /// <summary>SQL of the spawn (event 7) of a kill's animal: the latest of its individual before the kill's event 3, as <see cref="KillHits"/> bounds its hits.</summary>
    static string KillSpawn(string individual, string harvest) =>
        $"(SELECT MAX(s.id) FROM events s WHERE s.account_id = $a AND s.event = 7 AND s.individual = {individual}"
        + $" AND s.id < (SELECT MAX(t.id) FROM events t WHERE t.account_id = $a AND t.event = 3 AND t.body = {harvest}))";

    /// <summary>The link of the picture of a kill: the newest trophy shot among the photos asked for while its animal was the individual; null without one. No other photo type is a kill's picture.</summary>
    string? KillPhoto(Db con, long account, object? individual, string harvest) =>
        con.Scalar($"SELECT file FROM photos WHERE account_id = $a AND file IS NOT NULL AND photo_type = {TrophyShot} AND individual = ?1 AND spawn = {KillSpawn("?1", "?2")}"
                   + " ORDER BY id DESC LIMIT 1", individual, harvest) is string file
            ? PhotoUrl(account, file) : null;
}
