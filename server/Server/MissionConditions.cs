// What a mission objective's title asks of a harvest beyond its kind, species and gender: the weapon or ammunition, the shot, the harvest value, the distance, the
// score, the weight, the animal's state, the stand, the scope, the reserve, a named animal. The title is the objective's only definition (docs/server.md,
// "Missions"), so <see cref="Reader"/> reads those conditions from its English words; a title with words it does not understand is `Unknown`, and an unknown
// objective is never met. The checks run over the events of the kill (<see cref="Kill"/>).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    enum Check { Weapon, Ammo, Permitted, OneShot, Organ, Value, Distance, Weight, Score, State, FirstState, Stand, Scope, NoScope, Reserve, Named }

    /// <summary>One condition. `Min` and `Max` bound a number (an open bound is the next number up or down); `Ids` are the accepted ids (weapon, ammunition, animal state,
    /// stand `location`, reserve) or, for `Organ`, the accepted bit masks: one is met when the shot went through all its organs.</summary>
    sealed record Test(Check Check, double Min = double.NegativeInfinity, double Max = double.PositiveInfinity, HashSet<long>? Ids = null);

    /// <summary>A kill as the conditions see it: its harvest (event 3) and spawn (event 7), its hits (event 1, in order; a spot's own event 11), the reserve of the hunt it
    /// happened in, and, when asked for, whether a hit was not permitted and the score.</summary>
    sealed class Kill(JsonObject e3, JsonObject e7, Func<List<JsonObject>> hits, long? reserve, Func<(bool Disqualified, double? Score)> scored)
    {
        public readonly JsonObject E3 = e3, E7 = e7;
        public readonly long? Reserve = reserve;
        public readonly Lazy<List<JsonObject>> Hits = new(hits);
        public readonly Lazy<(bool Disqualified, double? Score)> Scored = new(scored);
    }

    static double? NumOf(JsonObject? o, string key) => o is not null && Py.IsNum(o[key]) ? Py.D(o[key]) : null;

    static bool In(Test t, double? v) => v is { } x && x >= t.Min && x <= t.Max;

    static bool Meets(Test t, Kill k)
    {
        var hits = k.Hits.Value;
        var (first, last) = (hits.FirstOrDefault(), hits.LastOrDefault());  // the killing hit is the last: the weapon, ammunition, distance, stand and state are its
        bool Has(double? v) => v is { } x && t.Ids!.Contains((long)x);
        return t.Check switch
        {
            Check.Weapon => Has(NumOf(last, "weapon_id")),
            Check.Ammo => Has(NumOf(last, "ammunition_id")),
            Check.Permitted => !k.Scored.Value.Disqualified,
            Check.OneShot => hits.Count == 1,
            Check.Organ => NumOf(last, "organ2") is { } bits && t.Ids!.Any(m => ((long)bits & m) == m),
            Check.Value => In(t, NumOf(k.E3, "harvest_value")),
            Check.Distance => In(t, NumOf(last, "distance_cms") / 100),
            Check.Weight => In(t, NumOf(k.E7, "weight_grams") / 1000),
            Check.Score => In(t, k.Scored.Value.Score),
            Check.State => Has(NumOf(last, "animal_state")),
            Check.FirstState => Has(NumOf(first, "animal_state")),
            Check.Stand => Has(NumOf(last, "location")),
            Check.Scope => NumOf(last, "scope_id") is { } scope && scope != -1,
            Check.NoScope => NumOf(last, "scope_id") == -1,
            Check.Reserve => k.Reserve is { } r && t.Ids!.Contains(r),
            Check.Named => NumOf(k.E7, "special") is { } special && special != 0,
            _ => false,
        };
    }

    /// <summary>Does the kill meet every condition of the rule (an unknown one never)?</summary>
    static bool Meets(Rule rule, Kill k) => !rule.Unknown && (rule.Tests ?? []).All(t => Meets(t, k));

    /// <summary>The reserve of the hunt an event happened in: the latest hunt that started before it.</summary>
    static long? ReserveAt(Db con, long eventId) =>
        con.Scalar("SELECT reserve FROM expeditions WHERE account_id = $a AND COALESCE(first_event, 0) < ? ORDER BY id DESC LIMIT 1", eventId) as long?;

    /// <summary>The kill of a harvest (event 3, stored as event `eventId`): its animal's spawn, its hits and its score are read from the stored events when a condition asks.</summary>
    Kill KillOf(Db con, JsonObject e3, long eventId, JsonObject? e7 = null)
    {
        var harvest = Py.Dumps(e3);
        return new(e3, e7 ?? KillEvents(con, e3["individual"], harvest).E7, () => KillHits(con, e3["individual"], harvest), ReserveAt(con, eventId), () =>
        {
            var r = ScoreKill(con, e3, harvest, Trophy, AmmoCheck);
            return (r.Disqualified, Py.IsNum(r.Score) ? Py.D(r.Score) : null);
        });
    }

    /// <summary>A spotted animal (event 11, stored as event `eventId`) as a kill with one hit: the spot itself, which carries the player's stand.</summary>
    Kill SpotOf(Db con, JsonObject e11, long eventId, JsonObject e7) => new(new JsonObject(), e7, () => [e11], ReserveAt(con, eventId), () => (false, null));

    /// <summary>The species (0 to 99) and gender of an animal: the harvest's `extended_species` (or the spawn's `species`) and the spawn's `gender`.</summary>
    static (long? Species, long? Gender) AnimalOf(JsonObject e7, JsonNode? number) =>
        (Py.IsNum(number) ? Mod100(number) : null, Py.IsNum(e7["gender"]) ? Py.L(e7["gender"]) : null);

    /// <summary>Reads a harvest or spot objective's title into its conditions. The title is folded to lower case without accents or apostrophes; each condition's words are
    /// cut out of it, and what is left after the words that carry no condition (articles, "any", "in the same hunt", the species and gender) makes the objective unknown.</summary>
    sealed class Reader
    {
        const RegexOptions Rx = RegexOptions.CultureInvariant;
        const string Number = @"(?<n>\d+(?:[.,]\d+)?)", MoreWords = "more|higher|greater|further|farther|longer|heavier|over|above", LessWords = "less|lower|closer|under|fewer|shorter|lighter|below";
        const string Gt = "more than|greater than|over|above|beyond|farther than|further than|longer than|heavier than|higher than|exceeding";
        const string Ge = @"(?:(?:at|from) )?a minimum(?: distance)? of|minimum(?: distance)? of|at least|minimum|min";
        const string Lt = "less than|under|below|closer than|shorter than|lighter than|lower than|fewer than";
        const string Le = @"(?:(?:at|from) )?a maximum(?: distance)? of|maximum(?: distance)? of|at most|maximum|up to|within|max";
        const string Metres = @"(?:meters?|metres?|m)", Kilos = "(?:kgs?|kilos?|kilograms?)", Pounds = "(?:lbs?|pounds?)";

        static Regex Quantity(string unit) => new(
            @"(?:(?:from|at)\s+)?(?:(?<gt>" + Gt + ")|(?<ge>" + Ge + ")|(?<lt>" + Lt + ")|(?<le>" + Le + @"))\s+(?:(?:a|an|the|of|distance|weight|range|weighing)\s+)*" + Number + @"\s*" + unit
            + @"\b|" + Number + @"\s*" + unit + @"\b\s+(?:or|and)\s+(?:(?<more>" + MoreWords + ")|(?<less>" + LessWords + @"))\b", Rx);

        static Regex Between(string unit) => new(@"between\s+(?<a>\d+(?:[.,]\d+)?)\s*(?:" + unit + @"\s*)?(?:and|to|-)\s*(?<b>\d+(?:[.,]\d+)?)\s*" + unit + @"\b", Rx);

        static readonly Regex
            OrderedStart = new(@"\A\s*(?:then|finally|first|after)\b[\s,:-]*", Rx),
            Verb = new(@"\A\s*(?:harvest|spot)\b", Rx),
            Conversion = new(@"\([^)]*(?:approx|\bft\b|feet|\blbs?\b|yards?|\byd\b|miles|\bkg\b|\bm\b)[^)]*\)", Rx),
            Coordinates = new(@"\(\s*x\s*:[^)]*\)", Rx),
            Notes = new(@"\b(?:other organs may be hit|no other vital organs or bones should be hit)\b\.?", Rx),
            Gauge = new(@"(\d)\s?ga\b", Rx),
            SameHunt = new(@"\b(?:at any point\s+)?(?:during|in|within|throughout)\s+(?:the\s+|a\s+)?(?:same|one|single)\s+(?:hunt|expedition|session)\b|\bat any point\b", Rx),
            Hundred = new(@"\b(?:at|with|on|of)\s+(?:an?\s+)?100\s?%(?:\s+of)?\s+harvest value", Rx),
            Quoted = new("\"([^\"]+)\"", Rx),
            PlacePhrase = new(@"\b(?:by|at|near|around|last seen at|in)\s+[^()\d]*?\(\s*x\s*:[^)]*\)", Rx),
            Distance = Quantity(Metres), Kg = Quantity(Kilos), Lb = Quantity(Pounds), BetweenMetres = Between(Metres), BetweenKg = Between(Kilos),
            DistanceWords = new(@"\b(?:(?:from|at)\s+)?(?:a\s+)?distance(?:\s+of)?\b", Rx),
            WeightWords = new(@"\b(?:weighing|weigh|weights?|a weight(?: of)?)\b", Rx),
            ScoreAfter = new(@"(?:(?:with|having|bringing in|and|of)\s+)?(?:an?\s+)?(?:trophy\s+)?scor(?:e|ing|in)\s+(?:of\s+)?(?:(?:(?<gt>" + Gt + ")|(?<ge>" + Ge + ")|(?<lt>" + Lt + ")|(?<le>" + Le
                + @"))\s+(?:a\s+|an\s+|the\s+)*)?" + Number + @"(?:\s+(?:or|and)\s+(?:(?<more>" + MoreWords + ")|(?<less>" + LessWords + @")))?(?:\s*points?)?\b", Rx),
            ScoreBefore = new(@"(?:with\s+|and\s+|of\s+)?(?:(?<gt>" + Gt + ")|(?<ge>" + Ge + ")|(?<lt>" + Lt + ")|(?<le>" + Le + @"))\s+(?:an?\s+)?" + Number + @"\s*points?(?:\s+score)?\b", Rx),
            Permitted = new(@"\b(?:any\s+)?(?:permitted|ethical)\b", Rx),
            OneShot = new(@"\b(?:(?:with|in|using|by)\s+)?(?:an?\s+)?(?:one|single|1)[ -]shots?\b", Rx),
            Organ = new(@"\b(?:(?:with|using|by|in|through|to|on)\s+)*(?:an?\s+|the\s+)*(?:(?:shot|hit)\s+(?:(?:to|through|in|on)\s+)*(?:the\s+)*)?(?<o>double lung|(?:(?:heart ?(?=shot)|heart|lungs?)(?:\s+(?:and or|or|and))?\s*)+|brain)(?:\s+shots?)?\b", Rx),
            NoScope = new(@"\b(?:without(?:\s+a|\s+the|\s+using\s+a)?\s+scope|no\s+scope|using\s+only\s+iron\s+sights|with\s+iron\s+sights)\b", Rx),
            Scoped = new(@"\b(?:an?\s+)?scoped\b", Rx),
            WeaponNoun = new(@"\s(?<c>shotguns?|rifles?|handguns?|revolvers?|pistols?|muzzleloaders?|crossbows?|longbows?|bows?(?<x>\s+(?:including|or|and)\s+crossbows?)?)(?=\s)", Rx),
            AmmoNoun = new(@"\s(?:ammo|ammunition|bullets?|rounds?)(?=\s)", Rx),
            AmmoWord = new(@"\s(?<w>buckshot|slugs?|birdshot)(?:\s+(?:shells?|ammo|ammunition|rounds?))?(?=\s)", Rx);

        const string Stand = @"(?:waterfowl blind|ground blind|blind|tree ?stand|tripod ?stand|shooting tripod rest|tripod rifle rest|rifle rest|tripod|hunting tower|tower)";
        static readonly Regex
            OnStand = new(@"\s(?:while\s+)?(?:from|in|on|inside|using)\s+(?:an?\s+|the\s+)?(?<s>" + Stand + @"(?:\s+(?:(?:or|and)\s+)?(?:an?\s+|the\s+)?" + Stand + @")*)(?=\s)", Rx),
            OffStand = new(@"\s(?:without|with no|no|not using|not from)\s+(?:using\s+)?(?:an?\s+|the\s+)?" + Stand + @"(?:\s+(?:(?:or|and)\s+)?(?:an?\s+|the\s+)?" + Stand + @")*(?=\s)", Rx);

        static readonly string[] Stops = "a an the another any one final in with using from at and or of to while by during same hunt then finally first harvest spot killed shot taken kill take it its that is are be on when only just either each also second third fourth last more ammo ammunition rounds shells loaded bullet bullets s".Split(' ');

        // an animal state word, the `animal_state`s of a hit it stands for, and whether the animal's first hit (not its last) is the one that tells
        static readonly (Regex Word, long[] States, bool First)[] States = [.. new (string, long[], bool)[]
        {
            ("airborne", [6, 11], false), ("standing", [3], false), ("walking", [4], false), ("running", [5], false), ("moving", [4, 5, 10], false), ("charging", [9], false),
            ("fleeing", [5, 10, 11, 15], false), ("unspooked", [1, 2, 3, 4, 6, 7, 8, 12], true), ("spooked", [5, 10, 11, 15, 16], true),
        }.Select(x => (new Regex(@"\s(?:(?:while|when)\s+)?(?:it\s+(?:s|is)\s+)?" + x.Item1 + @"(?=\s)", Rx), x.Item2, x.Item3))];

        // the `location` of a hit: 1 hunting tower, 2 tree stand, 3 ground blind, 4 tripod, 5 waterfowl blind, 6 tripod rifle rest; a bare "blind" is a ground or a waterfowl blind
        static IEnumerable<long> StandIds(string words)
        {
            bool Has(string pattern) => Regex.IsMatch(words, pattern, Rx);
            if (Has("tower")) yield return 1;
            if (Has("tree ?stand")) yield return 2;
            if (Has("ground blind") || Has(@"(?<!ground |waterfowl )\bblind\b")) yield return 3;
            if (Has(@"tripod ?stand|\btripod\b(?!\s+(?:rifle\s+)?rest)")) yield return 4;
            if (Has("waterfowl blind") || Has(@"(?<!ground |waterfowl )\bblind\b")) yield return 5;
            if (Has("rifle rest|tripod rest")) yield return 6;
        }

        static readonly string[] Male = ["male", "buck", "bull", "stag", "rooster", "tom", "ram"], Female = ["female", "doe", "cow", "hen", "ewe"];
        public static readonly Regex MaleWord = new(@"\b(?:" + string.Join('|', Male) + @")\b", Rx), FemaleWord = new(@"\b(?:" + string.Join('|', Female) + @")\b", Rx);

        readonly (long Id, string Name)[] species;
        readonly List<(string Name, long[] Ids)> weaponNames;
        readonly List<(long Id, string Name, string Class, HashSet<string> Words)> weapons = [];
        readonly (long Id, string Core, HashSet<string> Words)[] ammo;
        readonly (long Id, Regex In)[] reserves;

        /// <summary>The catalogue's species (normalised names), items and reserves.</summary>
        public Reader(IEnumerable<(long Id, string Name)> species, IEnumerable<JsonObject> items, IEnumerable<KeyValuePair<long, string?>> reserves)
        {
            this.species = [.. species.OrderByDescending(s => s.Name.Length)];
            var names = new Dictionary<string, HashSet<long>>();
            var ammoItems = new List<(long, string, HashSet<string>)>();
            foreach (var i in items.Where(i => Py.IsNum(i["id"])))
            {
                var (id, cat) = (Py.L(i["id"]), Py.IsNum(i["cat"]) ? Py.L(i["cat"]) : -1);
                if (cat == 1 && Py.IsStr(i["name"]))
                {
                    var name = Words((string)i["name"]!);
                    weapons.Add((id, name, Py.Str(G(i, "subcat", null) is JsonObject sub ? G(sub, "name", "") : ""), [.. name.Split(' ')]));
                    (names.TryGetValue(name, out var set) ? set : names[name] = []).Add(id);
                }
                else if (cat == 3 && Py.IsStr(i["name"]))
                    ammoItems.Add((id, Core(Py.Str(i["name"])), [.. Words((string)i["name"]!).Split(' ')]));
            }
            weaponNames = [.. names.Where(kv => kv.Key.Length > 0).Select(kv => (kv.Key, kv.Value.ToArray())).OrderByDescending(n => n.Key.Length)];
            ammo = [.. ammoItems.Where(a => a.Item2.Length > 0).OrderByDescending(a => a.Item2.Length)];
            this.reserves = [.. reserves.Where(r => r.Value is not null).Select(r => (r.Key, new Regex(@"\s(?:in|at|on|from)(?:\s+the)?\s+" + Regex.Escape(Words(r.Value!)) + @"(?:\s+reserve)?(?=\s)", Rx)))];
        }

        /// <summary>Lower case without accents or apostrophes, `12GA` as `12 GA`, then <see cref="Normalise"/>d.</summary>
        static string Words(string s) => Normalise(Gauge.Replace(Fold(s), "$1 ga"));

        static string Fold(string s) => new string([.. s.Normalize(System.Text.NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c is not ('\'' or '’' or '‘')).Select(c => c is '“' or '”' ? '"' : char.ToLowerInvariant(c))]);

        static readonly string[] Generic = ["shells", "ammunition", "bullet", "bullets", "ammo", "arrows", "bolts", "shell"];

        /// <summary>An ammunition's name without a bracketed note and its trailing generic words: `.17 HMR HV` names the item whatever follows it.</summary>
        static string Core(string name)
        {
            var w = Words(Regex.Replace(name, @"\([^)]*\)", "")).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (w.Count > 1 && Generic.Contains(w[^1])) w.RemoveAt(w.Count - 1);
            return string.Join(' ', w);
        }

        static bool Take(ref string s, Regex rx, out Match m)
        {
            m = rx.Match(s);
            if (m.Success) s = s[..m.Index] + " " + s[(m.Index + m.Length)..];
            return m.Success;
        }

        static double Parse(string n) => double.Parse(n.Replace(',', '.'), CultureInfo.InvariantCulture);

        /// <summary>The quantity's test, or false (nothing added) when it has no word that says over or under.</summary>
        static bool Bound(Match m, double scale, Check check, List<Test> tests)
        {
            var n = Parse(m.Groups["n"].Value) * scale;
            (double, double)? range = m.Groups["gt"].Success ? (Math.BitIncrement(n), double.PositiveInfinity) : m.Groups["ge"].Success || m.Groups["more"].Success ? (n, double.PositiveInfinity)
                : m.Groups["lt"].Success ? (double.NegativeInfinity, Math.BitDecrement(n)) : m.Groups["le"].Success || m.Groups["less"].Success ? (double.NegativeInfinity, n) : null;
            if (range is { } r) tests.Add(new(check, r.Item1, r.Item2));
            return range is not null;
        }

        /// <summary>The conditions the title asks and whether it has words this reader does not understand (the words of a condition it could not read stay in the title).</summary>
        public (Test[] Tests, bool Unknown) Read(string title)
        {
            var tests = new List<Test>();
            var s = Gauge.Replace(Regex.Replace(Fold(title).Trim(), @"\s+", " "), "$1 ga");
            s = Verb.Replace(OrderedStart.Replace(s, ""), " ");
            s = Notes.Replace(s, " ");
            var quotes = Quoted.Matches(s).Select(q => (Q: q.Groups[1].Value, Weapons: WeaponsNamed(q.Groups[1].Value).ToArray())).ToList();
            if (quotes.Any(q => q.Weapons.Length == 0) && Coordinates.IsMatch(s))
                s = PlacePhrase.Replace(s, " ", 1);  // a named animal is where its coordinates say: the words before them name the place
            s = Quoted.Replace(s, m => WeaponsNamed(m.Groups[1].Value).Any() ? " " + Words(m.Groups[1].Value) + " " : " ");
            if (quotes.Any(q => q.Weapons.Length == 0)) tests.Add(new(Check.Named));
            s = SameHunt.Replace(Coordinates.Replace(Conversion.Replace(s, " "), " "), " ");
            if (Take(ref s, Hundred, out _)) tests.Add(new(Check.Value, 100000));
            var (distance, weight) = (false, false);
            if (Take(ref s, BetweenMetres, out var between)) { tests.Add(new(Check.Distance, Parse(between.Groups["a"].Value), Parse(between.Groups["b"].Value))); distance = true; }
            else if (Take(ref s, Distance, out var d)) { distance = Bound(d, 1, Check.Distance, tests); if (!distance) s += " " + d.Value; }
            if (Take(ref s, BetweenKg, out between)) { tests.Add(new(Check.Weight, Parse(between.Groups["a"].Value), Parse(between.Groups["b"].Value))); weight = true; }
            else if (Take(ref s, Kg, out var kg)) { weight = Bound(kg, 1, Check.Weight, tests); if (!weight) s += " " + kg.Value; }
            else if (Take(ref s, Lb, out var lb)) { weight = Bound(lb, 0.45359237, Check.Weight, tests); if (!weight) s += " " + lb.Value; }
            if (distance) s = DistanceWords.Replace(s, " ");
            if (weight) s = WeightWords.Replace(s, " ");
            if (Take(ref s, ScoreAfter, out var score)) { if (!Bound(score, 1, Check.Score, tests)) s += " " + score.Value; }
            else if (Take(ref s, ScoreBefore, out score) && !Bound(score, 1, Check.Score, tests)) s += " " + score.Value;
            var sn = " " + Normalise(s) + " ";
            if (Take(ref sn, Permitted, out _)) tests.Add(new(Check.Permitted));
            foreach (var (id, inReserve) in reserves)
                if (Take(ref sn, inReserve, out _))
                {
                    tests.Add(new(Check.Reserve, Ids: [id]));
                    break;
                }
            foreach (var (_, name) in species)
                if (ReplaceFirst(ref sn, " " + name + " "))
                    break;
            if (!Take(ref sn, FemaleWord, out _)) Take(ref sn, MaleWord, out _);
            var weaponIds = new HashSet<long>();
            foreach (var (name, ids) in weaponNames)
                if (ReplaceFirst(ref sn, " " + name + " "))
                    weaponIds.UnionWith(ids);
            while (WeaponNoun.Match(sn) is { Success: true } noun)
            {
                var ids = TakeWords(ref sn, noun, 6, (phrase, noun) => weapons.Where(w => WeaponOf(w, noun, phrase)).Select(w => w.Id));
                weaponIds.UnionWith(ids);
            }
            var ammoIds = new HashSet<long>();
            foreach (var (id, core, _) in ammo)
                if (ReplaceFirst(ref sn, " " + core + " "))
                    ammoIds.Add(id);
            while (AmmoNoun.Match(sn) is { Success: true } noun)
                ammoIds.UnionWith(TakeWords(ref sn, noun, 4, (phrase, _) => phrase.Length == 0 ? [] : ammo.Where(a => phrase.All(a.Words.Contains)).Select(a => a.Id)));
            if (Take(ref sn, AmmoWord, out var word))
                ammoIds.UnionWith(ammo.Where(a => a.Core.Split(' ').Contains(word.Groups["w"].Value.TrimEnd('s'))).Select(a => a.Id));
            if (Take(ref sn, OneShot, out _)) tests.Add(new(Check.OneShot));
            if (Take(ref sn, Organ, out var organ) && OrganMasks(organ.Groups["o"].Value) is { Count: > 0 } masks) tests.Add(new(Check.Organ, Ids: masks));
            foreach (var (stateWord, states, first) in States)
                if (Take(ref sn, stateWord, out _))
                    tests.Add(new(first ? Check.FirstState : Check.State, Ids: [.. states]));
            if (Take(ref sn, OffStand, out _)) tests.Add(new(Check.Stand, Ids: [0]));
            else if (Take(ref sn, OnStand, out var stand)) tests.Add(new(Check.Stand, Ids: [.. StandIds(stand.Groups["s"].Value)]));
            if (Take(ref sn, NoScope, out _)) tests.Add(new(Check.NoScope));
            else if (Take(ref sn, Scoped, out _)) tests.Add(new(Check.Scope));
            if (weaponIds.Count > 0) tests.Add(new(Check.Weapon, Ids: weaponIds));
            if (ammoIds.Count > 0) tests.Add(new(Check.Ammo, Ids: ammoIds));
            return ([.. tests], sn.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => !Stops.Contains(w)));
        }

        /// <summary>The first occurrence of `what` (words with a space on each side) in `s` becomes one space; false when there is none.</summary>
        static bool ReplaceFirst(ref string s, string what)
        {
            var at = s.IndexOf(what, StringComparison.Ordinal);
            if (at >= 0) s = s[..at] + " " + s[(at + what.Length)..];
            return at >= 0;
        }

        /// <summary>The weapon items whose name has the quoted words (`"Snakebite"` names the compound bows of that name).</summary>
        IEnumerable<long> WeaponsNamed(string quoted)
        {
            var q = Words(quoted);
            return q.Length == 0 ? [] : weapons.Where(w => (" " + w.Name + " ").Contains(" " + q + " ", StringComparison.Ordinal)).Select(w => w.Id);
        }

        // does the weapon fit a class noun and the name words before it? (a bare "bow" is not a crossbow; "bow or crossbow" is both)
        static bool WeaponOf((long Id, string Name, string Class, HashSet<string> Words) w, Match noun, string[] phrase)
        {
            var word = noun.Groups["c"].Value.Split(' ')[0].TrimEnd('s');
            var fits = word switch
            {
                "shotgun" => w.Class == "Shotguns",
                "rifle" => w.Class == "Rifles",
                "handgun" => w.Class == "Handguns",
                "muzzleloader" => w.Class == "Muzzleloaders",
                "revolver" or "pistol" => w.Class == "Handguns" && w.Words.Contains(word),
                "crossbow" or "longbow" => w.Words.Contains(word),
                _ => w.Class == "Bows & Crossbows" && (noun.Groups["x"].Success || w.Words.Contains("bow")),
            };
            return fits && phrase.All(w.Words.Contains);
        }

        /// <summary>Cuts the noun (a match in `s`) and the longest run of up to `most` words before it that `find` accepts (it gets the words and the noun) out of `s`; the ids it found.</summary>
        static long[] TakeWords(ref string s, Match noun, int most, Func<string[], Match, IEnumerable<long>> find)
        {
            var before = s[..noun.Index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var k = Math.Min(most, before.Length); k >= 0; k--)
                if (find(before[^k..], noun).ToArray() is { Length: > 0 } ids)
                {
                    s = " " + string.Join(' ', before[..^k]) + " " + s[(noun.Index + noun.Length)..];
                    return ids;
                }
            s = s[..noun.Index] + " " + s[(noun.Index + noun.Length)..];  // a noun no weapon or ammunition fits is left out of the words: it names nothing
            return [];
        }

        // the bits of a hit's `organ2`: heart 1, right lung 2, left lung 4, brain 16; a mask is met when all its bits are
        static HashSet<long> OrganMasks(string words) =>
            words.Contains("double lung") ? [6] : words.Contains("brain") ? [16]
            : [.. (words.Contains("heart") ? new long[] { 1 } : []).Concat(words.Contains("lung") ? [2, 4] : [])];
    }

    /// <summary>The coverage report of the catalogue's missions: how many objectives the server understands (its kind, species, gender and conditions), how many it does not,
    /// and the ids of the missions with an objective it does not (those objectives are never met). The `coverage` command prints it.</summary>
    public IEnumerable<string> MissionCoverage()
    {
        if (missions.Count == 0)
        {
            yield return "missions: none (no recorded catalogue, or a local server)";
            yield break;
        }
        var all = missions.Values.SelectMany(m => m.Objectives.Select(o => (Mission: m.Id, Objective: o))).ToList();
        bool Understood(Rule r) => r.Kind != Kind.Other && !r.Unknown;
        var byKind = all.GroupBy(x => x.Objective.Rule.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count(x => Understood(x.Objective.Rule))}/{g.Count()}");
        var not = all.Where(x => !Understood(x.Objective.Rule)).ToList();
        yield return $"missions: {missions.Count}, objectives: {all.Count}";
        yield return $"understood: {all.Count - not.Count}, not understood: {not.Count} (never met until a version reads them)";
        yield return "understood by kind: " + string.Join(", ", byKind);
        yield return "conditions read: " + string.Join(", ", all.SelectMany(x => x.Objective.Rule.Tests ?? []).GroupBy(t => t.Check).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        yield return "not understood: of a kind the server does not read " + not.Count(x => x.Objective.Rule.Kind == Kind.Other) + ", with a condition it does not read " + not.Count(x => x.Objective.Rule.Kind != Kind.Other);
        yield return "missions with an objective not understood: " + string.Join(' ', not.Select(x => x.Mission).Distinct().Order());
    }
}
