using System.Net;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A fake avatar host: the thumbnail of user 12345, 777, 1001 and 3001 is a JPEG; user 404 has none (the real host answers 404 with an XML body).</summary>
public sealed class AvatarHost : HttpMessageHandler
{
    public readonly List<string> Asked = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Asked) Asked.Add(path);
        var uid = Path.GetFileNameWithoutExtension(path);
        var reply = uid is "12345" or "777" or "1001" or "3001" && path == $"/uploads/thumb/242x242/{uid}.jpg"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestZip.Jpeg("avatar" + uid)) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent("<?xml version=\"1.0\"?><Error/>"u8.ToArray()) };
        return Task.FromResult(reply);
    }
}

/// <summary>The avatar a player uploaded on the original site, when the save holds only its link: fetched from the avatar host at upload time (or claim), kept like a zip's picture, and set again by `admin avatars`.</summary>
public class AvatarLinksTests : StateTestBase
{
    int steam;
    const string Host = "avatar.test", Stock = "https://avatar.test/256/1000000.png", Default = "https://avatar.test/256/1000000.png";
    const string Dead = "https://site.example/media/avatar/uploads/thumb/242x242/12345.jpg?v=3";

    public AvatarLinksTests() =>
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false,
            avatars: (JsonArray)JsonNode.Parse($$"""[{"id": 1000000, "url": "{{Stock}}"}, {"id": 1000042, "url": "https://other.test/a.png"}]""")!);

    long Row(long account)
    {
        using var con = state.Store.Open(account);
        return (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
    }

    /// <summary>The zip uploaded by a player and approved; returns the restore's report.</summary>
    async Task<string> Approved(TestZip zip, PictureLinks? links)
    {
        var uploads = new Uploads(state, tmp) { Pictures = links };
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "player", $"7656119{++steam:D10}");
        var path = zip.Write(tmp, $"avatar{steam}.zip");
        var id = uploads.Start(player)!;
        await uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(path)));
        Assert.Equal("review", (await uploads.Finish(player, id)).Kind);
        var done = uploads.Approve(Row(player));
        Assert.Equal("restored", done.Kind);
        return done.Report;
    }

    string AvatarUrl(long account) => (string)state.Store.AccountJson(account)["avatar_url"]!;

    [Fact]
    public async Task ALinkOnlySaveGetsTheAvatarFetchedByTheLinksUidFromTheAvatarHost()
    {
        var host = new AvatarHost();
        await Approved(TestZip.Own(avatar: Dead), new PictureLinks([Host], host));
        Assert.Equal(["/uploads/thumb/242x242/12345.jpg"], host.Asked);  // the uid of the link, on the avatar host: the web host is gone
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", AvatarUrl(1001));
        Assert.Equal(TestZip.Jpeg("avatar12345"), File.ReadAllBytes(state.PhotoFile("/photos/1001/avatar.jpg")!));
        using var con = state.Store.Open(1001);
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", (string)RestoredReads.Me(con)!["avatar"]!);
    }

    [Fact]
    public async Task ARefusedHostOrANonJpegBodyKeepsTheDefaultAvatarWithTheWarning()
    {
        var refused = new AvatarHost();
        var report = await Approved(TestZip.Own(avatar: Dead), new PictureLinks(["elsewhere.test"], refused));
        Assert.Empty(refused.Asked);
        Assert.Contains("matches no avatar", report);
        Assert.Equal(Default, AvatarUrl(1001));
        Assert.Null(state.PhotoFile("/photos/1001/avatar.jpg"));

        var xml = new AvatarHost();  // user 404 has no upload: an XML body
        report = await Approved(TestZip.Own(uid: "1002", handle: "other", avatar: "https://site.example/media/avatar/uploads/thumb/242x242/404.jpg"), new PictureLinks([Host], xml));
        Assert.Equal(["/uploads/thumb/242x242/404.jpg", "/uploads/thumb/242x242/404.jpg"], xml.Asked);  // at upload, and again at approve
        Assert.Contains("matches no avatar", report);
        Assert.Equal(Default, AvatarUrl(1002));
    }

    [Fact]
    public async Task ASaveWithNoAvatarAtAllIsFetchedByItsUserIdButAStockAvatarIsNever()
    {
        var host = new AvatarHost();
        await Approved(TestZip.Own(avatar: null), new PictureLinks([Host], host));
        Assert.Equal(["/uploads/thumb/242x242/1001.jpg"], host.Asked);
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", AvatarUrl(1001));

        var stock = new AvatarHost();
        await Approved(TestZip.Own(uid: "1002", handle: "other", avatar: "https://other.test/a.png"), new PictureLinks([Host], stock));  // an avatar of the list: the player chose it
        Assert.Empty(stock.Asked);
        Assert.Equal("https://other.test/a.png", AvatarUrl(1002));
    }

    [Fact]
    public void ImportPicturesFetchesTheAvatarOfALinkOnlyZip()
    {
        var zip = TestZip.Own(avatar: Dead).Write(tmp, "link.zip");
        Restorer.Import(state, zip, null, tmp);
        Assert.Equal(Default, AvatarUrl(1001));
        var (_, avatar) = Restorer.ImportPictures(state, zip, 1001, tmp, new PictureLinks([Host], new AvatarHost()));
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", avatar);
        Assert.Equal(TestZip.Jpeg("avatar12345"), File.ReadAllBytes(state.PhotoFile("/photos/1001/avatar.jpg")!));
    }

    [Fact]
    public async Task TheSweepSetsADeadLinkAccountAndSkipsAStockAvatarAndAStoredPicture()
    {
        state.CreateAccount(51, "dead");
        state.Store.SetAvatar(51, Dead, null);
        state.CreateAccount(52, "stock");
        state.Store.SetAvatar(52, "https://other.test/a.png", 1000042);  // chosen here
        state.CreateAccount(57, "none");  // the default avatar and nothing saved
        state.CreateAccount(58, "gone");
        state.Store.SetAvatar(58, "https://site.example/media/avatar/uploads/thumb/242x242/404.jpg", null);
        state.CreateAccount(59, "stored");
        state.Store.SetAvatar(59, $"{BASE}/photos/59/avatar.jpg", null);
        var host = new AvatarHost();
        var links = new PictureLinks([Host], host);

        Assert.Equal(["51 dead would fetch the avatar of user 12345", "57 none no uploaded avatar", "58 gone would fetch the avatar of user 404"], await state.SweepAvatars(links, null, dry: true));
        Assert.Empty(host.Asked);

        Assert.Equal(["51 dead set", "57 none no uploaded avatar", "58 gone fetch failed"], await state.SweepAvatars(links, null, dry: false));
        Assert.Equal($"{BASE}/photos/51/avatar.jpg", AvatarUrl(51));
        Assert.Equal(TestZip.Jpeg("avatar12345"), File.ReadAllBytes(state.PhotoFile("/photos/51/avatar.jpg")!));
        Assert.Equal("https://other.test/a.png", AvatarUrl(52));
        Assert.Equal($"{BASE}/photos/59/avatar.jpg", AvatarUrl(59));
        Assert.Equal(["/uploads/thumb/242x242/12345.jpg", "/uploads/thumb/242x242/404.jpg"], host.Asked);
    }

    [Fact]
    public async Task TheSweepDoesNotCountTheDefaultAvatarARestoreWroteAsTheChoiceOfThePlayer()
    {
        const string StockOnWeb = "https://site.example/media/avatar/256/1000000.png";  // the default avatar, as the profile of a restored account names it
        void Restored(long account, string handle, string? extra = null, string? avatar = null, long? archived = null)
        {
            state.CreateAccount(account, handle);
            state.Store.SetAvatar(account, Default, null);
            using var con = state.Store.Open(account);
            PlayerRows.SetProfile(con, new JsonObject { ["avatar"] = StockOnWeb });
            if (extra is not null) PlayerRows.SetExtra(con, extra, new JsonObject { ["avatar"] = avatar });
            if (archived is { } uid) con.Exec("INSERT INTO claims (account_id, archived_uid, handle, hunterscore, state, ts, note, keep_handle) VALUES ($a, ?, ?, 0, 'restored', 0, '', 0)", uid, handle);
        }
        Restored(61, "me", "Me_me.json", "https://avatar.test/uploads/thumb/242x242/12345.jpg?h1");  // the upload is in the saved Me/me
        Restored(62, "host", "Public_user_getByHostname__hostname=host.json", "https://site.example/media/avatar/uploads/thumb/242x242/777.jpg?h2");  // or in the saved profile page
        Restored(63, "claim", archived: 3001);  // no avatar in the data at all: the claim's archived user id
        Restored(64, "chosen", "Me_me.json", "https://other.test/a.png");  // a stock avatar the player chose
        var host = new AvatarHost();
        var links = new PictureLinks([Host], host);

        Assert.Equal(["61 me would fetch the avatar of user 12345", "62 host would fetch the avatar of user 777", "63 claim would fetch the avatar of user 3001", "64 chosen no uploaded avatar"],
                     await state.SweepAvatars(links, null, dry: true));
        Assert.Equal(["61 me set", "62 host set", "63 claim set", "64 chosen no uploaded avatar"], await state.SweepAvatars(links, null, dry: false));
        Assert.Equal([$"{BASE}/photos/61/avatar.jpg", $"{BASE}/photos/62/avatar.jpg", $"{BASE}/photos/63/avatar.jpg", Default], new[] { 61L, 62, 63, 64 }.Select(AvatarUrl));
        Assert.Equal(TestZip.Jpeg("avatar3001"), File.ReadAllBytes(state.PhotoFile("/photos/63/avatar.jpg")!));
    }

    [Fact]
    public async Task TheAvatarUploadsSettingNamesTheHostTheAvatarIsFetchedFrom()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false) { AvatarUploads = "https://uploads.test" };  // the avatar list's own base is not used
        var host = new AvatarHost();
        var asked = new List<string>();
        var spy = new Spy(host, asked);
        await Approved(TestZip.Own(avatar: Dead), new PictureLinks(["uploads.test"], spy));
        Assert.Equal(["uploads.test/uploads/thumb/242x242/12345.jpg"], asked);
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", AvatarUrl(1001));
    }

    /// <summary>The hosts of an upload from the website: the avatar host serves the avatar by user id, the photo host the gallery pictures /p/1.jpg to /p/`gallery`.jpg (the others answer 404); every ask is recorded.
    /// With `gate`, a gallery picture waits (3 s at most) until the avatar was asked for, so the order the links are fetched in shows in `Asked`.</summary>
    sealed class TwoHosts(int gallery = 6, bool gate = true) : HttpMessageHandler
    {
        public readonly List<string> Asked = [];
        readonly TaskCompletionSource avatarAsked = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (gate && uri.Host == "photos.test") await Task.WhenAny(avatarAsked.Task, Task.Delay(3000, ct));
            lock (Asked) Asked.Add(uri.Host + uri.AbsolutePath);
            if (uri.Host == "avatar.test") avatarAsked.TrySetResult();
            return uri.AbsolutePath is "/uploads/thumb/242x242/12345.jpg" || (uri.AbsolutePath.StartsWith("/p/", StringComparison.Ordinal) && int.Parse(uri.Segments[^1].Split('.')[0]) <= gallery)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestZip.Jpeg(uri.Segments[^1])) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>A zip of another tool's layout (index.json and json_&lt;section&gt;.json): the player's own avatar is the uploaded one, by the website's address with a hash query, in `me` and in the public profile; two gallery links.</summary>
    string SectionZip(int links = 6)
    {
        const string Link = "https://site.example/media/avatar/uploads/thumb/242x242/12345.jpg?5f3a9c";
        var path = Path.Combine(tmp, "sections.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Put(string entry, string body)
            {
                using var s = new StreamWriter(zip.CreateEntry($"backup_1001/{entry}").Open(), new System.Text.UTF8Encoding(false));
                s.Write(body);
            }
            Put("index.json", """{"captured_at":"2026-01-02T03:04:05.000Z","user_id":"1001","handle":"sample","files":["json_me.json","json_public_profile.json","json_gallery.json"]}""");
            Put("json_me.json", $$"""{"id":1001,"handle":"sample","hostname":"sample","gender":1,"hunterscore":1234,"membershipType":"freeplay","ems":12.5,"gms":20000.25,"avatar":"{{Link}}"}""");
            Put("json_public_profile.json", $$"""{"id":1001,"handle":"sample","hostname":"sample","avatar":"{{Link}}"}""");
            Put("json_gallery.json", $$"""{"photos":[{{string.Join(",", Enumerable.Range(1, links).Select(n => $$"""{"url":"https://photos.test/p/{{n}}.jpg","type":1}"""))}}]}""");
        }
        return path;
    }

    /// <summary>The website's upload of such a zip through `upload`, then the operator's approve through `approve` (an Uploads with the pictures setting or not); returns the restore's report.</summary>
    async Task<string> UploadedAndApproved(string zipPath, Uploads upload, Uploads approve)
    {
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "player", "76561190000000001");
        var id = upload.Start(player)!;
        await upload.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zipPath)));
        Assert.Equal("review", (await upload.Finish(player, id)).Kind);
        var done = approve.Approve(Row(player));
        Assert.Equal("restored", done.Kind);
        return done.Report;
    }

    void AssertAvatarStored(string report)
    {
        Assert.DoesNotContain("matches no avatar", report);
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", AvatarUrl(1001));
        Assert.Equal(TestZip.Jpeg("12345.jpg"), File.ReadAllBytes(state.PhotoFile("/photos/1001/avatar.jpg")!));
    }

    [Fact]
    public async Task ASectionZipWithTheWebHostsAvatarLinkAndAGalleryGetsTheAvatarAtUploadAndAtApprove()
    {
        var host = new TwoHosts();
        var uploads = new Uploads(state, tmp) { Pictures = new PictureLinks(["avatar.test", "photos.test"], host) };
        AssertAvatarStored(await UploadedAndApproved(SectionZip(), uploads, uploads));
        Assert.Equal("avatar.test/uploads/thumb/242x242/12345.jpg", host.Asked[0]);  // the avatar is fetched first: a long gallery cannot use up the fetch's time before it
        Assert.Equal(7, host.Asked.Count);  // fetched once, at upload; approve found it in the kept pictures zip
        Assert.Equal(6, JsonNode.Parse(state.GalleryListReply(1001, []))!["photos"]!.AsArray().Count);
    }

    [Fact]
    public async Task AZipKeptWithoutAPicturesZipGetsEveryLinkFetchedAtApprove()
    {
        var host = new TwoHosts();
        var kept = new Uploads(state, tmp);  // no pictures setting at upload: the kept zip has no pictures zip, as one kept before the fix
        AssertAvatarStored(await UploadedAndApproved(SectionZip(), kept, new Uploads(state, tmp) { Pictures = new PictureLinks(["avatar.test", "photos.test"], host) }));
        Assert.Equal("avatar.test/uploads/thumb/242x242/12345.jpg", host.Asked[0]);  // the avatar first, then the gallery
        Assert.Equal(7, host.Asked.Count);
        Assert.Equal(6, JsonNode.Parse(state.GalleryListReply(1001, []))!["photos"]!.AsArray().Count);
    }

    [Fact]
    public async Task ALinkTheUploadDidNotGetIsFetchedAtApproveAndWhatTheUploadGotIsNotFetchedAgain()
    {
        var partial = new TwoHosts(gallery: 3, gate: false);  // the upload's budget ran out after the avatar and three pictures, or the host failed for the others
        var rest = new TwoHosts(gate: false);
        AssertAvatarStored(await UploadedAndApproved(SectionZip(), new Uploads(state, tmp) { Pictures = new PictureLinks(["avatar.test", "photos.test"], partial) },
                                                     new Uploads(state, tmp) { Pictures = new PictureLinks(["avatar.test", "photos.test"], rest) }));
        Assert.Equal(["photos.test/p/4.jpg", "photos.test/p/5.jpg", "photos.test/p/6.jpg"], rest.Asked.Order());  // only the three left
        Assert.Equal(6, JsonNode.Parse(state.GalleryListReply(1001, []))!["photos"]!.AsArray().Count);
    }

    [Fact]
    public void ImportPicturesCompletesAPicturesZipThatLacksLinks()
    {
        var zip = SectionZip();
        Restorer.Import(state, zip, null, tmp);
        var partial = new TwoHosts(gallery: 3, gate: false);
        Restorer.ImportPictures(state, zip, 1001, tmp, new PictureLinks(["avatar.test", "photos.test"], partial));
        Assert.Equal(3, JsonNode.Parse(state.GalleryListReply(1001, []))!["photos"]!.AsArray().Count);
        var rest = new TwoHosts(gate: false);
        var (_, avatar) = Restorer.ImportPictures(state, zip, 1001, tmp, new PictureLinks(["avatar.test", "photos.test"], rest));
        Assert.Equal(["photos.test/p/4.jpg", "photos.test/p/5.jpg", "photos.test/p/6.jpg"], rest.Asked.Order());
        Assert.Equal(6, JsonNode.Parse(state.GalleryListReply(1001, []))!["photos"]!.AsArray().Count);
        Assert.Equal($"{BASE}/photos/1001/avatar.jpg", avatar);
    }

    sealed class Spy(HttpMessageHandler inner, List<string> asked) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            asked.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            return base.SendAsync(request, ct);
        }
    }
}
