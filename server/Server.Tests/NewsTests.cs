using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

static class NewsFixtures
{
    public static long T(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2];
    public static readonly byte[] Webp = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBPVP8 "u8];

    public static string[] Titles(JsonObject feed) => [.. feed["posts"]!.AsArray().Select(p => (string)p!["title"]!)];

    /// <summary>The three posts a new database starts with are hidden, so a test sees only its own.</summary>
    public static void HideSeeded(News news)
    {
        foreach (var p in news.List()) news.Edit(p.Id, new NewsPatch(Hidden: true));
    }
}

/// <summary>The news posts (schema 36): what the feed lists and in which order, the reader's language, the schedule, the pictures and what a change refuses.</summary>
public class NewsTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("news").FullName;
    readonly Store store;
    readonly News news;

    public NewsTests()
    {
        store = new Store(Path.Combine(tmp, "state.db"));
        news = new News(store, tmp);
    }

    public void Dispose() => DeleteDir(tmp);

    long Add(string title, string? at = null, string? until = null, bool pin = false, string body = "text") =>
        news.Add(new(title, body, At: at, Until: until, Pinned: pin), null, NewsFixtures.T("2026-12-01T00:00:00Z"));

    string[] Feed(string? lang, string now) => NewsFixtures.Titles(news.Feed(lang, NewsFixtures.T(now)));

    [Fact]
    public void TheFeedListsPinnedPostsFirstThenTheNewestFirst()
    {
        NewsFixtures.HideSeeded(news);
        Add("old", "2026-12-01T00:00:00Z");
        Add("new", "2026-12-05T00:00:00Z");
        Add("same day, later id", "2026-12-05T00:00:00Z");
        Add("pinned and oldest", "2026-11-01T00:00:00Z", pin: true);

        Assert.Equal(["pinned and oldest", "same day, later id", "new", "old"], Feed("en", "2026-12-10T00:00:00Z"));
    }

    [Fact]
    public void APostShowsFromItsStartAndStopsAtItsEndAndAHiddenOneNeverShows()
    {
        NewsFixtures.HideSeeded(news);
        Add("brief", "2026-12-01T00:00:00Z", "2026-12-03T00:00:00Z");
        Add("event", "2026-12-20T18:00:00Z");
        var hidden = Add("hidden", "2026-12-01T00:00:00Z");
        news.Edit(hidden, new NewsPatch(Hidden: true));

        Assert.Equal(["brief"], Feed("en", "2026-12-02T00:00:00Z"));
        Assert.Empty(Feed("en", "2026-12-03T00:00:00Z"));  // until is the first second it no longer shows
        Assert.Empty(Feed("en", "2026-12-20T17:59:59Z"));  // an event's post can be written before it starts
        Assert.Equal(["event"], Feed("en", "2026-12-20T18:00:00Z"));

        news.Edit(hidden, new NewsPatch(Hidden: false));
        Assert.Equal(["event", "hidden"], Feed("en", "2026-12-21T00:00:00Z"));
    }

    [Fact]
    public void ThePostsAreInTheReadersLanguageWithEnglishWhereTheyLackIt()
    {
        NewsFixtures.HideSeeded(news);
        var id = news.Add(new("English only", "Body", Link: "https://example.test/x", LinkText: "Read"), null, NewsFixtures.T("2026-12-01T00:00:00Z"));
        var both = Add("Both");
        news.SetText(both, "fr", "Les deux", "Corps", null);
        news.SetText(both, "pt-br", "Os dois", "Corpo", null);
        var now = NewsFixtures.T("2026-12-02T00:00:00Z");

        Assert.Equal(["Both", "English only"], Feed("de", "2026-12-02T00:00:00Z"));  // no German: English for all
        Assert.Equal(["Les deux", "English only"], Feed("fr", "2026-12-02T00:00:00Z"));
        Assert.Equal(["Os dois", "English only"], Feed("pt-BR", "2026-12-02T00:00:00Z"));  // the code of a stored text is matched whatever its case
        Assert.Equal(["Both", "English only"], Feed("pt", "2026-12-02T00:00:00Z"));  // a bare pt is no pt-BR
        Assert.Equal(["Both", "English only"], Feed(null, "2026-12-02T00:00:00Z"));
        Assert.Equal(["Both", "English only"], Feed("x'; DROP TABLE news; --", "2026-12-02T00:00:00Z"));

        // a translation added later shows to its readers at once; the English link text stands in for one it lacks
        news.SetText(id, "fr", "Seulement anglais", "Corps", null);
        var french = news.Feed("fr", now)["posts"]!.AsArray().Single(p => (long)p!["id"]! == id)!;
        Assert.Equal(("Seulement anglais", "https://example.test/x", "Read"), ((string)french["title"]!, (string)french["link"]!, (string)french["linkText"]!));
        news.SetText(id, "fr", "Seulement anglais", "Corps", "Lire");
        Assert.Equal("Lire", (string?)news.Feed("fr", now)["posts"]!.AsArray().Single(p => (long)p!["id"]! == id)!["linkText"]);
    }

    [Fact]
    public void ALanguageCodeIsStoredWithTheCaseOfLanguageAndRegion()
    {
        Assert.Equal("fr", News.Language("FR"));
        Assert.Equal("pt-BR", News.Language("pt-br"));
        Assert.Equal("zh-Hans", News.Language("ZH-HANS"));
        Assert.All(new[] { "", "f", "french", "fr_FR", "fr-", "1r", "fr-B", "fr'; --" }, c => Assert.Null(News.Language(c)));
    }

    [Fact]
    public void BadValuesAreRefusedAndLeaveNoPost()
    {
        var before = news.List().Count;
        foreach (var bad in new NewsPatch[]
        {
            new(null, "body"), new("  ", "body"), new("title", null), new("title", " "), new(new string('t', 201), "body"), new("title", new string('b', 20_001)),
            new("title", "body", Link: "http://example.test/x", LinkText: "go"), new("title", "body", Link: "javascript:alert(1)", LinkText: "go"),
            new("title", "body", Link: "https://user:pass@example.test/x", LinkText: "go"), new("title", "body", Link: "https://example.test/x"),
            new("title", "body", Link: "link:Bad Key", LinkText: "go"), new("title", "body", At: "tomorrow-ish"), new("title", "body", Until: "nope"),
            new("title", "body", At: "2026-12-05T00:00:00Z", Until: "2026-12-05T00:00:00Z"), new("title", "body", At: "2026-12-05T00:00:00Z", Until: "2026-12-04T00:00:00Z"),
        })
            Assert.Throws<NewsException>(() => news.Add(bad));
        Assert.Equal(before, news.List().Count);

        var id = Add("fine");
        Assert.Equal(404, Assert.Throws<NewsException>(() => news.Edit(id + 99, new NewsPatch(Title: "x"))).Status);
        Assert.Equal(404, Assert.Throws<NewsException>(() => news.SetText(id + 99, "fr", "t", "b", null)).Status);
        Assert.Throws<NewsException>(() => news.SetText(id, "fr", "", "b", null));
        Assert.Throws<NewsException>(() => news.SetText(id, "klingon", "t", "b", null));
        Assert.Throws<NewsException>(() => news.Edit(id, new NewsPatch(Link: "https://example.test/x")));  // a link needs its text
        Assert.Throws<NewsException>(() => news.Edit(id, new NewsPatch(Until: "2000-01-01T00:00:00Z")));  // before it shows
    }

    [Fact]
    public void ALinkOfTheProjectAndAnHttpsLinkAreAcceptedAndEditTakesThemAway()
    {
        var id = news.Add(new("t", "b", Link: "link:source", LinkText: "Follow"), null, NewsFixtures.T("2026-12-01T00:00:00Z"));
        Assert.Equal("link:source", news.Get(id)!.Link);
        news.Edit(id, new NewsPatch(Link: "https://example.test/y"));
        Assert.Equal("https://example.test/y", news.Get(id)!.Link);
        news.Edit(id, new NewsPatch(Link: "", LinkText: ""));
        var post = news.Get(id)!;
        Assert.Equal((null, null), (post.Link, post.Texts["en"].LinkText));
        news.Edit(id, new NewsPatch(Until: "2026-12-09T00:00:00Z", Pinned: true, Title: "t2", Body: "line one\r\nline two"));
        post = news.Get(id)!;
        Assert.Equal((NewsFixtures.T("2026-12-09T00:00:00Z"), true, "t2", "line one\nline two"), (post.Until, post.Pinned, post.Texts["en"].Title, post.Texts["en"].Body));
        news.Edit(id, new NewsPatch(Until: ""));
        Assert.Null(news.Get(id)!.Until);
    }

    [Fact]
    public void APictureIsCheckedByItsFirstBytesAndKeptAsGiven()
    {
        Assert.Equal("jpg", News.PictureType(NewsFixtures.Jpeg));
        Assert.Equal("png", News.PictureType(NewsFixtures.Png));
        Assert.Equal("webp", News.PictureType(NewsFixtures.Webp));
        foreach (var bad in new byte[][] { "GIF89a"u8.ToArray(), "<svg onload=alert(1)>"u8.ToArray(), "RIFF\0\0\0\0WAVEfmt "u8.ToArray(), [0xFF, 0xD8], [] })
            Assert.Throws<NewsException>(() => News.PictureType(bad));
        var huge = new byte[News.MaxPictureBytes + 1];
        NewsFixtures.Jpeg.CopyTo(huge, 0);
        Assert.Throws<NewsException>(() => News.PictureType(huge));  // over 5 MB, though it starts as a JPEG

        NewsFixtures.HideSeeded(news);
        var id = Add("with a picture", "2026-12-01T00:00:00Z");
        Assert.Null(news.Picture(id, NewsFixtures.T("2026-12-02T00:00:00Z")));
        news.SetPicture(id, NewsFixtures.Png);
        var now = NewsFixtures.T("2026-12-02T00:00:00Z");
        var first = (string)news.Feed("en", now)["posts"]![0]!["picture"]!;
        Assert.StartsWith($"/news/{id}/picture?v=", first);
        var served = news.Picture(id, now)!.Value;
        Assert.Equal(NewsFixtures.Png, served.Bytes);
        Assert.Equal("image/png", served.Type);
        Assert.Equal(NewsFixtures.Png, File.ReadAllBytes(Path.Combine(tmp, "news", $"{id}.png")));  // as given: no resizing

        news.SetPicture(id, NewsFixtures.Jpeg);  // another type replaces it, and the link's version changes
        Assert.Equal([$"{id}.jpg"], Directory.GetFiles(Path.Combine(tmp, "news")).Select(Path.GetFileName));
        Assert.NotEqual(first, (string)news.Feed("en", now)["posts"]![0]!["picture"]!);
        Assert.Equal("image/jpeg", news.Picture(id, now)!.Value.Type);
        Assert.Throws<NewsException>(() => news.SetPicture(id, "GIF89a"u8.ToArray()));
        Assert.Equal("image/jpeg", news.Picture(id, now)!.Value.Type);  // a refused picture changes nothing

        // the picture of a post that does not show now (scheduled, ended, hidden) is not public
        Assert.Null(news.Picture(id, NewsFixtures.T("2026-11-30T00:00:00Z")));
        news.Edit(id, new NewsPatch(Hidden: true));
        Assert.Null(news.Picture(id, now));

        news.ClearPicture(id);
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "news")));
        Assert.Null(news.Get(id)!.Picture);
    }

    [Fact]
    public void APostMayNameAPictureSlotInsteadOfAFileAndEachReplacesTheOther()
    {
        NewsFixtures.HideSeeded(news);
        var id = Add("Slotted", "2026-12-01T00:00:00Z");
        news.SetPicture(id, NewsFixtures.Png);
        news.SetPictureSlot(id, "competition-hero-170039");
        var now = NewsFixtures.T("2026-12-02T00:00:00Z");
        Assert.Equal("slot:competition-hero-170039", (string)news.Feed("en", now)["posts"]![0]!["picture"]!);
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "news")));  // the file is gone
        Assert.Null(news.Picture(id, now));
        foreach (var bad in new[] { "", "../x", "a/b", "-x", "x;y", new string('a', 81) })
            Assert.Throws<NewsException>(() => news.SetPictureSlot(id, bad));
        Assert.Equal(404, Assert.Throws<NewsException>(() => news.SetPictureSlot(id + 99, "x")).Status);
        news.SetPicture(id, NewsFixtures.Jpeg);
        Assert.StartsWith($"/news/{id}/picture?v=", (string)news.Feed("en", now)["posts"]![0]!["picture"]!);
        news.SetPictureSlot(id, "a-slot");
        news.ClearPicture(id);
        Assert.Null(news.Get(id)!.Picture);
    }

    [Fact]
    public void AnAddWithABadPictureLeavesNoPost()
    {
        var before = news.List().Count;
        Assert.Throws<NewsException>(() => news.Add(new("t", "b"), "not a picture"u8.ToArray()));
        Assert.Equal(before, news.List().Count);
        var id = news.Add(new("t", "b"), NewsFixtures.Webp);
        Assert.Equal("webp", news.Get(id)!.Picture);
    }

    [Fact]
    public void TheFirstThreePostsAreSeededWithEveryTranslationTheyHad()
    {
        var posts = news.List();

        Assert.Equal([3L, 2L, 1L], posts.Select(p => p.Id));  // newest first
        Assert.All(posts, p =>
        {
            Assert.Equal(19, p.Texts.Count);
            Assert.All(p.Texts.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Body) || string.IsNullOrWhiteSpace(t.LinkText)));
            Assert.False(p.Hidden || p.Pinned);
        });
        Assert.Equal(["link:source", "link:backup", "link:source"], posts.Select(p => p.Link));
        // each keeps the picture slot the feed showed it with, which the feed gives as slot:<name>, and the server has no file for
        Assert.Equal(["slot:competition-hero-170039", "slot:competition-hero-170031", "slot:competition-hero-170026"], posts.Select(p => p.Picture));
        Assert.Equal(["slot:competition-hero-170039", "slot:competition-hero-170031", "slot:competition-hero-170026"], news.Feed("en", NewsFixtures.T("2027-01-01T00:00:00Z"))["posts"]!.AsArray().Select(p => (string)p!["picture"]!));
        Assert.Null(news.Picture(3, NewsFixtures.T("2027-01-01T00:00:00Z")));
        Assert.Equal(new[] { "cs", "da", "de", "en", "es", "fi", "fr", "hu", "it", "nb", "nl", "pl", "pt-BR", "pt-PT", "ro", "ru", "sl", "sv", "tr" }.Order(StringComparer.Ordinal), posts[0].Texts.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(NewsFixtures.T("2026-11-17T20:00:00Z"), posts[0].Published);
        Assert.Equal("Les serveurs officiels sont fermés", news.Get(2)!.Texts["fr"].Title);
        Assert.Equal(["Launcher 0.31: Game check and \"Report a problem\" feature added", "The official servers have closed", "Hunter Resurgence is in development"], Feed("en", "2027-01-01T00:00:00Z"));
        Assert.Equal("Les serveurs officiels sont fermés", Feed("fr", "2027-01-01T00:00:00Z")[1]);
    }

    [Fact]
    public void AnOlderDatabaseGetsTheTablesAndThePostsOnceAndKeepsWhatTheOperatorDidAfterwards()
    {
        var file = Path.Combine(tmp, "old.db");
        var old = new Store(file);
        using (var con = old.Open())
        {
            con.Exec("DROP TABLE news_texts");
            con.Exec("DROP TABLE news");
            con.Exec("PRAGMA user_version = 32");
        }
        var migrated = new Store(file);
        var posts = new News(migrated, tmp);
        Assert.Equal([3L, 2L, 1L], posts.List().Select(p => p.Id));
        var mine = posts.Add(new("mine", "text"));
        posts.Edit(1, new NewsPatch(Title: "Edited"));

        var again = new Store(file);  // the step is done: nothing is added or changed
        Assert.Equal([1L, 2L, 3L, mine], new News(again, tmp).List().Select(p => p.Id).Order());
        Assert.Equal("Edited", new News(again, tmp).Get(1)!.Texts["en"].Title);

        // right whatever order the schema steps were merged in: a database that already has the tables keeps its rows and is not seeded again, whatever its version
        foreach (var version in new[] { 32, Store.Version })
        {
            using (var con = again.Open()) con.Exec($"PRAGMA user_version = {version}");
            var third = new Store(file);
            Assert.Equal(4, new News(third, tmp).List().Count);
            using var check = third.Open();
            Assert.Equal((long)Store.Version, check.Scalar("PRAGMA user_version"));
        }
        Assert.Equal(36, Store.Version);
    }

    [Fact]
    public void ADatabaseAtTheCurrentVersionWithoutTheTablesGetsThemAtTheNextStart()
    {
        var file = Path.Combine(tmp, "current.db");
        using (var con = new Store(file).Open())
        {
            con.Exec("DROP TABLE news_texts");
            con.Exec("DROP TABLE news");
            Assert.Equal((long)Store.Version, con.Scalar("PRAGMA user_version"));  // another branch's steps raised the version past the one that has no news
        }
        Assert.Equal([3L, 2L, 1L], new News(new Store(file), tmp).List().Select(p => p.Id));
    }
}

/// <summary>`admin news`: the commands, their refusals and the post to Discord.</summary>
public class NewsCommandTests : IDisposable
{
    sealed class Fake(HttpStatusCode code = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Posted { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Posted.Add((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(code);
        }
    }

    readonly string tmp = Directory.CreateTempSubdirectory("newscmd").FullName;
    readonly News news;
    readonly JsonObject cfg = new() { ["web_host"] = "web.test", ["news_webhook"] = "https://hooks.test/announce" };

    public NewsCommandTests()
    {
        news = new News(new Store(Path.Combine(tmp, "state.db")), tmp);
        NewsFixtures.HideSeeded(news);
    }

    public void Dispose() => DeleteDir(tmp);

    string Write(string name, string content)
    {
        var path = Path.Combine(tmp, name);
        File.WriteAllText(path, content);
        return path;
    }

    string Write(string name, byte[] content)
    {
        var path = Path.Combine(tmp, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    (int Code, string Out, string Err) Run(string[] args, string stdin = "", HttpClient? http = null)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = NewsCommand.Run(args, news, cfg, output, error, new StringReader(stdin), http);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void AddPublishesTheEnglishPostFromAFileAndStandardInputAndListShowsIt()
    {
        var post = Write("post.md", "First **paragraph**.\n\n- one\n- two\n");
        var (code, output, _) = Run(["add", "--title", "Big release", "--body-file", post, "--link", "https://example.test/x", "--link-text", "Read more", "--at", "2099-01-01T10:00:00Z", "--until", "2099-02-01T00:00:00Z", "--pin"]);
        var id = news.List().Single(p => p.Texts["en"].Title == "Big release");
        Assert.Equal(0, code);
        Assert.Contains($"post {id.Id} added: scheduled", output);
        Assert.Equal(("First **paragraph**.\n\n- one\n- two", "https://example.test/x", "Read more", true, (long?)NewsFixtures.T("2099-01-01T10:00:00Z"), (long?)NewsFixtures.T("2099-02-01T00:00:00Z")),
            (id.Texts["en"].Body, id.Link, id.Texts["en"].LinkText, id.Pinned, id.Published, id.Until));

        var piped = Run(["add", "--title", "From a pipe", "--body-file", "-"], "Written\nin the shell.\r\n");
        Assert.Equal(0, piped.Code);
        Assert.Contains("shown", piped.Out);
        Assert.Equal("Written\nin the shell.", news.List().Single(p => p.Texts["en"].Title == "From a pipe").Texts["en"].Body);

        var list = Run(["list"]).Out;
        Assert.Contains("scheduled, pinned", list);
        Assert.Contains("Big release", list);
        Assert.Contains("hidden", list);  // the seeded ones are hidden here
        Assert.Contains("no end", list);
    }

    (int Code, string Out, string Err) Edit(long id, params string[] a) => Run(["edit", id.ToString(), .. a]);

    [Fact]
    public void EditChangesAPostAndNoneTakesAWayItsLinkItsEndAndItsPicture()
    {
        var id = news.Add(new("Title", "Body", Link: "https://example.test/x", LinkText: "Go", Until: "2099-03-01T00:00:00Z"), NewsFixtures.Jpeg);

        Assert.Equal(0, Edit(id, "--title", "Changed", "--body-file", Write("b.md", "New body"), "--pin").Code);
        Assert.Equal(("Changed", "New body", true), (news.Get(id)!.Texts["en"].Title, news.Get(id)!.Texts["en"].Body, news.Get(id)!.Pinned));
        Assert.Equal(0, Edit(id, "--unpin", "--link", "none", "--link-text", "none", "--until", "none", "--picture", "none").Code);
        var post = news.Get(id)!;
        Assert.Equal((false, null, null, null, null), (post.Pinned, post.Link, post.Until, post.Picture, post.Texts["en"].LinkText));
        Assert.Equal(0, Edit(id, "--picture", Write("p.png", NewsFixtures.Png), "--at", "2020-01-01T00:00:00Z").Code);
        Assert.Equal(("png", NewsFixtures.T("2020-01-01T00:00:00Z")), (news.Get(id)!.Picture, news.Get(id)!.Published));

        Assert.Equal(0, Run(["hide", id.ToString()]).Code);
        Assert.True(news.Get(id)!.Hidden);
        Assert.Equal(0, Edit(id, "--show").Code);
        Assert.False(news.Get(id)!.Hidden);
    }

    [Fact]
    public void EditNamesAPictureSlotAndRefusesASlotWithAFile()
    {
        var id = news.Add(new("Title", "Body"), NewsFixtures.Jpeg);
        Assert.Equal(0, Edit(id, "--picture-slot", "competition-hero-170031").Code);
        Assert.Equal("slot:competition-hero-170031", news.Get(id)!.Picture);
        Assert.Contains("slot:competition-hero-170031", Run(["list"]).Out);
        Assert.Equal(1, Edit(id, "--picture-slot", "x", "--picture", Write("p.png", NewsFixtures.Png)).Code);
        Assert.Equal(1, Edit(id, "--picture-slot", "../x").Code);
        Assert.Equal("slot:competition-hero-170031", news.Get(id)!.Picture);
    }

    [Fact]
    public void LangAddsATranslationAnyTimeLater()
    {
        var id = news.Add(new("Title", "Body", Link: "link:source", LinkText: "Follow"));
        var (code, output, _) = Run(["lang", id.ToString(), "fr", "--title", "Titre", "--body-file", Write("fr.md", "Corps"), "--link-text", "Suivre"]);

        Assert.Equal(0, code);
        Assert.Contains("fr text is set", output);
        Assert.Equal(new("Titre", "Corps", "Suivre"), news.Get(id)!.Texts["fr"]);
        Assert.Equal(["fr", "en"].Order(), news.Get(id)!.Texts.Keys.Order());
    }

    [Theory]
    [InlineData("")]
    [InlineData("add --title t")]
    [InlineData("add --body-file -")]
    [InlineData("add --title t --body-file - --bogus x")]
    [InlineData("add --title t --title u --body-file -")]
    [InlineData("add --title t --body-file - --unpin")]
    [InlineData("add --title t --body-file")]
    [InlineData("add stray --title t --body-file -")]
    [InlineData("list extra")]
    [InlineData("list --pin")]
    [InlineData("edit")]
    [InlineData("edit x --title t")]
    [InlineData("edit 1")]
    [InlineData("edit 1 --pin --unpin")]
    [InlineData("edit 1 --discord")]
    [InlineData("hide")]
    [InlineData("hide 1 --pin")]
    [InlineData("lang 1 fr")]
    [InlineData("lang 1 --title t --body-file -")]
    [InlineData("frobnicate")]
    public void AWrongCommandLinePrintsTheUsageAndChangesNothing(string line)
    {
        var before = news.List().Count;
        var (code, _, error) = Run(line.Length == 0 ? [] : line.Split(' '), "stdin body");

        Assert.Equal(1, code);
        Assert.Contains("Usage: admin news add", error);
        Assert.Equal(before, news.List().Count);
    }

    [Fact]
    public void ARefusedValueFilesAndPicturesSayWhyAndChangeNothing()
    {
        var before = news.List().Count;
        var ok = Write("ok.md", "body");
        Assert.Contains("no post 99", Run(["hide", "99"]).Err);
        Assert.Contains("no post 99", Run(["edit", "99", "--title", "x"]).Err);
        Assert.Contains("language code", Run(["lang", "1", "klingon", "--title", "t", "--body-file", ok]).Err);
        Assert.Contains("https", Run(["add", "--title", "t", "--body-file", ok, "--link", "http://example.test", "--link-text", "x"]).Err);
        Assert.Contains("ISO time", Run(["add", "--title", "t", "--body-file", ok, "--at", "soon"]).Err);
        Assert.Contains("cannot read", Run(["add", "--title", "t", "--body-file", Path.Combine(tmp, "missing.md")]).Err);
        Assert.Contains("JPEG, PNG or WebP", Run(["add", "--title", "t", "--body-file", ok, "--picture", Write("x.gif", "GIF89a")]).Err);
        Assert.Contains("cannot read", Run(["add", "--title", "t", "--body-file", ok, "--picture", Path.Combine(tmp, "missing.png")]).Err);
        Assert.Equal(before, news.List().Count);

        var id = news.Add(new("t", "b"), NewsFixtures.Jpeg);
        Assert.Equal(1, Run(["edit", id.ToString(), "--picture", Write("x.html", "<b>hi</b>")]).Code);
        Assert.Equal("jpg", news.Get(id)!.Picture);  // a picture that is refused is refused before anything is changed
    }

    [Fact]
    public void DiscordGetsTheTitleTheBodyTheLinkAndThePictureOfThePostThatShowsNow()
    {
        var http = new Fake();
        var picture = Write("p.jpg", NewsFixtures.Jpeg);
        var (code, output, error) = Run(["add", "--title", "Announced", "--body-file", Write("a.md", "Hello **players**."), "--link", "https://example.test/x", "--link-text", "Read", "--picture", picture, "--discord"], http: new HttpClient(http));
        var id = news.List().Single(p => p.Texts["en"].Title == "Announced");

        Assert.Equal((0, ""), (code, error));
        Assert.Contains("posted to Discord", output);
        var (url, body) = Assert.Single(http.Posted);
        Assert.Equal("https://hooks.test/announce", url);
        var embed = JsonNode.Parse(body)!["embeds"]![0]!;
        Assert.Equal(("Announced", "Hello **players**.", "https://example.test/x", $"https://web.test/news/{id.Id}/picture?v={id.Updated}"),
            ((string)embed["title"]!, (string)embed["description"]!, (string)embed["url"]!, (string)embed["image"]!["url"]!));
    }

    [Fact]
    public void DiscordIsOffUnlessAskedAndAskedWithoutTheSettingItIsRefusedBeforeAnythingIsWritten()
    {
        var http = new Fake();
        Assert.Equal(0, Run(["add", "--title", "Quiet", "--body-file", Write("q.md", "x")], http: new HttpClient(http)).Code);
        Assert.Empty(http.Posted);

        cfg["news_webhook"] = null;
        var before = news.List().Count;
        var (code, _, error) = Run(["add", "--title", "Loud", "--body-file", Write("l.md", "x"), "--discord"], http: new HttpClient(http));
        Assert.Equal(1, code);
        Assert.Contains("news_webhook", error);
        Assert.Equal(before, news.List().Count);
        Assert.Empty(http.Posted);
    }

    [Fact]
    public void ALaterPostAndAFailedPostAreSavedButNotAnnouncedAndTheCommandSaysSo()
    {
        var http = new Fake();
        var later = Run(["add", "--title", "Later", "--body-file", Write("a.md", "x"), "--at", "2099-01-01T00:00:00Z", "--discord"], http: new HttpClient(http));
        Assert.Equal(1, later.Code);
        Assert.Contains("is saved, but not posted to Discord", later.Err);
        Assert.Empty(http.Posted);

        var failing = new Fake(HttpStatusCode.InternalServerError);
        var failed = Run(["add", "--title", "Failing", "--body-file", Write("b.md", "x"), "--discord"], http: new HttpClient(failing));
        Assert.Equal(1, failed.Code);
        Assert.Contains("is saved, but not posted to Discord", failed.Err);
        Assert.Equal(2, news.List().Count(p => p.Texts["en"].Title is "Later" or "Failing"));

        cfg["news_webhook"] = "http://hooks.test/not-https";
        Assert.Contains("not an https URL", Run(["add", "--title", "Plain", "--body-file", Write("c.md", "x"), "--discord"], http: new HttpClient(failing)).Err);
    }
}

/// <summary>`GET /news` and the pictures on the website's host, and the admin routes on localhost.</summary>
public class NewsApiTests : IAsyncLifetime
{
    static readonly JsonObject Cfg = new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));

    readonly string tmp = Directory.CreateTempSubdirectory("newsapi").FullName;
    State state = null!;
    News news = null!;
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Path.Combine(tmp, "state.db"), Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        state.Clock = () => NewsFixtures.T("2026-12-10T00:00:00Z");
        news = new News(state.Store, tmp);
        NewsFixtures.HideSeeded(news);
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        DeleteDir(tmp);
    }

    async Task<JsonNode> Feed(string query = "")
    {
        var reply = await server.SendAsync("GET", "web.test", "/news" + query);
        Assert.Equal(200, reply.Status);
        return JsonNode.Parse(reply.Body)!;
    }

    [Fact]
    public async Task TheFeedIsPublicCachedForAMinuteAndListsPinnedFirstThenNewestFirstInTheReadersLanguage()
    {
        var old = news.Add(new("Old", "Old body", At: "2026-12-01T00:00:00Z"));
        news.Add(new("New", "New body", At: "2026-12-05T00:00:00Z", Link: "link:source", LinkText: "Follow"));
        news.Add(new("Pinned", "Pinned body", At: "2026-11-01T00:00:00Z", Pinned: true));
        news.Add(new("Tomorrow", "Later body", At: "2026-12-11T00:00:00Z"));
        news.SetText(old, "fr", "Ancien", "Ancien corps", null);

        var reply = await server.SendAsync("GET", "web.test", "/news");
        Assert.Equal(200, reply.Status);
        Assert.Contains(("Cache-Control", "public, max-age=60"), reply.Headers);
        var posts = JsonNode.Parse(reply.Body)!["posts"]!.AsArray();
        Assert.Equal(["Pinned", "New", "Old"], posts.Select(p => (string)p!["title"]!));
        Assert.Equal(("Follow", "link:source", false, true), ((string)posts[1]!["linkText"]!, (string)posts[1]!["link"]!, (bool)posts[1]!["pinned"]!, (bool)posts[0]!["pinned"]!));
        Assert.Null(posts[0]!["picture"]);
        Assert.Equal(["Pinned", "New", "Ancien"], (await Feed("?lang=fr"))["posts"]!.AsArray().Select(p => (string)p!["title"]!));
        Assert.Equal(["Pinned", "New", "Old"], (await Feed("?lang=zz"))["posts"]!.AsArray().Select(p => (string)p!["title"]!));
    }

    [Fact]
    public async Task ANewPostAndATranslationShowOnTheNextRequest()
    {
        Assert.Empty((await Feed())["posts"]!.AsArray());
        var id = news.Add(new("Fresh", "b", At: "2026-12-01T00:00:00Z"));
        Assert.Equal(["Fresh"], (await Feed())["posts"]!.AsArray().Select(p => (string)p!["title"]!));
        news.SetText(id, "de", "Frisch", "k", null);
        Assert.Equal(["Frisch"], (await Feed("?lang=de"))["posts"]!.AsArray().Select(p => (string)p!["title"]!));
    }

    [Fact]
    public async Task APictureIsServedOnlyWhileItsPostShowsAndIsNeverSniffedOrRunAsAPage()
    {
        var id = news.Add(new("Pic", "b", At: "2026-12-01T00:00:00Z"), NewsFixtures.Webp);
        var later = news.Add(new("Soon", "b", At: "2026-12-20T00:00:00Z"), NewsFixtures.Png);

        var shown = await server.SendAsync("GET", "web.test", $"/news/{id}/picture");
        Assert.Equal(200, shown.Status);
        Assert.Equal(NewsFixtures.Webp, shown.Body);
        Assert.Contains(("Content-Type", "image/webp"), shown.Headers);
        Assert.Contains(("X-Content-Type-Options", "nosniff"), shown.Headers);
        Assert.Contains(("Content-Security-Policy", "sandbox"), shown.Headers);
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", $"/news/{later}/picture")).Status);  // not yet public
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", "/news/9999/picture")).Status);
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", $"/news/{news.Add(new("None", "b", At: "2026-12-01T00:00:00Z"))}/picture")).Status);
        Assert.StartsWith($"/news/{id}/picture?v=", (string)(await Feed())["posts"]![0]!["picture"]!);
    }

    [Fact]
    public async Task TheAdminRoutesPublishEditTranslateAndAttachAPictureAndRefuseWhatTheCommandsRefuse()
    {
        var http = server.Http;
        var added = await http.PostAsJsonAsync("http://localhost/admin/news", new { title = "From the API", body = "Body **bold**", link = "https://example.test/x", linkText = "Read", at = "2026-12-01T00:00:00Z", pinned = true });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var post = JsonNode.Parse(await added.Content.ReadAsStringAsync())!;
        var id = (long)post["id"]!;
        Assert.Equal(("From the API", true), ((string)post["texts"]!["en"]!["title"]!, (bool)post["pinned"]!));

        var bad = await http.PostAsJsonAsync("http://localhost/admin/news", new { title = "No body" });
        Assert.Equal((HttpStatusCode.BadRequest, "application/problem+json"), (bad.StatusCode, bad.Content.Headers.ContentType?.MediaType));
        Assert.Equal(HttpStatusCode.NotFound, (await http.PatchAsJsonAsync("http://localhost/admin/news/999", new { title = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"http://localhost/admin/news/{id}", new { title = "Renamed", until = "2026-12-30T00:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync($"http://localhost/admin/news/{id}/lang/fr", new { title = "Titre", body = "Corps" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync($"http://localhost/admin/news/{id}/lang/klingon", new { title = "t", body = "b" })).StatusCode);
        var list = JsonNode.Parse(await http.GetStringAsync("http://localhost/admin/news"))!.AsArray();
        Assert.Equal(["Renamed"], list.Where(p => (long)p!["id"]! == id).Select(p => (string)p!["texts"]!["en"]!["title"]!));
        Assert.Equal("Titre", (string)(await Feed("?lang=fr"))["posts"]![0]!["title"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync($"http://localhost/admin/news/{id}/picture", new ByteArrayContent("GIF89a"u8.ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsync($"http://localhost/admin/news/{id}/picture", new ByteArrayContent(NewsFixtures.Jpeg))).StatusCode);
        Assert.Equal(NewsFixtures.Jpeg, (await server.SendAsync("GET", "web.test", $"/news/{id}/picture")).Body);
        // a picture is the one body of the admin API that may be larger than a form, up to 5 MB
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsync($"http://localhost/admin/news/{id}/picture", new ByteArrayContent([.. NewsFixtures.Png, .. new byte[1 << 20]]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.PutAsync($"http://localhost/admin/news/{id}/picture", new ByteArrayContent([.. NewsFixtures.Png, .. new byte[(int)News.MaxPictureBytes]]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.PostAsync("http://localhost/admin/news", new ByteArrayContent(new byte[100_000]) { Headers = { ContentType = new("application/json") } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsync($"http://localhost/admin/news/{id}/picture-slot/competition-hero-170039", null)).StatusCode);
        Assert.Equal("slot:competition-hero-170039", (string)(await Feed())["posts"]![0]!["picture"]!);
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", $"/news/{id}/picture")).Status);
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"http://localhost/admin/news/{id}/picture")).StatusCode);
        Assert.Equal(404, (await server.SendAsync("GET", "web.test", $"/news/{id}/picture")).Status);

        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"http://localhost/admin/news/{id}", new { hidden = true })).StatusCode);
        Assert.Empty((await Feed())["posts"]!.AsArray());
    }

    [Fact]
    public async Task TheAdminRoutesAreNotOnTheWebsitesHost()
    {
        var leak = await server.Http.GetAsync("http://api.test/admin/news");  // game hosts never reach the admin API
        Assert.Equal((HttpStatusCode.NotFound, 0L), (leak.StatusCode, leak.Content.Headers.ContentLength ?? 0));
    }
}
