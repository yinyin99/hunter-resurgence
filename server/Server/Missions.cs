// Missions on a community server: the catalogue's definitions (`missions[]`, `mission_groups[]` of the recorded application), the account's
// states (tables `missions` and `mission_prefs`, filled from a restored `Mission_missions.json` at the restore and on first use), the
// replies of Mission/*, and the progress the hunt's events make. The game keeps no mission state: an objective's only definition is its
// English title, which <see cref="RuleOf"/> turns into a rule over the events (docs/server.md, "Missions").
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    enum Kind { Other, Harvest, Track, Spot, Arrive, Start }

    /// <summary>What an objective needs: its kind, the species (catalogue id, 0 to 99) and gender (0 male, 1 female) its title names (null: any), how many
    /// clues of one animal (tracks), whether it counts only after the objectives before it, and its title in <see cref="Normalise"/>d words (a place).</summary>
    sealed record Rule(Kind Kind, long? Species, long? Gender, int Count, bool Ordered, string Text);

    sealed record Objective(long Id, string Title, Rule Rule);

    /// <summary>`Gm` and `Em` are the sums of the `N gm$` and `N em$` entries of `rewards`; the other entries (items, a trophy) are not paid.</summary>
    sealed record MissionDef(long Id, string Title, bool SingleExpedition, long[] Dependencies, long Gm, long Em, Objective[] Objectives);

    static readonly Regex OrderedWord = new(@"\A\s*(then|finally|first|after)\b[\s,:-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex TrackWord = new(@"\b(tracks?|droppings?|calls?|blood|bedding|feeding|footprints?|footsteps?|clues?|scat)\b", RegexOptions.CultureInvariant);
    static readonly Regex ClueCount = new(@"\b(\d+) (?:\w+ ){0,4}?(?:tracks?|droppings?|clues?|footprints?|footsteps?)\b", RegexOptions.CultureInvariant);
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

    /// <summary>Does the title name the place (both <see cref="Normalise"/>d)? A define carries its reserve in front and a number behind
    /// (`REGION_CALM_POND`): the place's own words match, then without leading ones, down to two words (one for a one-word place).
    /// ponytail: a title that names only the place's tail matches any place with that tail (two hunting towers); the title's coordinates are not used.</summary>
    static bool PlaceIn(string title, string place)
    {
        var words = place.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[^1].All(char.IsAsciiDigit))
            words.RemoveAt(words.Count - 1);
        for (var i = 0; words.Count > 0 && i < Math.Max(1, words.Count - 1); i++)
            if ((" " + title + " ").Contains(" " + string.Join(' ', words.Skip(i)) + " ", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Read the catalogue's missions and groups (a community server; none without a recorded catalogue).</summary>
    void LoadMissions()
    {
        var names = species.Where(s => s.Key < 100 && Py.IsStr(s.Value["name"]))  // a variant (100 and up) is its species'
            .Select(s => (Id: s.Key, Name: Normalise(((string)s.Value["name"]!).Split(" (")[0]))).Where(s => s.Name.Length > 0)
            .OrderByDescending(s => s.Name.Length).ToList();  // the longest name wins: "mule deer" before "deer"

        Rule RuleOf(string title)
        {
            var ordered = OrderedWord.Match(title);
            var text = Normalise(title[ordered.Length..]);
            var first = text.Split(' ')[0];
            var kind = first switch
            {
                "harvest" => Kind.Harvest,
                "spot" => Kind.Spot,
                "arrive" or "visit" or "walk" => Kind.Arrive,
                "start" => Kind.Start,
                "locate" or "id" or "identify" or "find" when TrackWord.IsMatch(text) => Kind.Track,
                _ => Kind.Other,
            };
            long? species = names.Where(x => (" " + text).Contains(" " + x.Name, StringComparison.Ordinal)).Select(x => (long?)x.Id).FirstOrDefault();
            long? gender = Regex.IsMatch(text, @"\bfemale\b") ? (long?)1 : Regex.IsMatch(text, @"\bmale\b") ? (long?)0 : null;
            var count = kind == Kind.Track && ClueCount.Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var clues) ? Math.Clamp(clues, 1, 100) : 1;
            return new Rule(kind, species, gender, count, ordered.Success, text);
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
            missions[id] = new MissionDef(id, Py.Str(G(d, "title", "")), Py.Truthy(d["singleExpedition"]), Ids(d["dependencies"]), gm, em,
                [.. Arr(d["objectives"]).OfType<JsonObject>().Where(o => TryKey(o["id"], out _))
                    .Select(o => new Objective(Py.L(o["id"]), Py.Str(G(o, "title", "")), RuleOf(Py.Str(G(o, "title", ""))))).ToArray()]);
        }
        foreach (var g in Arr(G(application, "mission_groups", null)).OfType<JsonObject>().Where(g => TryKey(g["id"], out _)))
            missionGroups[Py.L(g["id"])] = Ids(g["missions"]);
        foreach (var loc in Arr(G(application, "locations", null)).OfType<JsonObject>().Where(l => TryKey(l["id"], out _)))
            lodgeNames[Py.L(loc["id"])] = [.. new[] { loc["name"], loc["define"] }.Where(Py.IsStr).Select(n => Normalise((string)n!)).Where(n => n.Length > 0)];
    }

    static long[] Ids(JsonNode? n) => [.. Arr(n).Where(x => TryKey(x, out _)).Select(Py.L).Distinct()];

    static JsonArray List(IEnumerable<long> ids) => new([.. ids.Select(i => (JsonNode?)i)]);

    // ---- the account's rows

    /// <summary>The account's mission rows and switches exist. The first time (no `mission_prefs` row) they come from the restored mission page
    /// (`Mission_missions.json` of `extras`): `completed` and `active` with the finished objectives of `states`, the three switches and the favourite
    /// groups; ids without a definition are kept as they are. `available` is never stored. Call inside a transaction.</summary>
    public static void EnsureMissions(Db con)
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
            var completed = Ids(saved["completed"]);
            foreach (var id in completed)
                con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives) VALUES ($a, ?, 3, '[]')", id);
            foreach (var id in Ids(saved["active"]).Except(completed))
                con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives) VALUES ($a, ?, 2, ?)", id,
                         Py.Dumps(List(Ids((saved["states"] as JsonObject)?[id.ToString()] is JsonObject st ? st["objectives"] : null))));
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

    static void Activate(Db con, long id) =>
        con.Exec("INSERT INTO missions (account_id, mission_id, state, objectives) VALUES ($a, ?, 2, '[]')", id);

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

    static string Plain(string html) => Spaces.Replace(Tag.Replace(html, " "), " ").Trim();

    /// <summary>The website's missions page (GET /account/missions): `missions` (every definition: id, title, desc without its tags, state `active`, `available`, `completed` or `locked`,
    /// objective titles, reward text, and the first reserve, species and weapon name its filter lists name, "" for none), `groups` (id, title, desc, favourite, mission ids in order),
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
                ["id"] = id, ["title"] = Py.Str(G(d, "title", "")), ["desc"] = Plain(Py.Str(G(d, "desc", ""))),
                ["state"] = rows.TryGetValue(id, out var r) ? r.State == 3 ? "completed" : "active" : available.Contains(id) ? "available" : "locked",
                ["objectives"] = new JsonArray([.. missions[id].Objectives.Select(o => (JsonNode?)o.Title)]),
                ["reward"] = string.Join(", ", Arr(d["rewards"]).OfType<JsonObject>().Select(x => Py.Str(G(x, "text", "")))),
                ["reserve"] = First(d["reserves"], i => reserves.GetValueOrDefault(i)), ["species"] = First(d["species"], i => Named(species, i)),
                ["weapon"] = First(d["weapons"], i => Named(items, i)),
            };
        })]);
        var groups = new JsonArray([.. Arr(G(application, "mission_groups", null)).OfType<JsonObject>().Where(g => TryKey(g["id"], out _)).Select(g => (JsonNode?)new JsonObject
        {
            ["id"] = Py.L(g["id"]), ["title"] = Py.Str(G(g, "title", "")), ["desc"] = Plain(Py.Str(G(g, "desc", ""))),
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
        if (kind == Kind.Other || missions.Count == 0)
            return;
        EnsureMissions(con);
        long? animalSpecies = null, gender = null;
        if (kind != Kind.Arrive)
        {
            var animal = kind == Kind.Harvest ? KillEvents(con, e["individual"], Py.Dumps(e)).E7
                : con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 7 AND individual = ? ORDER BY id DESC LIMIT 1", e["individual"])
                    .Select(r => Parse(r[0])).FirstOrDefault() ?? new JsonObject();
            var number = kind == Kind.Harvest ? e["extended_species"] : animal["species"];
            (animalSpecies, gender) = (Py.IsNum(number) ? Mod100(number) : null, Py.IsNum(animal["gender"]) ? Py.L(animal["gender"]) : null);
        }
        var place = Py.IsStr(e["location_name"]) ? Normalise((string)e["location_name"]!) : "";
        Advance(con, kind, rule =>
            kind == Kind.Arrive ? Py.IsStr(e["direction"]) && (string)e["direction"]! == "enter" && place.Length > 0 && PlaceIn(rule.Text, place)
            : (rule.Species is null || rule.Species == animalSpecies) && (rule.Gender is null || rule.Gender == gender)
              && (kind != Kind.Track || rule.Count <= 1 || (long)con.Scalar(
                    "SELECT COUNT(*) FROM events WHERE account_id = $a AND event = 12 AND individual = ? AND id > COALESCE("
                    + "(SELECT first_event FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1), 0)", e["individual"])! >= rule.Count),
            u =>
            {
                // one update 15 per currency in a reply, as the game's server sends: a kill's css and a mission's reward add into the last balance
                if (updates.OfType<JsonObject>().LastOrDefault(x => Is(x["update"], 15) && Py.Str(x["type"]) == Py.Str(u["type"])) is { } same)
                    same["amount"] = u["amount"]!.DeepClone();
                else
                    updates.Add(u);
            });
    }

    /// <summary>A hunt starts at location `lodge` (Launch/start, inside its transaction): what a single-hunt mission had done is forgotten, then the
    /// `Start at ...` objectives of the lodge are met. update 15 goes into `pending`.</summary>
    void MissionLaunch(Db con, long? lodge)
    {
        if (missions.Count == 0)
            return;
        EnsureMissions(con);
        foreach (var (id, (state, done)) in MissionRows(con))
            if (state == 2 && done.Count > 0 && missions.TryGetValue(id, out var m) && m.SingleExpedition)
                con.Exec("UPDATE missions SET objectives = '[]' WHERE account_id = $a AND mission_id = ?", id);
        var names = lodge is { } l && lodgeNames.TryGetValue(l, out var n) ? n : [];
        Advance(con, Kind.Start, rule => names.Any(name => PlaceIn(rule.Text, name)), u => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(u)));
    }

    /// <summary>For each active mission, finish its first unfinished objective of this kind that `meets` accepts; an ordered objective only once every one
    /// before it is finished.</summary>
    void Advance(Db con, Kind kind, Func<Rule, bool> meets, Action<JsonObject> now)
    {
        foreach (var (id, (state, done)) in MissionRows(con))
        {
            if (state != 2 || !missions.TryGetValue(id, out var m))
                continue;
            for (var i = 0; i < m.Objectives.Length; i++)
            {
                var o = m.Objectives[i];
                if (o.Rule.Kind != kind || done.Contains(o.Id) || (o.Rule.Ordered && m.Objectives[..i].Any(p => !done.Contains(p.Id))) || !meets(o.Rule))
                    continue;
                done.Add(o.Id);
                if (m.Objectives.Any(x => !done.Contains(x.Id)))
                {
                    con.Exec("UPDATE missions SET objectives = ? WHERE account_id = $a AND mission_id = ?", Py.Dumps(List(done)), m.Id);
                    Toast(con, $"Mission \"{m.Title}\", objective \"{o.Title}\" completed.");
                }
                else
                    Complete(con, m, now);
                break;
            }
        }
    }

    /// <summary>The last objective is finished: the mission is completed, its gm$ and em$ are paid (update 15 with the new balance, a wallet row each),
    /// the toast follows, and the missions it unlocks are activated when the account's switches say so.</summary>
    void Complete(Db con, MissionDef m, Action<JsonObject> now)
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
                Activate(con, next.Id);
    }

    void Toast(Db con, string text) => con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject
    {
        ["text"] = text, ["colour"] = COLOUR, ["destination"] = $"{avatarBase}/doc_64.jpg", ["update"] = 3, ["timer"] = -1, ["type"] = 100,
    }));
}
