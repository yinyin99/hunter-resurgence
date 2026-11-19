using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Replay;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>An avatar the store sells (the facts file's `store_avatars`) is in the avatar list with the item that unlocks it: chosen only by an account that owns the item, shown locked to the others.
/// Invented data: item 19 (the fixtures' lure) is the "avatar" item, 1000099 its picture.</summary>
public class StoreAvatarTests : StateTestBase, IAsyncLifetime
{
    const long Item = 19, Picture = 1000099, Buyer = 5, Other = 6;
    static readonly Facts StoreFacts = Facts.Parse($$"""{"format": 1, "store_avatars": [{"item": {{Item}}, "id": {{Picture}}, "name": "Invented Hunter", "gender": "f"}, {"item": 98765, "id": 1000098, "name": "Not Sold", "gender": "m"}]}""");
    static readonly JsonObject Cfg = new(HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))));
    Loopback server = null!;

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false, facts: StoreFacts,
            avatars: (JsonArray)JsonNode.Parse("""[{"id": 1000000, "url": "https://avatar.test/256/1000000.png", "gender": "m", "name": "Male 1", "unlock_item_id": null, "unlocked": true}]""")!);
        state.CreateAccount(Buyer, "buyer");
        state.CreateAccount(Other, "other");
        var table = new HarTable();
        table.Entries[new("POST", "gameserver.test", Replay.CONNECT, null)] = new Hit(2, "", 200, [], "{}"u8.ToArray());
        var stock = Directory.CreateDirectory(Path.Combine(tmp, "images", "avatars", "256")).FullName;
        File.WriteAllBytes(Path.Combine(stock, $"{Picture}.png"), [4, 2]);
        server = await Loopback.StartAsync(new Game(table, new JsonObject(Cfg.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))) { ["images_dir"] = Path.Combine(tmp, "images") }, state, tmp), tmp);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    static string Names(JsonNode? list) => string.Join(",", ((JsonArray)list!).Select(a => (string)a!["name"]! + ((bool)a["unlocked"]! ? "+" : "-")));

    [Fact]
    public void TheStoreAvatarIsListedLockedUntilTheAccountOwnsItsItem()
    {
        Assert.Equal([1000000L, Picture], state.AvatarIds);  // the avatar of an item the catalogue lacks is not listed
        Assert.Equal("Male 1+,Invented Hunter-", Names(JsonNode.Parse(state.AvatarListReply(Other)!)));
        Assert.Equal("Male 1+,Invented Hunter-", Names(JsonNode.Parse(state.AvatarListReply(null)!)));
        Assert.True(state.GrantItems(Buyer, Item));
        var list = (JsonArray)JsonNode.Parse(state.AvatarListReply(Buyer)!)!;
        Assert.Equal("Male 1+,Invented Hunter+", Names(list));
        Assert.Equal((Item, "f", "TEST_LURE"), ((long)list[1]!["unlock_item_id"]!, (string)list[1]!["gender"]!, (string)list[1]!["define"]!));
    }

    [Fact]
    public void AStoreAvatarIsRefusedWithoutItsItemAndAcceptedWithIt()
    {
        Assert.False(state.SetAvatar(Buyer, Picture));
        Assert.Equal($"{BASE}/media/avatar/256/{State.DefaultAvatar}.png", state.Store.AvatarUrl(Buyer));  // nothing changed
        var before = state.AvatarChoices(Buyer, "https://web.test");
        Assert.Equal([1000000L], before["ids"]!.AsArray().Select(n => (long)n!));
        var locked = Assert.Single(before["locked"]!.AsArray());
        Assert.Equal((Picture, Item, "Invented Hunter"), ((long)locked!["id"]!, (long)locked["item"]!, (string)locked["name"]!));

        Assert.True(state.GrantItems(Buyer, Item));
        Assert.False(state.SetAvatar(Other, Picture));  // another account's purchase counts for nobody else
        Assert.True(state.SetAvatar(Buyer, Picture));
        var account = state.Store.AccountJson(Buyer);
        Assert.Equal(($"{BASE}/media/avatar/256/{Picture}.png", Picture, 0L), ((string)account["avatar_url"]!, (long)account["face"]!, (long)account["gender"]!));  // the female body of its entry
        var after = state.AvatarChoices(Buyer, "https://web.test");
        Assert.Equal($"https://web.test/images/avatars/256/{Picture}.png", (string)after["current"]!);
        Assert.Equal([1000000L, Picture], after["ids"]!.AsArray().Select(n => (long)n!));
        Assert.Empty(after["locked"]!.AsArray());
    }

    [Fact]
    public async Task TheGamesListIsAskedWithATokenAndItsPictureIsServed()
    {
        Assert.True(state.GrantItems(Buyer, Item));
        async Task<JsonArray> Ask(string? token) => (JsonArray)JsonNode.Parse((await server.SendAsync("POST", "api.test", AVATARS, [("Content-Type", "application/x-www-form-urlencoded")],
            Encoding.ASCII.GetBytes(token is null ? "" : "oauth_access_token=" + token))).Body)!;
        Assert.False((bool)(await Ask(null))[1]!["unlocked"]!);
        Assert.False((bool)(await Ask(state.Store.CreateToken(Other)))[1]!["unlocked"]!);
        Assert.True((bool)(await Ask(state.Store.CreateToken(Buyer)))[1]!["unlocked"]!);
        Assert.Equal([4, 2], (await server.SendAsync("GET", "game_web.test", $"/media/avatar/256/{Picture}.png")).Body);  // the stock copy
    }
}
