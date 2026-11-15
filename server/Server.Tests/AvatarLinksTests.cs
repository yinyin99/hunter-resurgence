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
        Assert.Equal(["/uploads/thumb/242x242/404.jpg"], xml.Asked);
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

    sealed class Spy(HttpMessageHandler inner, List<string> asked) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            asked.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            return base.SendAsync(request, ct);
        }
    }
}
