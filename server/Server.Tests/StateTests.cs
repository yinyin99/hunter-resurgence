using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using Microsoft.Data.Sqlite;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

public abstract class StateTestBase : IDisposable
{
    protected readonly string tmp = Directory.CreateTempSubdirectory("state").FullName;
    protected string Db => Path.Combine(tmp, "state.db");
    protected State state = null!;

    public void Dispose() => Directory.Delete(tmp, true);

    protected static JsonNode Reply(byte[] body) => JsonNode.Parse(body)!;

    protected static byte[] Events(params JsonNode[] events) =>
        Encoding.ASCII.GetBytes("{\"events\": [" + string.Join(", ", events.Select(e => Py.Dumps(e))) + "]}");

    /// <summary>A heartbeat with these events; its updates.</summary>
    protected JsonArray Hb(params JsonNode[] events) => Reply(state.HeartbeatReply(Events(events)))["updates"]!.AsArray();

    protected JsonArray HbOf(State s) => Reply(s.HeartbeatReply("{\"events\": []}"u8.ToArray()))["updates"]!.AsArray();

    /// <summary>{guid: item} of the connect reply's inventory.</summary>
    protected JsonObject Items(State? s = null)
    {
        var items = new JsonObject();
        foreach (var i in Reply((s ?? state).ConnectReply())["inventory"]!["items"]!.AsArray())
            items[(string)i!["guid"]!] = i.DeepClone();
        return items;
    }

    protected static JsonNode[] Repeat(JsonNode e, int n) => Enumerable.Repeat(e, n).ToArray();
}

public class InventoryTests : StateTestBase
{
    public InventoryTests()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);
        Scoring.StaticHost = "static.test";
        Scoring.OwnLogo = "testlogo";
        // variant.json bounds pinned: tests don't read files
        Scoring.Variants = new() { [1] = (20, 246, 1.2), [17] = (800, 2000, 0.73) };
    }

    [Fact]
    public void TestSeededFromConnect()
    {
        var expected = new JsonObject();
        foreach (var i in CONNECT["inventory"]!["items"]!.AsArray())
            expected[(string)i!["guid"]!] = i.DeepClone();
        Eq(expected, Items());
    }

    [Fact]
    public void TestMovePersistsAcrossRestart()
    {
        Assert.Equal("null"u8.ToArray(), state.MoveMultiple2Reply(Form(JsonNode.Parse("""[{"item_guid": 100, "slot_id": 12}]""")!)));
        var reopened = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES);  // same db: no reseed
        Eq("12", Items(reopened)["100"]!["slot"]);
    }

    [Fact]
    public void TestConnectQueuesOneToast()
    {
        state.ConnectReply();
        state.ConnectReply();
        var updates = Hb();
        Assert.Single(updates);
        Eq("3", updates[0]!["update"]);
        Assert.Equal(State.TOAST_TEXT, (string)updates[0]!["text"]!);
        Assert.Empty(Hb());
    }

    [Fact]
    public void TestCloneGetsNewGuidAndSlot()
    {
        var reply = Reply(state.MoveMultiple2Reply(Form(
            JsonNode.Parse("""[{"item_guid": "new_item_1", "slot_id": 13}, {"item_guid": 200, "slot_id": 0}]""")!,
            JsonNode.Parse("""[{"item_guid": "new_item_1", "item_id": 10}]"""))));
        var guid = Py.Str(reply["new_item_1"]);
        Assert.Equal("201", guid);
        var items = Items();
        Eq("[10, 13, 10]", T(items[guid]!["item_id"], items[guid]!["slot"], items[guid]!["units"]));
        Eq("0", items["200"]!["slot"]);
    }

    [Fact]
    public void TestUnknownGuidsIgnored()
    {
        state.MoveMultiple2Reply(Form(JsonNode.Parse("""[{"item_guid": 999, "slot_id": 5}, {"item_guid": "new_item_x", "slot_id": 5}]""")!));
        Assert.Equal(["100", "200"], Items().Select(kv => kv.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TestHarvestScoreArrivesNextHeartbeatOnce()
    {
        JsonNode HbReply(params JsonNode[] events) => Reply(state.HeartbeatReply(Events(events)));
        Eq("""{"updates": [], "err": 0}""", HbReply(O("""{"event": 3, "individual": 71, "extended_species": 1}""")));
        var score = Assert.Single(HbReply()["updates"]!.AsArray())!;
        Eq("""[6, 71, 1, "No score"]""", T(score["update"], score["individual"], score["kill_id"], score["score_text"]));
        Assert.Empty(HbReply()["updates"]!.AsArray());
        Assert.Equal("{\"updates\": [], \"err\": 0}"u8.ToArray(), state.HeartbeatReply("not json"u8.ToArray()));
    }

    [Fact]
    public void TestPhotoRequestUploadAndDownload()
    {
        var u11 = Assert.Single(Hb(O("""{"event": 4, "hash": 77, "photo_type": 2, "individual": 97}""")))!;
        Eq("[11, 77, 2]", T(u11["update"], u11["hash"], u11["photo_type"]));
        var url = new Uri((string)u11["url"]!);
        Assert.Equal(BASE, url.GetLeftPart(UriPartial.Authority));
        var (status, receiptBody) = state.UploadReply(url.Query.TrimStart('?'), [0xff, 0xd8, .. "jpeg"u8]);
        var receipt = Reply(receiptBody);
        Eq("""[200, "storage#object", "6"]""", T(status, receipt["kind"], receipt["size"]));
        var media = new Uri((string)receipt["mediaLink"]!).AbsolutePath;
        Assert.Equal([0xff, 0xd8, .. "jpeg"u8], File.ReadAllBytes(state.PhotoFile(media)!));
        Assert.Equal(404, state.UploadReply("upload_id=999", "x"u8.ToArray()).Status);
        Assert.Null(state.PhotoFile("/photos/../state.db"));
        state.UploadReply(new Uri((string)u11["thumb_url"]!).Query.TrimStart('?'), "thumb"u8.ToArray());
        Assert.Equal("thumb"u8.ToArray(), File.ReadAllBytes(state.PhotoFile("/photos/1-2-thumb.jpg")!));
        Assert.Equal([0xff, 0xd8, .. "jpeg"u8], File.ReadAllBytes(state.PhotoFile(media)!));
    }

    [Fact]
    public void TestPhotoNamesStayInThePhotosFolder()
    {
        var cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tmp;  // a drive-relative name ("C:x") resolves against the current directory
            File.WriteAllText(Path.Combine(tmp, "secret.txt"), "x");
            Assert.Null(state.PhotoFile($"/photos/{Path.GetPathRoot(tmp)![..2]}secret.txt"));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
        // photo_type comes from the heartbeat: a string is never used as part of the file name
        var u11 = Assert.Single(Hb(O("""{"event": 4, "hash": 5, "photo_type": "..\\..\\escaped"}""")))!;
        Assert.Equal(404, state.UploadReply(new Uri((string)u11["url"]!).Query.TrimStart('?'), "x"u8.ToArray()).Status);
        Assert.Empty(Directory.GetFiles(tmp, "*escaped*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tmp)!, "*escaped*"));
    }

    [Fact]
    public void TestShotEvidenceHasNoThumbUrl()
    {
        var u11 = Assert.Single(Hb(O("""{"event": 4, "hash": 1, "photo_type": 14, "shot_id": 3}""")))!;
        Assert.False(u11.AsObject().ContainsKey("thumb_url"));
    }

    [Fact]
    public void TestHarvestPageFromSpawnAndKill()
    {
        state.NoteLaunch("start=101&reserve=2&oauth_access_token=x"u8.ToArray());
        Hb(O("""{"event": 7, "individual": 71, "species": 1, "gender": 1, "weight_grams": 1, "texture": 5}"""),
           O("""{"event": 1, "individual": 71, "distance_cms": 1, "organ2": 1}"""));  // earlier life of individual 71
        Hb(O("""{"event": 7, "individual": 71, "species": 1, "gender": 0, "weight_grams": 72270, "texture": 0}"""),
           O("""
             {"event": 1, "individual": 71, "distance_cms": 9614, "organ1": 17, "organ2": 8388610, "weapon_id": 5,
              "ammunition_id": 10, "scope_id": 2, "pose": 2, "animal_state": 3, "player_x": -9593, "player_z": 8198}
             """),
           O("""
             {"event": 3, "individual": 71, "extended_species": 1, "antlers_seed": 46658, "base_texture": "b.dds",
              "fur_texture": "", "scale": 0.9, "antlers_score_percentage": 0.25, "trophy_integrity": 96731,
              "harvest_value": 89795, "wound_time": 6123}
             """));
        var page = Reply(state.HarvestReply(1)!);
        Eq("""["MULE_DEER", 0, 72270, 0, 2, 46658, false]""", T(page["species_define"], page["gender"], page["weight"],
                                                                page["texture_id"], page["reserve_id"], page["antlers_seed"], page["taxidermized"]));
        Assert.True(state.Taxidermize(1));
        var taxidermized = Reply(state.HarvestReply(1)!)["taxidermized"];
        Assert.True(Py.IsBool(taxidermized) && (bool)taxidermized!);
        Assert.Null(state.HarvestReply(2));
        Assert.False(state.Taxidermize(2));

        var reply = Reply(state.ExpeditionReply("{\"recorded\": 1}"u8.ToArray()));
        var expedition = reply["expedition"]!;
        Eq("""{"id": 1, "reserve": 2, "location_id": 101, "end_ts": null}""", new JsonObject
        {
            ["id"] = C(expedition["id"]), ["reserve"] = C(expedition["reserve"]),
            ["location_id"] = C(expedition["location_id"]), ["end_ts"] = C(expedition["end_ts"]),
        });
        var kill = Assert.Single(reply["kills"]!.AsArray())!;
        Eq("""[1, 1, "Test Deer", 72270, 0, 0]""", T(kill["id"], kill["expeditionId"], kill["speciesName"], kill["weight"],
                                                       kill["gender"], kill["texture"]));
        Eq("""[{"distance": 96140, "weapon_id": 5, "organ": 8388610, "ammo_id": 10}]""", kill["hits"]);
        Eq("[96.731, 89.795, 6.123]", T(kill["kill"]!["trophy_integrity"], kill["kill"]!["harvest_value"], kill["kill"]!["wound_time"]));

        var score = Encoding.UTF8.GetString(state.ScorePageReply(1)!);
        foreach (var cell in new[]
                 {
                     "<a href=\"/#store/item/5\">Test Rifle</a>", "<span title=\"Test Scope\">",
                     "96.140 m (315.4 ft)", "<td>Idle</td>", "<td>Neck (Right Lung, Shoulder Blade)</td>",
                     "<td>Prone</td>", "72.270 kg (159.3 lbs)", "Common (M)", "Test Reserve (-9593, 8198)",
                     "class=\"sponsor-image sponsor-testbrand_logo\"",
                 })
            Assert.Contains(cell, score);
        Assert.Null(state.ScorePageReply(2));
    }

    static JsonObject MuleAntler(int beam, int[] h, int[] g)  // event 22 antler: main beam (type 0) and points G-1..G-4, mm
    {
        var antler = new JsonObject
        {
            ["circumference_h1"] = h[0], ["circumference_h2"] = h[1], ["circumference_h3"] = h[2], ["circumference_h4"] = h[3],
        };
        antler["points"] = new JsonArray(new[] { new JsonObject { ["length"] = beam, ["type"] = 0 } }
            .Concat(g.Select((v, i) => new JsonObject { ["length"] = v, ["type"] = i + 1 })).ToArray<JsonNode?>());
        return antler;
    }

    [Fact]
    public void TestMuleDeerBuckScoresAndPaysGold()
    {
        // individual 60: expected score 199.6848, css 95.4
        var e22 = O("""{"event": 22, "individual": 60, "greatest_spread": 745, "tip_to_tip_spread": 627, "inside_spread_of_main_beams": 701}""");
        e22["right_antler"] = MuleAntler(695, [140, 135, 143, 128], [129, 602, 426, 450]);
        e22["left_antler"] = MuleAntler(865, [151, 148, 137, 131], [129, 586, 423, 450]);
        var updates = Hb(O("""{"event": 7, "individual": 60, "species": 1, "gender": 0, "weight_grams": 133264, "texture": 1}"""),
                         e22, O("""{"event": 3, "individual": 60, "extended_species": 1, "trophy_integrity": 100000}"""));
        Assert.Contains(updates, u => Py.Eq(u, O("""{"amount": 128.0, "type": "gm", "update": 15}""")));  // 33 + round(95.4)
        var score = Assert.Single(Hb(), u => Py.Eq(u!["update"], 6))!;
        Eq("""[199.6848, "Score: 199.6848", 95.4]""", T(score["score"], score["score_text"], score["css"]));
        Eq("199.6848", Reply(state.HarvestReply(1)!)["score"]);
        Eq("128.0", Reply(state.ConnectReply())["wallet"]!["gm"]);
        var page = Encoding.UTF8.GetString(state.ScorePageReply(1)!);
        Assert.Contains("Trophy score: 199.6848", page);
        Assert.Contains("scoring/antlers_mule.png", page);  // the worked B&C sheet, not the "no data" block
    }

    static JsonObject Antler(int[] h, (int Type, int Length)[] points)  // event 22 antler: circumferences h1..h4, points
    {
        var antler = new JsonObject();
        for (var i = 0; i < h.Length; i++)
            antler[$"circumference_h{i + 1}"] = h[i];
        antler["points"] = new JsonArray(points.Select(p => (JsonNode?)new JsonObject { ["length"] = p.Length, ["type"] = p.Type }).ToArray());
        return antler;
    }

    [Fact]
    public void TestAxisDeerAndFeralGoatScore()
    {
        Scoring.Variants[40] = (10, 500, 0.73);
        Scoring.Variants[54] = (20, 215, 1);
        // both were stored unscored before the tines / cic_horn mapping
        var axisDeer = O("""{"event": 22, "individual": 350, "greatest_spread": 259, "inside_spread_of_main_beams": 221}""");
        axisDeer["left_antler"] = Antler([131, 119, 121, 120], [(0, 550), (1, 181), (2, 12)]);
        axisDeer["right_antler"] = Antler([121, 128, 118, 119], [(0, 536), (1, 181), (2, 12)]);
        var feralGoat = O("""{"event": 22, "individual": 278, "greatest_spread": 456, "inside_spread_of_main_beams": 0}""");
        feralGoat["left_antler"] = Antler([94, 92, 84, 89], [(0, 81093)]);
        feralGoat["right_antler"] = Antler([90, 90, 91, 89], [(0, 80028)]);
        Hb(O("""{"event": 7, "individual": 350, "species": 54, "gender": 0, "weight_grams": 76570}"""), axisDeer,
           O("""{"event": 3, "individual": 350, "extended_species": 54}"""),
           O("""{"event": 7, "individual": 278, "species": 40, "gender": 0, "weight_grams": 57274}"""), feralGoat,
           O("""{"event": 3, "individual": 278, "extended_species": 40}"""));
        var scores = new JsonArray(Hb().Where(u => Py.Eq(u!["update"], 6)).Select(u => (JsonNode?)T(u!["score"], u["css"])).ToArray());
        Eq("[[86.2992, 34.0], [144.5605, 20.0]]", scores);
        Eq("87.0", Reply(state.ConnectReply())["wallet"]!["gm"]);  // 33 + 34 + 20
        // worked sheets, not the "no data" block: the axis deer (kill 1) gets a tines sheet, the feral goat (kill 2) a CIC horn sheet
        var axisPage = Encoding.UTF8.GetString(state.ScorePageReply(1)!);
        Assert.Contains("Length of Typical Tines T-3", axisPage);
        Assert.Contains("Common (M)", axisPage);  // these spawns carry no texture
        Assert.Contains("Length of horn, left", Encoding.UTF8.GetString(state.ScorePageReply(2)!));
    }

    [Fact]
    public void TestRabbitPaysGoldFromWeight()
    {
        var updates = Hb(O("""{"event": 7, "individual": 291, "species": 17, "gender": 1, "weight_grams": 1607, "feature": 1607694, "texture": 0}"""),
                         O("""{"event": 3, "individual": 291, "extended_species": 17}"""));
        Assert.Contains(updates, u => Py.Eq(u, O("""{"amount": 82.0, "type": "gm", "update": 15}""")));  // 33 + round(49.1)
        var score = Assert.Single(Hb(), u => Py.Eq(u!["update"], 6))!;
        Eq("[1607.694, 49.1]", T(score["score"], score["css"]));
    }

    [Fact]
    public void TestEmptyFeaturesArrayScoresZero()
    {
        // a female fallow deer has `features: []`; scoring must not crash on it
        var (score, type) = Scoring.Score(55, new JsonArray(), null, null);
        Eq("0.0", score);
        Assert.Null(type);
    }

    [Fact]
    public void TestTusksEmptyFeaturesScoresWeight()
    {
        // a sow has no tusks (`features: []`) but still scores her weight
        var (score, type) = Scoring.Score(5, new JsonArray(), JsonValue.Create(100000), null);
        Assert.NotEqual("0.0", score!.ToString());
        Assert.Equal("W", type);
    }

    [Fact]
    public void TestMeasuredTrophiesScoreAsExpected()
    {
        // measured trophy records and their expected scores: (species, scale_factor, event 7 weight_grams, expected score, event 22 without zero keys)
        (int Sp, double Scale, int Grams, double Expected, string E22)[] kills =
        [
            (10, 1.0, 156264, 774.1489, """{"left_antler":{"circumference_h1":106,"circumference_h2":100,"circumference_h3":92,"circumference_h4":98,"points":[{"length":104,"type":0}]},"right_antler":{"circumference_h1":107,"circumference_h2":104,"circumference_h3":92,"circumference_h4":90,"points":[{"length":110,"type":0}]}}"""),
            (8, 0.6, 528291, 121.181, """{"greatest_spread":1525,"left_antler":{"abnormals":[{}],"circumference_h1":225,"palm_length":678,"palm_width":442,"points":[{"type":0},{"type":0},{"type":0},{"type":0},{"type":0},{"type":0},{"type":0},{"type":0}]},"right_antler":{"circumference_h1":219,"palm_length":707,"palm_width":444,"points":[{"type":0},{"type":0},{"type":0},{"type":0},{"type":0},{"type":0},{"type":0},{"type":0}]}}"""),
            (18, 0.93, 121642, 362.9132, """{"greatest_spread":829,"inside_spread_of_main_beams":697,"left_antler":{"brow_palm_width":440,"circumference_h1":427,"circumference_h2":407,"circumference_h3":450,"circumference_h4":482,"nof_brow_points":4,"nof_points_excluding_brows":6,"points":[{"length":1447,"type":0},{"length":95,"type":1},{"length":475,"type":2},{"length":230,"type":3},{"length":198,"type":4},{"length":83,"type":5},{"length":100,"type":6},{"length":97,"type":7}],"top_palm_width":115},"right_antler":{"brow_palm_width":440,"circumference_h1":408,"circumference_h2":519,"circumference_h3":494,"circumference_h4":471,"nof_brow_points":4,"nof_points_excluding_brows":6,"points":[{"length":1397,"type":0},{"length":426,"type":1},{"length":89,"type":2},{"length":204,"type":3},{"length":344,"type":4},{"length":72,"type":5},{"length":74,"type":6},{"length":102,"type":7}],"top_palm_width":109},"tip_to_tip_spread":670}"""),
            (24, 1.0, 710884, 62.0102, """{"greatest_spread":894,"left_antler":{"circumference_h1":210,"circumference_h2":150,"circumference_h3":94,"circumference_h4":28,"points":[{"length":34223,"type":0}]},"right_antler":{"circumference_h1":192,"circumference_h2":136,"circumference_h3":92,"circumference_h4":32,"points":[{"length":33953,"type":0}]},"tip_to_tip_spread":811}"""),
            (39, 0.93, 83697, 144.3308, """{"greatest_spread":404,"inside_spread_of_main_beams":303,"left_antler":{"circumference_h1":345,"circumference_h2":334,"circumference_h3":300,"circumference_h4":285,"points":[{"length":733,"type":0}]},"right_antler":{"circumference_h1":319,"circumference_h2":333,"circumference_h3":320,"circumference_h4":297,"points":[{"length":739,"type":0}]},"tip_to_tip_spread":394}"""),
            (12, 0.7, 189997, 157.2475, """{"greatest_spread":1029,"inside_spread_of_main_beams":703,"left_antler":{"circumference_h1":286,"circumference_h2":198,"circumference_h3":191,"circumference_h4":174,"points":[{"length":1151,"type":0},{"length":394,"type":1},{"length":303,"type":2},{"length":345,"type":3},{"length":363,"type":4},{"length":252,"type":5},{"length":354,"type":6}]},"right_antler":{"circumference_h1":351,"circumference_h2":190,"circumference_h3":188,"circumference_h4":182,"points":[{"length":1190,"type":0},{"length":395,"type":1},{"length":301,"type":2},{"length":354,"type":3},{"length":382,"type":4},{"length":251,"type":5},{"length":363,"type":6}]},"tip_to_tip_spread":568,"weight":8971409}"""),
            (55, 0.51, 63849, 70.875, """{"greatest_spread":396,"inside_spread_of_main_beams":354,"left_antler":{"circumference_h1":173,"circumference_h2":119,"circumference_h3":151,"points":[{"length":435,"type":0},{"length":133,"type":1},{"length":106,"type":2},{"length":70,"type":4},{"length":39,"type":5},{"length":70,"type":6},{"length":54,"type":7},{"length":48,"type":8}],"top_palm_length":206,"top_palm_width":43},"right_antler":{"circumference_h1":155,"circumference_h2":140,"circumference_h3":132,"points":[{"length":453,"type":0},{"length":141,"type":1},{"length":111,"type":2},{"length":70,"type":4},{"length":39,"type":5},{"length":70,"type":6},{"length":54,"type":7},{"length":48,"type":8}],"top_palm_length":221,"top_palm_width":43},"tip_to_tip_spread":386,"weight":1335574}"""),
            (11, 0.8, 28550, 105.6499, """{"color":3,"coronets":2,"greatest_spread":127,"inside_spread_of_main_beams":107,"left_antler":{"circumference_h1":63,"circumference_h2":64,"circumference_h3":63,"circumference_h4":58,"points":[{"length":225,"type":0},{"length":65,"type":1},{"length":69,"type":2}]},"pearling":1,"right_antler":{"circumference_h1":64,"circumference_h2":64,"circumference_h3":64,"circumference_h4":62,"points":[{"length":237,"type":0},{"length":71,"type":1},{"length":79,"type":2}]},"tip_to_tip_spread":127,"volume":216062,"weight":432124}"""),
            (101, 0.8, 105656, 288.2676, """{"greatest_spread":840,"inside_spread_of_main_beams":626,"left_antler":{"abnormals":[{"length":225},{"length":92},{"length":301},{"length":324},{"length":48},{"length":101},{"length":83},{"length":315},{"length":157},{"length":48}],"circumference_h1":201,"circumference_h2":163,"circumference_h3":177,"circumference_h4":144,"points":[{"length":601,"type":0},{"length":137,"type":0},{"length":486,"type":1},{"length":276,"type":2},{"length":429,"type":3}]},"right_antler":{"abnormals":[{"length":238},{"length":92},{"length":290},{"length":345},{"length":48},{"length":103},{"length":83},{"length":310},{"length":156},{"length":48}],"circumference_h1":209,"circumference_h2":165,"circumference_h3":179,"circumference_h4":144,"points":[{"length":601,"type":0},{"length":137,"type":0},{"length":500,"type":1},{"length":270,"type":2},{"length":404,"type":3}]},"tip_to_tip_spread":626}"""),
        ];
        foreach (var k in kills)
        {
            var f = Scoring.Features(k.Sp, new JsonObject(), O(k.E22), k.Scale);
            var (score, _) = Scoring.Score(k.Sp, f, k.Grams, null);
            Assert.True(Math.Abs(Py.D(score) - k.Expected) < 0.00015, $"{k.Sp}: {score} vs expected {k.Expected}");  // server keeps sub-page precision
        }
        Assert.Null(Scoring.Features(55, new JsonObject(), new JsonObject(), 0.51));  // female fallow: no event 22, "No score"
    }

    [Fact]
    public void TestNonPermittedHitWarnsAndDisqualifies()
    {
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());
        // a permitted hit does not save a kill that also took a non-permitted one
        Hb(O("""{"event": 7, "individual": 253, "species": 17, "gender": 0, "weight_grams": 1241, "feature": 1241694}"""),
           O("""{"event": 1, "individual": 253, "ammunition_id": 14, "weapon_id": 257}"""),
           O("""{"event": 1, "individual": 253, "ammunition_id": 10, "weapon_id": 5}"""));
        var text = "Not permitted: Test Bullet may not be used on Test Rabbit. See your inbox.";
        Eq(new JsonArray(new JsonObject  // with the next heartbeat, once
        {
            ["text"] = text, ["colour"] = 4294967040, ["destination"] = "https://avatar.test/doc_64.jpg", ["update"] = 3,
            ["timer"] = -1, ["type"] = 100,
        }), Hb());
        Assert.Empty(Hb(O("""{"event": 3, "individual": 253, "extended_species": 17}""")));  // no gold
        var score = Assert.Single(Hb())!;
        Eq("""{"trophy_shot": 0, "score_text": "Disqualified ", "score": -1, "css": 0.0}""", new JsonObject
        {
            ["trophy_shot"] = C(score["trophy_shot"]), ["score_text"] = C(score["score_text"]),
            ["score"] = C(score["score"]), ["css"] = C(score["css"]),
        });
        var kill = Assert.Single(Reply(state.ExpeditionReply("{}"u8.ToArray()))["kills"]!.AsArray())!;
        Eq("[false, null]", T(kill["kill"]!["ethical"], kill["kill"]!["score"]));
        Assert.Equal(1, state.Unread());
        var thread = Assert.Single(Reply(state.ConversationsReply()).AsArray())!;
        Eq("[1024, 0]", T(thread["user_id"], thread["read"]));
        var message = Assert.Single(Reply(state.ConversationReply("user_id=1024"u8.ToArray()))["messages"]!.AsArray())!;
        Assert.Equal("Wrong ammunition for Test Rabbit. Permitted: Test Shot B, Test Shot A.", (string)message["content"]!);
        Assert.Equal(0, state.Unread());
        Eq("1", Reply(state.ConversationsReply())[0]!["read"]);
    }

    [Fact]
    public void TestScorePageLabels()
    {
        var p = ProtocolLiterals.TexturePrefix;
        var script = $"{p}ALBINO:2,{p}X:9;case n(\"{p}ALBINO\"):return\"albino\"";
        Assert.Equal(new Dictionary<long, string> { [2] = "albino" }, ScorePage.TextureNames(script));
        // neck bones are "Neck", no part is "-", the space stays without organs
        Assert.Equal("Head (Neck, Neck, Skull)", ScorePage.BodyPart(LOCALE, 3, 369098752));
        Assert.Equal("- (Right Lung, Shoulder Blade)", ScorePage.BodyPart(LOCALE, 1, 8388610));
        Assert.Equal(["- ", "Body "], new[] { 1L, 33 }.Select(b => ScorePage.BodyPart(LOCALE, b, 0)));
    }

    [Fact]
    public void TestExpeditionLifecycle()
    {
        var recordedList = "{\"total\": 11, \"expeditions\": [{\"id\": 482917301}]}"u8.ToArray();
        Assert.Equal("{\"recorded\": 1}"u8.ToArray(), state.ExpeditionReply("{\"recorded\": 1}"u8.ToArray()));  // none yet: replay
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());
        var first = Reply(state.ConnectReply())["expedition"]!["expedition_id"];
        Eq(first, Reply(state.ConnectReply())["expedition"]!["expedition_id"]);  // 2nd connect
        state.Disconnect([]);
        var second = Reply(state.ConnectReply())["expedition"]!["expedition_id"];  // direct launch: opens one
        Assert.False(Py.Eq(second, first));
        var listed = Reply(state.ExpeditionListReply(recordedList, "user_id=1&limit=2&offset=0"u8.ToArray()));
        Assert.Equal("13", Py.Dumps(listed["total"]));  // stays an int, like Python's
        Eq(T(second, first), new JsonArray(listed["expeditions"]!.AsArray().Select(e => C(e!["id"])).ToArray()));
        Assert.Equal([true, false], listed["expeditions"]!.AsArray().Select(e => e!["end_ts"] is null));
        Eq(second, Reply(state.ExpeditionReply("{}"u8.ToArray()))["expedition"]!["id"]);
        Assert.Equal(recordedList, state.ExpeditionListReply(recordedList, "limit=10&offset=10"u8.ToArray()));
    }

    [Fact]
    public void TestBuyDeliversWithNextHeartbeat()
    {
        var s = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, discounts: DISCOUNTS);
        var reply = s.BuyReply("item_id=19&oauth_access_token=x&amount=1&wallet=GOLD"u8.ToArray());
        Eq("""{"price": 8.0, "success": true}""", Reply(reply));  // 150 at 95 % off
        var updates = HbOf(s);
        Assert.Equal(2, updates.Count);
        var (items, balance) = (updates[0]!, updates[1]!);
        var row = Assert.Single(items["items"]!["items"]!.AsArray())!;
        Eq("[10, 19, 0, null]", T(items["update"], row["item_id"], row["slot"], row["units"]));
        var info = Assert.Single(items["items"]!["infos"]!.AsArray())!;
        Eq("[19, [0, 1, 11], 500, -2]", T(info["item_id"], info["allowedSlots"], info["weight"], info["units"]));  // built from the catalogue record
        Eq("""{"amount": 174.0, "type": "em", "update": 15}""", balance);
        Assert.Empty(HbOf(s));
        Eq("19", Items(s)[(string)row["guid"]!]!["item_id"]);
    }

    [Fact]
    public void TestBuyWithoutFundsChangesNothing()
    {
        foreach (var form in new[] { "item_id=19&amount=2&wallet=GOLD", "item_id=19&amount=1&wallet=SILVER", "item_id=5&amount=1" })
            Eq("""{"success": false}""", Reply(state.BuyReply(Encoding.ASCII.GetBytes(form))));
        Assert.Empty(HbOf(state));
        Eq("182.0", state.Wallet()["em"]);
        Assert.Equal(2, Items().Count);
    }

    [Fact]
    public void TestBuyBetweenHunts()
    {
        // launcher store, no hunt running, 106 em; item 6's rows in discount-list order (later rows start in the future)
        var connect = CONNECT;
        connect["wallet"]!["em"] = 106.0;
        var app = APP;
        app["items"]!.AsArray().Add(O("""
            {"id": 6, "name": "Test Rifle Two", "price": 650, "silver_price": 9800, "notbuyable": 0, "type": 1}
            """));
        var info = O("""
            {"allowedSlots": [0, 1, 2, 3, 32, 45], "group": 1, "name": "Test Rifle Two", "weight": 4000,
             "description": "With a composite stock.", "fitItems": [2, 3, 615, 616, 629, 889, 1147, 11],
             "usesCombinationSlots": false, "cat": 1, "units": -2, "client_type": 1, "item_id": 6, "capacity": 0,
             "allowedCombinationSlots": [], "type": 1, "image": "rifle_300", "variationName": "Composite",
             "define": "BOLT_ACTION_RIFLE"}
            """);
        var discounts = (JsonArray)JsonNode.Parse("""
            [{"id": 51730, "item_id": 6, "percentage": 95, "start_ts": 1700000000, "end_ts": 1099511627776},
             {"id": 51744, "item_id": 6, "percentage": 100, "start_ts": 1893542400, "end_ts": 1099511627776},
             {"id": 51802, "item_id": 6, "percentage": 100, "start_ts": 1893543300, "end_ts": 1099511627776}]
            """)!;
        var s = new State(Path.Combine(tmp, "buy.db"), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES,  // own db: 106 em
                          discounts: discounts, sampleInfos: new JsonArray(info.DeepClone()));
        Assert.Equal("{\"price\": 33.0, \"success\": true}"u8.ToArray(),
                     s.BuyReply("item_id=6&oauth_access_token=x&amount=1&wallet=GOLD"u8.ToArray()));  // 650 at 95 % off
        Eq("73.0", s.Wallet()["em"]);  // Me/poll ems
        var reply = Reply(s.ConnectReply());
        Eq("""{"em": 73.0, "gm": 33.0}""", reply["wallet"]);
        var row = (JsonObject)reply["inventory"]!["items"]!.AsArray().Single(i => Py.Eq(i!["item_id"], 6))!.DeepClone();
        row.Remove("guid");  // a new guid
        Eq("""{"item_id": 6, "slot": 0, "metadata": {}, "units": null}""", row);
        Eq(info, reply["inventory"]!["infos"]!.AsArray().Single(i => Py.Eq(i!["item_id"], 6)));
        // the first heartbeat: the balance again, no update 10 (the connect toast aside)
        var updates = HbOf(s);
        Eq("""{"amount": 73.0, "type": "em", "update": 15}""", updates[0]);
        Assert.DoesNotContain(updates, u => Py.Eq(u!["update"], 10));
    }

    [Fact]
    public void TestDiscountIsTheItemsFirstRow()
    {
        // the store script's getPrice reads only the item's first listed row: a later active row is not used
        var discounts = (JsonArray)JsonNode.Parse("""
            [{"item_id": 19, "percentage": 50, "start_ts": 1099511627000, "end_ts": 1099511627776},
             {"item_id": 19, "percentage": 95, "start_ts": 0, "end_ts": 1099511627776}]
            """)!;
        var s = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, discounts: discounts);
        Eq("""{"price": 150.0, "success": true}""", Reply(s.BuyReply("item_id=19&amount=1&wallet=GOLD"u8.ToArray())));
    }

    [Fact]
    public void TestConsumedUnitsFollowEvent6()
    {
        // 7 shots from a 10-cartridge stack left 3; quantity -7 refilled it
        Hb(Repeat(O("""{"event": 6, "item": "200", "parent_item": "300", "quantity": 1}"""), 7));
        Eq("3", Items()["200"]!["units"]);
        Hb(O("""{"event": 6, "item": "200", "parent_item": "", "quantity": -7}"""));
        Eq("10", Items()["200"]!["units"]);
        Hb(O("""{"event": 6, "item": "200", "quantity": 10}"""), O("""{"event": 6, "item": "100", "quantity": 1}"""),
           O("""{"event": 6, "item": "999", "quantity": 1}"""));
        var items = Items();
        Eq("[0, 0, null]", T(items["200"]!["units"], items["200"]!["slot"], items["100"]!["units"]));
    }

    [Fact]
    public void TestConnectStartsAtLaunchedLodge()
    {
        state.NoteLaunch("start=101&loctype=LODGE&reserve=2&time=540"u8.ToArray());
        var spawn = Reply(state.ConnectReply())["expedition"]!;
        Eq("[101, -8172.0, 217.0, 5228.0]", T(spawn["lodge"], spawn["x"], spawn["y"], spawn["z"]));
        Eq("-8172", Reply(state.ExpeditionReply("{}"u8.ToArray()))["expedition"]!["x"]);
        state.Disconnect([]);
        spawn = Reply(state.ConnectReply())["expedition"]!;  // direct launch: the recorded lodge
        Eq("[100, -10380.0]", T(spawn["lodge"], spawn["x"]));
    }

    [Fact]
    public void TestAdminGrantsItemsAndCurrency()
    {
        var ammo = O("""{"item_id": 13, "name": ".270 Soft Point", "units": 10}""");  // from the install's json_inventory.json
        var s = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, sampleInfos: new JsonArray(ammo.DeepClone()));
        Assert.False(s.GrantItems(99999));
        Assert.True(s.GrantItems(13, 2));
        Assert.Null(s.Give("gm", -1000));  // below 0: refused
        Eq("100.0", s.Give("gm", 67));
        var updates = HbOf(s);
        Assert.Equal(2, updates.Count);
        var (items, balance) = (updates[0]!, updates[1]!);
        Eq("[[13, 10], [13, 10]]", new JsonArray(items["items"]!["items"]!.AsArray().Select(r => (JsonNode?)T(r!["item_id"], r["units"])).ToArray()));
        Eq(T(new JsonArray(ammo), O("""{"amount": 100.0, "type": "gm", "update": 15}""")), T(items["items"]!["infos"], balance));
    }

    [Fact]
    public void TestLastHuntStatsAfterDisconnect()
    {
        Hb(O("""{"event": 1, "individual": 5, "weapon_id": 5, "ammunition_id": 10, "distance_cms": 1}"""));  // before the hunt
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());
        Hb(O("""{"event": 7, "individual": 5, "species": 1}"""), O("""{"event": 7, "individual": 6, "species": 4}"""),
           O("""{"event": 11, "individual": 5}"""), O("""{"event": 11, "individual": 5}"""),
           O("""{"event": 12, "individual": 6, "track_type": 24}"""),
           O("""{"event": 21, "weapon_id": 5, "ammunition_id": 10}"""),
           O("""{"event": 1, "individual": 5, "weapon_id": 5, "ammunition_id": 10, "distance_cms": 4000}"""),
           O("""{"event": 1, "individual": 5, "weapon_id": 5, "ammunition_id": 10, "distance_cms": 6000}"""),
           O("""{"event": 3, "individual": 5, "extended_species": 1}"""));
        Assert.Null(Reply(state.ExpeditionReply("{}"u8.ToArray()))["stats"]);  // ongoing
        state.Disconnect("{\"distance_travelled\": 466, \"shots_fired\": 3, \"shots_hit\": 2}"u8.ToArray());
        var stats = Reply(state.ExpeditionReply("{}"u8.ToArray()))["stats"]!;
        Eq("466", stats["distance"]);
        // finished hunt: achievements first, zero counters left out, Statistics/total key order
        Assert.Equal("""
                     {"achievements": [], "weapon": {"5": {"10": {"ethical_kills": 1, "hits": 2, "misses": 1, "kills": 1, "distance": 60000}}}, "animal": {"1": {"ethical_kills": 1, "spots": 1, "kills": 1}, "4": {"tracks": 1}}}
                     """, Py.Dumps(stats["data"]));
    }

    [Fact]
    public void TestLastHuntStats()
    {
        // a recorded hunt (Test Reserve, 945 s): every event of its heartbeats (item guids and population key
        // replaced), its disconnect, and its expected stats. `animal` keys are ordered by hash
        // ("1", "4", "17"); Eq ignores key order
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());
        foreach (var events in JsonNode.Parse("""
            [
            [{"direction": "enter", "event": 101, "location_name": "LOGGERSPOINT_HUNTING_LODGE", "player_x": -10379, "player_y": 215, "player_z": 7925, "x": -10379, "y": 215, "z": 7925}, {"direction": "enter", "event": 101, "location_name": "NORTH_OF_LOGGERSPOINT_HUNTING_LODGE", "player_x": -10379, "player_y": 215, "player_z": 7925, "x": -10379, "y": 215, "z": 7925}],
            [{"event": 6, "item": "200", "parent_item": "", "quantity": -2}],
            [{"direction": "leave", "event": 101, "location_name": "LOGGERSPOINT_HUNTING_LODGE", "player_x": -10317, "player_y": 212, "player_z": 7861, "x": -10317, "y": 212, "z": 7861}],
            [{"direction": "enter", "event": 101, "location_name": "LOGGERSPOINT_HUNTING_LODGE", "player_x": -10318, "player_y": 212, "player_z": 7857, "x": -10318, "y": 212, "z": 7857}],
            [{"event": 7, "gender": 0, "individual": 67, "population": 101, "special": 0, "species": 1, "texture": 0, "weight_grams": 112937, "x": -10326, "y": 217, "z": 7635}, {"age_rings": 0, "beard": 0, "color": 0, "coronets": 0, "event": 22, "greatest_spread": 729, "horn_curve": 0, "individual": 67, "inside_spread_of_main_beams": 684, "left_antler": {"abnormals": [{"length": 56}], "average_radius": 22, "brow_palm_width": 0, "circumference_h1": 141, "circumference_h2": 142, "circumference_h3": 132, "circumference_h4": 139, "nof_brow_points": 0, "nof_points_excluding_brows": 3, "palm_length": 0, "palm_width": 0, "points": [{"length": 835, "type": 0}, {"length": 129, "type": 1}, {"length": 555, "type": 2}, {"length": 214, "type": 3}, {"length": 439, "type": 4}], "right_most_root_joint_pos_x": 354, "top_palm_length": 0, "top_palm_width": 0}, "left_spur": 0, "pearling": 0, "right_antler": {"average_radius": 22, "brow_palm_width": 0, "circumference_h1": 145, "circumference_h2": 146, "circumference_h3": 135, "circumference_h4": 137, "nof_brow_points": 0, "nof_points_excluding_brows": 3, "palm_length": 0, "palm_width": 0, "points": [{"length": 834, "type": 0}, {"length": 129, "type": 1}, {"length": 556, "type": 2}, {"length": 217, "type": 3}, {"length": 438, "type": 4}], "right_most_root_joint_pos_x": 374, "top_palm_length": 0, "top_palm_width": 0}, "right_spur": 0, "tail": 0, "tip_to_tip_spread": 584, "volume": 5453818, "weight": 4908436}, {"event": 12, "individual": 67, "location": 0, "player_x": -10319, "player_y": 213, "player_z": 7843, "track_type": 9, "x": -10326, "y": 217, "z": 7635}],
            [{"direction": "leave", "event": 101, "location_name": "LOGGERSPOINT_HUNTING_LODGE", "player_x": -10317, "player_y": 215, "player_z": 7841, "x": -10317, "y": 215, "z": 7841}, {"direction": "enter", "event": 101, "location_name": "LOGGERSPOINT_HUNTING_LODGE", "player_x": -10318, "player_y": 215, "player_z": 7838, "x": -10318, "y": 215, "z": 7838}],
            [{"direction": "leave", "event": 101, "location_name": "LOGGERSPOINT_HUNTING_LODGE", "player_x": -10320, "player_y": 215, "player_z": 7835, "x": -10320, "y": 215, "z": 7835}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10319, "player_y": 219, "player_z": 7660, "track_type": 4, "x": -10318, "y": 219, "z": 7657}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10313, "player_y": 219, "player_z": 7639, "track_type": 4, "x": -10312, "y": 219, "z": 7637}, {"event": 13, "individual": 67, "time_taken": 188}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10319, "player_y": 218, "player_z": 7638, "track_type": 4, "x": -10322, "y": 218, "z": 7638}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10310, "player_y": 218, "player_z": 7585, "track_type": 4, "x": -10311, "y": 217, "z": 7583}],
            [{"event": 7, "gender": 1, "individual": 112, "population": 101, "special": 0, "species": 1, "texture": 0, "weight_grams": 63641, "x": -10114, "y": 239, "z": 7490}, {"event": 12, "individual": 112, "location": 0, "player_x": -10299, "player_y": 219, "player_z": 7568, "track_type": 9, "x": -10113, "y": 240, "z": 7490}],
            [{"event": 11, "individual": 67, "location": 0, "milliseconds_viewed": 8379, "percentage": 11, "player_x": -10326, "player_y": 214, "player_z": 7424, "x": -10288, "y": 227, "z": 7353}],
            [{"event": 6, "item": "200", "parent_item": "100", "quantity": 1}, {"event": 4, "hash": -1426822713, "photo_type": 14, "shot_id": 0, "thumbnail": false}, {"ammunition_id": 10, "animal_effects": [2], "animal_state": 4, "distance_cms": 9110, "event": 1, "has_license": true, "individual": 67, "location": 0, "object_type": 0, "organ1": 33, "organ2": 0, "player_x": -10333, "player_y": 214, "player_z": 7419, "population_key": "<population-key>", "pose": 1, "scope_id": 3, "shot_id": 0, "species": 2, "weapon_id": 5, "x": -10285, "y": 229, "z": 7344}],
            [{"event": 32, "hash": -1426822713, "photo_type": 14}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10298, "player_y": 223, "player_z": 7378, "track_type": 4, "x": -10297, "y": 224, "z": 7377}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10298, "player_y": 223, "player_z": 7360, "track_type": 4, "x": -10298, "y": 223, "z": 7357}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10286, "player_y": 228, "player_z": 7346, "track_type": 0, "x": -10285, "y": 229, "z": 7344}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10278, "player_y": 230, "player_z": 7338, "track_type": 0, "x": -10276, "y": 230, "z": 7337}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10269, "player_y": 232, "player_z": 7332, "track_type": 0, "x": -10267, "y": 232, "z": 7330}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10264, "player_y": 234, "player_z": 7321, "track_type": 0, "x": -10263, "y": 234, "z": 7320}],
            [{"event": 7, "feature": 1049841, "gender": 1, "individual": 298, "population": 105, "special": 0, "species": 17, "texture": 0, "weight_grams": 1049, "x": -10274, "y": 246, "z": 7214}, {"event": 12, "individual": 298, "location": 0, "player_x": -10250, "player_y": 242, "player_z": 7298, "track_type": 33, "x": -10247, "y": 243, "z": 7298}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10255, "player_y": 241, "player_z": 7292, "track_type": 4, "x": -10257, "y": 240, "z": 7290}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10228, "player_y": 242, "player_z": 7259, "track_type": 4, "x": -10226, "y": 243, "z": 7257}],
            [{"event": 12, "individual": 112, "location": 0, "player_x": -10227, "player_y": 242, "player_z": 7256, "track_type": 3, "x": -10227, "y": 243, "z": 7256}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10195, "player_y": 257, "player_z": 7233, "track_type": 4, "x": -10193, "y": 259, "z": 7232}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10179, "player_y": 263, "player_z": 7211, "track_type": 4, "x": -10178, "y": 262, "z": 7209}],
            [{"animal_state": 3, "event": 2, "individual": 67, "items": null, "player_x": -10174, "player_y": 263, "player_z": 7202, "state": 0, "x": -10135, "y": 265, "z": 7144}],
            [{"event": 12, "individual": 67, "location": 0, "player_x": -10149, "player_y": 264, "player_z": 7178, "track_type": 4, "x": -10148, "y": 264, "z": 7177}],
            [{"event": 7, "feature": 16711935, "gender": 0, "individual": 366, "population": 102, "special": 0, "species": 4, "texture": 3, "weight_grams": 24818, "x": -10165, "y": 265, "z": 6973}, {"event": 12, "individual": 366, "location": 0, "player_x": -10144, "player_y": 264, "player_z": 7164, "track_type": 9, "x": -10165, "y": 265, "z": 6973}],
            [{"antlers_score_percentage": 0.6787666082382202, "antlers_seed": 31162, "base_texture": "001_crea_muledeer_dif.dds", "event": 3, "extended_species": 1, "fur_texture": "", "harvest_value": 0, "individual": 67, "player_x": -10135, "player_y": 267, "player_z": 7145, "scale": 1, "trophy_integrity": 100000, "wound_time": 176922}]
            ]
            """)!.AsArray())
            state.HeartbeatReply(Encoding.ASCII.GetBytes(Py.Dumps(new JsonObject { ["events"] = events!.DeepClone() })));
        state.Disconnect("""{"animals_killed": 1, "animals_spotted": 1, "animals_tracked": 1, "distance_travelled": 1003, "shots_fired": 1, "shots_hit": 1}"""u8.ToArray());
        var reply = Reply(state.ExpeditionReply("{}"u8.ToArray()));
        Eq("[]", reply["antler_collectables"]);
        Eq("1003", reply["stats"]!["distance"]);
        Eq("""
           {"achievements": [], "weapon": {"5": {"10": {"ethical_kills": 1, "hits": 1, "kills": 1, "distance": 91100}}},
            "animal": {"1": {"ethical_kills": 1, "tracks": 2, "spots": 1, "kills": 1}, "4": {"tracks": 1}, "17": {"tracks": 1}}}
           """, reply["stats"]!["data"]);
    }

    [Fact]
    public void TestHuntEndsAtLastHeartbeatWithoutDisconnect()
    {
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());
        state.HeartbeatReply("{\"events\": []}"u8.ToArray());
        Assert.Null(Reply(state.ExpeditionReply("{}"u8.ToArray()))["stats"]);  // still within the timeout
        double lastSeen;
        using (var con = new SqliteConnection($"Data Source={Db};Pooling=False"))  // the client quit: last heartbeat 200 s ago
        {
            con.Open();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "UPDATE expeditions SET last_seen = last_seen - 200";
                cmd.ExecuteNonQuery();
            }
            using var get = con.CreateCommand();
            get.CommandText = "SELECT last_seen FROM expeditions";
            lastSeen = (double)get.ExecuteScalar()!;
        }
        var reply = Reply(state.ExpeditionReply("{}"u8.ToArray()));
        Assert.Equal((long)lastSeen, Py.L(reply["expedition"]!["end_ts"]));
        Assert.True(Py.IsInt(reply["expedition"]!["end_ts"]) && Py.IsInt(reply["expedition"]!["start_ts"]));  // whole seconds once closed
        Assert.NotNull(reply["stats"]);
        Assert.False(Py.Eq(Reply(state.ConnectReply())["expedition"]!["expedition_id"], reply["expedition"]!["id"]));  // a later connect opens a new one
    }

    static JsonNode? C(JsonNode? n) => n?.DeepClone();
}

public class SkillTests : StateTestBase
{
    public SkillTests()
    {
        var connect = CONNECT;
        connect["skills"] = LEVELS;
        state = new State(Db, Py.DumpBytes(connect), BASE, ICON, APP, LOCALE, TEXTURES, skillList: NUMBERS, itemDefs: ITEM_DEFS);
    }

    JsonNode Numbers() => Reply(state.SkillListReply("user_id=7"u8.ToArray())!);

    [Fact]
    public void TestSeedAndReplies()
    {
        Assert.Null(state.SkillListReply("user_id=8"u8.ToArray()));  // another player: replayed recording
        var numbers = Numbers();
        Eq("[1.14, 2, 1.007]", T(numbers["weapon"]!["5"], numbers["spotting"]![17], numbers["spotting"]![16]));
        Eq(LEVELS, Reply(state.ConnectReply())["skills"]);
    }

    [Fact]
    public void TestSpotAndPermittedHits()
    {
        Hb(O("""{"event": 7, "individual": 9, "species": 1}"""), O("""{"event": 11, "individual": 9}"""),
           O("""{"event": 1, "individual": 9, "weapon_id": 5, "ammunition_id": 10, "animal_state": 3}"""),
           O("""{"event": 21, "weapon_id": 257, "ammunition_id": 14}"""),  // a miss earns nothing
           O("""{"event": 7, "individual": 4, "species": 17}"""),  // rabbit: .243 not permitted, no XP
           O("""{"event": 1, "individual": 4, "weapon_id": 257, "ammunition_id": 10, "animal_state": 3}"""),
           O("""{"event": 7, "individual": 3, "species": 7}"""),  // pheasant: half on the ground, full in flight
           O("""{"event": 1, "individual": 3, "weapon_id": 257, "ammunition_id": 14, "animal_state": 3}"""),
           O("""{"event": 1, "individual": 3, "weapon_id": 257, "ammunition_id": 14, "animal_state": 6}"""));
        var numbers = Numbers();
        Eq("1.14", numbers["spotting"]![1]);  // 0.1 + 2 XP
        Eq("[1.273, 1.273]", T(numbers["weapon"]!["5"], numbers["weapon"]!["614"]));  // the family: 2.1 + 2
        Eq("1.207", numbers["weapon"]!["257"]);  // 0.1 + 1 + 2
    }

    [Fact]
    public void TestTrackCluesPayByCountPerAnimalAndHunt()
    {
        // 16 clues on a buck and 2 on a doe in one hunt, +8.4 mule deer tracking
        static JsonObject Clue(int individual) => new() { ["event"] = 12, ["individual"] = individual, ["track_type"] = 4 };
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());
        Hb([O("""{"event": 7, "individual": 67, "species": 1}"""), .. Repeat(Clue(67), 3), O("""{"event": 13, "individual": 67}""")]);
        Hb([.. Repeat(Clue(67), 13), O("""{"event": 7, "individual": 112, "species": 1}"""), Clue(112), Clue(112)]);
        Eq("1.567", Numbers()["tracking"]![1]);  // 0.1 + 6.4 + 2.0
        state.NoteLaunch("start=100&reserve=2"u8.ToArray());  // a new hunt counts the same animal from its first clue
        Hb(Clue(67));
        Eq("1.62", Numbers()["tracking"]![1]);  // + 0.8
    }

    [Fact]
    public void TestLevelUpToastNextHeartbeat()
    {
        var hit = O("""{"event": 1, "individual": 9, "weapon_id": 5, "ammunition_id": 10, "animal_state": 3}""");
        Assert.Empty(Hb([O("""{"event": 7, "individual": 9, "species": 1}"""), .. Repeat(hit, 6)]));  // 2.1 + 12 XP
        Assert.Empty(Hb(hit));  // 16.1 XP: level 2 from 15
        Eq(new JsonArray(new JsonObject
        {
            ["text"] = "Test Rifle: level 2 reached", ["colour"] = 4294967040,
            ["destination"] = ICON, ["update"] = 3, ["timer"] = 1000, ["type"] = 100,
        }), Hb());
        Eq("2", Reply(state.ConnectReply())["skills"]!["weapon"]!["614"]);  // same family
    }

    [Fact]
    public void TestItemMetadataEventMerges()
    {
        Hb(O("""{"event": 23, "item": "100", "metadata": {"dog_name": "Rex", "experience": 4.0, "stamina": 1288.0}}"""));
        Hb(O("""{"event": 23, "item": "100", "metadata": {"experience": 8.5}}"""));
        Eq("""{"dog_name": "Rex", "experience": 8.5, "stamina": 1288.0}""", Items()["100"]!["metadata"]);
    }
}
