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

    readonly HashSet<(long Mission, long Objective)> overridden = [];

    /// <summary>How many objectives have their rule from the operator's file.</summary>
    public int OverriddenObjectives => overridden.Count;

    static readonly Regex NamedNear = new(@"\Anamed animal near \((?<x>-?\d+(?:\.\d+)?), (?<z>-?\d+(?:\.\d+)?)\) m\z", RegexOptions.CultureInvariant);

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
                m.Objectives[i] = m.Objectives[i] with { Rule = RuleIn(words, m.Objectives[i].Rule.Text) };
                overridden.Add((mission, objective));
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{MissionRulesFile} \"{key}\": {e.Message}");
            }
    }

    /// <summary>A rule written as <see cref="RuleLines"/> prints it after `=>`: `<kind>; species: <names>; male|female; ordered; <conditions>`, separated by `;`. `text` is the title's,
    /// kept for nothing but the equal-objective test of the rollback.</summary>
    Rule RuleIn(string words, string text)
    {
        var parts = words.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidDataException("the rule is empty");
        var kind = parts[0] switch
        {
            "harvest" => Kind.Harvest, "spot" => Kind.Spot, "track" => Kind.Track,
            _ => throw new InvalidDataException($"\"{parts[0]}\" is no kind of rule the file sets: harvest, spot or track"),
        };
        long? speciesId = null, gender = null;
        var ordered = false;
        var tests = new List<Test>();
        foreach (var part in parts[1..].OrderBy(p => !p.StartsWith("species: ", StringComparison.Ordinal)))  // the species first: a coat word may be the species' own
            if (part is "male" or "female") gender = part == "male" ? 0 : 1;
            else if (part == "ordered") ordered = true;
            else if (part.StartsWith("species: ", StringComparison.Ordinal))
            {
                var ids = SpeciesIn(part["species: ".Length..]);
                if (ids.Length == 1) speciesId = ids[0];
                else tests.Add(new(Check.Species, Ids: [.. ids]));
            }
            else tests.Add(TestIn(part, speciesId is { } s ? [s] : tests.Where(t => t.Check == Check.Species).SelectMany(t => t.Ids!)));
        // what an event of the kind carries: a spot has no weapon, hit or harvest; a clue has its kind and its animal
        Check[]? only = kind switch
        {
            Kind.Spot => [Check.Stand, Check.Reserve, Check.Named, Check.Species, Check.Coat, Check.NotCoat],
            Kind.Track => [Check.Clue, Check.Reserve, Check.Species, Check.Coat, Check.NotCoat],
            _ => null,
        };
        if (tests.FirstOrDefault(t => only is null ? t.Check == Check.Clue : !only.Contains(t.Check)) is { } wrong)
            throw new InvalidDataException($"a {parts[0]} rule cannot check \"{Describe(wrong)}\"");
        if (kind == Kind.Track && tests.Count(t => t.Check == Check.Clue) != 1)
            throw new InvalidDataException("a track rule has one clues condition");
        return new Rule(kind, speciesId, gender, ordered, text, [.. tests]);
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
                new(Check.Named, double.Parse(near.Groups["x"].Value, CultureInfo.InvariantCulture), double.Parse(near.Groups["z"].Value, CultureInfo.InvariantCulture)),
            "weapon" => new(Check.Weapon, Ids: ItemsIn(value, 1, "weapon")),
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
            "state" or "first state" => new(name == "state" ? Check.State : Check.FirstState,
                Ids: Reader.States.FirstOrDefault(s => s.Label == value && s.First == (name == "first state")) is { Label: not null } st ? [.. st.States]
                    : throw new InvalidDataException($"\"{value}\" is no {name}")),
            "not state" => new(Check.NotState, Ids: [.. List(value).SelectMany(s => s switch
            {
                "charging" => new long[] { 9 }, "running away" => [5, 10, 11, 15], _ => throw new InvalidDataException($"\"{s}\" is no state a rule refuses: charging or running away"),
            })]),
            "pose" => new(Check.Pose, Ids: NamesIn(value, PoseNames.Select((n, i) => ((long)i, n)), "pose")),
            "stand" => new(Check.Stand, Ids: NamesIn(value, StandNames.Select((n, i) => ((long)i, n)), "stand")),
            "reserve" => new(Check.Reserve, Ids: NamesIn(value, reserves.Where(r => r.Value is not null).Select(r => (r.Key, r.Value!)), "reserve")),
            "clues" => CluesIn(value),
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

    // the catalogue's items of a `cat` (1 weapons, 2 sights, 3 ammunition) by name: items of one name (skins, variants) are all of them
    HashSet<long> ItemsIn(string value, long cat, string what) =>
        NamesIn(value, items.Where(i => Py.IsNum(i.Value["cat"]) && Py.L(i.Value["cat"]) == cat).Select(i => (i.Key, ItemName(i.Key))), what);

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
}
