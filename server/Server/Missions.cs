// Missions on a community server: the catalogue's definitions (`missions[]`, `mission_groups[]` of the recorded application), the account's
// states (tables `missions` and `mission_prefs`, filled from a restored `Mission_missions.json` at the restore and on first use), the
// replies of Mission/*, and the progress the hunt's events make. The game keeps no mission state: an objective's only definition is its
// English title, which <see cref="RuleOf"/> turns into a rule over the events (docs/server.md, "Missions").
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    enum Kind { Other, Harvest, Track, Spot, Arrive, Start }

    /// <summary>What an objective needs: its kind, the species (catalogue id, 0 to 99) and gender (0 male, 1 female) its title names (null: any; a title that names several species, a short
    /// name of some, or any animal but some, has a `Species` test instead), whether it counts only after the objectives before it, its title in
    /// <see cref="Normalise"/>d words (a place), and the conditions its title asks of the kill, the spot or the clue (<see cref="Reader"/>); `Unknown` when it
    /// has words the server does not understand: it is never met. `Near` is an arrive objective's point from the title's coordinates (the map's x and z) and the reserves it
    /// may be in (none listed: any).</summary>
    sealed record Rule(Kind Kind, long? Species, long? Gender, bool Ordered, string Text, Test[]? Tests = null, bool Unknown = false, string Left = "", (double X, double Z, long[] Reserves)? Near = null);

    sealed record Objective(long Id, string Title, Rule Rule);

    /// <summary>`Gm` and `Em` are the sums of the `N gm$` and `N em$` entries of `rewards`; the other entries (items, a trophy) are not paid.</summary>
    sealed record MissionDef(long Id, string Title, bool SingleExpedition, long[] Dependencies, long Gm, long Em, Objective[] Objectives);

    // `Then`, `Finally`, `First`, `Lastly`, `After that,` and `After spotting the Wolves,` (up to the comma) put the objective after the ones before it
    static readonly Regex OrderedWord = new(@"\A\s*(?:(?:then|finally|first|lastly)\b|after\b[^,:]*(?=[,:])|after\b)[\s,:-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // `At any point during the same hunt,` and `At any time,` say nothing; `At any point while in <reserve>,` names the reserve
    static readonly Regex AtAny = new(@"\A\s*at any (?:point|time)\b(?:\s+(?:(?:during|in|within|throughout)\s+(?:the\s+|a\s+)?(?:same|one|single)\s+(?:hunt|expedition|session)|while\s+in\s+(?<r>[^,:]+)))?\s*[,:]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex GoTo = new(@"\A(?:(?:head|go|travel)(?: over| out)? to|make it to)\b", RegexOptions.CultureInvariant);
    static readonly Regex OneHunt = new(@"\b(?:same|one|single)\s+(?:hunt|expedition|session|outing)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // the plurals of species names no `s` makes (`two Grey Wolves`, `5 Snow Geese`)
    static readonly (Regex Plural, string Name)[] Plurals = [(new(@"\bwolves\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "wolf"), (new(@"\bgeese\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "goose")];
    static readonly Regex TrackWord = new(@"\b(tracks?|droppings?|calls?|audio|blood|bedding|beds?|feeding|footprints?|footsteps?|clues?|scat)\b", RegexOptions.CultureInvariant);
    static readonly Regex Reward = new(@"\A\s*(\d+)\s*(gm|em)\$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly string[] Switches = ["auto_mission_activate", "activate_only_favourite", "hide_completed_favourite"];
    const string MissionsPath = "Mission_missions.json";

    readonly Dictionary<long, MissionDef> missions = [];
    readonly Dictionary<long, long[]> missionGroups = [];  // group id → its mission ids
    readonly Dictionary<long, string[]> lodgeNames = [];  // location id → its normalised name and define

    /// <summary>Lower case letters and digits in single-space words, accents removed: the form titles, names and defines are compared in.</summary>
    static string Normalise(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // words that name no place alone: the last word of a place is tried by itself only when it is none of these
    static readonly HashSet<string> CommonWords = ["lodge", "tower", "campsite", "camp", "site", "point", "bay", "lake", "pond", "creek", "river", "falls", "fall", "hill", "pass", "island", "bridge", "road", "area", "north", "south", "east", "west"];

    /// <summary>Does the title name the place (both <see cref="Normalise"/>d)? A define carries `HUNTER_GAME_LOCATION_` and its reserve in front and a number behind
    /// (`REGION_CALM_POND`): the place's own words match, then without leading ones, down to two words (one for a one-word place), with or without a final `s`; then its last word alone, unless
    /// it is a common word, shorter than 4 letters or a number.
    /// ponytail: a title that names only the place's tail matches any place with that tail (two hunting towers), or any place ending in the same uncommon word; a title with coordinates
    /// is also met near them (<see cref="ArrivesAt"/>).</summary>
    static bool PlaceIn(string title, string place)
    {
        var words = place.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 3 && words[0] == "hunter" && words[1] == "game" && words[2] == "location")
            words.RemoveRange(0, 3);
        while (words.Count > 1 && words[^1].All(char.IsAsciiDigit))
            words.RemoveAt(words.Count - 1);
        if (words.Count == 0)
            return false;
        var padded = " " + title + " ";
        bool Has(IEnumerable<string> w) => padded.Contains(" " + string.Join(' ', w) + " ", StringComparison.Ordinal);
        var last = words[^1];
        var other = last.EndsWith('s') ? last[..^1] : last + "s";  // a final `s` is optional
        for (var i = 0; i < Math.Max(1, words.Count - 1); i++)
            if (Has(words.Skip(i)) || Has(words.Skip(i).SkipLast(1).Append(other)))
                return true;
        return last.Length >= 4 && !last.All(char.IsAsciiDigit) && !CommonWords.Contains(last) && (Has([last]) || Has([other]));
    }

    /// <summary>The player entering a place (event 101, `direction` enter): its normalised name, its position in metres (the map's x and z) and the reserve of the hunt it is in (read when asked).</summary>
    sealed record Arrival(string Place, double? X, double? Z, Lazy<long?> Reserve);

    /// <summary>The arrival an event 101 stored as `eventId` is; null for a place left or an event without a name.</summary>
    static Arrival? ArrivalOf(Db con, long eventId, JsonObject e) =>
        Py.IsStr(e["direction"]) && (string)e["direction"]! == "enter" && Py.IsStr(e["location_name"]) && Normalise((string)e["location_name"]!) is { Length: > 0 } place
            ? new(place, NumOf(e, "player_x"), NumOf(e, "player_z"), new(() => ReserveAt(con, eventId))) : null;

    /// <summary>Does the arrival meet an arrive rule: the exact place of an operator's `arrive; place: NAME`, else the place the title names (<see cref="PlaceIn"/>) or, when the title
    /// has coordinates, a point within 200 m of them in one of the mission's reserves, whatever the place's name.</summary>
    static bool ArrivesAt(Rule r, Arrival a) =>
        r.Tests?.FirstOrDefault(t => t.Check == Check.Place) is { } at ? a.Place == Normalise(at.Place!)
        : PlaceIn(r.Text, a.Place) || r.Near is { } n && a.X is { } ax && a.Z is { } az && Math.Sqrt((ax - n.X) * (ax - n.X) + (az - n.Z) * (az - n.Z)) <= 200
            && (n.Reserves.Length == 0 || a.Reserve.Value is { } here && n.Reserves.Contains(here));

    /// <summary>Read the catalogue's missions and groups (a community server; none without a recorded catalogue).</summary>
    void LoadMissions()
    {
        var names = species.Where(s => s.Key < 100 && Py.IsStr(s.Value["name"]))  // a variant (100 and up) is its species'
            .Select(s => (Id: s.Key, Name: Normalise(((string)s.Value["name"]!).Split(" (")[0]))).Where(s => s.Name.Length > 0)
            .OrderByDescending(s => s.Name.Length).ToList();  // the longest name wins: "mule deer" before "deer"

        var towers = Arr(G(application, "locations", null)).OfType<JsonObject>()
            .Where(l => Py.IsStr(l["type"]) && (string)l["type"]! == "HUNTING_TOWER" && Py.IsStr(l["name"]) && Py.IsStr(l["define"]) && TryKey(l["reserve"], out _)
                        && l["pos"] is JsonObject pos && Py.IsNum(pos["x"]) && Py.IsNum(pos["z"]))
            .Select(l => (Py.L(l["reserve"]), Reader.Words((string)l["name"]!), (string)l["define"]!, Py.D(l["pos"]!["x"]), Py.D(l["pos"]!["z"])));
        var reader = new Reader(names, items.Values, reserves, towers);
        // a plural of a species name (`two Snowshoe Hares`, `two Red Foxes`) is read as the name
        var plural = names.Count == 0 ? null : new Regex(@"\b(" + string.Join('|', names.Select(n => Regex.Escape(n.Name))) + @")e?s\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        Rule RuleOf(string title, long[]? own = null, long[]? named = null, bool singleHunt = false, long[]? places = null)
        {
            var ordered = OrderedWord.Match(title);
            var rest = title[ordered.Length..];
            var lead = AtAny.Match(rest);
            rest = rest[lead.Length..];
            var body = lead.Groups["r"].Success ? rest.TrimEnd().TrimEnd('.') + " in " + lead.Groups["r"].Value : rest;  // the reserve is read like one at the end of the title
            var text = Normalise(body);
            var animals = body;  // the title as its animals are read, plurals as names (a place keeps its words)
            foreach (var (word, name) in Plurals)
                animals = word.Replace(animals, name);
            animals = plural?.Replace(animals, "$1") ?? animals;
            var first = text.Split(' ')[0];
            var kind = first switch
            {
                "harvest" or "take" or "kill" => Kind.Harvest,
                "spot" => Kind.Spot,
                "arrive" or "visit" or "walk" => Kind.Arrive,
                "head" or "go" or "travel" or "make" when GoTo.IsMatch(text) => Kind.Arrive,
                "start" => Kind.Start,
                "begin" when text.StartsWith("begin at ", StringComparison.Ordinal) => Kind.Start,
                "locate" or "id" or "identify" or "find" or "register" when TrackWord.IsMatch(text) => Kind.Track,
                _ => Kind.Other,
            };
            var words = " " + Normalise(animals);
            long? species = names.Where(x => words.Contains(" " + x.Name, StringComparison.Ordinal)).Select(x => (long?)x.Id).FirstOrDefault();
            long? gender = Reader.FemaleWord.IsMatch(text) ? (long?)1 : Reader.MaleWord.IsMatch(text) ? (long?)0 : null;
            Test[] tests = [];
            var left = "";
            if (kind is Kind.Harvest or Kind.Spot)
                (tests, left) = reader.Read(animals, own, named, kind == Kind.Harvest && (singleHunt || OneHunt.IsMatch(title)), places);  // a spot's ordinal says nothing, as before
            else if (kind == Kind.Track)
                (tests, left) = reader.ReadClue(animals, own);
            if (tests.Any(t => t.Check == Check.Species))
                species = null;  // several species, a short name or "any animal except ...": the test decides, not the first name found
            // a spot has no weapon, hit or harvest: only its stand, its reserve, its place, its species, its coat and its name can be checked
            if (kind == Kind.Spot)
                left = string.Join(' ', tests.Where(t => t.Check is not (Check.Stand or Check.Reserve or Check.Place or Check.Named or Check.Species or Check.Coat or Check.NotCoat)).Select(t => t.Check.ToString().ToLowerInvariant()).Prepend(left)).Trim();
            return new Rule(kind, species, gender, ordered.Success, text, tests, left.Length > 0, left, kind == Kind.Arrive && Reader.PointOf(body) is { } point ? (point.X, point.Z, places ?? []) : null);
        }

        foreach (var d in Arr(G(application, "missions", null)).OfType<JsonObject>())
        {
            if (!TryKey(d["id"], out var id))
                continue;
            long gm = 0, em = 0;
            foreach (var text in Arr(d["rewards"]).OfType<JsonObject>().Select(r => Py.IsStr(r["text"]) ? (string)r["text"]! : ""))
                if (Reward.Match(text) is { Success: true } paid && long.TryParse(paid.Groups[1].Value, out var amount))
                    if (paid.Groups[2].Value.Equals("gm", StringComparison.OrdinalIgnoreCase)) gm += amount;
                    else em += amount;
            var listed = Ids(d["species"]).Select(x => x % 100).Distinct().ToArray();
            var mentioned = reader.WeaponsIn(Py.Str(G(d, "desc", "")));  // the weapons the mission's text names
            Rule Build(string title)
            {
                var rule = RuleOf(title, listed, mentioned, Py.Truthy(d["singleExpedition"]), Ids(d["reserves"]));  // a named animal's title names no species: the mission's own one does
                return rule.Species is null && listed.Length == 1 && (rule.Tests ?? []).Any(t => t.Check == Check.Named) ? rule with { Species = listed[0] } : rule;
            }
            missions[id] = new MissionDef(id, Py.Str(G(d, "title", "")), Py.Truthy(d["singleExpedition"]), Ids(d["dependencies"]), gm, em,
                [.. Arr(d["objectives"]).OfType<JsonObject>().Where(o => TryKey(o["id"], out _))
                    .Select(o => new Objective(Py.L(o["id"]), Py.Str(G(o, "title", "")), Build(Py.Str(G(o, "title", ""))))).ToArray()]);
        }
        foreach (var g in Arr(G(application, "mission_groups", null)).OfType<JsonObject>().Where(g => TryKey(g["id"], out _)))
            missionGroups[Py.L(g["id"])] = Ids(g["missions"]);
        foreach (var loc in Arr(G(application, "locations", null)).OfType<JsonObject>().Where(l => TryKey(l["id"], out _)))
            lodgeNames[Py.L(loc["id"])] = [.. new[] { loc["name"], loc["define"] }.Where(Py.IsStr).Select(n => Normalise((string)n!)).Where(n => n.Length > 0)];
        LoadDailyMissions(title => RuleOf(title));
        LoadCommunity(title => RuleOf(title));
    }

    static long[] Ids(JsonNode? n) => [.. Arr(n).Where(x => TryKey(x, out _)).Select(Py.L).Distinct()];

    static JsonArray List(IEnumerable<long> ids) => new([.. ids.Select(i => (JsonNode?)i)]);

    // ---- the account's rows

    /// <summary>The account's mission rows and switches exist. The first time (no `mission_prefs` row) they come from the restored mission page
    /// (`Mission_missions.json` of `extras`): `completed` and `active` with the finished objectives of `states`, the three switches and the favourite
    /// mission packs; ids without a definition are kept as they are. `available` is never stored. A restored completed mission is marked as restored (`kept` is `*`), a restored
    /// active one keeps its finished objectives (`kept`), and the events up to `before` (default: the last one) are not the missions': they are older than the rows.
    /// Call inside a transaction.</summary>
    public static void EnsureMissions(Db con, long? before = null)
    {
        if (con.Scalar("SELECT 1 FROM mission_prefs WHERE account_id = $a") is not null)
            return;
        var prefs = new JsonObject { ["favourites"] = new JsonArray() };
        foreach (var key in Switches)
            prefs[key] = 0;
        if (con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = ?", MissionsPath) is string text && JsonNode.Parse(text) is JsonObject saved)
        {
            foreach (var key in Switches)
                prefs[key] = Py.Truthy(saved[key]) ? 1 : 0;
            prefs["favourites"] = List(Ids(saved["favourites"]));
            var (completed, seen) = (Ids(saved["completed"]), before ?? LastEvent(con));
            foreach (var id in completed)
                con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives, since, seen, kept) VALUES ($a, ?, 3, '[]', NULL, ?, ?)", id, seen, RestoredMark);
            foreach (var id in Ids(saved["active"]).Except(completed))
            {
                var done = Py.Dumps(List(Ids((saved["states"] as JsonObject)?[id.ToString()] is JsonObject st ? st["objectives"] : null)));
                con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives, since, seen, kept, earlier) VALUES ($a, ?, 2, ?, ?, ?, ?, ?)", id, done, seen, seen, done, done);
            }
        }
        con.Exec("INSERT INTO mission_prefs (account_id, body) VALUES ($a, ?)", Py.Dumps(prefs));
    }

    static JsonObject PrefsOf(Db con) => Parse(con.Scalar("SELECT body FROM mission_prefs WHERE account_id = $a")!);

    static void SetPref(Db con, string key, JsonNode value)
    {
        var prefs = PrefsOf(con);
        prefs[key] = value;
        con.Exec("UPDATE mission_prefs SET body = ? WHERE account_id = $a", Py.Dumps(prefs));
    }

    /// <summary>The rows by mission id: state (2 active, 3 completed) and the finished objective ids in the order they were finished.</summary>
    static Dictionary<long, (long State, List<long> Done)> MissionRows(Db con) =>
        con.Rows("SELECT mission_id, state, objectives FROM missions WHERE account_id = $a ORDER BY mission_id")
            .ToDictionary(r => (long)r[0], r => ((long)r[1], Ids(JsonNode.Parse((string)r[2])).ToList()));

    /// <summary>The missions with a definition that are not started and whose dependencies are all completed.</summary>
    IEnumerable<MissionDef> Available(Dictionary<long, (long State, List<long> Done)> rows) =>
        missions.Values.Where(m => !rows.ContainsKey(m.Id) && m.Dependencies.All(d => rows.TryGetValue(d, out var r) && r.State == 3)).OrderBy(m => m.Id);

    /// <summary>What a row remembers besides its objectives: `Since`, the last event before the mission was activated here (null: not known, the row is older than schema 30);
    /// `Seen`, the last event the mission's progress is not owed from (the harvests after it are the ones to check); `Kept`, the finished objectives a restore brought
    /// (a JSON list), or `*` for a mission completed before the restore: those are never checked or reopened, no event of theirs exists; `Earlier`, the objectives finished
    /// before `Seen` (a JSON list): no harvest after it finished them.</summary>
    sealed record Mark(long? Since, long Seen, string Kept, string Earlier)
    {
        public bool Restored => Kept == RestoredMark;
        public long[] KeptIds => Restored ? [] : Ids(JsonNode.Parse(Kept));
        public long[] EarlierIds => Ids(JsonNode.Parse(Earlier));
    }

    const string RestoredMark = "*";

    static Dictionary<long, Mark> Marks(Db con) =>
        con.Rows("SELECT mission_id, since, seen, kept, earlier FROM missions WHERE account_id = $a")
            .ToDictionary(r => (long)r[0], r => new Mark(r[1] as long?, (long)r[2], (string)r[3], (string)r[4]));

    /// <summary>The mission becomes active; its progress counts the events after `at` (default: the last one stored).</summary>
    static void Activate(Db con, long id, long? at = null)
    {
        var seen = at ?? LastEvent(con);
        con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives, since, seen) VALUES ($a, ?, 2, '[]', ?, ?)", id, seen, seen);
    }

    // ---- the replies

    /// <summary>Mission/missions of the account; null when the catalogue has no missions (the empty reply of ReplayScope stands).</summary>
    public byte[]? MissionsReply(long account)
    {
        if (missions.Count == 0)
            return null;
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var prefs = PrefsOf(con);
        con.Commit();
        var available = Available(rows).Select(m => m.Id).ToList();
        var states = new JsonObject();
        foreach (var id in available)
            states[id.ToString()] = new JsonObject { ["state"] = 1 };
        foreach (var (id, (state, done)) in rows)
            states[id.ToString()] = state == 3 ? new JsonObject { ["state"] = 3 } : new JsonObject { ["state"] = 2, ["objectives"] = List(done) };
        var reply = new JsonObject
        {
            ["available"] = List(available), ["active"] = List(rows.Where(r => r.Value.State == 2).Select(r => r.Key)),
            ["completed"] = List(rows.Where(r => r.Value.State == 3).Select(r => r.Key)), ["states"] = states,
        };
        foreach (var key in Switches)
            reply[key] = prefs[key]!.DeepClone();
        reply["favourites"] = prefs["favourites"]!.DeepClone();
        return Py.DumpBytes(reply);
    }

    /// <summary>One of the page's writes (`activate`, `activate_all_available`, `update_automatic_activation`, `update_activate_only_favourite`,
    /// `update_hide_completed_favourite`, `update_favourite`): true when it was done, false for a mission that is not available or a group id that is
    /// no number; null when the catalogue has no missions.</summary>
    public bool? MissionCall(long account, string call, byte[] formBody)
    {
        if (missions.Count == 0)
            return null;
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        static bool Yes(string v) => v is "true" or "1";
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var ok = true;
        switch (call)
        {
            case "activate":
                ok = long.TryParse(Form1(form, "mission_id"), out var id) && Available(rows).Any(m => m.Id == id);
                if (ok)
                    Activate(con, id);
                break;
            case "activate_all_available":
                foreach (var m in Available(rows))
                    Activate(con, m.Id);
                break;
            case "update_favourite":
                ok = long.TryParse(Form1(form, "group_id"), out var group);
                if (ok)
                {
                    var favourites = Ids(PrefsOf(con)["favourites"]).Where(g => g != group);
                    SetPref(con, "favourites", List(Yes(Form1(form, "is_favourite")) ? favourites.Append(group) : favourites));
                }
                break;
            default:
                var key = call["update_".Length..] switch { "automatic_activation" => Switches[0], var other => other };
                SetPref(con, key, Yes(Form1(form, key)) ? 1 : 0);
                break;
        }
        con.Commit();
        return ok;
    }

    static readonly Regex Tag = new("<[^>]*>", RegexOptions.CultureInvariant), Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    static string Unmarked(string html) => WebUtility.HtmlDecode(Spaces.Replace(Tag.Replace(html, " "), " ").Trim());

    /// <summary>The website's missions page (GET /account/missions): `missions` (every definition: id, title, desc without its tags, state `active`, `available`, `completed` or `locked`,
    /// objective titles and `objectives_done` (one flag per objective: the account holds it, all for a completed one), `needs` (the titles of the missions it depends on that are not completed yet, `#<id>` for one with no definition: why a `locked` one is locked; a restored active or completed mission may carry some too), reward text, and the first reserve, species and weapon name its filter lists name, "" for none), `groups` (id, title, desc, favourite, mission ids in order),
    /// `completed`, and the three switches. Null when the catalogue has no missions.</summary>
    public JsonObject? WebMissions(long account)
    {
        if (missions.Count == 0)
            return null;
        using var con = Con(account);
        con.Begin();
        EnsureMissions(con);
        var rows = MissionRows(con);
        var prefs = PrefsOf(con);
        con.Commit();
        var available = Available(rows).Select(m => m.Id).ToHashSet();
        string First(JsonNode? ids, Func<long, string?> name) => Ids(ids).Select(name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";
        string Named(Dictionary<long, JsonObject> from, long id) => from.GetValueOrDefault(id) is { } o ? Py.Str(G(o, "name", "")).Split(" (")[0] : "";
        var favourites = Ids(prefs["favourites"]);
        var list = new JsonArray([.. Arr(G(application, "missions", null)).OfType<JsonObject>().Where(d => TryKey(d["id"], out _) && missions.ContainsKey(Py.L(d["id"]))).Select(d =>
        {
            var id = Py.L(d["id"]);
            return (JsonNode?)new JsonObject
            {
                ["id"] = id, ["title"] = Py.Str(G(d, "title", "")), ["desc"] = Unmarked(Py.Str(G(d, "desc", ""))),
                ["state"] = rows.TryGetValue(id, out var r) ? r.State == 3 ? "completed" : "active" : available.Contains(id) ? "available" : "locked",
                ["objectives"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)o.Title)]),
                // beside `objectives` (titles), not inside it: a launcher of an earlier release reads `objectives` as strings
                ["objectives_done"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)(rows.TryGetValue(id, out var row) && (row.State == 3 || row.Done.Contains(o.Id))))]),
                ["needs"] = new JsonArray([.. missions[id].Dependencies.Where(n => !(rows.TryGetValue(n, out var done) && done.State == 3)).Select(n => (JsonNode?)(missions.TryGetValue(n, out var dep) ? dep.Title : $"#{n}"))]),
                ["reward"] = string.Join(", ", Arr(d["rewards"]).OfType<JsonObject>().Select(x => Py.Str(G(x, "text", "")))),
                ["reserve"] = First(d["reserves"], i => reserves.GetValueOrDefault(i)), ["species"] = First(d["species"], i => Named(species, i)),
                ["weapon"] = First(d["weapons"], i => Named(items, i)),
            };
        })]);
        var groups = new JsonArray([.. Arr(G(application, "mission_groups", null)).OfType<JsonObject>().Where(g => TryKey(g["id"], out _)).Select(g => (JsonNode?)new JsonObject
        {
            ["id"] = Py.L(g["id"]), ["title"] = Py.Str(G(g, "title", "")), ["desc"] = Unmarked(Py.Str(G(g, "desc", ""))),
            ["favourite"] = favourites.Contains(Py.L(g["id"])), ["missions"] = List(Ids(g["missions"])),
        })]);
        var reply = new JsonObject { ["missions"] = list, ["groups"] = groups, ["completed"] = rows.Count(r => r.Value.State == 3 && missions.ContainsKey(r.Key)) };
        foreach (var key in Switches)
            reply[key] = Py.Truthy(prefs[key]);
        return reply;
    }

    // ---- progress

    /// <summary>The hunt's event as the missions see it (called by Heartbeat after each stored event): a harvest (3), a spotted animal (11), a clue (12) or a
    /// place entered (101) finishes the first objective of each active mission that it meets. update 15 goes into `updates` (this heartbeat's reply),
    /// the toasts into `pending` (the next one's: after the trophy score update 6 of the harvest).</summary>
    void MissionEvent(Db con, JsonObject e, JsonArray updates)
    {
        var kind = Is(e["event"], 3) ? Kind.Harvest : Is(e["event"], 11) ? Kind.Spot : Is(e["event"], 12) ? Kind.Track : Is(e["event"], 101) ? Kind.Arrive : Kind.Other;
        if (kind == Kind.Other || (missions.Count == 0 && dailies.Count == 0 && community.Count == 0))
            return;
        var eventId = LastEvent(con);  // the event itself: the missions it meets are those activated before it
        EnsureMissions(con, eventId - 1);
        long? animalSpecies = null, gender = null;
        Kill? kill = null;
        if (kind != Kind.Arrive)
        {
            var animal = SpawnBefore(con, e["individual"], eventId);
            (animalSpecies, gender) = AnimalOf(animal, kind == Kind.Harvest ? e["extended_species"] : animal["species"]);
            kill = kind == Kind.Harvest ? KillOf(con, e, eventId, animal) : kind == Kind.Spot ? SpotOf(con, e, eventId, animal) : ClueOf(con, eventId, e, animal);
        }
        var arrival = ArrivalOf(con, eventId, e);
        Advance(con, kind, (rule, after) =>
            kind == Kind.Arrive ? arrival is not null && ArrivesAt(rule, arrival)
            : (rule.Species is null || rule.Species == animalSpecies) && (rule.Gender is null || rule.Gender == gender) && (kill is null || Meets(rule, kill, after)),
            u =>
            {
                // one update 15 per currency in a reply, as the game's server sends: a kill's css and a mission's reward add into the last balance
                if (updates.OfType<JsonObject>().LastOrDefault(x => Is(x["update"], 15) && Py.Str(x["type"]) == Py.Str(u["type"])) is { } same)
                    same["amount"] = u["amount"]!.DeepClone();
                else
                    updates.Add(u);
            }, eventId);
    }

    /// <summary>A hunt starts at location `lodge` (Launch/start, inside its transaction): what a single-hunt mission had done is forgotten, then the
    /// `Start at ...` objectives of the lodge are met. update 15 goes into `pending`.</summary>
    void MissionLaunch(Db con, long? lodge)
    {
        if (missions.Count == 0 && dailies.Count == 0 && community.Count == 0)
            return;
        EnsureMissions(con);
        foreach (var (id, (state, done)) in MissionRows(con))
            if (state == 2 && done.Count > 0 && missions.TryGetValue(id, out var m) && m.SingleExpedition)
                con.Exec("UPDATE missions SET objectives = '[]', kept = '[]', earlier = '[]' WHERE account_id = $a AND mission_id = ?", id);
        var names = lodge is { } l && lodgeNames.TryGetValue(l, out var n) ? n : [];
        Advance(con, Kind.Start, (rule, _) => names.Any(name => PlaceIn(rule.Text, name)), u => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(u)), long.MaxValue);
    }

    /// <summary>Finish the first unfinished objective of this kind that `meets` accepts (an ordered objective only once every one before it is finished; `after` is the last event the
    /// progress is not owed from): its id is added to `done`, and it is returned; null when none is met.</summary>
    static Objective? Meet(MissionDef m, List<long> done, Kind kind, Func<Rule, long, bool> meets, long after = 0)
    {
        for (var i = 0; i < m.Objectives.Length; i++)
        {
            var o = m.Objectives[i];
            if (o.Rule.Kind != kind || done.Contains(o.Id) || (o.Rule.Ordered && m.Objectives[..i].Any(p => !done.Contains(p.Id))) || !meets(o.Rule, after))
                continue;
            done.Add(o.Id);
            return o;
        }
        return null;
    }

    /// <summary>For each active mission that was activated before the event `eventId`, finish its first unfinished objective of this kind that `meets` accepts; then the same
    /// for today's daily mission.</summary>
    void Advance(Db con, Kind kind, Func<Rule, long, bool> meets, Action<JsonObject> now, long eventId)
    {
        var marks = Marks(con);
        foreach (var (id, (state, done)) in MissionRows(con))
        {
            if (state != 2 || !missions.TryGetValue(id, out var m) || marks[id].Seen >= eventId || Meet(m, done, kind, meets, marks[id].Seen) is not { } o)
                continue;
            if (m.Objectives.Any(x => !done.Contains(x.Id)))
            {
                con.Exec("UPDATE missions SET objectives = ? WHERE account_id = $a AND mission_id = ?", Py.Dumps(List(done)), m.Id);
                Toast(con, $"Mission \"{m.Title}\", objective \"{o.Title}\" completed.");
            }
            else
                Complete(con, m, now);
        }
        DailyAdvance(con, kind, meets, now);
        CommunityCount(con, kind, rule => meets(rule, 0));
    }

    /// <summary>The last objective is finished: the mission is completed, its gm$ and em$ are paid (update 15 with the new balance, a wallet row each),
    /// the toast follows, and the missions it unlocks are activated when the account's switches say so (counting the events after `at`, default the last one).</summary>
    void Complete(Db con, MissionDef m, Action<JsonObject> now, long? at = null)
    {
        con.Exec("UPDATE missions SET state = 3, objectives = '[]' WHERE account_id = $a AND mission_id = ?", m.Id);
        foreach (var (currency, amount) in new[] { ("gm", m.Gm), ("em", m.Em) })
            if (amount > 0 && AddCurrency(con, currency, amount, $"{currency}$ reward") is { } balance)
                now(new JsonObject { ["amount"] = balance, ["type"] = currency, ["update"] = 15 });
        Toast(con, $"Mission {m.Title} completed.");
        var prefs = PrefsOf(con);
        var (auto, onlyFavourite) = (Py.Truthy(prefs["auto_mission_activate"]), Py.Truthy(prefs["activate_only_favourite"]));
        if (!auto && !onlyFavourite)
            return;
        var favourites = Ids(prefs["favourites"]);
        foreach (var next in Available(MissionRows(con)).Where(n => n.Dependencies.Contains(m.Id)).ToList())
            if (!onlyFavourite || missionGroups.Any(g => favourites.Contains(g.Key) && g.Value.Contains(next.Id)))
                Activate(con, next.Id, at);
    }

    void Toast(Db con, string text) => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject
    {
        ["text"] = text, ["colour"] = COLOUR, ["destination"] = MediaUrl("doc_64.jpg"), ["update"] = 3, ["timer"] = -1, ["type"] = 100,
    }));
}
