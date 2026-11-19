using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The game draws an avatar from `/media/avatar/256/&lt;id&gt;.png` (game web host) or `/256/&lt;id&gt;.png` (avatar host): the operator's `avatar-&lt;id&gt;.png` for an id of the list, the default avatar's for a missing one.</summary>
public class AvatarPictureTests : StateTestBase, IAsyncLifetime
{
    const string Web = "game_web.test", Avatar = "avatar.test";
    static readonly byte[] Listed = [1, 2, 3], Default = [9, 9];
    readonly string images;
    Loopback server = null!;

    public AvatarPictureTests() => images = Directory.CreateDirectory(Path.Combine(tmp, "images")).FullName;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES,
            avatars: (JsonArray)JsonNode.Parse("""[{"id": 1000000, "url": "https://avatar.test/256/1000000.png"}, {"id": 1000002, "url": "https://avatar.test/256/1000002.png"}, {"id": 1000003, "url": "https://avatar.test/256/1000003.png"}]""")!);
        var cfg = new JsonObject(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test")))) { ["images_dir"] = images };
        File.WriteAllBytes(Path.Combine(images, "avatar-1000000.png"), Default);
        File.WriteAllBytes(Path.Combine(images, "avatar-1000002.png"), Listed);
        File.WriteAllBytes(Path.Combine(images, "avatar-1000001.png"), [7]);  // not in the list
        File.WriteAllBytes(Path.Combine(tmp, "secret.png"), [5]);
        server = await Loopback.StartAsync(new Game(new HarTable(), cfg, state, Path.Combine(tmp, "mirror")));
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    Task<Reply> Get(string host, string path) => server.SendAsync("GET", host, path, []);

    [Fact]
    public async Task AListedAvatarIsItsPictureOnBothHosts()
    {
        foreach (var (host, path) in new[] { (Web, "/media/avatar/256/1000002.png"), (Avatar, "/256/1000002.png") })
        {
            var reply = await Get(host, path);
            Assert.Equal(200, reply.Status);
            Assert.Contains(("Content-Type", "image/png"), reply.Headers);
            Assert.Equal(Listed, reply.Body);
        }
    }

    [Fact]
    public async Task AListedAvatarWithoutAFileIsTheDefaultPicture() => Assert.Equal(Default, (await Get(Web, "/media/avatar/256/1000003.png")).Body);

    [Fact]
    public async Task TheStockCopyServesAnAvatarWithoutAnOperatorFileAndTheSenderIconToo()
    {
        var stock = Directory.CreateDirectory(Path.Combine(images, "avatars", "256")).FullName;
        File.WriteAllBytes(Path.Combine(stock, "1000003.png"), [4, 4]);
        File.WriteAllBytes(Path.Combine(stock, "1000002.png"), [8]);  // the operator's avatar-1000002.png wins
        File.WriteAllBytes(Path.Combine(stock, $"{State.SenderAvatar}.png"), [6, 6]);
        Assert.Equal([4, 4], (await Get(Web, "/media/avatar/256/1000003.png")).Body);
        Assert.Equal(Listed, (await Get(Web, "/media/avatar/256/1000002.png")).Body);
        Assert.Equal([6, 6], (await Get(Web, $"/media/avatar/256/{State.SenderAvatar}.png")).Body);
        Assert.Equal(404, (await Get(Web, "/media/avatar/256/10000001.png")).Status);
    }

    [Fact]
    public void AvatarsAreHandedOutOnThisServersGameHostWhateverHostTheListNames()
    {
        state.CreateAccount(5, "new");
        Assert.Equal($"{BASE}/media/avatar/256/1000000.png", state.Store.AvatarUrl(5));  // the list names avatar.test
        Assert.True(state.SetAvatar(5, 1000002));
        Assert.Equal($"{BASE}/media/avatar/256/1000002.png", state.Store.AvatarUrl(5));
        state.Store.SetAvatar(5, "https://avatar.test/256/1000003.png", null);  // a row from before: the original's address
        Assert.Equal("https://web.test/images/avatars/256/1000003.png", state.AvatarUrl(5, "https://web.test"));  // a page is told the website's copy, never the original's host or the game host
    }

    [Fact]
    public void TheStartMovesASavedAddressOfTheOriginalsHostToThisServer()
    {
        foreach (var id in new[] { 51, 52, 53 }) state.CreateAccount(id, $"p{id}");
        state.Store.SetAvatar(51, "https://avatar.test/256/1000002.png", null);  // the list's own address
        state.Store.SetAvatar(53, "https://other.test/photos/53/avatar.jpg", null);  // not an avatar of the list
        state.MigrateListedAvatarUrls();
        state.MigrateListedAvatarUrls();  // idempotent
        Assert.Equal($"{BASE}/media/avatar/256/1000002.png", state.Store.AvatarUrl(51));
        Assert.Equal($"{BASE}/media/avatar/256/1000000.png", state.Store.AvatarUrl(52));
        Assert.Equal("https://other.test/photos/53/avatar.jpg", state.Store.AvatarUrl(53));
    }

    [Theory]
    [InlineData(Web, "/media/avatar/256/1000001.png")]  // a file, but not an avatar of the list
    [InlineData(Web, "/media/avatar/256/1000004.png")]
    [InlineData(Web, "/media/avatar/256/01000000.png")]
    [InlineData(Web, "/media/avatar/256/..%2Fsecret.png")]
    [InlineData(Web, "/media/avatar/256/%2E%2E/%2E%2E/secret.png")]
    [InlineData(Web, "/media/avatar/256/..\\secret.png")]
    [InlineData(Web, "/media/avatar/256/1000000.png/")]
    [InlineData(Web, "/256/1000000.png")]  // the avatar host's path on the game web host
    [InlineData(Avatar, "/media/avatar/256/1000000.png")]
    [InlineData("web.test", "/media/avatar/256/1000000.png")]
    public async Task AnythingElseIsRefused(string host, string path) => Assert.Equal(404, (await Get(host, path)).Status);
}
