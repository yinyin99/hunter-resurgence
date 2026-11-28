// The operator's rules of single objectives: `mission_rules.json` beside the recorded catalogue (in `recordings_dir`). An entry replaces the rule the title reader made of one
// objective and is written in the words `coverage --rules` prints (docs/server.md, "Overridden rules"; ADR 0012). The titles, texts and rewards still come only from the catalogue.
// A bad entry stops the start with its key: nothing in the file is ignored.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    public const string MissionRulesFile = "mission_rules.json";

    readonly HashSet<(long Mission, long Objective)> overridden = [], unordered = [];  // `unordered`: the file undid the order the title implies (printed back as `unordered`)

    /// <summary>How many objectives have their rule from the operator's file.</summary>
    public int OverriddenObjectives => overridden.Count;

    // `, N animals` and a radius are bounded: a herd of 0 never exists, one of a thousand would be spawned
    const long MaxHerd = 20, MaxRadius = 5000;
    static readonly Regex NamedNear = new(@"\Anamed animal near \((?<x>-?\d+(?:\.\d+)?), (?<z>-?\d+(?:\.\d+)?)\) m(?:, (?<n>\d+) animals)?(?:, otherwise place (?<p>[A-Z0-9_]+))?\z", RegexOptions.CultureInvariant);
    static readonly Regex EachFootprint = new(@"\Afootprints of each of (?<s>.+) within (?<r>\d+) m of \((?<x>-?\d+(?:\.\d+)?), (?<z>-?\d+(?:\.\d+)?)\)\z", RegexOptions.CultureInvariant);
    static readonly Regex AfterObjective = new(@"\Aafter objective (?<n>\d+)\z", RegexOptions.CultureInvariant);
    static readonly Regex ArriveWithin = new(@"\Awithin 200 m of \((?<x>-?\d+(?:\.\d+)?), (?<z>-?\d+(?:\.\d+)?)\)\z", RegexOptions.CultureInvariant);

    /// <summary>A whole number of the bounds, or the start stops with the word it was for (a number too big for a long is no exception of its own).</summary>
    static long Whole(string digits, long min, long max, string what) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : throw new InvalidDataException($"{what} is a whole number from {min} to {max}, not {digits}");

    /// <summary>A coordinate in metres: a finite number.</summary>
    static double Coordinate(string digits) =>
        double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new InvalidDataException($"{digits} is no coordinate");

    // a place the game names when the player enters it (event 101 `location_name`): no catalogue lists them all, so only its form is checked
    static readonly Regex PlaceDefine = new(@"\A[A-Z0-9_]+\z", RegexOptions.CultureInvariant);
    // `in the hunt: <n>[ within <m> min]`, `<n>, each within <m> min of the one before`, `<n> killed by one shot`, `<n> different animals[ within <m> min]` (<see cref="Describe"/>)
    static readonly Regex InTheHunt = new(@"\A(?<n>\d+)(?:(?<shot> killed by one shot)|, each within (?<e>\d+(?:\.\d+)?) min of the one before|(?<different> different animals)?(?: within (?<m>\d+(?:\.\d+)?) min)?)\z",
        RegexOptions.CultureInvariant);

    // `in the mission: <n> different animals` (<see cref="Describe"/>): the animals of every hunt since the mission was activated
    static readonly Regex InTheMission = new(@"\A(?<n>\d+) different animals\z", RegexOptions.CultureInvariant);

    /// <summary>The file's object of `"<mission id>/<objective id>": "<rule in words>"` entries; `//` comments and trailing commas are allowed, a key twice is not. Not JSON or not an
    /// object: the start stops.</summary>
    public static JsonObject ReadMissionRules(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false })
                as JsonObject ?? throw new InvalidDataException($"{path}: an object of \"<mission id>/<objective id>\": \"<rule>\" entries");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}");
        }
    }

    /// <summary>Replace the rule of each objective the file has an entry for. An entry for an objective the catalogue does not have, a word that is no part of a rule, or an item,
    /// species, coat or reserve no name of the catalogue matches stops the start with the entry's key.</summary>
    void OverrideMissionRules(JsonObject? file)
    {
        foreach (var (key, value) in file ?? [])
            try
            {
                var at = key.Split('/');
                if (at.Length != 2 || !long.TryParse(at[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mission) || !long.TryParse(at[1], NumberStyles.None, CultureInfo.InvariantCulture, out var objective))
                    throw new InvalidDataException("a key is <mission id>/<objective id>");
                var m = missions.GetValueOrDefault(mission) ?? throw new InvalidDataException($"the catalogue has no mission {mission}");
                var i = Array.FindIndex(m.Objectives, o => o.Id == objective);
                if (i < 0)
                    throw new InvalidDataException($"mission {mission} has no objective {objective}");
                if (value is not JsonValue v || !v.TryGetValue<string>(out var words))
                    throw new InvalidDataException("a rule is a string of words, as coverage --rules prints them");
                var stated = RuleIn(words);
                var title = m.Objectives[i].Rule;
                var undone = words.Split(';', StringSplitOptions.TrimEntries).Contains("unordered");
                // the order: what the entry says, `after objective <n>`, `ordered` (every objective before it) or `unordered`; with none of the three the title's own order stays (`Then`, `After visiting X`, `At any point during the same hunt`)
                var (after, ordered) = stated.After is not null ? (stated.After, true) : stated.Ordered ? (null, true) : undone ? (null, false) : (title.After, title.Ordered || title.After is not null);
                if (after is { } waits && Array.FindIndex(m.Objectives, o => o.Id == waits) is var before && (before < 0 || before >= i))
                    throw new InvalidDataException($"after objective {waits}: an objective of the mission before this one");
                var rule = stated with { After = after, Ordered = ordered };
                if (stated.Kind == Kind.Arrive && stated.Tests is { Length: 0 } && stated.Zone is null && title.Kind != Kind.Arrive)  // a bare `arrive` keeps the title's own arrival: with no title that is one it would be an arrival nothing can meet
                    throw new InvalidDataException("a bare \"arrive\" is for an objective whose title is an arrival: give \"place: <NAME>\", \"places: <NAME>, <NAME>\" or \"within 200 m of (<x>, <z>)\"");
                if (stated.Kind == Kind.Arrive)
                {
                    // an arrive entry keeps the title's reserves (a point or a place of the title is met only in the mission's reserves); a `place:` entry is met by that name in any reserve unless the title's point stays (the game's area may be
                    // smaller or larger than 200 m); `places:`, a point or a bare `arrive` are still met by the title's words (the game's areas the catalogue lacks), so they keep the title's text
                    var reserves = title.Zone?.Reserves ?? [];
                    rule = rule with
                    {
                        Zone = stated.Zone is { } z ? new Zone(z.Point, reserves, z.Places) : stated.Tests is { Length: > 0 } ? title.Zone is { Point: not null } tz ? tz with { Places = [] } : null : title.Zone,
                        Text = stated.Tests is { Length: > 0 } ? stated.Text : title.Text,
                    };
                }
                m.Objectives[i] = m.Objectives[i] with { Rule = rule };
                if (undone)
                    unordered.Add((mission, objective));
                overridden.Add((mission, objective));
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{MissionRulesFile} \"{key}\": {e.Message}");
            }
        foreach (var (mission, objective) in overridden)  // `together` finishes the objectives of the mission with the same words: it needs a twin
            if (missions[mission].Objectives.Single(o => o.Id == objective).Rule is { Together: true } rule && !missions[mission].Objectives.Any(o => o.Id != objective && o.Rule.Together && o.Rule.Text == rule.Text))
                throw new InvalidDataException($"{MissionRulesFile} \"{mission}/{objective}\": together needs another objective of the mission with the same rule and \"together\"");
    }

    /// <summary>A rule written as <see cref="RuleLines"/> prints it after `=>`: `<kind>; species: <names>; male|female; ordered|after objective <n>|unordered; together; <conditions>`, separated by `;`; an arrive rule is
    /// `arrive; [order]; place: <NAME>`, `places: <NAME>, <NAME>` or `within 200 m of (<x>, <z>)`. Its `Text` is these words, so <see cref="StuckMissions"/> takes two objectives for equal when their rules are, not
    /// their titles. A gender, a species, an order word or `together` given twice stops the start.</summary>
    Rule RuleIn(string words)
    {
        var parts = words.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidDataException("the rule is empty");
        // the order: `ordered` (after every objective before), `after objective <n>` (after that one only), `unordered` (none: the file undoes the order the title implies)
        var orders = parts.Where(p => p is "ordered" or "unordered" || AfterObjective.IsMatch(p)).ToArray();
        if (orders.Length > 1)
            throw new InvalidDataException("one of \"ordered\", \"unordered\" and \"after objective <n>\" at most, once");
        long? after = orders.Length == 1 && AfterObjective.Match(orders[0]) is { Success: true } a ? Whole(a.Groups["n"].Value, 0, long.MaxValue, "an objective id") : null;
        var ordered = orders is ["ordered"] || after is not null;
        var rest = parts.Where(p => !orders.Contains(p)).ToArray();
        if (rest.Length == 0)
            throw new InvalidDataException("the rule has no kind");
        if (rest[0] == "arrive")  // an arrival the title cannot name: the file gives the place's game name, met by that place only, or the places or the point of a title
        {
            Test? at = null;
            var named = new List<string>();
            (double X, double Z)? point = null;
            foreach (var p in rest[1..])
                if (p.StartsWith("place: ", StringComparison.Ordinal) && at is null)
                    at = TestIn(p, []);
                else if (p.StartsWith("places: ", StringComparison.Ordinal) && named.Count == 0)
                    named.AddRange(List(p["places: ".Length..]).Select(d => PlaceDefine.IsMatch(d) ? d : throw new InvalidDataException($"\"{d}\" is no name of a place: capital letters, digits and _")));
                else if (ArriveWithin.Match(p) is { Success: true } within && point is null)
                    point = (Coordinate(within.Groups["x"].Value), Coordinate(within.Groups["z"].Value));
                else
                    throw new InvalidDataException("an arrive rule is \"arrive[; ordered|unordered|after objective <n>]; place: <NAME>\", \"places: <NAME>, <NAME>\" or \"within 200 m of (<x>, <z>)\", each once");
            if (at is not null && named.Count > 0)
                throw new InvalidDataException("an arrive rule has \"place: <NAME>\" or \"places: <NAME>, <NAME>\", not both");
            return new Rule(Kind.Arrive, null, null, ordered, string.Join("; ", parts), at is null ? [] : [at], Zone: point is null && named.Count == 0 ? null : new Zone(point, [], [.. named]), After: after);
        }
        var kind = rest[0] switch
        {
            "harvest" => Kind.Harvest,
            "spot" => Kind.Spot,
            "track" => Kind.Track,
            _ => throw new InvalidDataException($"\"{rest[0]}\" is no kind of rule the file sets: harvest, spot, track or arrive"),
        };
        if (rest.Count(p => p is "male" or "female") > 1 || rest.Count(p => p.StartsWith("species: ", StringComparison.Ordinal)) > 1 || rest.Count(p => p == "together") > 1)
            throw new InvalidDataException("a gender, a species or \"together\" is given twice");
        long? speciesId = null, gender = null;
        string? barrels = null;
        var tests = new List<Test>();
        foreach (var part in rest[1..].OrderBy(p => !p.StartsWith("species: ", StringComparison.Ordinal)))  // the species first: a coat word may be the species' own
            if (part is "male" or "female") gender = part == "male" ? 0 : 1;
            else if (part == "together") { }  // one kill finishes every objective of the mission with this rule (read below)
            else if (part.StartsWith("species: ", StringComparison.Ordinal))
            {
                var ids = SpeciesIn(part["species: ".Length..]);
                if (ids.Length == 1) speciesId = ids[0];
                else tests.Add(new(Check.Species, Ids: [.. ids]));
            }
            else if (part.StartsWith("weapon barrels: ", StringComparison.Ordinal)) barrels = barrels is null ? part["weapon barrels: ".Length..] : throw new InvalidDataException("\"weapon barrels\" is given twice");
            else tests.Add(TestIn(part, speciesId is { } s ? [s] : tests.Where(t => t.Check == Check.Species).SelectMany(t => t.Ids!)));
        if (barrels is not null)  // a weapon of two classes counts for a class only with the ammunition of that class: `<weapon> = <ammunition>, ... | <weapon> = ...`
        {
            var weapon = tests.FindIndex(t => t.Check == Check.Weapon);
            if (weapon < 0)
                throw new InvalidDataException("\"weapon barrels\" needs a \"weapon\" condition");
            var when = new Dictionary<long, HashSet<long>>();
            foreach (var barrel in barrels.Split(" | "))
            {
                var eq = barrel.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0)
                    throw new InvalidDataException("a barrel is \"<weapon> = <ammunition>, ...\"");
                var rounds = ItemsIn(barrel[(eq + 3)..], 3, "ammunition");
                var named = items.Where(i => Py.IsNum(i.Value["cat"]) && Py.L(i.Value["cat"]) == State.WeaponCategory && ItemName(i.Key).Equals(barrel[..eq], StringComparison.OrdinalIgnoreCase)).Select(i => i.Key).ToList();
                if (named.Count == 0)
                    throw new InvalidDataException($"no weapon is named \"{barrel[..eq]}\"");
                if (!named.Any(tests[weapon].Ids!.Contains))
                    throw new InvalidDataException($"the weapon \"{barrel[..eq]}\" of a barrel is not one of the \"weapon\" condition's");
                foreach (var id in named.Where(tests[weapon].Ids!.Contains))
                    when[id] = [.. rounds];
            }
            tests[weapon] = tests[weapon] with { When = when };
        }
        // what an event of the kind carries: a spot has no weapon, hit or harvest; a clue has its kind and its animal
        Check[]? only = kind switch
        {
            Kind.Spot => [Check.Stand, Check.Reserve, Check.Place, Check.Named, Check.Species, Check.Coat, Check.NotCoat, Check.Different],
            Kind.Track => [Check.Clue, Check.Reserve, Check.Place, Check.Species, Check.Coat, Check.NotCoat, Check.Different, Check.EachSpecies, Check.Within, Check.Each],  // `3 tracks from a <species>` counted over the hunt: `in the hunt: 3`
            _ => null,
        };
        if (tests.FirstOrDefault(t => only is null ? t.Check is Check.Clue or Check.Different or Check.EachSpecies : !only.Contains(t.Check)) is { } wrong)
            throw new InvalidDataException($"a {rest[0]} rule cannot check \"{Describe(wrong)}\"");
        if (kind == Kind.Track && tests.Count(t => t.Check == Check.Clue) != 1)
            throw new InvalidDataException("a track rule has one clues condition");
        return new Rule(kind, speciesId, gender, ordered, string.Join("; ", parts), [.. tests.OrderBy(t => IsSequence(t.Check))], After: after, Together: rest.Contains("together"));  // a sequence last, as the reader puts it
    }

    /// <summary>One condition in the words of <see cref="Describe"/>.</summary>
    Test TestIn(string part, IEnumerable<long> animals)
    {
        var colon = part.IndexOf(": ", StringComparison.Ordinal);
        var (name, value) = colon >= 0 ? (part[..colon], part[(colon + 2)..]) : (part, "");
        return name switch
        {
            "permitted" when value == "" => new(Check.Permitted),
            "one shot" when value == "" => new(Check.OneShot),
            "no sight" when value == "" => new(Check.NoScope),
            "any sight" when value == "" => new(Check.Scope),
            "named animal, no place" when value == "" => new(Check.Named),
            _ when NamedNear.Match(part) is { Success: true } near =>
                new(Check.Named, Coordinate(near.Groups["x"].Value), Coordinate(near.Groups["z"].Value),
                    Place: near.Groups["p"].Success ? near.Groups["p"].Value : null, Count: near.Groups["n"].Success ? Whole(near.Groups["n"].Value, 1, MaxHerd, "a herd size") : 1),
            _ when EachFootprint.Match(part) is { Success: true } each =>
                new(Check.EachSpecies, Coordinate(each.Groups["x"].Value), Coordinate(each.Groups["z"].Value),
                    Ids: [.. SpeciesIn(each.Groups["s"].Value)], Count: Whole(each.Groups["r"].Value, 1, MaxRadius, "a radius in metres")),
            "weapon" => WeaponsIn(value),
            "ammunition" => new(Check.Ammo, Ids: ItemsIn(value, 3, "ammunition")),
            "not ammunition" => new(Check.NotAmmo, Ids: ItemsIn(value, 3, "ammunition")),
            "sight" => new(Check.Scope, Ids: ItemsIn(value, 2, "sight")),
            "organs, one of" => new(Check.Organ, Ids: [.. value.Split(" | ").Select(MaskIn)]),
            "organs only" => new(Check.OrganOnly, Ids: [MaskIn(value)]),
            "avoids" => new(Check.OrganAvoid, Ids: [.. List(value).Select(MaskIn)]),
            "harvest value" => Bounded(Check.Value, value),
            "distance" => Bounded(Check.Distance, value, " m"),
            "weight" => Bounded(Check.Weight, value, " kg"),
            "score" => Bounded(Check.Score, value),
            "typical points" => Bounded(Check.Points, value),
            "beard" => Bounded(Check.Beard, value, " mm"),
            "spurs" => Bounded(Check.Spurs, value, " mm"),
            "tusks" => Bounded(Check.Tusks, value, " mm"),
            "place" => PlaceDefine.IsMatch(value) ? new(Check.Place, Place: value) : throw new InvalidDataException($"\"{value}\" is no name of a place: capital letters, digits and _"),
            "state" or "first state" => new(name == "state" ? Check.State : Check.FirstState,
                Ids: Reader.States.FirstOrDefault(s => s.Label == value) is { Label: not null } st ? [.. st.States]  // `first state` takes every state word: the earlier files said `first state: spooked`
                    : throw new InvalidDataException($"\"{value}\" is no {name}")),
            "not state" => new(Check.NotState, Ids: [.. List(value).SelectMany(s => s switch
            {
                "charging" => new long[] { 9 }, "running away" => Reader.RunningAway, _ => throw new InvalidDataException($"\"{s}\" is no state a rule refuses: charging or running away"),
            })]),
            "pose" => new(Check.Pose, Ids: NamesIn(value, PoseNames.Select((n, i) => ((long)i, n)), "pose")),
            "stand" => new(Check.Stand, Ids: NamesIn(value, StandNames.Select((n, i) => ((long)i, n)), "stand")),
            "reserve" => new(Check.Reserve, Ids: NamesIn(value, reserves.Where(r => r.Value is not null).Select(r => (r.Key, r.Value!)), "reserve")),
            "clues" => CluesIn(value),
            "in the hunt" => SequenceIn(value),
            "in the mission" => InTheMission.Match(value) is { Success: true } across && long.TryParse(across.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 2
                ? new(Check.Different, n, Across: true) : throw new InvalidDataException("an across-hunts rule is \"in the mission: <n of 2 or more> different animals\""),
            "coat" or "not coat" => new(name == "coat" ? Check.Coat : Check.NotCoat, Ids: [.. List(value).SelectMany(c =>  // `coat 77`: a coat the game names nowhere, by its id
                c.StartsWith("coat ", StringComparison.Ordinal) && long.TryParse(c[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? [id]
                : Reader.CoatIds(c.ToLowerInvariant(), animals) is { Length: > 0 } ids ? ids : throw new InvalidDataException($"no coat is named \"{c}\""))]),
            _ => throw new InvalidDataException($"\"{part}\" is no condition the server knows"),
        };
    }

    static string[] List(string value) => value.Split(", ", StringSplitOptions.TrimEntries);

    /// <summary>The ids of each name (the case is not compared); a name none has stops the start.</summary>
    static HashSet<long> NamesIn(string value, IEnumerable<(long Id, string Name)> all, string what)
    {
        var known = all.ToList();
        var ids = new HashSet<long>();
        foreach (var n in List(value))
            ids.UnionWith(known.Where(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToList() is { Count: > 0 } found ? found
                : throw new InvalidDataException($"no {what} is named \"{n}\""));
        return ids;
    }

    // the catalogue's items of a `cat` (2 sights, 3 ammunition) by name: items of one name (skins, variants) are all of them
    // `Test Pistol (Festive)` is the one variant of items that share a name; the name alone is all of them (a weapon is <see cref="WeaponsIn"/>)
    HashSet<long> ItemsIn(string value, long cat, string what) =>
        NamesIn(value, items.Where(i => Py.IsNum(i.Value["cat"]) && Py.L(i.Value["cat"]) == cat)
            .SelectMany(i => Py.IsStr(i.Value["variationname"]) ? new[] { (i.Key, ItemName(i.Key)), (i.Key, $"{ItemName(i.Key)} ({(string)i.Value["variationname"]!})") } : [(i.Key, ItemName(i.Key))]), what);

    // a `weapon:` condition: a weapon's name is its whole weapon family (docs/server.md, "Weapon families"), `Name (Finish)` is that one item only
    Test WeaponsIn(string value)
    {
        var finishes = items.Where(i => Py.IsNum(i.Value["cat"]) && Py.L(i.Value["cat"]) == State.WeaponCategory && Py.IsStr(i.Value["variationname"])).Select(i => (i.Key, $"{ItemName(i.Key)} ({(string)i.Value["variationname"]!})")).ToList();
        var names = items.Where(i => Py.IsNum(i.Value["cat"]) && Py.L(i.Value["cat"]) == State.WeaponCategory).SelectMany(i => FamilyInCatalogue(i.Key).Select(m => (m, ItemName(i.Key))));
        var exact = List(value).Any(n => finishes.Any(f => f.Item2.Equals(n, StringComparison.OrdinalIgnoreCase)));
        return new(Check.Weapon, Ids: NamesIn(value, finishes.Concat(names), "weapon"), Basis: exact ? WeaponBasis.Finish : WeaponBasis.Name);
    }

    // the species of `species: <names>` or `species: any species except <names>` (0 to 99, as printed)
    long[] SpeciesIn(string value)
    {
        var all = species.Keys.Where(k => k < 100).Select(k => (k, SpeciesName(k))).ToList();
        const string except = "any species except ";
        return value.StartsWith(except, StringComparison.Ordinal) ? [.. all.Select(a => a.k).Except(NamesIn(value[except.Length..], all, "species"))] : [.. NamesIn(value, all, "species")];
    }

    // an organ mask as <see cref="Mask"/> prints it: `soft organs`, `bones`, or organ names (and `bit N`) joined by `+`
    static long MaskIn(string words) => words switch
    {
        "soft organs" => Reader.Organs,
        "bones" => Reader.Bits(10, 21, 22, 23, 24, 25, 26, 28, 29),
        _ => words.Split('+').Aggregate(0L, (mask, w) => mask | (w.StartsWith("bit ", StringComparison.Ordinal) && int.TryParse(w[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b < 63 ? 1L << b
            : OrganBits.Where(o => o.Name == w).Aggregate(0L, (m, o) => m | 1L << o.Bit) is > 0 and var bits ? bits : throw new InvalidDataException($"\"{w}\" is no organ"))),
    };

    // bounds as <see cref="Bounds"/> prints them: `>=100`, `>100` (the next number up), `<=200`, `<200`, then the unit
    static Test Bounded(Check check, string value, string unit = "")
    {
        if (!value.EndsWith(unit, StringComparison.Ordinal))
            throw new InvalidDataException($"\"{value}\" has no unit \"{unit.Trim()}\"");
        var (min, max) = (double.NegativeInfinity, double.PositiveInfinity);
        var words = value[..^unit.Length].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            throw new InvalidDataException("a bound is >=, >, <= or < and a number");
        foreach (var w in words)
        {
            var op = w.StartsWith(">=", StringComparison.Ordinal) || w.StartsWith("<=", StringComparison.Ordinal) ? w[..2] : w[..1];
            if (!double.TryParse(w[op.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                throw new InvalidDataException($"\"{w}\" is no bound: >=, >, <= or < and a number");
            if (op[0] == '>' ? !double.IsNegativeInfinity(min) : !double.IsPositiveInfinity(max))
                throw new InvalidDataException($"\"{value}\" has two bounds of one side");
            switch (op)
            {
                case ">=": min = n; break;
                case ">": min = Math.BitIncrement(n); break;
                case "<=": max = n; break;
                case "<": max = Math.BitDecrement(n); break;
                default: throw new InvalidDataException($"\"{w}\" is no bound: >=, >, <= or < and a number");
            }
        }
        return new(check, min, max);
    }

    // `clues: <kinds or any> x<count>`
    static Test CluesIn(string value)
    {
        var at = value.LastIndexOf(" x", StringComparison.Ordinal);
        if (at < 0 || !long.TryParse(value[(at + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            throw new InvalidDataException("clues are \"clues: <kinds, or any> x<count>\"");
        HashSet<long> kinds = value[..at] == "any" ? [] : [.. List(value[..at]).Select(k => Enum.GetNames<ClueKind>().Contains(k, StringComparer.OrdinalIgnoreCase) ? (long)Enum.Parse<ClueKind>(k, true)
            : throw new InvalidDataException($"\"{k}\" is no kind of clue"))];
        return new(Check.Clue, count, Ids: kinds);
    }

    // a sequence of the hunt as <see cref="Describe"/> prints it, after `in the hunt: `; the number is at least 2
    static Test SequenceIn(string value)
    {
        if (InTheHunt.Match(value) is not { Success: true } m || !long.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 2)
            throw new InvalidDataException("a sequence is \"in the hunt: <n of 2 or more>[ within <m> min]\", \"<n>, each within <m> min of the one before\", \"<n> killed by one shot\" or \"<n> different animals[ within <m> min]\"");
        static double Seconds(Group g) => g.Success ? double.Parse(g.Value, CultureInfo.InvariantCulture) * 60 : double.PositiveInfinity;
        return m.Groups["shot"].Success ? new(Check.SameShot, n) : m.Groups["e"].Success ? new(Check.Each, n, Seconds(m.Groups["e"]))
            : new(m.Groups["different"].Success ? Check.Different : Check.Within, n, Seconds(m.Groups["m"]));
    }
}
