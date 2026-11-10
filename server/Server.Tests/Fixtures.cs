using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Data.Sqlite;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>Player-state test fixtures; fresh nodes on every access (a node has one parent).</summary>
public static class Fixtures
{
    public const string BASE = "https://gameserver.test";
    public const string ICON = "https://static.test/icon.png";
    public const long ME = 7;  // the user id of CONNECT's account: the local account of a State built from it

    /// <summary>Delete a test folder: the pooled connections to its database files go first, or Windows keeps them locked.</summary>
    public static void DeleteDir(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    public static JsonObject TEST_LURE => O("""
        {"id": 19, "name": "Test Lure", "description": "Draws deer.", "define": "TEST_LURE",
         "type": 4, "group": 13, "cat": 4, "client_type": 4, "image": "caller_test_lure", "variationname": null,
         "units": null, "fit": {}, "inventory": {"slots": [0, 1, 11], "capacity": 0, "weight": 500},
         "price": 170, "silver_price": 1900, "notbuyable": 0}
        """);

    public static JsonObject APP
    {
        get
        {
            var app = O("""
                {"species": [{"id": 0, "name": "Test Dog", "define": "DOG"}, {"id": 58, "name": "Test Dog 2", "define": "DOG_02"}, {"id": 59, "name": "Test Dog 3", "define": "DOG_03"}, {"id": 56, "name": "Test Wolf", "define": "WOLF"}, {"id": 1, "name": "Test Deer (Typical)", "define": "MULE_DEER"},
                             {"id": 17, "name": "Test Rabbit", "define": "COTTONTAIL_RABBIT"}],
                 "items": [{"id": 5, "name": "Test Rifle"}, {"id": 2, "name": "Test Scope"},
                           {"id": 10, "name": "Test Bullet", "sponsor_image": "testbrand_logo.jpg"},
                           {"id": 14, "name": "Test Shot A"}, {"id": 241, "name": "Test Shot B"},
                           {"id": 700, "name": "Test Hound", "cat": 12, "type": 25}, {"id": 701, "name": "Test Collar", "cat": 12, "type": 26}],
                 "reserves": [{"id": 2, "name": "Test Reserve"}],
                 "locations": [{"id": 101, "pos": {"y": 150, "x": -6420, "z": 3310}, "type": "LODGE", "reserve": 2}]}
                """);
            app["tutorials"] = JsonNode.Parse("""
                [{"id": 1, "name": "Test One", "reserve_id": 2, "url": "https://files.test/one.scn", "crc32": 1, "x": -1010, "y": 11, "z": 1020, "is_demo": 0},
                 {"id": 2, "name": "Test Two", "reserve_id": 4, "url": "https://files.test/two.scn", "crc32": 2, "x": -2020, "y": 12, "z": -2030, "is_demo": 0},
                 {"id": 3, "name": "Test Three", "reserve_id": 1, "url": "https://files.test/three.scn", "crc32": 3, "x": -3030.5, "y": 13.25, "z": 3040.75, "is_demo": 0},
                 {"id": 4, "name": "Test Four", "reserve_id": 10, "url": "https://files.test/four.scn", "crc32": 4, "x": -4040.5, "y": 14.5, "z": -4050.25, "is_demo": 0},
                 {"id": 5, "name": "Test Five", "reserve_id": 2, "url": "https://files.test/five.scn", "crc32": 5, "x": -5050.75, "y": 15.5, "z": 5060.5, "is_demo": 0}]
                """);
            app["items"]!.AsArray().Add(TEST_LURE);
            return app;
        }
    }

    // 95 % off applies although a 100 % entry listed after it is also active
    public static JsonArray DISCOUNTS => (JsonArray)JsonNode.Parse("""
        [{"item_id": 19, "percentage": 95, "start_ts": 0, "end_ts": 1099511627776},
         {"item_id": 19, "percentage": 100, "start_ts": 0, "end_ts": 1099511627776}]
        """)!;

    public static JsonObject LOCALE => O("""
        {"pose_stand": "Standing", "pose_crouch": "Crouching", "pose_prone": "Prone",
         "animal_state_sleeping": "Sleeping", "animal_state_lying_down": "Lying down", "animal_state_idle": "Idle",
         "animal_body_part_head": "Head", "animal_body_part_neck": "Neck", "animal_body_part_body": "Body",
         "animal_organ_heart": "Heart",
         "animal_organ_rightlung": "Right Lung", "animal_organ_shoulder_blade": "Shoulder Blade",
         "animal_organ_skull": "Skull", "animal_texture_common": "Common",
         "animal_texture_albino": "Albino"}
        """);

    public static Dictionary<long, string> TEXTURES => new() { [2] = "albino" };

    public static JsonObject CONNECT
    {
        get
        {
            var connect = O("""
                {"err": 0, "inventory": {
                    "items": [{"item_id": 512, "slot": 0, "guid": "100", "metadata": {}, "units": null},
                              {"item_id": 10, "slot": 1, "guid": "200", "metadata": {}, "units": 10}],
                    "infos": [{"item_id": 10, "units": 10}, {"item_id": 512, "units": null}],
                    "active_services": {}},
                 "expedition": {"expedition_id": 730218845, "lodge": 100, "x": -9150.0, "y": 140.0, "z": 4875.0},
                 "account": {"handle": "hunter", "user_id": 7, "avatar_url": "https://avatar.test/256/1.png"},
                 "wallet": {"em": 182.0, "gm": 33.0},
                 "dog_skills": [{"id": 1, "lvl": 1, "xp": 0, "unlocks": [{"type": "loyalty", "skill": "DOG_SKILL_LOYALTY_ONE"}]},
                                {"id": 2, "lvl": 2, "xp": 100, "unlocks": [{"type": "pointing", "skill": "DOG_SKILL_POINT_TWO"}, {"type": "trophy", "define": "RIBBON"}]},
                                {"id": 3, "lvl": 3, "xp": 300, "unlocks": [{"type": "rewards", "data": [{"type": "SILVER", "amount": 100}]}]},
                                {"id": 4, "lvl": 5, "xp": 600, "unlocks": [{"type": "rewards", "data": [{"type": "SILVER", "amount": 300}]}]}]}
                """);
            connect["species"] = new JsonArray(Enumerable.Range(0, 55)
                .Select(i => (JsonNode?)new JsonObject { ["scale_factor"] = i == 1 ? 0.8 : 1.0, ["variant_id"] = i }).ToArray());
            // permitted ammo per species id; 17 = Test Rabbit
            connect["ethical"] = new JsonArray(Enumerable.Range(0, 17).Select(_ => (JsonNode?)new JsonArray())
                .Append(new JsonArray(241, 14)).ToArray());
            return connect;
        }
    }

    // a variant item has a parent: variant and parent share one weapon skill; the shotgun is its own family
    public static JsonArray ITEM_DEFS => (JsonArray)JsonNode.Parse("""
        [{"id": 5, "define": "TEST_RIFLE"},
         {"id": 706, "define": "TEST_RIFLE_VARIANT", "parent": "TEST_RIFLE"},
         {"id": 391, "define": "TEST_SHOTGUN"}]
        """)!;

    public static JsonObject LEVELS => new()
    {
        ["weapon"] = O("""{"5": 1, "706": 1, "391": 1}"""),
        ["tracking"] = new JsonArray(Enumerable.Repeat(1, 18).Select(n => (JsonNode?)n).ToArray()),
        ["spotting"] = new JsonArray(Enumerable.Repeat(1, 17).Append(2).Select(n => (JsonNode?)n).ToArray()),
    };

    // skill list older than the connect: the rabbit (17) spotting level 2 is not in it yet
    public static JsonObject NUMBERS => new()
    {
        ["weapon"] = O("""{"5": 1.14, "706": 1.14, "391": 1.007}"""),
        ["tracking"] = new JsonArray(Enumerable.Repeat(1.007, 18).Select(n => (JsonNode?)n).ToArray()),
        ["spotting"] = new JsonArray(Enumerable.Repeat(1.007, 18).Select(n => (JsonNode?)n).ToArray()),
    };

    public static byte[] Form(JsonNode mapping, JsonNode? cloned = null) => Encoding.ASCII.GetBytes(
        $"mapping={HttpUtility.UrlEncode(Py.Dumps(mapping))}&clonedItems={HttpUtility.UrlEncode(Py.Dumps(cloned ?? new JsonArray()))}"
        + "&oauth_access_token=x");

    public static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Equality of parsed JSON values, with the actual value in the failure message.</summary>
    public static void Eq(string expected, JsonNode? actual) => Eq(JsonNode.Parse(expected), actual);

    public static void Eq(JsonNode? expected, JsonNode? actual) =>
        Assert.True(Py.Eq(expected, actual), $"expected {Py.Dumps(expected)}\n  actual {Py.Dumps(actual)}");

    /// <summary>A tuple of JSON values (cloned) for one Eq.</summary>
    public static JsonArray T(params JsonNode?[] xs) => new(xs.Select(x => x?.DeepClone()).ToArray());
}
