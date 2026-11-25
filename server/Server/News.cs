// The news posts (schema 36): what `admin news`, the admin API and GET /news (NewsApi.cs) read and change. A post's texts are Markdown; the clients draw them (src/Ui/NewsMarkdown.cs), the server never sends HTML.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

/// <summary>A refused change: <see cref="Status"/> 400 for a bad value, 404 for a post that does not exist.</summary>
public sealed class NewsException(string message, int status = 400) : Exception(message)
{
    public int Status => status;
}

/// <summary>A change to a post, or the English text and the settings of a new one. A null field is left as it is; an empty string takes a <paramref name="Link"/>, <paramref name="LinkText"/> or <paramref name="Until"/> away.
/// <paramref name="At"/> and <paramref name="Until"/> are ISO times (no zone: UTC).</summary>
public sealed record NewsPatch(string? Title = null, string? Body = null, string? Link = null, string? LinkText = null, string? At = null, string? Until = null, bool? Pinned = null, bool? Hidden = null);

public sealed record NewsText(string Title, string Body, string? LinkText);

/// <summary>A post as the operator sees it: times in Unix seconds, <see cref="Picture"/> the stored file's extension (null: the default slide picture), <see cref="Texts"/> by language code, English always there.</summary>
public sealed record NewsPost(long Id, long Published, long? Until, string? Link, string? Picture, bool Pinned, bool Hidden, long Updated, SortedDictionary<string, NewsText> Texts)
{
    /// <summary>hidden, scheduled (shows later), ended, or shown.</summary>
    public string State(long now) => Hidden ? "hidden" : Published > now ? "scheduled" : Until <= now ? "ended" : "shown";
}

public sealed class News(Store store, string dataDir)
{
    /// <summary>The largest picture stored: pictures are kept as given, never resized (the server has no image library).</summary>
    public const long MaxPictureBytes = 5L << 20;

    const int MaxTitle = 200, MaxBody = 20_000, MaxLinkText = 100, MaxLink = 500;
    static readonly Regex LanguageCode = new(@"\A[A-Za-z]{2,3}(-[A-Za-z]{2,4})?\z", RegexOptions.ECMAScript);
    static readonly Regex BrandLink = new(@"\Alink:[a-z0-9_]{1,40}\z", RegexOptions.ECMAScript);
    static readonly string[] Extensions = ["jpg", "png", "webp"];
    static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF], Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // a post shows from `published`, hides at `until`; the second parameter is now
    const string Visible = "n.hidden = 0 AND n.published <= ?2 AND (n.until IS NULL OR n.until > ?2)";

    string PictureDir => Path.Combine(dataDir, "news");

    string PictureFile(long id, string extension) => Path.Combine(PictureDir, $"{id}.{extension}");

    static NewsException Bad(string message, int status = 400) => new(message, status);

    /// <summary>A language code as it is stored (`pt-BR`, `fr`), or null when it is not one.</summary>
    public static string? Language(string? code)
    {
        if (code is null || !LanguageCode.IsMatch(code)) return null;
        var parts = code.Split('-');
        return parts.Length == 1 ? parts[0].ToLowerInvariant()
            : parts[0].ToLowerInvariant() + "-" + (parts[1].Length == 2 ? parts[1].ToUpperInvariant() : char.ToUpperInvariant(parts[1][0]) + parts[1][1..].ToLowerInvariant());
    }

    /// <summary>The extension of a picture by its first bytes (JPEG, PNG or WebP); anything else, or one over <see cref="MaxPictureBytes"/>, is refused.</summary>
    public static string PictureType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxPictureBytes) throw Bad($"the picture is over {MaxPictureBytes >> 20} MB");
        if (bytes.StartsWith<byte>(Jpeg)) return "jpg";
        if (bytes.StartsWith<byte>(Png)) return "png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "webp";
        throw Bad("the picture is not a JPEG, PNG or WebP file");
    }

    public static string ContentType(string extension) => extension switch { "jpg" => "image/jpeg", "png" => "image/png", _ => "image/webp" };

    static string Required(string? text, string what, int max)
    {
        var s = text?.Trim() ?? "";
        if (s.Length == 0) throw Bad($"{what} is needed");
        return s.Length <= max ? s : throw Bad($"{what} is over {max} characters");
    }

    static string? Optional(string? text, string what, int max)
    {
        var s = text?.Trim() ?? "";
        return s.Length <= max ? s : throw Bad($"{what} is over {max} characters");
    }

    /// <summary>A link: an https address, or `link:&lt;key&gt;`, a link of the project's own (the website's and the launcher's settings.json holds the address).</summary>
    static string Link(string link)
    {
        var s = link.Trim();
        return s.Length == 0 || BrandLink.IsMatch(s) || s.Length <= MaxLink && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? s : throw Bad("the link is an https address (or link:<key>)");
    }

    static long Time(string text, string what) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeSeconds()
            : throw Bad($"{what}: an ISO time such as 2026-12-24T18:00:00Z");

    static string Normal(string body) => body.Replace("\r\n", "\n").Trim();

    /// <summary>A new post with its English text; the id. A picture is checked first and stored with the row.</summary>
    public long Add(NewsPatch p, byte[]? picture = null, long? at = null)
    {
        var now = at ?? Store.Now();
        var title = Required(p.Title, "the title", MaxTitle);
        var body = Required(p.Body is null ? null : Normal(p.Body), "the body", MaxBody);
        var published = p.At is { Length: > 0 } from ? Time(from, "at") : now;
        long? until = p.Until is { Length: > 0 } to ? Time(to, "until") : null;
        if (until <= published) throw Bad("until is not after the time the post shows");
        var link = p.Link is null ? null : Link(p.Link);
        var linkText = Optional(p.LinkText, "the link text", MaxLinkText);
        if (link is { Length: > 0 } && linkText is not { Length: > 0 }) throw Bad("a link needs its text");
        var extension = picture is null ? null : PictureType(picture);
        using var con = store.Open();
        con.Begin();
        var id = con.Insert("INSERT INTO news (published, until, link, picture, pinned, hidden, updated) VALUES (?, ?, ?, ?, ?, 0, ?)",
            published, until, link is { Length: > 0 } ? link : null, extension, p.Pinned == true, now);
        con.Exec("INSERT INTO news_texts (news_id, lang, title, body, link_text) VALUES (?, 'en', ?, ?, ?)", id, title, body, linkText is { Length: > 0 } ? linkText : null);
        if (picture is not null) WritePicture(id, extension!, picture);
        con.Commit();
        return id;
    }

    /// <summary>Changes the post: its English text and settings (see <see cref="NewsPatch"/>). A picture is changed with <see cref="SetPicture"/>.</summary>
    public void Edit(long id, NewsPatch p)
    {
        var post = Get(id) ?? throw Bad($"no post {id}", 404);
        var en = post.Texts["en"];
        var title = p.Title is null ? en.Title : Required(p.Title, "the title", MaxTitle);
        var body = p.Body is null ? en.Body : Required(Normal(p.Body), "the body", MaxBody);
        var published = p.At is null ? post.Published : Time(p.At, "at");
        var until = p.Until is null ? post.Until : p.Until.Length == 0 ? null : Time(p.Until, "until");
        if (until <= published) throw Bad("until is not after the time the post shows");
        var link = p.Link is null ? post.Link : Link(p.Link) is { Length: > 0 } l ? l : null;
        var linkText = p.LinkText is null ? en.LinkText : Optional(p.LinkText, "the link text", MaxLinkText) is { Length: > 0 } t ? t : null;
        if (link is not null && linkText is null) throw Bad("a link needs its text");
        using var con = store.Open();
        con.Begin();
        con.Exec("UPDATE news SET published = ?, until = ?, link = ?, pinned = ?, hidden = ? WHERE id = ?", published, until, link, p.Pinned ?? post.Pinned, p.Hidden ?? post.Hidden, id);
        con.Exec("UPDATE news_texts SET title = ?, body = ?, link_text = ? WHERE news_id = ? AND lang = 'en'", title, body, linkText, id);
        con.Commit();
    }

    /// <summary>A text of the post in another language (or its English one again). Title and body are both needed; without a link text the English one is shown.</summary>
    public void SetText(long id, string code, string title, string body, string? linkText)
    {
        var lang = Language(code) ?? throw Bad($"{code}: not a language code such as fr or pt-BR");
        if (Get(id) is null) throw Bad($"no post {id}", 404);
        var text = Optional(linkText, "the link text", MaxLinkText);
        using var con = store.Open();
        con.Exec("INSERT INTO news_texts (news_id, lang, title, body, link_text) VALUES (?, ?, ?, ?, ?) ON CONFLICT (news_id, lang) DO UPDATE SET title = excluded.title, body = excluded.body, link_text = excluded.link_text",
            id, lang, Required(title, "the title", MaxTitle), Required(Normal(body), "the body", MaxBody), text is { Length: > 0 } ? text : null);
    }

    /// <summary>Stores the post's picture as given (replacing the last one).</summary>
    public void SetPicture(long id, byte[] bytes)
    {
        var post = Get(id) ?? throw Bad($"no post {id}", 404);
        var extension = PictureType(bytes);
        using var con = store.Open();
        con.Begin();
        con.Exec("UPDATE news SET picture = ?, updated = ? WHERE id = ?", extension, Math.Max(Store.Now(), post.Updated + 1), id);  // ?v= of the picture's link changes with every picture
        WritePicture(id, extension, bytes);
        con.Commit();
    }

    /// <summary>Takes the picture away: the post shows the default slide picture.</summary>
    public void ClearPicture(long id)
    {
        var post = Get(id) ?? throw Bad($"no post {id}", 404);
        using var con = store.Open();
        con.Exec("UPDATE news SET picture = NULL, updated = ? WHERE id = ?", Math.Max(Store.Now(), post.Updated + 1), id);
        foreach (var e in Extensions) File.Delete(PictureFile(id, e));
    }

    void WritePicture(long id, string extension, byte[] bytes)
    {
        Directory.CreateDirectory(PictureDir);
        File.WriteAllBytes(PictureFile(id, extension), bytes);
        foreach (var e in Extensions.Where(e => e != extension)) File.Delete(PictureFile(id, e));
    }

    /// <summary>Every post, newest first, hidden and scheduled ones too.</summary>
    public List<NewsPost> List()
    {
        using var con = store.Open();
        return Load(con, "");
    }

    public NewsPost? Get(long id)
    {
        using var con = store.Open();
        return Load(con, "WHERE n.id = ?1", id).FirstOrDefault();
    }

    static List<NewsPost> Load(Db con, string where, params object?[] args)
    {
        var posts = con.Rows($"SELECT n.id, n.published, n.until, n.link, n.picture, n.pinned, n.hidden, n.updated FROM news n {where} ORDER BY n.published DESC, n.id DESC", args)
            .Select(r => new NewsPost((long)r[0], (long)r[1], r[2] as long?, r[3] as string, r[4] as string, (long)r[5] != 0, (long)r[6] != 0, (long)r[7], new())).ToList();
        var byId = posts.ToDictionary(p => p.Id);
        foreach (var r in con.Rows("SELECT news_id, lang, title, body, link_text FROM news_texts"))
            if (byId.TryGetValue((long)r[0], out var post)) post.Texts[(string)r[1]] = new((string)r[2], (string)r[3], r[4] as string);
        return posts;
    }

    /// <summary>What `GET /news?lang=` sends: `{posts: [{id, published, pinned, title, body, link, linkText, picture}]}`, the posts that show at <paramref name="now"/>, pinned first, then newest first, each in the reader's language
    /// (a bare or unknown code is English) with English for what that language lacks.</summary>
    public JsonObject Feed(string? lang, long now)
    {
        var code = Language(lang) ?? "en";
        using var con = store.Open();
        var posts = new JsonArray();
        foreach (var r in con.Rows($"""
            SELECT n.id, n.published, n.pinned, COALESCE(l.title, e.title), COALESCE(l.body, e.body), n.link, COALESCE(l.link_text, e.link_text), n.picture, n.updated
            FROM news n
            JOIN news_texts e ON e.news_id = n.id AND e.lang = 'en'
            LEFT JOIN news_texts l ON l.news_id = n.id AND l.lang = ?1
            WHERE {Visible}
            ORDER BY n.pinned DESC, n.published DESC, n.id DESC
            """, code, now))
            posts.Add(new JsonObject
            {
                ["id"] = (long)r[0], ["published"] = (long)r[1], ["pinned"] = (long)r[2] != 0, ["title"] = (string)r[3], ["body"] = (string)r[4],
                ["link"] = r[5] as string, ["linkText"] = r[6] as string, ["picture"] = r[7] is string ? $"/news/{(long)r[0]}/picture?v={(long)r[8]}" : null,
            });
        return new JsonObject { ["posts"] = posts };
    }

    /// <summary>The stored picture of a post that shows at <paramref name="now"/> (a scheduled or hidden post's picture is not public), else null.</summary>
    public (byte[] Bytes, string Type)? Picture(long id, long now)
    {
        using var con = store.Open();
        if (con.Rows($"SELECT n.picture FROM news n WHERE n.id = ?1 AND {Visible}", id, now) is not [{ } row] || row[0] is not string extension) return null;
        var file = PictureFile(id, extension);
        return File.Exists(file) ? (File.ReadAllBytes(file), ContentType(extension)) : null;
    }
}
