// The rules of the mission objectives in words (`coverage --rules`) and the warnings where a rule disagrees with the data its mission carries beside the titles. Output only: nothing here
// changes what an objective asks (docs/server.md, "Missions").
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    // the bit of a hit's `organ2` and the name of the organ it stands for (see Reader.Bits)
    static readonly (int Bit, string Name)[] OrganBits =
        [(0, "heart"), (1, "right lung"), (2, "left lung"), (3, "liver"), (4, "brain"), (5, "intestines"), (10, "spine"), (21, "spine"), (22, "spine"), (23, "shoulder blade"),
         (24, "neck bones"), (25, "neck bones"), (26, "neck bones"), (28, "skull"), (29, "hip bone")];

    static readonly string[] StandNames = ["no stand", "hunting tower", "tree stand", "ground blind", "tripod", "waterfowl blind", "tripod rifle rest"], PoseNames = ["standing", "crouching", "prone"];

    static string Num(double d) => Math.Round(d, 9).ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>A test's bounds as `>=100 <=200`; an exclusive bound (the next number up or down) is `>` or `<`.</summary>
    static string Bounds(Test t) => string.Join(' ', new[]
    {
        double.IsNegativeInfinity(t.Min) ? "" : (Math.BitIncrement(Math.Round(t.Min, 9)) == t.Min ? ">" : ">=") + Num(t.Min),
        double.IsPositiveInfinity(t.Max) ? "" : (Math.BitDecrement(Math.Round(t.Max, 9)) == t.Max ? "<" : "<=") + Num(t.Max),
    }.Where(b => b.Length > 0));

    static string Mask(long mask) => mask == Reader.Organs ? "soft organs" : mask == Reader.Bits(10, 21, 22, 23, 24, 25, 26, 28, 29) ? "bones"
        : string.Join('+', OrganBits.Where(o => (mask & 1L << o.Bit) != 0).Select(o => o.Name).Distinct().Concat(Enumerable.Range(0, 63).Where(b => (mask & 1L << b) != 0 && OrganBits.All(o => o.Bit != b)).Select(b => "bit " + b)));

    string ItemName(long id) => items.TryGetValue(id, out var item) && Py.IsStr(item["name"]) ? (string)item["name"]! : "item " + id;

    string SpeciesName(long id) => species.TryGetValue(id, out var s) && Py.IsStr(s["name"]) ? ((string)s["name"]!).Split(" (")[0] : "species " + id;

    /// <summary>Species by name; more than half of them are `any species except` the others.</summary>
    string SpeciesNames(IEnumerable<long> ids)
    {
        var set = ids.ToHashSet();
        var all = species.Keys.Where(k => k < 100).ToList();
        return all.Count(set.Contains) * 2 > all.Count ? "any species except " + Names(all.Where(k => !set.Contains(k)).Select(SpeciesName)) : Names(set.Select(SpeciesName));
    }

    static string Names(IEnumerable<string> names) => string.Join(", ", names.Distinct().Order(StringComparer.Ordinal));

    static string StateNames(Test t)
    {
        var ids = t.Ids!;
        if (t.Check == Check.NotState)
            return Names((ids.Contains(9) ? new[] { "charging" } : []).Concat(ids.Contains(5) ? new[] { "running away" } : []).Concat(ids.Except([9, 5, 10, 11, 15]).Select(i => "state " + i)));
        return Reader.States.FirstOrDefault(s => s.First == (t.Check == Check.FirstState) && ids.SetEquals(s.States)).Label ?? "states " + string.Join(',', ids.Order());
    }

    /// <summary>One condition in words, with names instead of ids.</summary>
    string Describe(Test t) => t.Check switch
    {
        Check.Weapon => "weapon: " + Names(t.Ids!.Select(ItemName)),
        Check.Ammo => "ammunition: " + Names(t.Ids!.Select(ItemName)),
        Check.NotAmmo => "not ammunition: " + Names(t.Ids!.Select(ItemName)),
        Check.Scope => t.Ids is null ? "any sight" : "sight: " + Names(t.Ids.Select(ItemName)),
        Check.NoScope => "no sight",
        Check.Permitted => "permitted",
        Check.OneShot => "one shot",
        Check.Organ => "organs, one of: " + string.Join(" | ", t.Ids!.Order().Select(Mask)),
        Check.OrganOnly => "organs only: " + Mask(t.Ids!.Aggregate(0L, (a, m) => a | m)),
        Check.OrganAvoid => "avoids: " + Names(t.Ids!.Select(Mask)),
        Check.Value => "harvest value: " + Bounds(t),
        Check.Distance => "distance: " + Bounds(t) + " m",
        Check.Weight => "weight: " + Bounds(t) + " kg",
        Check.Score => "score: " + Bounds(t),
        Check.Points => "typical points: " + Bounds(t),
        Check.State or Check.FirstState => (t.Check == Check.FirstState ? "first state: " : "state: ") + StateNames(t),
        Check.NotState => "not state: " + StateNames(t),
        Check.Pose => "pose: " + Names(t.Ids!.Select(i => i is >= 0 and < 3 ? PoseNames[i] : "pose " + i)),
        Check.Stand => "stand: " + Names(t.Ids!.Select(i => i is >= 0 and < 7 ? StandNames[i] : "stand " + i)),
        Check.Reserve => "reserve: " + Names(t.Ids!.Select(i => reserves.GetValueOrDefault(i) ?? "reserve " + i)),
        Check.Named => double.IsInfinity(t.Min) ? "named animal, no place" : $"named animal near ({Num(t.Min)}, {Num(t.Max)}) m",
        Check.Species => "species: " + SpeciesNames(t.Ids!),
        Check.Clue => "clues: " + (t.Ids!.Count == 0 ? "any" : Names(t.Ids.Select(i => ((ClueKind)i).ToString().ToLowerInvariant()))) + " x" + Num(Math.Max(t.Min, 1)),
        Check.Coat => "coat: " + Names(t.Ids!.Select(CoatName)),
        Check.NotCoat => "not coat: " + Names(t.Ids!.Select(CoatName)),
        Check.Within => $"in the hunt: {Num(t.Min)}" + (double.IsPositiveInfinity(t.Max) ? "" : $" within {Num(t.Max / 60)} min"),
        Check.Each => $"in the hunt: {Num(t.Min)}, each within {Num(t.Max / 60)} min of the one before",
        Check.SameShot => $"in the hunt: {Num(t.Min)} killed by one shot",
        Check.Different => $"in the hunt: {Num(t.Min)} different animals" + (double.IsPositiveInfinity(t.Max) ? "" : $" within {Num(t.Max / 60)} min"),
        _ => t.Check.ToString().ToLowerInvariant(),
    };

    static string CoatName(long id) => id >= 0 && id < Reader.CoatNames.Length ? Reader.CoatNames[id] : "coat " + id;

    /// <summary>The rule of every objective in words, one line each, sorted by mission and objective: `mission 1 objective 0: <title> => <kind>; <species>; <gender>; ordered; <conditions>`, the
    /// conditions sorted; an objective not understood has `not understood, left over: <words>` before the conditions it did read; an overridden one (`mission_rules.json`, whose entries are
    /// written in these words) has `(override)` after its id.</summary>
    public IEnumerable<string> RuleLines()
    {
        foreach (var m in missions.Values.OrderBy(m => m.Id))
            foreach (var o in m.Objectives.OrderBy(o => o.Id))
            {
                var r = o.Rule;
                var parts = new List<string> { r.Kind.ToString().ToLowerInvariant() };
                if (r.Unknown || r.Kind == Kind.Other)
                    parts.Add("not understood, left over: " + (r.Kind == Kind.Other ? r.Text : r.Left));
                if (r.Species is { } s && r.Kind is Kind.Harvest or Kind.Spot or Kind.Track) parts.Add("species: " + SpeciesName(s));
                if (r.Gender is { } g) parts.Add(g == 1 ? "female" : "male");
                if (r.Ordered) parts.Add("ordered");
                parts.AddRange((r.Tests ?? []).Select(Describe).Order(StringComparer.Ordinal));
                yield return $"mission {m.Id} objective {o.Id}{(overridden.Contains((m.Id, o.Id)) ? " (override)" : "")}: {o.Title} => {string.Join("; ", parts)}";
            }
    }

    /// <summary>Warnings, one line each, where a rule disagrees with the data its mission carries beside the titles; a lead to look at, not an error. In order: `weapons` (the mission's `weapons`
    /// list has a weapon of the family of one the objectives accept, which none accepts), `description` (the `desc` names a weapon or an ammunition no objective accepts while the
    /// objectives name others), `species` (a rule accepts no species of the mission's `species` list) and `never met` (a condition no event can meet); each by mission, then objective.</summary>
    public IEnumerable<string> RuleWarnings()
    {
        var found = new List<(long Mission, long Objective, int Kind, string Text)>();
        foreach (var d in Arr(G(application, "missions", null)).OfType<JsonObject>().Where(d => TryKey(d["id"], out _)).OrderBy(d => Py.L(d["id"])))
        {
            if (!missions.TryGetValue(Py.L(d["id"]), out var m))
                continue;
            var read = m.Objectives.Where(o => o.Rule.Kind == Kind.Harvest && !o.Rule.Unknown).ToList();
            // the names of the items the objectives name (those that ask nothing of the item do not count); null when none does
            HashSet<string>? Accepted(Check check, Func<long, string> name) =>
                read.SelectMany(o => o.Rule.Tests!.Where(t => t.Check == check).SelectMany(t => t.Ids!)).Select(name).ToHashSet() is { Count: > 0 } named ? named : null;
            string Weapon(long id) => Reader.Words(ItemName(id));
            string Ammo(long id) => Reader.Core(ItemName(id));
            var (weaponsAccepted, ammoAccepted) = (Accepted(Check.Weapon, Weapon), Accepted(Check.Ammo, Ammo));

            if (weaponsAccepted is not null)
                foreach (var name in Ids(d["weapons"]).Select(Weapon).Where(n => !weaponsAccepted.Contains(n) && weaponsAccepted.Any(a => Related(a, n))).Distinct())
                    found.Add((m.Id, -1, 0, $"warning weapons: mission {m.Id}: the weapons list has \"{name}\", which no objective accepts, and objectives accept {Quoted(weaponsAccepted.Where(a => Related(a, name)))}"));

            var desc = " " + Reader.Words(Unmarked(Py.Str(G(d, "desc", "")))) + " ";
            IEnumerable<string> Named(int category, Func<long, string> name, HashSet<string>? accepted)
            {
                if (accepted is null)
                    yield break;
                var all = items.Where(i => Py.IsNum(i.Value["cat"]) && Py.L(i.Value["cat"]) == category).Select(i => name(i.Key))
                    .Where(n => n.Length > 0 && desc.Contains(" " + n + " ", StringComparison.Ordinal)).Distinct().ToList();
                foreach (var n in all.Where(n => !accepted.Contains(n) && !all.Any(o => o != n && (" " + o + " ").Contains(" " + n + " ", StringComparison.Ordinal))))
                    yield return n;  // the longest names only: `recurve bow` inside `heavy recurve bow` is not another weapon
            }
            foreach (var name in Named(1, Weapon, weaponsAccepted).Concat(Named(3, Ammo, ammoAccepted)).Order(StringComparer.Ordinal))
                found.Add((m.Id, -1, 1, $"warning description: mission {m.Id}: the description names \"{name}\", which no objective accepts, while objectives name other items"));

            var listed = Ids(d["species"]).Select(x => x % 100).ToHashSet();
            foreach (var o in m.Objectives.OrderBy(o => o.Id))
            {
                var r = o.Rule;
                if (r.Kind is Kind.Harvest or Kind.Spot or Kind.Track && !r.Unknown && listed.Count > 0
                    && (r.Tests?.FirstOrDefault(t => t.Check == Check.Species)?.Ids ?? (r.Species is { } s ? new HashSet<long> { s } : null)) is { } accepts && !accepts.Overlaps(listed))
                    found.Add((m.Id, o.Id, 2, $"warning species: mission {m.Id} objective {o.Id}: the rule accepts {SpeciesNames(accepts)}, none of the mission's species ({SpeciesNames(listed)})"));
                if (!r.Unknown)
                    foreach (var t in r.Tests ?? [])
                        if (Never(t) is { } why)
                            found.Add((m.Id, o.Id, 3, $"warning never met: mission {m.Id} objective {o.Id}: {why}"));
            }
        }
        return found.OrderBy(w => w.Mission).ThenBy(w => w.Kind).ThenBy(w => w.Objective).ThenBy(w => w.Text, StringComparer.Ordinal).Select(w => w.Text);
    }

    static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Distinct().Order(StringComparer.Ordinal).Select(n => "\"" + n + "\""));

    /// <summary>Are two item names of one family: all the words of one are in the other (`recurve bow` and `heavy recurve bow`)?</summary>
    static bool Related(string a, string b)
    {
        var (x, y) = (a.Split(' '), b.Split(' '));
        return x.All(y.Contains) || y.All(x.Contains);
    }

    /// <summary>Why no event can meet the condition, or null: no id is accepted, the lower bound is above the upper one, a named animal has no place (it needs a `special` other than 0, which
    /// the game never sends).</summary>
    string? Never(Test t) =>
        t.Check == Check.Named ? double.IsInfinity(t.Min) ? "a named animal without a place" : null
        : t.Check is not (Check.Clue or Check.NotAmmo or Check.NotState or Check.NotCoat) && t.Ids is { Count: 0 } ? Describe(t) + " accepts nothing"
        : t.Min > t.Max ? Describe(t) + ": the lower bound is above the upper one" : null;
}
