// Challenges on a community server: the definitions of the recorded `Challenge/get_challenges` list, the account's completed ones (table `challenges`,
// filled from a restored `Challenge_get_user_challenges` page at the restore), the reply of Challenge/get_user_challenges and the completions the
// hunt's events make. A definition's rule is its `conditions` string: a Python expression over the animal `a`, the events `e` and the constants `c`.
// <see cref="RuleParser"/> reads the subset the definitions use, <see cref="Hunt"/> answers its calls from the account's events (docs/server.md, "Challenges").
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    delegate object? Expr(Hunt h);

    /// <summary>`Objectives` is the definition's `objectives` text as recorded; `Rules` the objectives that can be evaluated: the heartbeat event that tries it, its index in the list, its condition.</summary>
    sealed record ChallengeDef(long Id, string Message, string Objectives, string Hash, (long Event, int Index, Expr Rule)[] Rules);

    static readonly Dictionary<string, long> EventTypes = new() { ["KILL"] = 2, ["CONFIRMKILL"] = 3, ["SHOT_HIT"] = 1, ["CLUE"] = 12, ["COLLECTABLE"] = 24 };

    // the constants of the tables with no catalogue entry: animal states, organ bits, textures and clue types
    static readonly Dictionary<string, long> FixedConstants = new()
    {
        ["state.FLYING"] = 6, ["state.FLYING_NOT_SPOOKED"] = 11, ["organ.HEART"] = 1, ["texture.ALBINO"] = 2, ["clue.BLOODTRAIL"] = 0,
    };

    readonly Dictionary<long, ChallengeDef> challenges = [];

    /// <summary>Read the recorded challenge list (a community server; none without a recording). An objective whose event type or condition is not understood is left out: it never completes.</summary>
    void LoadChallenges(JsonArray? list)
    {
        var byDefine = new Dictionary<string, long>();
        foreach (var (id, item) in items.Where(i => Py.IsStr(i.Value["define"])))
            byDefine[(string)item["define"]!] = id;
        var categories = Arr(G(application, "collectables", null)).OfType<JsonObject>().Where(c => TryKey(c["id"], out _) && Py.IsStr(c["cat"]))
            .GroupBy(c => (string)c["cat"]!).ToDictionary(g => g.Key, g => g.Select(c => Py.L(c["id"])).ToArray());

        object Constant(string word)
        {
            var p = word.Split('.');
            var (table, name) = p.Length == 3 && p[0] == "c" ? (p[1], p[2]) : p.Length == 1 ? ("item", p[0]) : throw new FormatException(word);  // a bare name is an item define
            if (FixedConstants.TryGetValue($"{table}.{name}", out var fixedValue))
                return fixedValue;
            return table switch
            {
                "species" or "variant" when speciesIds.TryGetValue(name, out var s) => s,
                "item" when byDefine.TryGetValue(name, out var i) => i,
                "collectableCategory" when categories.TryGetValue(name, out var ids) => ids,
                _ => throw new FormatException(word),
            };
        }

        foreach (var d in Arr(list).OfType<JsonObject>().Where(d => TryKey(d["id"], out _)))
        {
            var text = Py.IsStr(d["objectives"]) ? (string)d["objectives"]! : Py.Dumps(d["objectives"]);  // the page sends a JSON text; a recording may hold the list itself
            JsonNode? objectives;
            try { objectives = JsonNode.Parse(text); }
            catch (JsonException) { continue; }
            var rules = new List<(long, int, Expr)>();
            for (var i = 0; i < Arr(objectives).Count; i++)
                if (objectives![i] is JsonObject o && Py.IsStr(o["event_type"]) && EventTypes.TryGetValue((string)o["event_type"]!, out var kind)
                    && o["objective_data"] is JsonObject data && Py.IsStr(data["conditions"]))
                    try { rules.Add((kind, i, RuleParser.Parse((string)data["conditions"]!, Constant))); }
                    catch (FormatException) { }
            challenges[Py.L(d["id"])] = new(Py.L(d["id"]),
                Py.IsStr(d["update_message"]) ? (string)d["update_message"]! : $"Challenge: {Py.Str(G(d, "title", ""))} completed", text, Py.Str(G(d, "objectives_hash", "")), [.. rules]);
        }
    }

    // ---- the reply

    /// <summary>Challenge/get_user_challenges for a user_id: on a community server any account's (<see cref="PublicAccount(long)"/>), null for a user id that is none
    /// (the empty reply of ReplayScope stands) and on a local server.</summary>
    public byte[]? ChallengesReply(long account, byte[] formBody) => Online ? PublicReply(account, formBody, ChallengeRows) : null;

    /// <summary>The account's completed challenges by challenge id, as the game's API sends its rows; a challenge never completed has no entry.</summary>
    static JsonNode ChallengeRows(Db con)
    {
        var reply = new JsonObject();
        foreach (var r in con.Rows("SELECT id, challenge_id, objectives, objectives_hash, challenge_date FROM challenges WHERE account_id = $a ORDER BY challenge_id"))
            reply[Py.Str(Py.FromSql(r[1]))] = new JsonObject
            {
                ["id"] = (long)r[0], ["user_id"] = con.Account, ["challenge_id"] = (long)r[1], ["objectives"] = (string)r[2], ["objectives_hash"] = (string)r[3],
                ["completed"] = 1, ["challenge_date"] = Py.FromSql(r[4]),
            };
        return reply;
    }

    // ---- progress

    /// <summary>The hunt's event as the challenges see it (called by Heartbeat after each stored event): each challenge not yet completed that has an objective for this
    /// event is evaluated, and one whose condition holds is completed: the row is written (no wallet change) and its announcement (`update 3`) queued for the next heartbeat.
    /// ponytail: a challenge completes with its first met objective (every definition has one); several objectives would need a stored progress.</summary>
    void ChallengeEvent(Db con, JsonObject e)
    {
        var open = challenges.Values.Where(d => d.Rules.Any(r => Is(e["event"], r.Event))).ToList();
        if (open.Count == 0)
            return;
        var done = con.Rows("SELECT challenge_id FROM challenges WHERE account_id = $a").Select(r => (long)r[0]).ToHashSet();
        var hunt = new Hunt(this, con, e, LastEvent(con), Clock());
        foreach (var d in open.Where(d => !done.Contains(d.Id)))
            foreach (var (_, index, rule) in d.Rules.Where(r => Is(e["event"], r.Event)))
            {
                bool met;
                try { met = RuleParser.Truth(rule(hunt)); }
                catch (FormatException) { met = false; }  // a call with arguments it cannot read
                if (!met)
                    continue;
                var objectives = JsonNode.Parse(d.Objectives)!.AsArray();
                if (objectives[index]!["objective_data"] is JsonObject data)
                    data["completed"] = true;
                con.Exec("INSERT OR IGNORE INTO challenges (account_id, challenge_id, objectives, objectives_hash, challenge_date) VALUES ($a, ?, ?, ?, ?)",
                         d.Id, Py.Dumps(objectives), d.Hash, DateTimeOffset.FromUnixTimeSeconds((long)Clock()).ToString("yyyy-MM-dd"));
                Toast(con, d.Message);
                break;
            }
    }

    /// <summary>One animal's death: its row id, receive time, species (0 to 99) and the shot that killed it (null when none is known).</summary>
    sealed record Death(long Id, double Ts, long Species, long? Shot);

    /// <summary>What a condition's `a` (the animal of the event) and `e` (the events of the hunt) answer, read from the account's stored events up to the current one.
    /// Distances are millimetres; a number a call cannot read is -1.</summary>
    sealed class Hunt(State s, Db con, JsonObject e, long id, double now)
    {
        (JsonObject? Spawn, List<JsonObject> Hits)? own;
        long? first;

        /// <summary>The latest spawn (event 7) of the individual before row `upTo` and the hits (event 1) on it since.</summary>
        (JsonObject? Spawn, List<JsonObject> Hits) Animal(JsonNode? individual, long upTo)
        {
            var spawn = con.Rows("SELECT id, body FROM events WHERE account_id = $a AND event = 7 AND individual = ? AND id < ? ORDER BY id DESC LIMIT 1", individual, upTo).FirstOrDefault();
            var hits = con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 1 AND individual = ? AND id < ? AND id > ? ORDER BY id",
                                individual, upTo, spawn?[0] ?? 0L).Select(r => Parse(r[0])).ToList();
            return (spawn is null ? null : Parse(spawn[1]), hits);
        }

        (JsonObject? Spawn, List<JsonObject> Hits) Own => own ??= Animal(e["individual"], id + 1);

        /// <summary>A variant number's species (0 to 99), -1 for none.</summary>
        static long SpeciesOf(long variant) => variant < 0 ? -1 : variant % 100;

        /// <summary>The variant number of the animal: a harvest's `extended_species`, else its spawn's `species`.</summary>
        long Variant => Is(e["event"], 3) ? (Py.IsNum(e["extended_species"]) ? Py.L(e["extended_species"]) : -1) : Py.IsNum(Own.Spawn?["species"]) ? Py.L(Own.Spawn!["species"]) : -1;

        long Species => SpeciesOf(Variant);

        long FirstEvent => first ??= con.Scalar("SELECT first_event FROM expeditions WHERE account_id = $a ORDER BY id DESC LIMIT 1") is long f ? f : 0;

        static long Number(JsonNode? n) => Py.IsNum(n) ? Py.L(n) : -1;

        static long Int(object? v) => v is long l ? l : throw new FormatException();

        /// <summary>The deaths (event 2) of the last `seconds` by receive time, or of the hunt when there is no window, up to the current event.</summary>
        List<Death> Deaths(long? seconds) => [.. con.Rows("SELECT id, ts, body FROM events WHERE account_id = $a AND event = 2 AND id <= ? AND (id > ? OR ts >= ?) ORDER BY id",
                id, FirstEvent, now - (seconds ?? 0)).Select(r => (Id: (long)r[0], Ts: r[1] is double t ? t : double.NegativeInfinity, Body: Parse(r[2])))
            .Where(d => seconds is { } w ? d.Ts >= now - w : d.Id > FirstEvent)
            .Select(d =>
            {
                var (spawn, hits) = Animal(d.Body["individual"], d.Id);
                return new Death(d.Id, d.Ts, SpeciesOf(Number(spawn?["species"])),
                                 Py.IsNum(d.Body["shot_id"]) ? Py.L(d.Body["shot_id"]) : hits.Count > 0 && Py.IsNum(hits[^1]["shot_id"]) ? Py.L(hits[^1]["shot_id"]) : null);
            })];

        public object? Call(string name, object?[] a, Dictionary<string, object?> kw)
        {
            var (_, hits) = Own;
            switch (name)
            {
                case "a.texture": return Number(Own.Spawn?["texture"]);
                case "a.species": return Species;
                case "a.variant": return Variant;
                case "a.ethical": return s.Ethical(Variant < 0 ? null : JsonValue.Create(Variant), hits);
                case "a.range":  // ponytail: "minimum" and "maximum" are the shortest and the longest hit
                    var mm = hits.Where(h => Py.IsNum(h["distance_cms"])).Select(h => (long)Math.Round(Py.D(h["distance_cms"]) * 10)).ToList();
                    return mm.Count == 0 ? 0L : a[0] as string == "minimum" ? mm.Min() : mm.Max();
                case "a.wounds.count": return (long)hits.Count;
                case "a.wounds.state":  // the state the animal died in: the event's own, else its last hit's
                    return Number(Py.IsNum(e["animal_state"]) ? e["animal_state"] : hits.Count > 0 ? hits[^1]["animal_state"] : null) is var st && RuleParser.Set(a[0]).Contains(st);
                // ponytail: ammunition, weapon and scope must hold for every hit; an organ for any hit
                case "a.wounds.ammo": return hits.Count > 0 && hits.All(h => RuleParser.Set(a[0]).Contains(Number(h["ammunition_id"])));
                case "a.wounds.weapon": return hits.Count > 0 && hits.All(h => RuleParser.Set(a[0]).Contains(Number(h["weapon_id"])));
                case "a.wounds.scope": return hits.Count > 0 && hits.All(h => (Py.IsNum(h["scope_id"]) && Py.L(h["scope_id"]) != -1) == (a[0] is true));
                case "a.wounds.organ": return hits.Any(h => RuleParser.Set(a[0]).Any(o => (Number(h["organ2"]) & o) != 0));
                case "e.trackType": return Number(e["track_type"]);
                case "e.individualCount":
                    return (long)con.Scalar("SELECT COUNT(*) FROM events WHERE account_id = $a AND event = 12 AND individual = ? AND id > ? AND id <= ? AND json_extract(body, '$.track_type') = ?",
                                            e["individual"], FirstEvent, id, Number(e["track_type"]))!;
                case "e.animalCount":  // ponytail: the harvests of the animal's own species
                    return (long)con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 3 AND ts >= ?", now - Int(a[0])).Count(r => SpeciesOf(Number(Parse(r[0])["extended_species"])) == Species);
                case "e.animalCountWithFilters":
                    var seconds = kw.TryGetValue("seconds", out var w) ? Int(w) : (long?)null;
                    var deaths = Deaths(seconds);
                    var shot = deaths.FirstOrDefault(d => d.Id == id)?.Shot;
                    return (long)deaths.Count(d => (a.Length == 0 || d.Species == Int(a[0])) && (kw.GetValueOrDefault("same_shot") is not true || shot is not null && d.Shot == shot));
                case "e.collectableCountWithFilters":
                    var ids = RuleParser.Set(kw.GetValueOrDefault("collectable_category"));
                    return (long)Pickups(con, JsonValue.Create(FirstEvent), id).Count(p => !p.Antler && ids.Contains(p.Type));
                default: throw new FormatException(name);
            }
        }
    }

    /// <summary>The expression subset of the conditions: `or`, `and`, `not`, comparisons, calls with positional and keyword arguments, `{}` and `[]` lists, numbers, strings,
    /// `True`, `False` and constants (`c.<table>.<NAME>`). It compiles to a delegate once; anything else is a FormatException.</summary>
    sealed class RuleParser
    {
        static readonly Regex Token = new(@"\G\s*(\d+|'[^']*'|""[^""]*""|[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*|>=|<=|==|!=|[(){}\[\],=<>])", RegexOptions.CultureInvariant);

        static readonly HashSet<string> Calls =
        [
            "a.texture", "a.species", "a.variant", "a.ethical", "a.range", "a.wounds.count", "a.wounds.state", "a.wounds.ammo", "a.wounds.weapon", "a.wounds.scope",
            "a.wounds.organ", "e.trackType", "e.individualCount", "e.animalCount", "e.animalCountWithFilters", "e.collectableCountWithFilters",
        ];

        readonly List<string> t = [];
        readonly Func<string, object> constant;
        int i;

        RuleParser(string text, Func<string, object> constant)
        {
            this.constant = constant;
            for (var pos = 0; pos < text.Length && text[pos..].Trim().Length > 0;)
            {
                var m = Token.Match(text, pos);
                if (!m.Success)
                    throw new FormatException(text);
                t.Add(m.Groups[1].Value);
                pos = m.Index + m.Length;
            }
        }

        public static Expr Parse(string text, Func<string, object> constant)
        {
            var p = new RuleParser(text, constant);
            var rule = p.Or();
            return p.i == p.t.Count ? rule : throw new FormatException(text);
        }

        public static long[] Set(object? v) => v as long[] ?? (v is long l ? [l] : throw new FormatException());

        public static bool Truth(object? v) => v switch { bool b => b, long l => l != 0, null => false, _ => true };

        string Peek => i < t.Count ? t[i] : "";

        bool Take(string token)
        {
            if (Peek != token)
                return false;
            i++;
            return true;
        }

        void Expect(string token)
        {
            if (!Take(token))
                throw new FormatException(token);
        }

        Expr Or()
        {
            var left = And();
            while (Take("or"))
            {
                var (l, r) = (left, And());
                left = h => Truth(l(h)) || Truth(r(h));
            }
            return left;
        }

        Expr And()
        {
            var left = Not();
            while (Take("and"))
            {
                var (l, r) = (left, Not());
                left = h => Truth(l(h)) && Truth(r(h));
            }
            return left;
        }

        Expr Not()
        {
            if (!Take("not"))
                return Compare();
            var inner = Not();
            return h => !Truth(inner(h));
        }

        Expr Compare()
        {
            var left = Atom();
            if (Peek is not (">=" or "<=" or ">" or "<" or "==" or "!="))
                return left;
            var (op, right) = (t[i++], Atom());
            return h =>
            {
                var (l, r) = (left(h), right(h));
                if (op is "==" or "!=")
                    return Equals(l, r) == (op == "==");
                var (x, y) = (l as long? ?? throw new FormatException(op), r as long? ?? throw new FormatException(op));
                return op switch { ">=" => x >= y, "<=" => x <= y, ">" => x > y, _ => x < y };
            };
        }

        Expr Atom()
        {
            var token = Peek;
            i++;
            if (token == "(")
            {
                var inner = Or();
                Expect(")");
                return inner;
            }
            if (token is "{" or "[")
            {
                var items = new List<Expr>();
                while (Peek != (token == "{" ? "}" : "]"))
                {
                    items.Add(Or());
                    if (!Take(",") && Peek != (token == "{" ? "}" : "]"))
                        throw new FormatException(token);
                }
                i++;
                return h => items.SelectMany(x => Set(x(h))).ToArray();
            }
            if (token.Length > 0 && char.IsAsciiDigit(token[0]))
            {
                var number = long.Parse(token);
                return _ => number;
            }
            if (token.Length > 1 && token[0] is '\'' or '"')
            {
                var text = token[1..^1];
                return _ => text;
            }
            if (token is "True" or "False")
            {
                var value = token == "True";
                return _ => value;
            }
            if (token.Length == 0 || !(char.IsAsciiLetter(token[0]) || token[0] == '_') || token is "and" or "or" or "not")
                throw new FormatException(token);
            if (Peek != "(")
            {
                var value = constant(token);
                return _ => value;
            }
            if (!Calls.Contains(token))
                throw new FormatException(token);
            i++;
            var (args, named) = (new List<Expr>(), new List<(string, Expr)>());
            while (Peek != ")")
            {
                if (i + 1 < t.Count && t[i + 1] == "=" && !t[i].Contains('.') && char.IsAsciiLetter(t[i][0]))
                {
                    var key = t[i];
                    i += 2;
                    named.Add((key, Or()));
                }
                else
                    args.Add(Or());
                if (!Take(",") && Peek != ")")
                    throw new FormatException(token);
            }
            i++;
            return h => h.Call(token, [.. args.Select(x => x(h))], named.ToDictionary(k => k.Item1, k => k.Item2(h)));
        }
    }
}
