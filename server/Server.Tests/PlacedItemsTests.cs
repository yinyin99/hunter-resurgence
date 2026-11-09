using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>Heartbeat events 25, 8 and 33: a placed item, a pick-up and a stack transfer survive a reconnect and show in the item lists.</summary>
public class PlacedItemsTests : StateTestBase
{
    const long RESTORED = 1001;

    public PlacedItemsTests()
    {
        state = Open(false);
    }

    /// <summary>The state over this test's database: a new one is a reconnect (the rows come from the database).</summary>
    State Open(bool online) => new(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: !online);

    State Reopen() => Open(state.Online);

    void Beat(long account, params JsonNode[] events) => state.HeartbeatReply(account, Events(events));

    static JsonObject Row(JsonObject items, string guid) => items[guid]!.AsObject();

    /// <summary>{guid: row} of the connect reply's inventory of an account.</summary>
    static JsonObject Inventory(State s, long account)
    {
        var items = new JsonObject();
        foreach (var i in Reply(s.ConnectReply(account))["inventory"]!["items"]!.AsArray())
            items[(string)i!["guid"]!] = i.DeepClone();
        return items;
    }

    /// <summary>The `own` row with this guid in Inventory/list and in Inventory/dict: both must be the same.</summary>
    JsonNode Listed(State s, long account, long guid)
    {
        var fromList = Reply(s.InventoryListReply(account)).AsArray().SelectMany(g => g!["own"]!.AsArray()).Single(o => (long)o!["id"]! == guid)!;
        var fromDict = Reply(s.InventoryDictReply(account)).AsObject().SelectMany(kv => kv.Value!.AsArray()).Single(o => (long)o!["id"]! == guid)!;
        Eq(fromList, fromDict);
        return fromList;
    }

    [Fact]
    public void TestAPlacedItemStaysPlacedAtTheNextConnect()
    {
        state.NoteLaunch(ME, "start=100&reserve=2"u8.ToArray());
        Beat(ME, O("""{"event": 25, "item": "100", "position_x": 1500, "position_y": -250.4, "position_z": 2000, "direction_x": 500, "direction_y": 0, "direction_z": -500}"""),
             O("""{"event": 8, "item": "100", "slot": 23}"""));
        var s = Reopen();
        var item = Row(Inventory(s, ME), "100");
        Eq("""[23, {"x": 1500, "y": -250, "z": 2000}, {"x": 500, "y": 0, "z": -500}, {}]""", T(item["slot"], item["position"], item["direction"], item["metadata"]));
        Eq("""{"reserve": 2, "pos": {"x": 1.5, "y": -0.25, "z": 2}, "dir": {"x": 0.5, "y": 0, "z": -0.5}}""", Listed(s, ME, 100)["location"]);
        Eq("null", Listed(s, ME, 200)["location"]);  // an item that is not placed has none
        Assert.False(item.ContainsKey("reserve"));  // the connect row has no reserve
    }

    [Fact]
    public void TestAnItemWithoutAPositionOrWithABadOneIsNotPlaced()
    {
        Beat(ME, O("""{"event": 25, "item": "100", "position_x": 1, "position_y": 2}"""),
             O("""{"event": 25, "item": "100", "position_x": 1, "position_y": 2, "position_z": 3000000}"""),
             O("""{"event": 25, "item": "x", "position_x": 1, "position_y": 2, "position_z": 3}"""));
        Assert.False(Row(Inventory(state, ME), "100").ContainsKey("position"));
        // without a hunt there is no reserve; a placement with no direction drops the old one
        Beat(ME, O("""{"event": 25, "item": "100", "position_x": 1, "position_y": 2, "position_z": 3, "direction_x": 1, "direction_y": 0, "direction_z": 0}"""),
             O("""{"event": 25, "item": "100", "position_x": 4, "position_y": 5, "position_z": 6}"""), O("""{"event": 8, "item": "100", "slot": 23}"""));
        var item = Row(Inventory(state, ME), "100");
        Eq("""[{"x": 4, "y": 5, "z": 6}, null]""", T(item["position"], item["direction"]));
        Eq("""{"reserve": null, "pos": {"x": 0.004, "y": 0.005, "z": 0.006}}""", Listed(state, ME, 100)["location"]);
    }

    [Fact]
    public void TestAPickedUpRestoredItemStaysPickedUp()
    {
        state = Open(true);
        Restorer.Import(state, TestZip.Own().Write(tmp), null, tmp);
        Eq("""{"reserve": 2, "pos": {"x": 3144.645, "y": -0.999, "z": 10.001}, "dir": {"x": 0.5, "y": 0, "z": -0.5}}""", Listed(state, RESTORED, 9101)["location"]);
        Beat(RESTORED, O("""{"event": 8, "item": "9101", "slot": 0}"""));
        var s = Reopen();
        var dog = Row(Inventory(s, RESTORED), "9101");
        Eq("""[0, {"dog_name": "Rex"}, false, false]""", T(dog["slot"], dog["metadata"], dog.ContainsKey("position"), dog.ContainsKey("direction")));
        Eq("null", Listed(s, RESTORED, 9101)["location"]);
        // placed again where it was put down, in this hunt's reserve
        s.NoteLaunch(RESTORED, "start=100&reserve=3"u8.ToArray());
        s.HeartbeatReply(RESTORED, Events(O("""{"event": 25, "item": "9101", "position_x": 10, "position_y": 20, "position_z": 30, "direction_x": 0, "direction_y": 1000, "direction_z": 0}"""),
                                          O("""{"event": 8, "item": "9101", "slot": 23}""")));
        Eq("""{"reserve": 3, "pos": {"x": 0.01, "y": 0.02, "z": 0.03}, "dir": {"x": 0, "y": 1, "z": 0}}""", Listed(Reopen(), RESTORED, 9101)["location"]);
    }

    [Fact]
    public void TestUnitsMoveBetweenStacks()
    {
        JsonNode Units() => T(Inventory(state, ME)["200"]!["units"], Inventory(state, ME).TryGetPropertyValue("201", out var n) ? n!["units"] : null);
        // no item_to: a new stack in the stash
        Beat(ME, O("""{"event": 33, "item_from": "200", "quantity": 4}"""));
        Eq("[6, 4]", Units());
        Eq("[1, 0]", T(Inventory(state, ME)["200"]!["slot"], Inventory(state, ME)["201"]!["slot"]));
        Beat(ME, O("""{"event": 33, "item_from": "201", "quantity": 3, "item_to": "200"}"""));
        Eq("[9, 1]", Units());
        // nothing moves: more than the stack holds, another item, a stack without units, the same stack, a bad quantity, a stack that is not there
        Beat(ME, O("""{"event": 33, "item_from": "201", "quantity": 5, "item_to": "200"}"""), O("""{"event": 33, "item_from": "201", "quantity": 1, "item_to": "100"}"""),
             O("""{"event": 33, "item_from": "100", "quantity": 1, "item_to": "200"}"""), O("""{"event": 33, "item_from": "200", "quantity": 1, "item_to": "200"}"""),
             O("""{"event": 33, "item_from": "200", "quantity": -1, "item_to": "201"}"""), O("""{"event": 33, "item_from": "200", "quantity": 1, "item_to": "999"}"""));
        Eq("[9, 1]", Units());
        // an emptied stack goes to the stash like a used-up one, and the move survives a reconnect
        Beat(ME, O("""{"event": 33, "item_from": "200", "quantity": 9, "item_to": "201"}"""));
        var items = Inventory(Reopen(), ME);
        Eq("[0, 0, 10]", T(items["200"]!["units"], items["200"]!["slot"], items["201"]!["units"]));
    }
}
