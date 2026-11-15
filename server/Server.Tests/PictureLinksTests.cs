using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The gallery pictures a save lists by link and does not hold: fetched from an allow-listed host at upload time, kept with the zip, and restored by an approve or `import pictures`. The host is a fake.</summary>
public class PictureLinksTests : StateTestBase
{
    const string Host = "photos.example";

    /// <summary>A saved gallery: one picture the zip holds (a), a good one (ok, with its kill), a redirect to the host's other picture, and every link that must not be fetched or taken:
    /// another host, plain http, a redirect to another host, a body that is no JPEG, and two pictures over the cap.</summary>
    static TestZip Zip() => TestZip.Own().Gallery("""
        [{"id":9,"type":2,"url":"https://photos.example/p/ok.jpg","thumbnail":"","label":null,"animal":{"scoreType":0,"species_id":1,"score":"55.5","id":777,"species":"Test Deer"}},
         {"id":8,"type":3,"url":"https://photos.example/p/hop.jpg","thumbnail":"","label":null,"animal":null},
         {"id":7,"type":2,"url":"https://other.example/p/x.jpg","thumbnail":"","label":null,"animal":null},
         {"id":6,"type":2,"url":"http://photos.example/p/plain.jpg","thumbnail":"","label":null,"animal":null},
         {"id":5,"type":2,"url":"https://photos.example/p/moved.jpg","thumbnail":"","label":null,"animal":null},
         {"id":4,"type":2,"url":"https://photos.example/p/text.jpg","thumbnail":"","label":null,"animal":null},
         {"id":3,"type":2,"url":"https://photos.example/p/big.jpg","thumbnail":"","label":null,"animal":null},
         {"id":2,"type":2,"url":"https://photos.example/p/huge.jpg","thumbnail":"","label":null,"animal":null},
         {"id":1,"type":2,"url":"https://photos.example/p/a.jpg","thumbnail":"","label":null,"animal":null}]
        """).Media("photos/photos.example_p_a.jpg", TestZip.Jpeg("a"));

    const string Report = "pictures from links: 2 of 8";

    sealed class Fake : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Asked) Asked.Add(uri);
            var reply = uri.AbsolutePath switch
            {
                "/p/ok.jpg" => Picture("ok"),
                "/p/ok2.jpg" => Picture("ok2"),
                "/p/hop.jpg" => Moved("https://photos.example/p/ok2.jpg"),
                "/p/moved.jpg" => Moved("https://other.example/p/y.jpg"),
                "/p/text.jpg" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("not a picture"u8.ToArray()) },
                "/p/big.jpg" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. TestZip.Jpeg("big"), .. new byte[PictureLinks.MaxBytes]]) },
                "/p/huge.jpg" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Forward([.. TestZip.Jpeg("huge"), .. new byte[PictureLinks.MaxBytes]])) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(reply);
        }

        static HttpResponseMessage Picture(string tag) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(TestZip.Jpeg(tag)) };

        static HttpResponseMessage Moved(string to)
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri(to);
            return r;
        }
    }

    /// <summary>A body whose length is not known up front: the cap is enforced while reading.</summary>
    sealed class Forward(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    public PictureLinksTests() => state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);

    JsonArray Gallery(long account) => JsonNode.Parse(state.GalleryListReply(account, []))!["photos"]!.AsArray();

    byte[] Bytes(JsonNode? photo) => File.ReadAllBytes(state.PhotoFile(new Uri((string)photo!["url"]!).AbsolutePath)!);

    long Row(long account)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
    }

    [Fact]
    public async Task TheFetchTakesOnlyHttpsPicturesOfAnAllowedHostThatAreJpegsUnderTheCap()
    {
        var host = new Fake();
        var path = Path.Combine(tmp, "links.zip");
        var urls = new[] { "https://photos.example/p/ok.jpg", "https://PHOTOS.example/p/hop.jpg", "https://other.example/p/x.jpg", "http://photos.example/p/plain.jpg", "https://photos.example:8443/p/ok.jpg",
                           "https://user@photos.example/p/ok.jpg", "https://photos.example/p/moved.jpg", "https://photos.example/p/text.jpg", "https://photos.example/p/big.jpg", "https://photos.example/p/huge.jpg",
                           "https://photos.example/p/missing.jpg", "ftp://photos.example/p/ok.jpg", "not a link" };
        Assert.Equal(2, await new PictureLinks([Host], host).Fetch(urls, path));
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal(new[] { PictureLinks.EntryName(urls[0]), PictureLinks.EntryName(urls[1]) }.Order(), zip.Entries.Select(e => e.Name).Order());
            using var body = new MemoryStream();
            using (var entry = zip.GetEntry(PictureLinks.EntryName(urls[1]))!.Open()) entry.CopyTo(body);
            Assert.Equal(TestZip.Jpeg("ok2"), body.ToArray());  // the redirect to the host's other picture was followed
        }
        Assert.DoesNotContain(host.Asked, u => u.Host != Host || u.Scheme != "https" || !u.IsDefaultPort || u.UserInfo != "");  // nothing else was ever asked, the redirect to another host included
        Assert.Equal(0, await new PictureLinks([], host).Fetch(urls, Path.Combine(tmp, "none.zip")));  // no allow-list: nothing is fetched
        Assert.False(File.Exists(Path.Combine(tmp, "none.zip")));
        Assert.Equal(0, await new PictureLinks([Host], host).Fetch(["https://photos.example/p/text.jpg"], Path.Combine(tmp, "bad.zip")));  // no picture: no zip
        Assert.Empty(Directory.GetFiles(tmp, "*.part"));
    }

    /// <summary>The first picture answers at once; the others hang until the fetch is cut (`Hang`) or answer at once too.</summary>
    sealed class Slow : HttpMessageHandler
    {
        public volatile bool Hang = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Hang && request.RequestUri!.AbsolutePath != "/p/1.jpg") await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestZip.Jpeg(request.RequestUri!.AbsolutePath)) };
        }
    }

    [Fact]
    public async Task AFetchThatRunsOutOfItsBudgetLeavesTheRestAndALaterFetchAddsItKeepingWhatCame()
    {
        var host = new Slow();
        var links = new PictureLinks([Host], host);
        var urls = Enumerable.Range(1, 6).Select(n => $"https://photos.example/p/{n}.jpg").ToList();
        var path = Path.Combine(tmp, "budget.zip");
        Assert.Equal(1, await links.Fetch(urls, path, TimeSpan.FromMilliseconds(500)));  // the other five were cut
        host.Hang = false;
        Assert.Equal(5, await links.Fetch(urls, path));  // only the five left are fetched; the first stays
        Assert.Equal(0, await links.Fetch(urls, path));  // nothing left to fetch
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(urls.Select(PictureLinks.EntryName).Order(), zip.Entries.Select(e => e.Name).Order());
        Assert.Empty(Directory.GetFiles(tmp, "*.part"));
    }

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::ffff:93.184.216.34", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001::1", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.20.0.1", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    public void OnlyPublicAddressesAreConnectedTo(string address, bool allowed) => Assert.Equal(allowed, PictureLinks.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task AnUploadFetchesTheListedPicturesAtOnceKeepsThemWithTheZipAndAnApproveRestoresThem()
    {
        var host = new Fake();
        var uploads = new Uploads(state, tmp) { Pictures = new PictureLinks([Host], host) };
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "player", "76561190000000000");
        var zip = Zip().Write(tmp, "links.zip");
        var id = uploads.Start(player)!;
        Assert.Equal("received", (await uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zip)))).Kind);
        Assert.Equal("review", (await uploads.Finish(player, id)).Kind);

        var kept = Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(zip) + ".zip");
        using (var links = ZipFile.OpenRead(Restorer.PicturesZip(kept)))
            Assert.Equal(2, links.Entries.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(tmp, "uploads-tmp")));  // the upload's own files are gone
        var asked = host.Asked.Count;

        var done = uploads.Approve(Row(player));
        Assert.Equal("restored", done.Kind);
        Assert.Contains(Report, done.Report);
        Assert.DoesNotContain(host.Asked.Skip(asked), u => u.AbsolutePath is "/p/ok.jpg" or "/p/hop.jpg");  // the approve fetches only what the upload did not get (the links that failed are tried again), not what it kept
        var gallery = Gallery(1001);  // the account adopted the zip's user id: the pictures follow it
        Assert.Equal(3, gallery.Count);
        Assert.Equal(["ok", "ok2", "a"], gallery.Select(p => System.Text.Encoding.ASCII.GetString(Bytes(p)[4..])));  // newest first, as saved
        Assert.Equal(["55.5", "Test Deer", "2"], new[] { (string)gallery[0]!["label"]!, (string)gallery[0]!["animal"]!, (string)gallery[0]!["type"]!.ToString() });
        Assert.Equal(3L, (long)gallery[1]!["type"]!);  // the saved type of the redirected one
        using var con = state.Store.Open(1001);
        Assert.Equal("777", con.Scalar("SELECT json_extract(saved, '$.kill_id') FROM photos WHERE account_id = $a AND saved LIKE '%55.5%'"));
        Assert.True(File.Exists(Restorer.PicturesZip(Path.Combine(tmp, "restores", "1001", Restorer.Sha256(zip) + ".zip"))));  // kept beside the zip under the new id
    }

    [Fact]
    public async Task AnUploadWithoutAnAllowListKeepsNoPicturesAndRejectDeletesThem()
    {
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "player", "76561190000000000");
        var zip = Zip().Write(tmp, "links.zip");
        var plain = new Uploads(state, tmp);
        var id = plain.Start(player)!;
        await plain.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zip)));
        Assert.Equal("review", (await plain.Finish(player, id)).Kind);
        var kept = Path.Combine(tmp, "restores", player.ToString(), Restorer.Sha256(zip) + ".zip");
        Assert.False(File.Exists(Restorer.PicturesZip(kept)));
        Assert.True(plain.Reject(Row(player)));

        var host = new Fake();
        var uploads = new Uploads(state, tmp) { Pictures = new PictureLinks([Host], host) };
        id = uploads.Start(player)!;
        await uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zip)));
        Assert.Equal("review", (await uploads.Finish(player, id)).Kind);
        Assert.True(File.Exists(Restorer.PicturesZip(kept)));
        Assert.True(uploads.Reject(Row(player)));
        Assert.False(File.Exists(Restorer.PicturesZip(kept)));
        Assert.False(File.Exists(kept));
    }

    [Fact]
    public void ImportPicturesAddsOnlyWhatIsMissingAndNeverRemovesAPictureOfTheGallery()
    {
        Restorer.Import(state, Zip().Write(tmp, "full.zip"), null, tmp);  // a restore from files: picture a only
        Assert.Equal([TestZip.Jpeg("a")], Gallery(1001).Select(Bytes));
        var host = new Fake();
        var links = new PictureLinks([Host], host);
        var linkOnly = TestZip.Own().Gallery("""
            [{"id":9,"type":2,"url":"https://photos.example/p/ok.jpg","thumbnail":"","label":null,"animal":{"scoreType":0,"species_id":1,"score":"55.5","id":777,"species":"Test Deer"}},
             {"id":7,"type":2,"url":"https://other.example/p/x.jpg","thumbnail":"","label":null,"animal":null},
             {"id":4,"type":2,"url":"https://photos.example/p/text.jpg","thumbnail":"","label":null,"animal":null}]
            """).Write(tmp, "link-only.zip");

        var (first, _) = Restorer.ImportPictures(state, linkOnly, 1001, tmp, links);
        Assert.Contains("pictures stored: gallery 1", first);
        Assert.Contains("pictures from links: 1 of 3", first);  // the other host and the non-JPEG are skipped
        Assert.Equal([TestZip.Jpeg("ok"), TestZip.Jpeg("a")], Gallery(1001).Select(Bytes));  // the picture of the earlier restore stays
        var asked = host.Asked.Count;

        var (second, _) = Restorer.ImportPictures(state, linkOnly, 1001, tmp, links);
        Assert.Contains("pictures stored: gallery 0", second);
        Assert.Contains("1 gallery picture already in the gallery", second);
        Assert.DoesNotContain(host.Asked.Skip(asked), u => u.AbsolutePath == "/p/ok.jpg");  // what was fetched is kept: no second fetch (a link that failed is tried again)
        Assert.Equal(2, Gallery(1001).Count);

        // a picture with the same bytes under another link is the same picture
        var again = TestZip.Own().Gallery("""[{"id":1,"type":2,"url":"https://photos.example/p/z.jpg","thumbnail":"","label":null,"animal":null}]""")
            .Media("photos/photos.example_p_z.jpg", TestZip.Jpeg("a")).Write(tmp, "same.zip");
        Assert.Contains("gallery 0", Restorer.ImportPictures(state, again, 1001, tmp).Report);
        Assert.Equal(2, Gallery(1001).Count);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(tmp, "photos", "1001")).Length);
    }

    [Fact]
    public async Task LinksOnlyListedAsPlainStringsInTheFullListAreFetchedAndTheDetailedEntryWins()
    {
        var host = new Fake();
        var links = new PictureLinks([Host], host);
        var zip = TestZip.Own()  // the shape of a save of another format: one page with details, the full list as plain links
            .Gallery("""[{"id":9,"type":3,"url":"https://photos.example/p/ok.jpg","thumbnail":"","label":null,"animal":{"scoreType":0,"species_id":1,"score":"55.5","id":777,"species":"Test Deer"}}]""")
            .Reply("Gallery_list_all.json", "/v1/Gallery/list_all", """{"photos":["https://photos.example/p/ok.jpg","https://photos.example/p/ok2.jpg","https://other.example/p/x.jpg"]}""")
            .Write(tmp, "foreign.zip");
        Restorer.Import(state, Zip().Write(tmp, "mine.zip"), null, tmp);  // an account to import onto
        var (report, _) = Restorer.ImportPictures(state, zip, 1001, tmp, links);
        Assert.Contains("pictures from links: 2 of 3", report);
        var gallery = Gallery(1001);
        Assert.Equal([TestZip.Jpeg("ok"), TestZip.Jpeg("ok2"), TestZip.Jpeg("a")], gallery.Select(Bytes));  // newest first: the link only the full list holds is older than the detailed one
        Assert.Equal("Test Deer|55.5|3", $"{gallery[0]!["animal"]}|{gallery[0]!["label"]}|{gallery[0]!["type"]}");  // the details of the detailed entry
    }
}
