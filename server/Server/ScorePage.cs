// Kill details page for server kills. Labels come from the English locale strings and the Application/application
// reply; organ bits and textures from the web script's constants.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public static class ScorePage
{
    static readonly string[] POSES = ["pose_stand", "pose_crouch", "pose_prone"];  // pose = index (2 → Prone)
    static readonly string[] ANIMAL_STATES =
    [
        "animal_state_sleeping", "animal_state_lying_down", "animal_state_idle", "animal_state_walking",
        "animal_state_running", "animal_state_flying", "animal_state_swimming", "animal_state_eating",
        "animal_state_charging", "animal_state_trotting", "animal_state_flying_not_spooked",
        "animal_state_hiding", "animal_state_enter_burrow", "animal_state_exit_burrow",
        "animal_state_flee_to_burrow",
    ];  // animal_state = index + 1 (3 → Idle)

    // bodypart = organ bit values: 0 no hit (not shown), 1 head … 9 right front leg
    static readonly (int Bit, string Key)[] BODY_PARTS =
    [
        (1, "animal_body_part_head"), (2, "animal_body_part_left_ear"), (3, "animal_body_part_right_ear"),
        (4, "animal_body_part_neck"), (5, "animal_body_part_body"), (6, "animal_body_part_left_leg"),
        (7, "animal_body_part_left_front_leg"), (8, "animal_body_part_right_leg"),
        (9, "animal_body_part_right_front_leg"),
    ];

    // organ = organ bit values → (locale key, number for "Spine %s"), ascending by bit; neck bones are "Neck"
    // ponytail: veins (bits 7-9, 11-20) and antlers (27) aren't listed: label unknown
    static readonly (int Bit, string Key, int? N)[] ORGANS =
    [
        (0, "animal_organ_heart", null), (1, "animal_organ_rightlung", null), (2, "animal_organ_leftlung", null),
        (3, "animal_organ_liver", null), (4, "animal_organ_brain", null), (5, "animal_organ_intestines", null),
        (6, "animal_organ_stomach", null), (10, "animal_organ_spine", 3), (21, "animal_organ_spine", 1),
        (22, "animal_organ_spine", 2), (23, "animal_organ_shoulder_blade", null), (24, "animal_body_part_neck", null),
        (25, "animal_body_part_neck", null), (26, "animal_body_part_neck", null), (28, "animal_organ_skull", null),
        (29, "animal_organ_hip_bone", null),
    ];

    static readonly Regex TextureValue = new($@"\b{ProtocolLiterals.TexturePrefix}([A-Z0-9_]+):(-?\d+)");
    static readonly Regex TextureCase = new($@"case n\(""{ProtocolLiterals.TexturePrefix}([A-Z0-9_]+)""\):return""(\w+)""");

    /// <summary>html.escape(s, quote); quote=false leaves " and ' alone.</summary>
    public static string Escape(string s, bool quote = true)
    {
        s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return quote ? s.Replace("\"", "&quot;").Replace("'", "&#x27;") : s;
    }

    /// <summary>{texture id: name} from the web script's App.animal.texture switch and its texture constant values.</summary>
    public static Dictionary<long, string> TextureNames(string script)
    {
        var values = new Dictionary<string, long>();
        foreach (Match m in TextureValue.Matches(script)) values[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
        var names = new Dictionary<long, string>();
        foreach (Match m in TextureCase.Matches(script))
            if (values.TryGetValue(m.Groups[1].Value, out var id)) names[id] = m.Groups[2].Value;
        return names;
    }

    static string Loc(JsonObject locale, string key, string fallback) => locale[key] is JsonValue v ? v.GetValue<string>() : fallback;  // locale.get(key, fallback)

    /// <summary>The Body Part cell: the parts or "-", a space, then the organs in parentheses if any
    /// ("Body ", "- (Right Lung, Left Lung)").</summary>
    public static string BodyPart(JsonObject locale, long bodypart, long organ)
    {
        var parts = BODY_PARTS.Where(p => (bodypart >> p.Bit & 1) != 0).Select(p => Loc(locale, p.Key, p.Key)).ToList();
        var organs = ORGANS.Where(o => (organ >> o.Bit & 1) != 0)
            .Select(o => o.N is { } n ? Loc(locale, o.Key, o.Key).Replace("%s", n.ToString()) : Loc(locale, o.Key, o.Key)).ToList();
        return (parts.Count > 0 ? string.Join(", ", parts) : "-") + " " + (organs.Count > 0 ? $"({string.Join(", ", organs)})" : "");
    }

    /// <summary>kill: the page's killData object; items: {item id: Application item}; locale: English label strings;
    /// textures: TextureNames() of the web script (ids 0, 1 and unknown ones are "common", as on the site);
    /// sheet: the worked score sheet markup (Scoring.SheetHtml), null for the "no data" block.</summary>
    public static string Render(JsonObject kill, string handle, string speciesName, string cssClass, string reserveName,
                                Dictionary<long, JsonObject> items, JsonObject locale, Dictionary<long, string> textures, string? sheet)
    {
        var tex = kill["texture"];  // a spawn without texture (null) is "common", as Python's textures.get(None, "common")
        var texture = Py.IsNum(tex) && Py.D(tex) is var t && t != 0 && t != 1 && t == Math.Floor(t) && textures.TryGetValue((long)t, out var tn)
            ? tn : "common";

        string Label(string[] table, long i) => i >= 0 && i < table.Length ? Loc(locale, table[i], "") : "";
        string Text(string s) => Escape(s, false);  // text nodes keep quotes as-is ("Fox's Hollow")
        JsonObject? Item(JsonNode? id) => Py.IsNum(id) && Py.D(id) is var d && d == Math.Floor(d) ? items.GetValueOrDefault((long)d) : null;
        string Name(JsonNode? id) => Item(id)?["name"] is { } n && Py.Truthy(n) ? n.GetValue<string>() : "";

        var hits = (JsonArray)kill["hits"]!;
        var rows = new List<string>();
        var number = 0;
        foreach (var h in hits)
        {
            number++;
            var scope = Item(h!["scope_id"])?["name"];
            var scopeCell = Py.Truthy(scope) ? $"<span title=\"{Escape(scope!.GetValue<string>())}\">&#10003;</span>" : "";
            var d = Py.D(h["distance"]) / 1000;
            rows.Add(
                $"<tr>\n\t<td>{number}</td>\n\t<td><a href=\"/{Escape(handle)}\">{Text(handle)}</a></td>\n" +
                $"\t<td><a href=\"/#store/item/{Py.Str(h["weapon_id"])}\">{Text(Name(h["weapon_id"]))}</a></td>\n" +
                $"\t<td>{scopeCell}</td>\n" +
                $"\t<td>{Text(Name(h["ammo_id"]))}</td>\n\t<td>{Py.Fixed(d, 3)} m ({Py.Fixed(d * 3.28084, 1)} ft)</td>\n" +
                $"\t<td>{Text(Label(ANIMAL_STATES, Py.L(h["animal_state"]) - 1))}</td>\n" +
                $"\t<td>{Text(BodyPart(locale, Py.L(h["bodypart"]), Py.L(h["organ"])))}</td>\n" +
                $"\t<td>{Text(Label(POSES, Py.L(h["pose"])))}</td>\n\t<td>None</td>\n</tr>");
        }
        var k = kill["kill"]!;
        var w = Py.D(kill["weight"]) / 1000;
        var expedition = kill["expedition"];
        var expeditionId = Py.Truthy(expedition) ? Py.Str(expedition![0]!["id"]) : "";
        var harvested = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(Py.D(k["confirmTs"]))).ToString("yyyy-MM-dd HH:mm");
        var score = k["score"] is null ? "No score" : Py.Str(k["score"]);
        var last = hits.Count > 0 ? hits[^1]!.AsObject() : new JsonObject();
        var sponsor = Item(last["ammo_id"])?["sponsor_image"];
        var data = Py.Dumps(kill, compact: true).Replace("</", "<\\/");  // can't close the <script>
        var noSheet = $"<div class=\"{Escape(cssClass)}\">\n" +
            $"\t\t<div class=\"species-title\">{Text(speciesName)}</div>\n" +
            $"\t\t<div>{Text(Loc(locale, "score_no_data_available", ""))}</div>\n" +
            $"</div>\n";
        var sheetOrNo = string.IsNullOrEmpty(sheet) ? noSheet : sheet;
        var joined = string.Join("\n", rows);
        var genderLetter = Py.D(kill["gender"]) == 0 ? "M" : "F";
        var textureText = Text(Loc(locale, "animal_texture_" + texture, texture));
        var pos = k["pos"]!;
        var sponsorName = Py.Truthy(sponsor) ? sponsor!.GetValue<string>() : "";
        var dotAt = sponsorName.LastIndexOf('.');
        var sponsorHtml = sponsorName.Length > 0
            ? $"<a href=\"/#store/item/{Py.Str(last["ammo_id"])}\" class=\"sponsor-image sponsor-{Escape(dotAt >= 0 ? sponsorName[..dotAt] : sponsorName)}\" title=\"{Escape(Name(last["ammo_id"]))}\"></a>"
            : "";
        return $"<div class=\"widgetRoot scoresheet\">\n" +
            $"{sheetOrNo}\t<table class=\"scoretable shots\">\n" +
            $"\t   <thead>\n" +
            $"\t   <tr>\n" +
            $"\t<th>#</th>\n" +
            $"\t<th>Hunter</th>\n" +
            $"\t<th>Weapon</th>\n" +
            $"\t<th>Scope</th>\n" +
            $"\t<th>Ammo</th>\n" +
            $"\t<th>Distance</th>\n" +
            $"\t<th>Animal state</th>\n" +
            $"\t<th>Hit</th>\n" +
            $"\t<th>Stance</th>\n" +
            $"\t<th>Platform</th>\n" +
            $"</tr>\n" +
            $"\t   </thead>\n" +
            $"\t   <tbody>\n" +
            $"\t   {joined}\n" +
            $"\t   </tbody>\n" +
            $"</table><table class=\"summary\" style=\"width: 98%; margin-right:1%\">\n" +
            $"\t<tr>\n" +
            $"\t\t<td class=\"summarywide1\">Weight</td>\n" +
            $"\t\t<td class=\"summarywide2\">Type</td>\n" +
            $"\t\t<td class=\"mapbutton\" rowspan=\"2\">\n" +
            $"\t\t\t<button id=\"scoremapbutton\" class=\"btn btn-primary btn-large\">\n" +
            $"\t\t\t\tMap\n" +
            $"\t\t\t</button>\n" +
            $"\t\t</td>\n" +
            $"\t</tr>\n" +
            $"\t<tr>\n" +
            $"\t\t<td class=\"value\">{Py.Fixed(w, 3)} kg ({Py.Fixed(w * 2.20462, 1)} lbs)</td>\n" +
            $"\t\t<td class=\"value\">{textureText} ({genderLetter})</td>\n" +
            $"\t</tr>\n" +
            $"\t\t   <tr>\n" +
            $"\t\t\t <td class=\"empty\">Wound time</td>\n" +
            $"\t\t\t\t<td class=\"empty\">Trophy integrity</td>\n" +
            $"\t\t   </tr>\n" +
            $"\t\t   <tr>\n" +
            $"\t\t\t   <td class=\"value\">{Py.Fixed(Py.D(k["wound_time"]), 3)}</td>\n" +
            $"\t\t\t   <td class=\"value\">{Py.D(k["trophy_integrity"]).ToString("G6")}%</td>\n" +
            $"\t\t   </tr>\n" +
            $"\t<tr>\n" +
            $"\t\t<td class=\"empty\">Shot location</td>\n" +
            $"\t\t<td class=\"empty\">Shots</td>\n" +
            $"\t\t<td class=\"scoresheet-label big\" rowspan=\"2\">Final score</td>\n" +
            $"\t</tr>\n" +
            $"\t<tr>\n" +
            $"\t\t<td class=\"value\">{Text(reserveName)} ({Py.Str(pos["x"])}, {Py.Str(pos["z"])})</td>\n" +
            $"\t\t<td class=\"value\">{hits.Count}</td>\n" +
            $"\t</tr>\n" +
            $"\t<tr>\n" +
            $"\t\t<td class=\"empty\">Harvested</td>\n" +
            $"\t\t<td class=\"empty\">Hunter</td>\n" +
            $"\t\t<td class=\"value big\" rowspan=\"2\">Trophy score: {score}<br/>Harvest value: {Py.Str(k["harvest_value"])}</td>\n" +
            $"\t</tr>\n" +
            $"\t<tr>\n" +
            $"\t\t<td class=\"value\"><a href=\"/#profile/{Escape(handle)}/expedition/{expeditionId}\">{harvested}</a></td>\n" +
            $"\t\t<td class=\"value\">{Escape(handle)}</td>\n" +
            $"\t</tr>\n" +
            $"</table>\n" +
            $"<script>\n" +
            $"var killData = {data},\n" +
            $"\tstrs = {{\n" +
            $"\t\tplayer: 'Player',\n" +
            $"\t\tanimal: 'Animal',\n" +
            $"\tclue: 'Clue'\n" +
            $"\t}};\n" +
            $"</script>\n" +
            $"<div id=\"mapcontainer\">\n" +
            $"\t <div id=\"mapclose\">x</div>\n" +
            $"\t <div id=\"scoremap\"></div>\n" +
            $"</div>\n" +
            $"<div id=\"scoreSheetSponsors\">{sponsorHtml}</div>\n" +
            $"</div>\n";
    }
}
