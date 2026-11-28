using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>A restore brings the inventory, and the lodge rows only when the save had them: the lodge items it brings without a lodge get one.</summary>
public class LodgeRestoreTests : StateTestBase
{
    const string Item = """{"780": [{"id": 9001, "slot": 1, "units": 0, "metadata": {}, "location": null}]}""";  // the rustic lodge item
    int steam;

    public LodgeRestoreTests() => state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);

    void Approved(string uid, string lodges)
    {
        var uploads = new Uploads(state, tmp) { MinFreeBytes = 0 };
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "player" + uid, $"7656119{++steam:D10}");
        var zip = TestZip.Own(uid: uid, handle: "h" + uid).Replace("Inventory_dict.json", Item).Replace($"game_user_{uid}_lodges.json", lodges);
        var id = uploads.Start(player)!;
        uploads.Part(player, id, 0, new MemoryStream(File.ReadAllBytes(zip.Write(tmp, $"z{uid}.zip")))).GetAwaiter().GetResult();
        Assert.Equal("review", uploads.Finish(player, id).GetAwaiter().GetResult().Kind);
        long row;
        using (var con = state.Store.Open(player)) row = (long)con.Scalar("SELECT MAX(id) FROM uploads WHERE account_id = $a")!;
        Assert.Equal("restored", uploads.Approve(row).Kind);
    }

    int Lodges(long account)
    {
        using var con = state.Store.Open(account);
        return (int)(long)con.Scalar("SELECT COUNT(*) FROM lodges WHERE account_id = $a AND source = '/game/user/lodges'")!;
    }

    [Fact]
    public void ARestoreThatBringsALodgeItemAndNoLodgeGetsOneListedAndEnterableAndAMatchingOneGetsNone()
    {
        Approved("1001", "[]");
        Assert.Equal(1, Lodges(1001));
        Assert.Equal("Rustic Lodge", (string)Reply(state.LodgesReply(1001))[0]!["name"]!);
        Assert.NotNull(state.LodgeReply(1001, 1));
        Approved("1002", """[{"id": 61, "name": "mine", "lodge_type_id": 780}]""");
        Assert.Equal(1, Lodges(1002));  // its own, no second
        Assert.Equal("mine", (string)Reply(state.LodgesReply(1002))[0]!["name"]!);
    }

    [Fact]
    public void ALodgeWithAnObjectOrArrayTrophyIdIsReadWithTheOtherTrophiesInPlace()
    {
        var player = state.Store.NextServerId();
        state.CreateAccount(player, "odd", "76561190000000999");
        const string odd = """{"id": 5, "name": "odd", "animal_slots": [{"slot_id": 1, "trophy_id": {"x": 1}}, {"slot_id": 2, "trophy_id": [3]}, {"slot_id": 3, "trophy_id": 77}]}""";
        using (var con = state.Store.Open(player))
            foreach (var source in new[] { "/game/user/lodges", "/game/user/lodge" })  // a lodge has two rows: the list entry and the single entry; both reach the game and the website
                con.Exec("INSERT INTO lodges (account_id, source, id, node) VALUES ($a, ?, '5', ?)", source, odd);
        foreach (var slots in new[] { Reply(state.LodgesReply(player))[0]!["animal_slots"]!.AsArray(), Reply(state.LodgeReply(player, 5)!)["animal_slots"]!.AsArray() })
        {
            Assert.Equal(3, slots.Count);
            Assert.Equal(77, (int)slots[2]!["trophy_id"]!);
            Assert.Equal(1, (int)slots[0]!["slot_id"]!);
            Assert.Equal(2, (int)slots[1]!["slot_id"]!);
            Assert.Null(slots[0]!["trophy_details"]);  // an odd id mounts nothing
            Assert.Null(slots[1]!["trophy_details"]);
        }
        Assert.NotNull(state.TrophiesReply(player));
    }
}
