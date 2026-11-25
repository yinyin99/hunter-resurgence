// `admin news`: publish and change the news posts from the command line (docs/server.md#news). The admin API (NewsApi.cs) does the same from a form.
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public static class NewsCommand
{
    public const string Usage = """
        Usage: admin news add --title "…" --body-file post.md [--link URL --link-text "…"] [--picture file.jpg] [--at ISO] [--until ISO] [--pin] [--discord]
               admin news list
               admin news edit <id> [--title "…"] [--body-file post.md] [--link URL|none] [--link-text "…"|none] [--picture file|none | --picture-slot name] [--at ISO] [--until ISO|none] [--pin | --unpin] [--show]
               admin news hide <id>
               admin news lang <id> <code> --title "…" --body-file post.md [--link-text "…"]
        The title and body are the English text. A body is Markdown (paragraphs, **bold**, *italic*, [links](https://…), - lists); --body-file - reads it from the standard input.
        A link is an https address, or link:<key>, a link of the project's own (settings.json of the website and the launcher). A picture is a JPEG, PNG or WebP file up to 5 MB, stored as it is; --picture-slot names instead a picture slot of the website and the launcher (docs/images.md), such as competition-hero-170039.
        --at is when the post starts to show (default: now), --until when it stops; ISO times such as 2026-12-24T18:00:00Z, no zone means UTC. --pin shows it above the newer posts.
        --discord also posts it to the announcements channel (setting news_webhook); not with a later --at. hide takes a post off (edit --show brings it back). lang adds or replaces a translation, any time later.
        """;

    static readonly string[] Values = ["--title", "--body-file", "--link", "--link-text", "--picture", "--picture-slot", "--at", "--until"];
    static readonly string[] Flags = ["--pin", "--unpin", "--show", "--discord"];

    /// <summary>Runs the command (the words after `admin news`); 0 when it did what was asked. <paramref name="http"/> is for tests (the Discord post).</summary>
    public static int Run(string[] a, News news, JsonObject cfg, TextWriter output, TextWriter error, TextReader input, HttpClient? http = null)
    {
        try { return Do(a, news, cfg, output, error, input, http); }
        catch (NewsException e) { return Fail(error, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(error, $"cannot read: {e.Message}"); }
    }

    static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 1;
    }

    static int Do(string[] a, News news, JsonObject cfg, TextWriter output, TextWriter error, TextReader input, HttpClient? http)
    {
        if (a is not [var sub, .. var rest] || Parse(rest) is not var (values, flags, words)) return Fail(error, Usage);
        string? Get(string name) => values.GetValueOrDefault(name);
        string? Clear(string name) => Get(name) is "none" ? "" : Get(name);  // `none` takes a link, a link text or an end away
        string? Body() => Get("--body-file") is { } path ? path == "-" ? input.ReadToEnd() : File.ReadAllText(path, Encoding.UTF8) : null;
        byte[] Picture(string path) => new FileInfo(path).Length > News.MaxPictureBytes ? throw new NewsException($"the picture is over {News.MaxPictureBytes >> 20} MB") : File.ReadAllBytes(path);
        var now = Store.Now();
        switch (sub, words)
        {
            case ("list", []) when values.Count == 0 && flags.Count == 0:
                var all = news.List();
                foreach (var p in all)
                    output.WriteLine($"{p.Id}\t{p.State(now)}{(p.Pinned ? ", pinned" : "")}\tfrom {At(p.Published)}\t{(p.Until is { } until ? "until " + At(until) : "no end")}\t{(p.Picture is null ? "no picture" : p.Picture.StartsWith(News.SlotPrefix, StringComparison.Ordinal) ? p.Picture : "picture")}\t{string.Join(',', p.Texts.Keys)}\t{p.Texts["en"].Title}");
                if (all.Count == 0) output.WriteLine("no news posts");
                return 0;
            case ("add", []) when !flags.Overlaps(["--unpin", "--show"]):
                if (Get("--title") is not { } title || Body() is not { } body) return Fail(error, Usage);
                var discord = flags.Contains("--discord");
                if (discord && (string?)cfg["news_webhook"] is not { Length: > 0 }) return Fail(error, "--discord needs the setting news_webhook (the announcements channel's webhook)");
                var picture = Get("--picture") is { } pictureFile ? Picture(pictureFile) : null;
                var id = news.Add(new NewsPatch(title, body, Get("--link"), Get("--link-text"), Get("--at"), Get("--until"), flags.Contains("--pin")), picture, now);
                var post = news.Get(id)!;
                output.WriteLine($"post {id} added: {post.State(now)}, from {At(post.Published)}");
                if (!discord) return 0;
                if (Announce(post, cfg, now, http).GetAwaiter().GetResult() is { } why) return Fail(error, $"post {id} is saved, but not posted to Discord: {why}");
                output.WriteLine("posted to Discord");
                return 0;
            case ("edit", [var text]) when long.TryParse(text, out var edited) && !(flags.Contains("--pin") && flags.Contains("--unpin")) && !flags.Contains("--discord"):
                var changed = new NewsPatch(Get("--title"), Body(), Clear("--link"), Clear("--link-text"), Get("--at"), Clear("--until"),
                    flags.Contains("--pin") ? true : flags.Contains("--unpin") ? false : null, flags.Contains("--show") ? false : null);
                var newPicture = Get("--picture") is { } pic and not "none" ? Picture(pic) : null;
                if (newPicture is not null) News.PictureType(newPicture);  // refused before anything changes
                if (Get("--picture") is not null && Get("--picture-slot") is not null || changed == new NewsPatch() && newPicture is null && Get("--picture") is null && Get("--picture-slot") is null) return Fail(error, Usage);
                news.Edit(edited, changed);
                if (newPicture is not null) news.SetPicture(edited, newPicture);
                else if (Get("--picture") == "none") news.ClearPicture(edited);
                if (Get("--picture-slot") is { } slot) news.SetPictureSlot(edited, slot);
                output.WriteLine($"post {edited} changed");
                return 0;
            case ("hide", [var text]) when long.TryParse(text, out var hidden) && values.Count == 0 && flags.Count == 0:
                news.Edit(hidden, new NewsPatch(Hidden: true));
                output.WriteLine($"post {hidden} hidden (edit {hidden} --show brings it back)");
                return 0;
            case ("lang", [var text, var code]) when long.TryParse(text, out var translated) && flags.Count == 0:
                if (Get("--title") is not { } name || Body() is not { } words_) return Fail(error, Usage);
                news.SetText(translated, code, name, words_, Get("--link-text"));
                output.WriteLine($"post {translated}: the {News.Language(code)} text is set");
                return 0;
            default:
                return Fail(error, Usage);
        }
    }

    static string At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("u");

    /// <summary>The options and the bare words of the arguments; null for an option that is unknown, repeated or has no value.</summary>
    static (Dictionary<string, string> Values, HashSet<string> Flags, List<string> Words)? Parse(string[] a)
    {
        Dictionary<string, string> values = [];
        HashSet<string> flags = [];
        List<string> words = [];
        for (var i = 0; i < a.Length; i++)
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) words.Add(a[i]);
            else if (Flags.Contains(a[i])) flags.Add(a[i]);
            else if (Values.Contains(a[i]) && i + 1 < a.Length && values.TryAdd(a[i], a[i + 1])) i++;
            else return null;
        return (values, flags, words);
    }

    /// <summary>Posts the post to the announcements channel: null when it was posted, else why not. A post that shows later is not announced (the picture and the link would lead nowhere yet).</summary>
    public static async Task<string?> Announce(NewsPost post, JsonObject cfg, long now, HttpClient? http = null) =>
        post.Published > now ? $"the post shows from {At(post.Published)}, and --discord posts at once" : await Alerts.Post(cfg, "news_webhook", Embed(post, cfg), http);

    /// <summary>The Discord message of a post: an embed with its English title, body (Markdown, which Discord shows), link and picture. The picture is the one the website serves, so it needs the post to show.</summary>
    public static object Embed(NewsPost post, JsonObject cfg)
    {
        static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
        var en = post.Texts["en"];
        var embed = new Dictionary<string, object> { ["title"] = Cut(en.Title, 256), ["description"] = Cut(en.Body, 4000) };
        if (post.Link is { } link && link.StartsWith("https://", StringComparison.Ordinal)) embed["url"] = link;
        if (post.Picture is not null && !post.Picture.StartsWith(News.SlotPrefix, StringComparison.Ordinal) && (string?)cfg["web_host"] is { Length: > 0 } host) embed["image"] = new { url = $"https://{host}/news/{post.Id}/picture?v={post.Updated}" };
        return new { embeds = new[] { embed } };
    }
}
