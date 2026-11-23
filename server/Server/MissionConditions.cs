// What a mission objective's title asks of a harvest beyond its kind: the species (when it names several or a short name), the weapon, ammunition or sight, the
// shot and the organs it may or may not hit, the harvest value, the distance, the score, the weight, the animal's state and coat, the player's stance and stand, the reserve,
// a named animal. The title is the objective's only definition (docs/server.md,
// "Missions"), so <see cref="Reader"/> reads those conditions from its English words; a title with words it does not understand is `Unknown`, and an unknown
// objective is never met. The checks run over the events of the kill (<see cref="Kill"/>).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Resurgence.Server;

public sealed partial class State
{
    enum Check { Weapon, Ammo, NotAmmo, Permitted, OneShot, Organ, OrganOnly, OrganAvoid, Value, Distance, Weight, Score, Points, State, NotState, FirstState, Pose, Stand, Scope, NoScope, Reserve, Named, Species, Clue, Coat, NotCoat }

    /// <summary>One condition. `Min` and `Max` bound a number (an open bound is the next number up or down); `Ids` are the accepted ids (species, weapon, ammunition, sight,
    /// animal state, stance, stand `location`, reserve, coat), the refused ones (`NotAmmo`, `NotState`, `NotCoat`) or, for the organ checks, the bit masks: `Organ` is met when the killing shot went
    /// through all the organs of one mask, `OrganOnly` when every shot's organs are within the masks together, `OrganAvoid` when no shot touched one. `Clue` is met by a clue of one of the
    /// kinds in `Ids` (none: any clue) when the animal has `Min` clues of those kinds in its hunt.</summary>
    sealed record Test(Check Check, double Min = double.NegativeInfinity, double Max = double.PositiveInfinity, HashSet<long>? Ids = null);

    /// <summary>A kill as the conditions see it: its harvest (event 3) and spawn (event 7), its hits (event 1, in order; a spot's own event 11), the reserve of the hunt it
    /// happened in, and, when asked for, whether a hit was not permitted, the score and the typical points of the antlers (null: the kill has no trophy event, or a non-typical rack).
    /// A clue has its own event (12) and, when asked for, the clues of its animal in its hunt up to it.</summary>
    sealed class Kill(JsonObject e3, JsonObject e7, Func<List<JsonObject>> hits, long? reserve, Func<(bool Disqualified, double? Score)> scored, Func<double?>? points = null,
                      JsonObject? e12 = null, Func<List<(long Id, JsonObject Body)>>? clues = null)
    {
        public readonly JsonObject E3 = e3, E7 = e7, E12 = e12 ?? new();
        public readonly long? Reserve = reserve;
        public readonly Lazy<List<JsonObject>> Hits = new(hits);
        public readonly Lazy<List<(long Id, JsonObject Body)>> Clues = new(clues ?? (() => []));
        public readonly Lazy<(bool Disqualified, double? Score)> Scored = new(scored);
        public readonly Lazy<double?> Points = new(points ?? (() => null));
    }

    static double? NumOf(JsonObject? o, string key) => o is not null && Py.IsNum(o[key]) ? Py.D(o[key]) : null;

    /// <summary>Is the animal within 1000 m of the place (x, z in metres)? Its position is where its last hit found it, else where its spawn was sent from.
    /// ponytail: the radius is a guess at "by the large beach"; a title's place has no extent.</summary>
    static bool Near(Kill k, double x, double z)
    {
        var at = k.Hits.Value.LastOrDefault() is { } hit && NumOf(hit, "x") is not null ? hit : k.E7;
        return NumOf(at, "x") is { } ax && NumOf(at, "z") is { } az && Math.Sqrt((ax - x) * (ax - x) + (az - z) * (az - z)) <= 1000;
    }

    static bool In(Test t, double? v) => v is { } x && x >= t.Min && x <= t.Max;

    /// <summary>The number a threshold test compares with its bounds: the harvest value, the distance of the killing shot in metres, the weight in kilograms, the score, the typical
    /// points (a female has none); null for another test, or when the kill does not have it.</summary>
    static double? Measured(Test t, Kill k) => t.Check switch
    {
        Check.Value => NumOf(k.E3, "harvest_value"),
        Check.Distance => NumOf(k.Hits.Value.LastOrDefault(), "distance_cms") / 100,
        Check.Weight => NumOf(k.E7, "weight_grams") / 1000,
        Check.Score => k.Scored.Value.Score,
        Check.Points => NumOf(k.E7, "gender") != 1 ? k.Points.Value : null,  // a female never has the rack a title asks for
        _ => null,
    };

    static bool Meets(Test t, Kill k, long after = 0)
    {
        var hits = k.Hits.Value;
        var (first, last) = (hits.FirstOrDefault(), hits.LastOrDefault());  // the killing hit is the last: the weapon, ammunition, distance, stand and state are its
        bool Has(double? v) => v is { } x && t.Ids!.Contains((long)x);
        return t.Check switch
        {
            Check.Weapon => Has(NumOf(last, "weapon_id")),
            Check.Ammo => Has(NumOf(last, "ammunition_id")),
            Check.NotAmmo => NumOf(last, "ammunition_id") is { } ammunition && !t.Ids!.Contains((long)ammunition),
            Check.Permitted => !k.Scored.Value.Disqualified,
            Check.OneShot => hits.Count == 1,
            Check.Organ => NumOf(last, "organ2") is { } bits && t.Ids!.Any(m => ((long)bits & m) == m),
            Check.OrganOnly => hits.Count > 0 && hits.All(h => NumOf(h, "organ2") is > 0 and var b && ((long)b & ~t.Ids!.Aggregate(0L, (a, m) => a | m)) == 0),
            Check.OrganAvoid => hits.Count > 0 && hits.All(h => NumOf(h, "organ2") is { } b && t.Ids!.All(m => ((long)b & m) == 0)),
            Check.Value or Check.Distance or Check.Weight or Check.Score or Check.Points => In(t, Measured(t, k)),
            Check.State => Has(NumOf(last, "animal_state")),
            Check.NotState => NumOf(last, "animal_state") is { } state && !t.Ids!.Contains((long)state),
            Check.FirstState => Has(NumOf(first, "animal_state")),
            Check.Pose => NumOf(last, "pose") is { } pose && t.Ids!.Contains((long)pose & 15),  // the low nibble: 0 stand, 1 crouch, 2 prone
            Check.Stand => Has(NumOf(last, "location")),
            Check.Scope => NumOf(last, "scope_id") is { } scope && scope != -1 && (t.Ids is null || t.Ids.Contains((long)scope)),
            Check.NoScope => NumOf(last, "scope_id") == -1,
            Check.Reserve => k.Reserve is { } r && t.Ids!.Contains(r),
            // a title with a place is met by its species (and gender) there; one without a place, by an animal that carries `special`
            Check.Named => double.IsInfinity(t.Min) ? NumOf(k.E7, "special") is { } special && special != 0 : Near(k, t.Min, t.Max),
            Check.Species => AnimalOf(k.E7, Py.IsNum(k.E3["extended_species"]) ? k.E3["extended_species"] : k.E7["species"]).Species is { } species && t.Ids!.Contains(species),
            Check.Clue => ClueIs(t, k.E12, k.E7) && (t.Min <= 1 || k.Clues.Value.Count(c => c.Id > after && ClueIs(t, c.Body, k.E7)) >= t.Min),
            Check.Coat => Has(NumOf(k.E7, "texture")),
            Check.NotCoat => NumOf(k.E7, "texture") is { } coat && !t.Ids!.Contains((long)coat),  // an animal with no spawn has no coat: it is no other coat either
            _ => false,
        };
    }

    /// <summary>Does the kill meet every condition of the rule (an unknown one never)? `after` is the last event the mission's progress is not owed from: the clues a count adds up are later.</summary>
    static bool Meets(Rule rule, Kill k, long after = 0) => !rule.Unknown && (rule.Tests ?? []).All(t => Meets(t, k, after));

    /// <summary>The reserve of the hunt an event happened in: the latest hunt that started before it.</summary>
    static long? ReserveAt(Db con, long eventId) =>
        con.Scalar("SELECT reserve FROM expeditions WHERE account_id = $a AND COALESCE(first_event, 0) < ? ORDER BY id DESC LIMIT 1", eventId) as long?;

    /// <summary>The kill of a harvest (event 3, stored as event `eventId`): its animal's spawn, its hits, its score and its antler points are read from the stored events when a condition asks.
    /// The animal is the one of that event: individual ids are reused by the next hunt, and two harvests may carry the same JSON.</summary>
    Kill KillOf(Db con, JsonObject e3, long eventId, JsonObject? e7 = null)
    {
        var (individual, harvest) = (e3["individual"], Py.Dumps(e3));
        return new(e3, e7 ?? SpawnBefore(con, individual, eventId), () => HitsBefore(con, individual, eventId), ReserveAt(con, eventId), () =>
        {
            var r = ScoreKill(con, e3, harvest, Trophy, AmmoCheck);
            return (r.Disqualified, Py.IsNum(r.Score) ? Py.D(r.Score) : null);
        }, () => TypicalPoints(e3, TrophyOf(con, individual, eventId)));
    }

    // the event id after which the events of the animal `?1` of the event `?2` are: the later of its spawn (the latest event 7 before) and the first event of its hunt
    const string AnimalFrom = "MAX(COALESCE((SELECT MAX(id) FROM events WHERE account_id = $a AND event = 7 AND individual = ?1 AND id < ?2), 0),"
        + " COALESCE((SELECT first_event FROM expeditions WHERE account_id = $a AND COALESCE(first_event, 0) < ?2 ORDER BY id DESC LIMIT 1), 0))";

    /// <summary>The hits (event 1) on the animal of the harvest stored as event `harvestId`, in order.</summary>
    static List<JsonObject> HitsBefore(Db con, JsonNode? individual, long harvestId) =>
        [.. con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 1 AND individual = ?1 AND id < ?2 AND id > " + AnimalFrom + " ORDER BY id", individual, harvestId).Select(r => Parse(r[0]))];

    /// <summary>The trophy event (22) of the animal of the harvest stored as event `harvestId`: the latest one of its individual after its spawn and in its hunt, and before the individual's
    /// next spawn, so the animal of the same number in a later hunt has its own; one sent after the harvest counts. {} when there is none.</summary>
    static JsonObject TrophyOf(Db con, JsonNode? individual, long harvestId) =>
        con.Rows("SELECT body FROM events WHERE account_id = $a AND event = 22 AND individual = ?1 AND id > " + AnimalFrom
            + " AND id < COALESCE((SELECT MIN(id) FROM events WHERE account_id = $a AND event = 7 AND individual = ?1 AND id > ?2), 9223372036854775807)"
            + " AND id <= COALESCE((SELECT MIN(first_event) FROM expeditions WHERE account_id = $a AND first_event >= ?2), 9223372036854775807) ORDER BY id DESC LIMIT 1",
            individual, harvestId).Select(r => Parse(r[0])).FirstOrDefault() ?? new JsonObject();

    /// <summary>The typical points of both antlers added (the `points` of the trophy event's `left_antler` and `right_antler`: the main beam and the tines, not the `abnormals`); null for a
    /// kill with no trophy event, a trophy that lacks the list of a side (the total is not known) and a non-typical rack (`extended_species` 100 and up).</summary>
    static double? TypicalPoints(JsonObject e3, JsonObject e22)
    {
        var sides = new[] { "left_antler", "right_antler" }.Select(side => e22[side] is JsonObject antler ? antler["points"] as JsonArray : null).ToList();
        return Py.IsNum(e3["extended_species"]) && Py.L(e3["extended_species"]) >= 100 || sides.Any(s => s is null) ? null : sides.Sum(s => s!.Count);
    }

    /// <summary>A spotted animal (event 11, stored as event `eventId`) as a kill with one hit: the spot itself, which carries the player's stand.</summary>
    Kill SpotOf(Db con, JsonObject e11, long eventId, JsonObject e7) => new(new JsonObject(), e7, () => [e11], ReserveAt(con, eventId), () => (false, null));

    /// <summary>A found clue (event 12 `e12`, stored as event `eventId`) as a kill with no hit: the reserve of its hunt, its kind and the clues of the same animal in that hunt can be checked.</summary>
    Kill ClueOf(Db con, long eventId, JsonObject e12, JsonObject e7) =>
        new(new JsonObject(), e7, () => [], ReserveAt(con, eventId), () => (false, null), e12: e12, clues: () => ClueEvents(con, e12["individual"], eventId));

    /// <summary>The clues (event 12) of the animal `individual` in the hunt that event `eventId` is in, up to that event, with their event ids.</summary>
    static List<(long Id, JsonObject Body)> ClueEvents(Db con, JsonNode? individual, long eventId) =>
        [.. con.Rows("SELECT id, body FROM events WHERE account_id = $a AND event = 12 AND individual = ?1 AND id <= ?2 AND id > COALESCE("
            + "(SELECT first_event FROM expeditions WHERE account_id = $a AND COALESCE(first_event, 0) < ?2 ORDER BY id DESC LIMIT 1), 0) ORDER BY id", individual, eventId).Select(r => ((long)r[0], Parse(r[1])))];

    /// <summary>The kinds of clue a title names; `Other` (rooting, water bubbles) no title names, and only a trail takes it.</summary>
    enum ClueKind { Blood, Footprints, Droppings, Call, Bedding, Other }

    static Dictionary<long, ClueKind> ClueTable(params (ClueKind Kind, int[] Types)[] kinds) => kinds.SelectMany(k => k.Types.Select(t => ((long)t, k.Kind))).ToDictionary();

    // The `track_type` of an event 12 by the kind of clue it is. 0 (blood trail), 9 (a heard call) and 12 (bedding) are one id each. The other ids come in pairs of one species' clues: its
    // droppings, then its footprints (a trotting or a fleeing animal leaves the footprints' id too): 1 and 2 for a whitetail, 3 and 4 for a mule deer, 16 and 17 for a moose, 5 and 6 for a
    // turkey, and so on up to 102 and 103 for a wood grouse. 38, 39 and 93 (the European rabbit's droppings and footprints, the wendigo's droppings) are unverified entries, every other id is
    // verified. 20 (rooting) and 31 (water bubbles, the id of nearly every species' bubbles) are `Other`: only a trail's count takes them.
    static readonly Dictionary<long, ClueKind> ClueKinds = ClueTable(
        (ClueKind.Blood, [0]), (ClueKind.Call, [9]), (ClueKind.Bedding, [12]), (ClueKind.Other, [20, 31]),
        (ClueKind.Droppings, [1, 3, 5, 7, 10, 14, 16, 18, 23, 25, 32, 34, 36, 38, 40, 44, 56, 58, 60, 62, 64, 66, 68, 70, 78, 80, 82, 84, 88, 92, 93, 94, 96, 98, 100, 102]),
        (ClueKind.Footprints, [2, 4, 6, 8, 11, 15, 17, 19, 21, 22, 24, 26, 28, 33, 35, 37, 39, 41, 45, 57, 59, 61, 63, 65, 67, 69, 71, 73, 79, 81, 83, 85, 89, 95, 97, 99, 101, 103]));

    /// <summary>The kind of the clue of event 12 `e12` by an animal of `species`; null for a whole `track_type` no kind is known for, and for any other value. The red deer's water bubbles (25)
    /// and the wendigo's (32) use the ids of the roe deer's and the rabbits' droppings: those two ids are `Other` for the red deer and the wendigo, and have no kind without a known animal.</summary>
    static ClueKind? ClueKindOf(JsonObject e12, long? species) =>
        NumOf(e12, "track_type") is { } type && type == Math.Floor(type) && (long)type is var id && (id, species) is not (25 or 32, null)
            ? (id, species) is (25, 12) or (32, 58) ? ClueKind.Other : ClueKinds.TryGetValue(id, out var kind) ? kind : null
            : null;

    /// <summary>Is the clue `e12` of the animal `e7` one the test accepts: a clue of one of the kinds named, or of any kind but `Other` when it names none?</summary>
    static bool ClueIs(Test t, JsonObject e12, JsonObject e7) =>
        ClueKindOf(e12, AnimalOf(e7, e7["species"]).Species) is { } kind && (t.Ids!.Count == 0 ? kind != ClueKind.Other : t.Ids.Contains((long)kind));

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

        static Regex Between(string unit) => new(@"(?:between|(?:(?:at|from)\s+)?(?:a\s+)?range\s+of)\s+(?<a>\d+(?:[.,]\d+)?)\s*(?:" + unit + @"\s*)?(?:and|to|-)\s*(?<b>\d+(?:[.,]\d+)?)\s*" + unit + @"\b", Rx);

        // the number words a title may use for a count (a number of typical points, a number of clues): the index is the number
        static readonly string[] NumberWords = "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty".Split(' ');

        static readonly Regex
            Verb = new(@"\A\s*(?:harvest|take|kill|spot)\b", Rx),
            Conversion = new(@"\([^)]*(?:approx|\bft\b|feet|\blbs?\b|yards?|\byd\b|miles|\bkg\b|\bm\b)[^)]*\)", Rx),
            Coordinates = new(@"\(\s*x\s*:[^)]*\)", Rx),
            Place = new(@"\(\s*x\s*:\s*(?<x>-?\d+(?:[.,]\d+)?)\s*,?\s*y\s*:\s*(?<y>-?\d+(?:[.,]\d+)?)\s*\)", Rx),
            Notes = new(@"\b(?:other organs may be hit|to blame it for making the noise)\b\.?", Rx),
            Gauge = new(@"(\d)\s?ga\b", Rx),
            SameHunt = new(@"\b(?:at any point\s+)?(?:during|in|within|throughout)\s+(?:the\s+|a\s+)?(?:same|one|single)\s+(?:hunt|expedition|session|outing)\b|\bat any point\b", Rx),
            Hundred = new(@"\b(?:at|with|on|of)\s+(?:an?\s+)?100\s?%(?:\s+of)?\s+harvest value", Rx),
            TypicalPoints = Quantity(@"typical\s+points?"), PointCount = new(@"(?<![\w.-])(?<n>\d+)(?:-point|\s?pt)\b(?!\s+score)", Rx),
            NumberWord = new(@"\b(?<w>" + string.Join('|', NumberWords[1..]) + @")(?=\s+typical\s+points?\b)", Rx),
            Quoted = new("\"([^\"]+)\"", Rx),
            PlacePhrase = new(@"\b(?:by|at|near|around|last seen at|in)\s+[^()\d]*?\(\s*x\s*:[^)]*\)", Rx),
            Distance = Quantity(Metres), Kg = Quantity(Kilos), Lb = Quantity(Pounds), BetweenMetres = Between(Metres), BetweenKg = Between(Kilos),
            DistanceWords = new(@"\b(?:(?:from|at)\s+)?(?:a\s+)?distance(?:\s+of)?\b", Rx),
            WeightWords = new(@"\b(?:weighing|weighin|weigh|weights?|a weight(?: of)?)\b", Rx),
            WeightPlain = new(@"\bweight\s+(?:of\s+)?(?:(?<gt>" + Gt + ")|(?<ge>" + Ge + ")|(?<lt>" + Lt + ")|(?<le>" + Le + @"))\s+" + Number + @"\b", Rx),
            ScoreAfter = new(@"(?:(?:with|having|bringing in|and|of)\s+)?(?:an?\s+)?(?:(?:trophy|total)\s+)?scor(?:e|ing|in)\s+(?:of\s+)?(?:(?:(?<gt>" + Gt + ")|(?<ge>" + Ge + ")|(?<lt>" + Lt + ")|(?<le>" + Le
                + @"))\s+(?:a\s+|an\s+|the\s+)*)?" + Number + @"(?:\s+(?:or|and)\s+(?:(?<more>" + MoreWords + ")|(?<less>" + LessWords + @")))?(?:\s*points?)?\b", Rx),
            ScoreBefore = new(@"(?:with\s+|and\s+|of\s+)?(?:(?<gt>" + Gt + ")|(?<ge>" + Ge + ")|(?<lt>" + Lt + ")|(?<le>" + Le + @"))\s+(?:an?\s+)?" + Number + @"\s*points?(?:\s+score)?\b", Rx),
            Permitted = new(@"\b(?:any\s+)?(?:permitted|ethical)\b", Rx),
            OneShot = new(@"\b(?:(?:killed|killing)\s+(?:it\s+)?)?(?:(?:with|in|using|by)\s+)?(?:an?\s+)?(?:one|single|1)[ -](?:shots?|arrows?|bolts?)\b", Rx),
            HeartKilling = new(@"\b(?:the\s+)?heart\s+must\s+be\s+(?:the\s+)?(?:hit\s+)?(?:with\s+the\s+)?killing\s+(?:shot|hit)\b\.?", Rx),
            Avoid = new(@"\b(?:without\s+(?:hitting|penetrating|damaging|touching)\s+(?:it\s+in\s+)?(?:the\s+|any\s+)?(?<o>skull|liver|intestines?|bones?)|(?:the\s+)?(?<o>skull|liver|intestines?|bones?)\s+must\s+not\s+be\s+hit)\b\.?", Rx),
            Organ = new(@"\b(?:(?:with|using|by|in|through|to|on|hitting)\s+)*(?:an?\s+|the\s+)*(?:(?:shot|hit)\s+(?:(?:to|through|in|on)\s+)*(?:the\s+)*)?(?<o>double lung|(?:(?:heart ?(?=shot)|heart|lungs?|brain)(?:\s+(?:and or|or|and))?\s*)+)(?:\s*shots?)?(?<only>\s+only)?\b", Rx),
            NoScope = new(@"\b(?:without(?:\s+a|\s+the|\s+using\s+a)?\s+scope|no\s+scope|using\s+only\s+iron\s+sights|with\s+iron\s+sights)\b", Rx),
            Scoped = new(@"\b(?:an?\s+)?scoped\b|\b(?:with|using)\s+an?\s+scope\b", Rx),
            WeaponNoun = new(@"\s(?<c>shotguns?|rifles?|handguns?|revolvers?|pistols?|muzzleloaders?|crossbows?|longbows?|guns?|bows?(?<x>\s+(?:including|or|and)\s+crossbows?)?)(?=\s)", Rx),
            Traditional = new(@"\s(?:any\s+)?traditional\s+bows?(?=\s)", Rx),
            AmmoNoun = new(@"\s(?:ammo|ammunition|bullets?|rounds?|arrows?|bolts)(?=\s)", Rx),
            AmmoWord = new(@"\s(?<w>buckshot|slugs?|birdshot)(?:\s+(?:shells?|ammo|ammunition|rounds?))?(?=\s)", Rx),
            MountedWith = new(@"\s(?:mounted|fitted|equipped)\s+with(?=\s)", Rx),
            SightNoun = new(@"\ssights?(?=\s)", Rx),
            ThatWas = new(@"\sthat\s+(?:was|were)\s+(?:shot|killed|hit)(?=\s)", Rx),
            Stance = new(@"\s(?:while\s+)?(?<p>prone|crouching|crouched|standing upright|upright)(?=\s)", Rx),
            ExceptAnimal = new(@"\s(?:any\s+)?animals?\s+except(?=\s)", Rx),
            SpeciesOf = new(@"\s(?:one\s+of\s+)?(?:any\s+)?species\s+of(?=\s)", Rx),
            Duck = new(@"\sducks?(?=\s)", Rx),
            Alias = new(@"(?<=\s)(?<a>elk|whitetail|blacktail|pintail|ptarmigan|sitka)(?=\s)", Rx),
            NoCrossbows = new(@"\(\s*no crossbows?\s*\)", Rx),
            OnlyOrgans = new(@"\bonly organs may be hit\b\.?", Rx),
            OnlyNamed = new(@"\bno other vital organs or bones should be hit\b\.?", Rx),
            AvoidAmmo = new(@"\swithout\s+using\s+any\s+type\s+of\s+(?<w>buckshot|slugs?|birdshot)(?:\s+(?:shells?|ammo|ammunition|rounds?))?(?=\s)", Rx),
            AvoidState = new(@"\sthe\s+[a-z0-9]+(?:\s+[a-z0-9]+)?\s+should\s+not\s+be\s+(?<s>(?:running away|charging|fleeing)(?:\s+or\s+(?:running away|charging|fleeing))*)(?:\s+you)?(?=\s)", Rx),
            Examples = new(@"(?<=\b(?:shotguns?|rifles?|handguns?|revolvers?|pistols?|bows?|crossbows?))\s*\((?<list>[^()]*)\)", Rx);

        // spellings of the titles that are no item or species name: a typo, a short form (the title may keep its accents or not, as the platform's text normalising goes); a weight in a
        // made-up unit is read from the kilograms the title gives after it
        static readonly (Regex Spelling, string Name)[] Spellings =
        [
            (new(@"\banch(?=[uü]tz\b)", Rx), "ansch"), (new(@"\bsemi[- ]auto(?!matic)\b", Rx), "semi-automatic"), (new(@"\bcanada moose\b", Rx), "moose"),
            (new(@"\.357 magnum\b(?!\s+(?:revolver|ammunition))", Rx), ".357 revolver"), (new(@"\d+ (?:[a-z]+ )?libras \(or (\d+(?:[.,]\d+)?) kg[^)]*\)", Rx), "$1 kg"),
        ];

        const string Stand = @"(?:waterfowl blinds?|ground blinds?|blinds?|tree ?stands?|tripod ?stands?|shooting tripod rests?|tripod rifle rests?|rifle rests?|tripods?|hunting towers?|towers?|platforms?|stands?)";
        const string StandList = Stand + @"(?:\s+(?:(?:or|and)\s+)?(?:an?\s+|the\s+)?" + Stand + @")*";
        static readonly Regex
            OnStand = new(@"\s(?:while\s+)?(?:from|in|on|inside|using)\s+(?:an?\s+|the\s+)?(?<s>" + StandList + @")(?=\s)", Rx),
            OffStand = new(@"\s(?:without|with no|no|not using|not from)\s+(?:using\s+)?(?:an?\s+|the\s+)?" + StandList + @"(?=\s)", Rx),
            NoStands = new(@"\s(?:an?\s+|the\s+)?" + StandList + @"\s+(?:is|are)\s+not\s+allowed(?=\s)", Rx);

        static readonly string[] Stops = "a an the another any one final in with using from at and or of to while by during same hunt then finally first harvest spot killed shot taken kill take it its that is are be on when only just either each also second third fourth last more ammo ammunition rounds shells loaded bullet bullets s weapon weapons but animal animals fine dandy anywhere".Split(' ');

        // words of a clue objective that ask nothing more: each clue finishes one objective, in order (`another set of tracks`, `a third track`, `a pile of droppings`)
        static readonly string[] ClueStops = "set sets pile piles unique".Split(' ');

        static readonly Regex
            ClueVerb = new(@"\A\s*(?:locate|identify|id|find|register)\b", Rx),
            ClueCount = new(@"\b(?<n>\d+|" + string.Join('|', NumberWords[2..11]) + @")\s+(?:\w+\s+){0,4}?(?:tracks?|droppings?|clues?|footprints?|footsteps?|calls?)\b", Rx),
            ClueWord = new(@"\bclues?\b", Rx), Tracks = new(@"\btracks?\b", Rx),
            Trail = new(@"\A\s*(?:locate|find)\b|\bsame\s+animal\b|\bone\s+and\s+the\s+same\b", Rx);

        // the words of a clue's kind, the most specific first (`blood trail` and `droppings track` before the `tracks` they end with)
        static readonly (Regex Word, ClueKind Kind)[] ClueWords =
        [
            (new(@"\bblood(?:\s+(?:trails?|tracks?))?\b", Rx), ClueKind.Blood), (new(@"\b(?:droppings?|scat)(?:\s+tracks?)?\b", Rx), ClueKind.Droppings),
            (new(@"\b(?:audio(?:\s+(?:clues?|calls?))?|calls?)\b", Rx), ClueKind.Call), (new(@"\b(?:bedding|beds?)\b", Rx), ClueKind.Bedding),
            (new(@"\b(?:footprints?|footsteps?)\b", Rx), ClueKind.Footprints),
        ];

        // an animal state word, the `animal_state`s of a hit it stands for, and whether the animal's first hit (not its last) is the one that tells
        public static readonly (string Label, Regex Word, long[] States, bool First)[] States = [.. new (string, string, long[], bool)[]
        {
            ("airborne", "airborne", [6, 11], false), ("standing", "standing", [3], false), ("walking", "walking", [4], false), ("running", "running", [5], false), ("moving", "moving", [4, 5, 10], false),
            ("charging", @"charging(?:\s+(?:at\s+)?you)?", [9], false), ("fleeing", "fleeing", [5, 10, 11, 15], false), ("swimming", "swimming", [7], false),
            ("burrow exit", @"(?:emerging from|diving into)(?:\s+or\s+(?:emerging from|diving into))?\s+an?\s+burrow\s+exit", [13, 14, 15], false),
            ("idle", "idle", [1, 2, 3, 8, 12], false), ("still", "still", [1, 2, 3, 8, 12], false),
            ("unspooked", "unspooked", [1, 2, 3, 4, 6, 7, 8, 12], true), ("spooked", "spooked", [5, 10, 11, 15, 16], true),
        }.Select(x => (x.Item1, new Regex(@"\s(?:(?:while|when)\s+)?(?:(?:it|the animal)\s+(?:s|is)\s+)?" + x.Item2 + @"(?=\s)", Rx), x.Item3, x.Item4))];

        // the `location` of a hit: 1 hunting tower, 2 tree stand, 3 ground blind, 4 tripod, 5 waterfowl blind, 6 tripod rifle rest; a bare "blind" is a ground or a waterfowl blind,
        // a bare "stand" any raised one but the ground blind, a "platform" any of them
        static IEnumerable<long> StandIds(string words)
        {
            bool Has(string pattern) => Regex.IsMatch(words, pattern, Rx);
            var all = Has(@"\bplatforms?\b");
            var raised = Has(@"(?<!tree ?|tripod ?)\bstands?\b");
            if (all || raised || Has("tower")) yield return 1;
            if (all || raised || Has("tree ?stand")) yield return 2;
            if (all || Has("ground blind") || Has(@"(?<!ground |waterfowl )\bblind\b")) yield return 3;
            if (all || raised || Has(@"tripod ?stand|\btripod\b(?!\s+(?:rifle\s+)?rest)")) yield return 4;
            if (all || raised || Has("waterfowl blind") || Has(@"(?<!ground |waterfowl )\bblind\b")) yield return 5;
            if (all || raised || Has("rifle rest|tripod rest")) yield return 6;
        }

        static readonly string[] Male = ["male", "buck", "bull", "stag", "rooster", "tom", "ram"], Female = ["female", "doe", "cow", "hen", "ewe"];
        public static readonly Regex MaleWord = new(@"\b(?:" + string.Join('|', Male) + @")\b", Rx), FemaleWord = new(@"\b(?:" + string.Join('|', Female) + @")\b", Rx);

        // The coat of an animal is its spawn's (event 7) `texture`. The name of each id, in id order, is the game's own English name of that coat, as its trophy and its harvest
        // show it (several ids share a name: `Common`, `Dark`, `Piebald`, `Chocolate`, `Albino`); the ids from 77 on are coats of the Halloween 2026 event that the game names nowhere.
        public static readonly string[] CoatNames =
        [
            "common", "common", "albino", "common", "melanistic", "piebald", "blonde", "chocolate", "cinnamon", "glacier", "gold", "silver tipped", "grey", "cross", "mythical",
            "brown hybrid", "leucistic", "black hybrid", "albino", "bald leucistic", "light grey leucistic", "brown leucistic", "amber leucistic", "common blue", "mallard hybrid",
            "piebald black", "dark", "dark red", "dark", "orange", "chocolate", "golden", "dark", "light", "dilute", "common", "common", "piebald", "piebald", "piebald", "belted",
            "belted", "common", "abraxis", "caerbannog", "whiro", "sabbatic", "izuna", "ghostaroo", "pale", "camo", "gangsta", "russet", "candy", "monster", "skinless", "hiiden hirvi",
            "mummy hybrid", "cyborg", "pumpkin", "reptile", "blocky normal", "blocky no cream", "blocky normal", "blocky all cream", "blocky badly stirred", "wendigo", "gyuki",
            "eikthyrnir", "anukite", "nanuq", "mahaha", "tulikettu", "jiibayaabooz", "black shuck", "dryad", "orange shuck",
        ];

        // the words for a coat that the names lack: `blue` is the game's short name of `Common Blue` (the snow goose's blue phase, its only coat of that name); `halloween` is any coat
        // the game's Halloween 2026 event gives an animal (each species there has its own, and no animal has one of them outside the event)
        static readonly (string Word, long[] Ids)[] CoatWords =
            [("blue", [23]), ("halloween", [43, 44, 45, 46, 47, 48, 54, 55, 56, 57, 58, 59, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82])];

        // a species' coats that the titles tell apart and the game does not: the Grey Wolf's (36) four common coats are all `Common`; by their colour 0 is grey (the title's
        // `common`), 1 and 42 (black and tan) dark and 3 brown
        static readonly Dictionary<long, (string Word, long[] Ids)[]> SpeciesCoats = new() { [36] = [("common", [0]), ("dark", [1, 42]), ("brown", [3])] };

        static readonly string CoatAlternatives = string.Join('|', CoatNames.Concat(CoatWords.Select(c => c.Word)).Concat(SpeciesCoats.Values.SelectMany(v => v.Select(c => c.Word)))
            .Distinct().OrderByDescending(w => w.Length).Select(Regex.Escape));  // the longest first: "dark red" before "dark"

        // a coat named with its frame: `with fur variation Grey`, `with any fur variation other than common`, `with a color other than Common`, `Chocolate colored`, `(Blue color variation)`
        static readonly Regex CoatFramed = new(@"\s(?:with\s+(?:an?\s+|any\s+)?(?:fur\s+variation|colou?r)\s+(?<other>other\s+than\s+)?(?<c>" + CoatAlternatives + @")|(?<c>" + CoatAlternatives
            + @")\s+(?:colou?red|colou?r\s+variation))(?=\s)", Rx);

        /// <summary>The ids of a coat word for an animal of one of `animals` (none known: any).</summary>
        static long[] CoatIds(string word, IEnumerable<long> animals) =>
            animals.SelectMany(a => SpeciesCoats.GetValueOrDefault(a, [])).Where(c => c.Word == word).SelectMany(c => c.Ids).ToArray() is { Length: > 0 } own ? own
                : [.. CoatWords.Where(c => c.Word == word).SelectMany(c => c.Ids), .. Enumerable.Range(0, CoatNames.Length).Where(i => CoatNames[i] == word).Select(i => (long)i)];

        readonly (long Id, string Name)[] species;
        readonly Regex? speciesList, speciesName, coatBefore;
        readonly List<(string Name, long[] Ids)> weaponNames, sightNames;
        readonly List<(long Id, string Name, string Class, HashSet<string> Words)> weapons = [], sights = [];
        readonly (long Id, string Core, HashSet<string> Words)[] ammo;
        readonly List<(Regex In, long[] Ids)> ammoNames;
        readonly (long Id, Regex In)[] reserves;

        /// <summary>The catalogue's species (normalised names), items and reserves.</summary>
        public Reader(IEnumerable<(long Id, string Name)> species, IEnumerable<JsonObject> items, IEnumerable<KeyValuePair<long, string?>> reserves)
        {
            this.species = [.. species.OrderByDescending(s => s.Name.Length)];
            if (this.species.Length > 0)
            {
                var any = string.Join('|', this.species.Select(s => Regex.Escape(s.Name)));  // the longest name first: "mule deer" before "deer"
                speciesName = new Regex(@"(?<![a-z0-9])(?:" + any + @")(?![a-z0-9])", Rx);
                speciesList = new Regex(@"(?<=\s)(?:" + any + @")(?:\s+or\s+(?:an?\s+|the\s+)?(?:" + any + @"))*(?=\s)", Rx);  // "and" is two animals, not one of two
                // a bare coat word is one only right before the species, a gender word between (`a dark Grey Wolf`, `an Orange female Axis Deer`)
                coatBefore = new Regex(@"\s(?<c>" + CoatAlternatives + @")(?=\s+(?:(?:" + string.Join('|', Male.Concat(Female)) + @")\s+)?(?<s>" + any + @")\s)", Rx);
            }
            var ammoItems = new List<(long Id, string Core, string Note)>();
            var ammoWords = new Dictionary<long, HashSet<string>>();
            foreach (var i in items.Where(i => Py.IsNum(i["id"]) && Py.IsStr(i["name"])))
            {
                var (id, cat, full) = (Py.L(i["id"]), Py.IsNum(i["cat"]) ? Py.L(i["cat"]) : -1, (string)i["name"]!);
                var name = Words(full);
                if (cat is 1 or 2)
                    (cat == 1 ? weapons : sights).Add((id, name, Py.Str(G(i, "subcat", null) is JsonObject sub ? G(sub, "name", "") : ""), [.. name.Split(' ')]));
                else if (cat == 3)
                {
                    ammoItems.Add((id, Core(full), Regex.Match(full, @"\(([^)]*)\)") is { Success: true } note ? Words(note.Groups[1].Value) : ""));
                    ammoWords[id] = [.. name.Split(' ')];
                }
            }
            weaponNames = ByName(weapons);
            sightNames = ByName(sights);
            ammo = [.. ammoItems.Where(a => a.Core.Length > 0).Select(a => (a.Id, a.Core, ammoWords[a.Id])).OrderByDescending(a => a.Core.Length)];
            // an ammunition is named by its core and, when the title gives it, the bracketed note after it (`7mm-08 Ammunition (HV)`); by the core alone it is every item of that core
            ammoNames = [.. ammoItems.Where(a => a.Core.Length > 0)
                .SelectMany(a => a.Note.Length > 0 ? new[] { (a.Core, Note: a.Note, a.Id), (a.Core, Note: "", a.Id) } : new[] { (a.Core, Note: "", a.Id) })
                .GroupBy(a => (a.Core, a.Note)).OrderByDescending(g => g.Key.Core.Length + g.Key.Note.Length)
                .Select(g => (new Regex(@"\s" + Regex.Escape(g.Key.Core) + @"(?:\s+(?:" + string.Join('|', Generic) + @"))*" + (g.Key.Note.Length > 0 ? @"\s+" + Regex.Escape(g.Key.Note) : "") + @"(?=\s)", Rx),
                                g.Select(a => a.Id).ToArray()))];
            this.reserves = [.. reserves.Where(r => r.Value is not null).Select(r => (r.Key, new Regex(@"\s(?:in|at|on|from)(?:\s+the)?\s+" + Regex.Escape(Words(r.Value!)) + @"(?:\s+reserve)?(?=\s)", Rx)))];
        }

        /// <summary>The items by name, the longest name first (items of one name are one entry: skins and variants share it).</summary>
        static List<(string Name, long[] Ids)> ByName(IEnumerable<(long Id, string Name, string Class, HashSet<string> Words)> items) =>
            [.. items.Where(i => i.Name.Length > 0).GroupBy(i => i.Name).Select(g => (g.Key, g.Select(i => i.Id).ToArray())).OrderByDescending(n => n.Key.Length)];

        /// <summary>Lower case without accents or apostrophes, `12GA` as `12 GA`, then <see cref="Normalise"/>d.</summary>
        public static string Words(string s) => Normalise(Gauge.Replace(Fold(s), "$1 ga"));

        static string Fold(string s) => new string([.. s.Normalize(System.Text.NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c is not ('\'' or '’' or '‘')).Select(c => c is '“' or '”' ? '"' : char.ToLowerInvariant(c))]);

        static readonly string[] Generic = ["shells", "ammunition", "bullet", "bullets", "ammo", "arrows", "bolts", "shell"];

        /// <summary>An ammunition's name without a bracketed note and its trailing generic words: `.17 HMR HV` names the item whatever follows it.</summary>
        public static string Core(string name)
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

        static double Coordinate(string n) => Parse(n) is var v && (n.Contains('.') || n.Contains(',')) && Math.Abs(v) < 100 ? v * 1000 : v;

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

        /// <summary>The title's reserve (`in <reserve>`) is cut out of `sn` and becomes a test.</summary>
        void TakeReserve(ref string sn, List<Test> tests)
        {
            foreach (var (id, inReserve) in reserves)
                if (Take(ref sn, inReserve, out _))
                {
                    tests.Add(new(Check.Reserve, Ids: [id]));
                    break;
                }
        }

        /// <summary>The title's coat (`a dark Grey Wolf`, `a Chocolate colored Black Bear`, `with fur variation Piebald Black`, `(Blue color variation)`, `with a color other than Common`)
        /// is cut out of `sn` and becomes a test. A word of one species' coats only (`brown`, the Grey Wolf's) stays for another species, and the objective is not understood; a name of
        /// the game's is read for any species, one that never has that coat too (it is then never met).</summary>
        void TakeCoat(ref string sn, List<Test> tests)
        {
            var m = CoatFramed.Match(sn);
            if (!m.Success && coatBefore?.Match(sn) is { Success: true } before)
                m = before;
            if (!m.Success)
                return;
            var name = m.Groups["s"].Success ? m.Groups["s"].Value : speciesName?.Match(sn).Value;
            var ids = CoatIds(m.Groups["c"].Value, species.Where(x => x.Name == name).Select(x => x.Id));
            sn = sn[..m.Index] + " " + sn[(m.Index + m.Length)..] + (ids.Length == 0 ? " coat " : "");
            if (ids.Length > 0)
                tests.Add(new(m.Groups["other"].Success ? Check.NotCoat : Check.Coat, Ids: [.. ids]));
        }

        /// <summary>The title's species and gender are cut out of `sn`; several species, a short name of some (`Elk`) or `any animal except ...` become a species test.</summary>
        void TakeSpecies(ref string sn, long[]? own, List<Test> tests)
        {
            var except = Take(ref sn, ExceptAnimal, out _);
            var named = new List<long>();
            if (speciesList?.Match(sn) is { Success: true } list)
            {
                sn = sn[..list.Index] + " " + sn[(list.Index + list.Length)..];
                foreach (Match m in speciesName!.Matches(list.Value))
                    named.AddRange(species.Where(x => x.Name == m.Value).Select(x => x.Id));
            }
            long[] like = [], aliased = [];  // a short name of species (`Elk`): the species with that word in their name, the mission's own ones when it lists some of them
            if (named.Count == 0 && Alias.Match(sn) is { Success: true } alias
                && species.Where(x => x.Name.Split(' ').Contains(alias.Groups["a"].Value)).Select(x => x.Id).ToArray() is { Length: > 0 } found)  // a short name no species has stays in the title
            {
                sn = sn[..alias.Index] + " " + sn[(alias.Index + alias.Length)..];
                like = found;
                aliased = like.Intersect(own ?? []).ToArray() is { Length: > 0 } mine ? mine : like;
            }
            if (named.Count > 0 || aliased.Length > 0)
                Take(ref sn, Duck, out _);  // `Mallard duck`
            Take(ref sn, SpeciesOf, out _);  // `one of any species of Ptarmigan`
            if (except && named.Count == 0 && like.Length == 0)
                sn += " animals_except ";  // no species after `any animal except`: the word stays, the objective is not understood
            else if (except) tests.Add(new(Check.Species, Ids: [.. species.Select(x => x.Id).Except(named).Except(like)]));
            else if (named.Count > 1) tests.Add(new(Check.Species, Ids: [.. named]));
            else if (aliased.Length > 0) tests.Add(new(Check.Species, Ids: [.. aliased]));
            if (!Take(ref sn, FemaleWord, out _)) Take(ref sn, MaleWord, out _);
        }

        /// <summary>The conditions of a clue objective's title (its first word is the verb: `ID`, `Locate`, ...): the reserve, the species and the gender as for a harvest, and a `Clue` test:
        /// the kinds the title names (`footprints`, `droppings`, `call`, `audio clue`, `blood trail`, `bedding`, `tracks`; `any clue` names none: any), and how many clues of the one animal it
        /// needs (a number before them, else one). `tracks` are footprints, but the clues of one animal's trail (`Locate 3 ... tracks`, `3 tracks from the same animal`) are any clues. A clue
        /// word it does not read, a place, `different` animals or any other word make it not understood.</summary>
        public (Test[] Tests, string Left) ReadClue(string title, long[]? own = null)
        {
            var tests = new List<Test>();
            var folded = Regex.Replace(Fold(title).Trim(), @"\s+", " ");
            var s = ClueVerb.Replace(folded, " ");
            var sn = " " + Normalise(SameHunt.Replace(Coordinates.Replace(s, " "), " ")) + (Coordinates.IsMatch(s) ? " coordinates" : "") + " ";  // a place is not checked: coordinates leave it not understood
            TakeReserve(ref sn, tests);
            TakeSpecies(ref sn, own, tests);
            var count = 1L;
            if (ClueCount.Match(sn) is { Success: true } many)
            {
                var n = many.Groups["n"];
                count = Math.Clamp(long.TryParse(n.Value, out var digits) ? digits : Array.IndexOf(NumberWords, n.Value), 1, 100);
                sn = sn[..n.Index] + " " + sn[(n.Index + n.Length)..];
            }
            var kinds = new HashSet<long>();
            foreach (var (word, kind) in ClueWords)
                if (Take(ref sn, word, out _))
                    kinds.Add((long)kind);
            // `tracks` are footprints (the original's daily missions `ID tracks from ...` counted footprints only), but several of one animal followed as a trail (`Locate 3 ... tracks`,
            // `3 tracks from the same animal`: a trail goes on with every clue of its animal, rooting and water bubbles too) are any clues; listed with another kind (`tracks or
            // droppings`) they are the footprints
            var tracks = Take(ref sn, Tracks, out _);
            if (tracks && (kinds.Count > 0 || count < 2 || !Trail.IsMatch(folded)))
                kinds.Add((long)ClueKind.Footprints);
            else if (tracks)
                kinds.UnionWith(Enum.GetValues<ClueKind>().Select(k => (long)k));
            var any = Take(ref sn, ClueWord, out _) || tracks;
            if (kinds.Count == 0 && !any)
                sn += " clue_type ";  // a clue word that names no kind this reader knows (`feeding`): the objective is not understood
            tests.Add(new(Check.Clue, count, Ids: kinds));
            return ([.. tests], string.Join(' ', sn.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Stops.Contains(w) && !ClueStops.Contains(w))));
        }

        /// <summary>The conditions the title asks and the words this reader does not understand, empty when it reads all of it (the words of a condition it could not read stay in the title).</summary>
        public (Test[] Tests, string Left) Read(string title, long[]? own = null)
        {
            var tests = new List<Test>();
            var s = Gauge.Replace(Regex.Replace(Fold(title).Trim(), @"\s+", " "), "$1 ga");
            foreach (var (spelling, name) in Spellings)
                s = spelling.Replace(s, name);
            s = Verb.Replace(s, " ");
            s = Notes.Replace(s, " ");
            if (Take(ref s, OnlyOrgans, out _)) tests.Add(new(Check.OrganOnly, Ids: [Organs]));  // every hit has organs and none a bone
            var noCrossbows = Take(ref s, NoCrossbows, out _);  // `(no crossbows)`
            var onlyNamed = Take(ref s, OnlyNamed, out _);  // `through the heart and/or lungs. No other vital organs or bones should be hit`
            // weapons listed as examples of the class before them (`any semi-auto shotgun (20 GA Semi-Automatic Shotgun, ...)`) say nothing more: when each is part of an item's name
            s = Examples.Replace(s, m => m.Groups["list"].Value.Split(',').All(p => Words(p) is { Length: > 0 } w && weapons.Any(x => (" " + x.Name + " ").Contains(" " + w + " ", StringComparison.Ordinal))) ? " " : m.Value);
            var place = Place.Match(s);  // a coordinate with a fraction is in thousands of metres (6.273 is 6273), a whole one in metres
            var quotes = Quoted.Matches(s).Select(q => (Q: q.Groups[1].Value, Weapons: WeaponsNamed(q.Groups[1].Value).ToArray())).ToList();
            if (quotes.Any(q => q.Weapons.Length == 0) && Coordinates.IsMatch(s))
                s = PlacePhrase.Replace(s, " ", 1);  // a named animal is where its coordinates say: the words before them name the place
            s = Quoted.Replace(s, m => WeaponsNamed(m.Groups[1].Value).Any() ? " " + Words(m.Groups[1].Value) + " " : " ");
            if (quotes.Any(q => q.Weapons.Length == 0))
                tests.Add(place.Success ? new(Check.Named, Coordinate(place.Groups["x"].Value), Coordinate(place.Groups["y"].Value)) : new(Check.Named));
            s = SameHunt.Replace(Coordinates.Replace(Conversion.Replace(s, " "), " "), " ");
            if (Take(ref s, Hundred, out _)) tests.Add(new(Check.Value, 100000));
            s = NumberWord.Replace(s, m => Array.IndexOf(NumberWords, m.Groups["w"].Value).ToString(CultureInfo.InvariantCulture));  // `ten typical points`
            if (Take(ref s, TypicalPoints, out var points)) Bound(points, 1, Check.Points, tests);  // `at least 8 typical points`: both antlers together, before the score reads "points"
            else if (Take(ref s, PointCount, out points)) tests.Add(new(Check.Points, Parse(points.Groups["n"].Value)));  // `a 12-point Elk`: at least 12
            var (distance, weight) = (false, false);
            if (Take(ref s, BetweenMetres, out var between)) { tests.Add(new(Check.Distance, Parse(between.Groups["a"].Value), Parse(between.Groups["b"].Value))); distance = true; }
            else if (Take(ref s, Distance, out var d)) { distance = Bound(d, 1, Check.Distance, tests); if (!distance) s += " " + d.Value; }
            if (Take(ref s, BetweenKg, out between)) { tests.Add(new(Check.Weight, Parse(between.Groups["a"].Value), Parse(between.Groups["b"].Value))); weight = true; }
            else if (Take(ref s, Kg, out var kg)) { weight = Bound(kg, 1, Check.Weight, tests); if (!weight) s += " " + kg.Value; }
            else if (Take(ref s, Lb, out var lb)) { weight = Bound(lb, 0.45359237, Check.Weight, tests); if (!weight) s += " " + lb.Value; }
            else if (Take(ref s, WeightPlain, out var plain)) { weight = Bound(plain, 1, Check.Weight, tests); if (!weight) s += " " + plain.Value; }  // no unit: kilograms
            if (distance) s = DistanceWords.Replace(s, " ");
            if (weight) s = WeightWords.Replace(s, " ");
            if (Take(ref s, ScoreAfter, out var score)) { if (!Bound(score, 1, Check.Score, tests)) s += " " + score.Value; }
            else if (Take(ref s, ScoreBefore, out score) && !Bound(score, 1, Check.Score, tests)) s += " " + score.Value;
            var sn = " " + Normalise(s) + " ";
            if (Take(ref sn, Permitted, out _)) tests.Add(new(Check.Permitted));
            TakeReserve(ref sn, tests);
            TakeCoat(ref sn, tests);  // before the species: a bare coat word is one right before it
            TakeSpecies(ref sn, own, tests);
            // `except` ends what the title allows: the weapons named after it (a class, a name, a list of them) are the ones it refuses; what else is left of the tail is read as the rest of the title
            var refused = new HashSet<long>();
            if (sn.IndexOf(" except ", StringComparison.Ordinal) is var cut and >= 0)
            {
                var tail = sn[(cut + " except".Length)..];
                sn = sn[..cut] + " ";
                foreach (var (name, ids) in weaponNames)
                    if (ReplaceFirst(ref tail, " " + name + " "))
                        refused.UnionWith(ids);
                while (WeaponNoun.Match(tail) is { Success: true } noun)
                    refused.UnionWith(TakeWords(ref tail, noun, 6, WeaponsOf));
                sn += refused.Count == 0 ? " except " + tail : tail;  // no weapon after it: the word stays, the objective is not understood
            }
            if (noCrossbows)
                refused.UnionWith(weapons.Where(w => w.Words.Contains("crossbow")).Select(w => w.Id));
            var weaponIds = new HashSet<long>();
            foreach (var (name, ids) in weaponNames)
                if (ReplaceFirst(ref sn, " " + name + " "))
                    weaponIds.UnionWith(ids);
            if (Take(ref sn, Traditional, out _))  // the bows without a compound's wheels or a crossbow's stock
                weaponIds.UnionWith(weapons.Where(w => w.Class == "Bows & Crossbows" && !w.Words.Contains("compound") && !w.Words.Contains("crossbow")).Select(w => w.Id));
            if (Take(ref sn, OneShot, out _)) tests.Add(new(Check.OneShot));  // after the names (a "Single Shot Shotgun" is a weapon), before the nouns (`a single arrow`)
            while (WeaponNoun.Match(sn) is { Success: true } noun)
                weaponIds.UnionWith(TakeWords(ref sn, noun, 6, WeaponsOf));
            if (refused.Count > 0 && weaponIds.Count == 0)
                weaponIds.UnionWith(weapons.Select(w => w.Id));  // `any weapon except a rifle`: every weapon but those
            weaponIds.ExceptWith(refused);
            if (Take(ref sn, AvoidAmmo, out var noAmmo))  // `without using any type of buckshot`
                tests.Add(new(Check.NotAmmo, Ids: [.. ammo.Where(a => a.Core.Split(' ').Contains(noAmmo.Groups["w"].Value.TrimEnd('s'))).Select(a => a.Id)]));
            var ammoIds = new HashSet<long>();
            foreach (var (named_, ids) in ammoNames)
                if (Take(ref sn, named_, out _))
                    ammoIds.UnionWith(ids);
            while (AmmoNoun.Match(sn) is { Success: true } noun)
                ammoIds.UnionWith(TakeWords(ref sn, noun, 4, (phrase, _) => phrase.Length == 0 ? [] : ammo.Where(a => phrase.All(a.Words.Contains)).Select(a => a.Id)));
            if (Take(ref sn, AmmoWord, out var word))
                ammoIds.UnionWith(ammo.Where(a => a.Core.Split(' ').Contains(word.Groups["w"].Value.TrimEnd('s'))).Select(a => a.Id));
            if (Take(ref sn, NoScope, out _)) tests.Add(new(Check.NoScope));  // before the sights: "iron sights" are no sight item
            else if (Take(ref sn, Scoped, out _)) tests.Add(new(Check.Scope));
            Take(ref sn, MountedWith, out _);
            var sightIds = new HashSet<long>();
            foreach (var (name, ids) in sightNames)
                if (ReplaceFirst(ref sn, " " + name + " "))
                    sightIds.UnionWith(ids);
            while (SightNoun.Match(sn) is { Success: true } noun)  // `an Aimpoint sight`
            {
                var found = TakeWords(ref sn, noun, 4, (phrase, _) => Content(phrase) is { Length: > 0 } p ? sights.Where(w => p.All(w.Words.Contains)).Select(w => w.Id) : []);
                if (found.Length == 0 && sightIds.Count == 0)
                {
                    sn += " sight ";  // a sight that is none of the items: the word stays, the objective is not understood
                    break;
                }
                sightIds.UnionWith(found);
            }
            if (Take(ref sn, HeartKilling, out _)) tests.Add(new(Check.Organ, Ids: [1]));
            while (Take(ref sn, Avoid, out var avoid))
                tests.Add(new(Check.OrganAvoid, Ids: AvoidMasks(avoid.Groups["o"].Value)));
            var organTaken = Take(ref sn, Organ, out var organ) && OrganMasks(organ.Groups["o"].Value) is { Count: > 0 };
            if (organTaken)
                tests.Add(new(organ.Groups["only"].Success || onlyNamed ? Check.OrganOnly : Check.Organ, Ids: OrganMasks(organ.Groups["o"].Value)));
            else if (onlyNamed)
                sn += " organs ";  // no organ named for it to be about: the words stay, the objective is not understood
            if (Take(ref sn, Stance, out var stance))
                tests.Add(new(Check.Pose, Ids: [stance.Groups["p"].Value.StartsWith("prone", StringComparison.Ordinal) ? 2 : stance.Groups["p"].Value.StartsWith("crouch", StringComparison.Ordinal) ? 1 : 0]));
            Take(ref sn, ThatWas, out _);
            if (Take(ref sn, AvoidState, out var notNow))  // `the Puma should not be running away or charging you`
                tests.Add(new(Check.NotState, Ids: AvoidedStates(notNow.Groups["s"].Value)));
            foreach (var (_, stateWord, states, first) in States)
                if (Take(ref sn, stateWord, out _))
                    tests.Add(new(first ? Check.FirstState : Check.State, Ids: [.. states]));
            if (Take(ref sn, OffStand, out _) || Take(ref sn, NoStands, out _)) tests.Add(new(Check.Stand, Ids: [0]));
            else if (Take(ref sn, OnStand, out var stand)) tests.Add(new(Check.Stand, Ids: [.. StandIds(stand.Groups["s"].Value)]));
            if (sightIds.Count > 0) tests.Add(new(Check.Scope, Ids: sightIds));
            if (weaponIds.Count > 0) tests.Add(new(Check.Weapon, Ids: weaponIds));
            if (ammoIds.Count > 0) tests.Add(new(Check.Ammo, Ids: ammoIds));
            return ([.. tests], string.Join(' ', sn.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Stops.Contains(w))));
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

        static readonly string[] Articles = ["a", "an", "the", "any"];

        static string[] Content(string[] phrase) => [.. phrase.Where(w => !Articles.Contains(w))];

        // the alternatives of a phrase before a class noun (`any 243 or 223 rifle`): the name words of each, an empty phrase being one empty alternative
        static string[][] Alternatives(string[] phrase) => [.. string.Join(' ', phrase).Split(" or ").Select(g => Content(g.Split(' ', StringSplitOptions.RemoveEmptyEntries)))];

        /// <summary>The weapons of a class noun with the name words before it; with alternatives (`any .243 or .223 rifle`) the weapons of each, and none when one of them names none.</summary>
        IEnumerable<long> WeaponsOf(string[] phrase, Match noun)
        {
            var groups = Alternatives(phrase);
            if (groups.Length > 1 && groups.Any(g => g.Length == 0))
                return [];
            var found = groups.Select(g => weapons.Where(w => WeaponOf(w, noun, g)).Select(w => w.Id).ToArray()).ToArray();
            return found.All(f => f.Length > 0) ? found.SelectMany(f => f) : [];
        }

        // does the weapon fit a class noun and the name words before it? (a bare "bow" is not a crossbow; "bow or crossbow" is both)
        static bool WeaponOf((long Id, string Name, string Class, HashSet<string> Words) w, Match noun, string[] words)
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
                "gun" => w.Class != "Bows & Crossbows" && !w.Words.Contains("crossbow"),
                _ => w.Class == "Bows & Crossbows" && (noun.Groups["x"].Success || w.Words.Contains("bow")),
            };
            return fits && words.All(w.Words.Contains);
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

        // the `animal_state`s a title refuses: charging 9, running away (fleeing) 5, 10, 11 and 15
        static HashSet<long> AvoidedStates(string words) =>
            [.. (words.Contains("charging") ? new long[] { 9 } : []).Concat(words.Contains("running") || words.Contains("fleeing") ? [5, 10, 11, 15] : [])];

        // the bits of a hit's `organ2` (bit n is 1 << n): heart 0, right lung 1, left lung 2, liver 3, brain 4, intestines 5, the spine 10, 21 and 22, the shoulder blade 23, the neck
        // bones 24 to 26, the skull 28, the hip bone 29
        public static long Bits(params int[] bits) => bits.Aggregate(0L, (m, b) => m | 1L << b);

        // the soft organs of a hit's `organ2`: every bit but the bones (and the antlers' 27)
        public static readonly long Organs = Bits([.. Enumerable.Range(0, 30).Where(b => b is not (10 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29))]);

        // a mask is met when all its bits are: the heart, either lung, a double lung shot both, the brain
        static HashSet<long> OrganMasks(string words) =>
            words.Contains("double lung") ? [Bits(1, 2)]
            : [.. (words.Contains("heart") ? new[] { Bits(0) } : []).Concat(words.Contains("lung") ? [Bits(1), Bits(2)] : []).Concat(words.Contains("brain") ? [Bits(4)] : [])];

        static HashSet<long> AvoidMasks(string word) => [word switch
        {
            "skull" => Bits(28), "liver" => Bits(3), var w when w.StartsWith("intestine", StringComparison.Ordinal) => Bits(5),
            _ => Bits(10, 21, 22, 23, 24, 25, 26, 28, 29),  // the bones
        }];
    }

    /// <summary>The coverage report of the catalogue's missions: how many objectives the server understands (its kind, species, gender and conditions), how many it does not,
    /// and the objectives it does not, with their mission ids (those objectives are never met); with `rules` every objective's rule in words (<see cref="RuleLines"/>), then the warnings (<see cref="RuleWarnings"/>) when there are any.
    /// The `coverage` command prints it.</summary>
    public IEnumerable<string> MissionCoverage(bool rules = false)
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
        yield return $"missions fully readable: {missions.Count - not.Select(x => x.Mission).Distinct().Count()} of {missions.Count}";
        yield return "understood by kind: " + string.Join(", ", byKind);
        yield return "conditions read: " + string.Join(", ", all.SelectMany(x => x.Objective.Rule.Tests ?? []).GroupBy(t => t.Check).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        yield return "not understood: of a kind the server does not read " + not.Count(x => x.Objective.Rule.Kind == Kind.Other) + ", with a condition it does not read " + not.Count(x => x.Objective.Rule.Kind != Kind.Other);
        yield return "objectives not understood, by mission:";
        foreach (var x in not.OrderBy(x => x.Mission))
            yield return $"mission {x.Mission} objective {x.Objective.Id}: {x.Objective.Title}";
        if (rules)
        {
            yield return "rules:";
            foreach (var line in RuleLines())
                yield return line;
        }
        var warnings = RuleWarnings().ToList();
        if (warnings.Count > 0)
            yield return "warnings:";
        foreach (var warning in warnings)
            yield return warning;
    }
}
