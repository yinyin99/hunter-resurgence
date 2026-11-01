using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>Player-state test fixtures; fresh nodes on every access (a node has one parent).</summary>
public static class Fixtures
{
    public const string BASE = "https://gameserver.test";
    public const string ICON = "https://static.test/icon.png";

    public static JsonObject TEST_LURE => O("""
        {"id": 19, "name": "Test Lure", "description": "Draws deer.", "define": "TEST_LURE",
         "type": 4, "group": 13, "cat": 4, "client_type": 4, "image": "caller_test_lure", "variationname": null,
         "units": null, "fit": {}, "inventory": {"slots": [0, 1, 11], "capacity": 0, "weight": 500},
         "price": 150, "silver_price": 1900, "notbuyable": 0}
        """);

    public static JsonObject APP
    {
        get
        {
            var app = O("""
                {"species": [{"id": 1, "name": "Test Deer (Typical)", "define": "MULE_DEER"},
                             {"id": 17, "name": "Test Rabbit", "define": "COTTONTAIL_RABBIT"}],
                 "items": [{"id": 5, "name": "Test Rifle"}, {"id": 2, "name": "Test Scope"},
                           {"id": 10, "name": "Test Bullet", "sponsor_image": "testbrand_logo.jpg"},
                           {"id": 14, "name": "Test Shot A"}, {"id": 241, "name": "Test Shot B"}],
                 "reserves": [{"id": 2, "name": "Test Reserve"}],
                 "locations": [{"id": 101, "pos": {"y": 217, "x": -8172, "z": 5228}, "type": "LODGE", "reserve": 2}]}
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
                 "expedition": {"expedition_id": 482917305, "lodge": 100, "x": -10380.0, "y": 215.0, "z": 7926.0},
                 "account": {"handle": "hunter", "user_id": 7, "avatar_url": "https://avatar.test/256/1.png"},
                 "wallet": {"em": 182.0, "gm": 33.0}}
                """);
            connect["species"] = new JsonArray(Enumerable.Range(0, 55)
                .Select(i => (JsonNode?)new JsonObject { ["scale_factor"] = i == 1 ? 0.8 : 1.0, ["variant_id"] = i }).ToArray());
            // permitted ammo per species id; 17 = Test Rabbit
            connect["ethical"] = new JsonArray(Enumerable.Range(0, 17).Select(_ => (JsonNode?)new JsonArray())
                .Append(new JsonArray(241, 14)).ToArray());
            return connect;
        }
    }

    // py_data item.json: 614 is a variant of 5 (one weapon skill); 257 its own family
    public static JsonArray ITEM_DEFS => (JsonArray)JsonNode.Parse("""
        [{"id": 5, "define": "WIN_BOLT_ACTION_RIFLE"},
         {"id": 614, "define": "BOLT_ACTION_RIFLE_243_01", "parent": "WIN_BOLT_ACTION_RIFLE"},
         {"id": 257, "define": "SINGLE_SHOT_SHOTGUN_01"}]
        """)!;

    public static JsonObject LEVELS => new()
    {
        ["weapon"] = O("""{"5": 1, "614": 1, "257": 1}"""),
        ["tracking"] = new JsonArray(Enumerable.Repeat(1, 18).Select(n => (JsonNode?)n).ToArray()),
        ["spotting"] = new JsonArray(Enumerable.Repeat(1, 17).Append(2).Select(n => (JsonNode?)n).ToArray()),
    };

    // recorded Skill/list, older than the connect: the rabbit (17) spotting level 2 is not in it yet
    public static JsonObject NUMBERS => new()
    {
        ["weapon"] = O("""{"5": 1.14, "614": 1.14, "257": 1.007}"""),
        ["tracking"] = new JsonArray(Enumerable.Repeat(1.007, 18).Select(n => (JsonNode?)n).ToArray()),
        ["spotting"] = new JsonArray(Enumerable.Repeat(1.007, 18).Select(n => (JsonNode?)n).ToArray()),
    };

    public static byte[] Form(JsonNode mapping, JsonNode? cloned = null) => Encoding.ASCII.GetBytes(
        $"mapping={HttpUtility.UrlEncode(Py.Dumps(mapping))}&clonedItems={HttpUtility.UrlEncode(Py.Dumps(cloned ?? new JsonArray()))}"
        + "&oauth_access_token=x");

    public static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Python == of parsed JSON, with the actual value in the failure message.</summary>
    public static void Eq(string expected, JsonNode? actual) => Eq(JsonNode.Parse(expected), actual);

    public static void Eq(JsonNode? expected, JsonNode? actual) =>
        Assert.True(Py.Eq(expected, actual), $"expected {Py.Dumps(expected)}\n  actual {Py.Dumps(actual)}");

    /// <summary>A tuple of JSON values (cloned) for one Eq.</summary>
    public static JsonArray T(params JsonNode?[] xs) => new(xs.Select(x => x?.DeepClone()).ToArray());
}
