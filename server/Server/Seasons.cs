// The leaderboards by season (docs/server.md#leaderboards-and-seasons): every hunter's best harvests, sorted per species by score and by range, for one season or for all time (season 0).
// A harvest counts in the season its kill time falls in (the `seasons` table). Season 1 holds the restored harvests (the saved personal bests, with their original time), Season 2 and
// later this server's own harvests (`harvests`); hidden harvests, refused ones (score 0) and banned accounts are left out. One row per hunter and species on each board.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The first season that holds this server's harvests; an earlier time (a harvest made before the first day) belongs here too, since Season 1 is the restored ones only.</summary>
    public const long FirstServerSeason = 2;

    /// <summary>A kill that may rank: `Rec` is its personal-best record (`{distance` in mm, `confirm_ts`, `weapon_id`, `animal_id`, `gender`, `texture`, `score`, `species}`), `Kill` its id here (0: restored).</summary>
    sealed record Cand(long Species, double Score, double Mm, JsonObject Rec, long Ts, long Season, long Kill);

    /// <summary>One place on a board.</summary>
    public sealed record BoardRow(long Rank, long Account, string Handle, double Value, JsonNode? Weapon, long Ts, long Kill, long Animal);

    /// <summary>The kills of an account that may rank, with their seasons: the restored personal bests (the saved reply of season 0: each species' best score and best range, as the game saved them)
    /// and this server's harvests that are not hidden and have a score.
    /// ponytail: every view reads the hit events of each scored kill (a scan of the account's events each); store the last hit on the harvest when that gets slow.</summary>
    List<Cand> Candidates(Db con, IReadOnlyList<Store.Season> seasons)
    {
        var found = new List<Cand>();
        var saved = con.Rows("SELECT path, body FROM extras WHERE account_id = $a AND path LIKE 'Leaderboard!_personal!_!_%' ESCAPE '!'")
            .Where(r => System.Text.RegularExpressions.Regex.Match((string)r[0], @"__season_no=0\.json$").Success)
            .Select(r => JsonNode.Parse((string)r[1])).FirstOrDefault();
        foreach (var row in ((saved as JsonObject)?["data"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var key in (string[])["score", "distance"])
                if (row[key] is JsonObject rec && Py.IsNum(row["specie"]))
                {
                    var ts = (long)Num(rec["confirm_ts"]);
                    found.Add(new(Py.L(row["specie"]), Num(rec["score"]), Num(rec["distance"]), (JsonObject)rec.DeepClone(), ts, 1, 0));  // a restored kill is Season 1 whatever time its backup says
                }
        foreach (var r in con.Rows("SELECT k.individual, k.harvest, h.body, k.kill_id FROM kills k JOIN harvests h ON h.account_id = $a AND h.kill_id = k.kill_id"
                                   + " WHERE k.account_id = $a AND h.hidden = 0 ORDER BY k.kill_id"))
            try
            {
                var page = Parse(r[2]);
                if (Num(page["score"]) <= 0 || !Py.IsNum(page["species"])) continue;
                var last = KillHits(con, Py.FromSql(r[0]), (string)r[1]) is { Count: > 0 } hits ? hits[^1] : null;
                var ts = new DateTimeOffset(DateTime.ParseExact((string)page["timestamp"]!, "yyyy-MM-dd'T'HH:mm:ss'Z'", null,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)).ToUnixTimeSeconds();
                var rec = new JsonObject
                {
                    ["distance"] = Times10(G(last ?? new JsonObject(), "distance_cms", 0)), ["confirm_ts"] = ts,
                    ["weapon_id"] = C(last?["weapon_id"]), ["animal_id"] = (long)r[3], ["gender"] = C(page["gender"]), ["texture"] = C(page["texture_id"]),
                    ["score"] = Py.Fixed(Num(page["score"]), 4), ["species"] = C(page["species"]),
                };
                found.Add(new(Py.L(page["species"]), Num(page["score"]), Num(rec["distance"]), rec, ts, Math.Max(FirstServerSeason, Store.SeasonAt(seasons, ts)), (long)r[3]));
            }
            catch (Exception ex) when (ex is not Microsoft.Data.Sqlite.SqliteException)  // a value a player sent, of any kind: the kill is not counted
            {
                Console.WriteLine($"leaderboards: a kill is not counted: {ex.Message}");
            }
        return found;
    }

    /// <summary>Each species' best score and best range among the kills of a season (0: all of them); the earlier kill wins a tie.</summary>
    static IEnumerable<(long Species, Cand Score, Cand Range)> Bests(IEnumerable<Cand> kills, long season) =>
        kills.Where(c => season == 0 || c.Season == season).OrderBy(c => c.Ts).GroupBy(c => c.Species)
            .Select(g => (g.Key, g.MaxBy(c => c.Score)!, g.MaxBy(c => c.Mm)!));

    /// <summary>`Leaderboard/personal` of an account for a season (0: all time): `{status: "done", data: [{specie, score, distance}]}`, one row per species with a kill in the season,
    /// `score` and `distance` the record kills (`{distance` in mm, `confirm_ts`, `weapon_id`, `animal_id`, `gender`, `texture`, `score` as text, `species}`).</summary>
    JsonObject PersonalBest(Db con, long season)
    {
        var data = new JsonArray();
        foreach (var (species, score, range) in Bests(Candidates(con, Store.Seasons()), season).OrderBy(b => b.Species))
            data.Add(new JsonObject { ["distance"] = range.Rec.DeepClone(), ["score"] = score.Rec.DeepClone(), ["specie"] = species });
        return new JsonObject { ["status"] = "done", ["data"] = data };
    }

    /// <summary>The boards of a season (0: all time) by species id: the score board (`range` false) or the range board, every hunter with a best above 0 (a banned account and the recorded
    /// player's are left out), best first, a tie to the earlier kill, then the lower account id. The callers cut the top.
    /// ponytail: scans every account on each call (the per-player tables are read one connection each, as <see cref="Store.GameBoard"/>); keep the result a few seconds when the accounts grow into the thousands.</summary>
    public SortedDictionary<long, List<BoardRow>> Boards(bool range, long season)
    {
        var seasons = Store.Seasons();
        List<(long Id, string Handle)> accounts;
        using (var con = Store.Open())
            accounts = [.. con.Rows("SELECT id, handle FROM accounts WHERE banned = 0 AND id IS NOT ? ORDER BY id", LocalAccount).Select(r => ((long)r[0], (string)r[1]))];
        var all = new SortedDictionary<long, List<(long Id, string Handle, Cand Kill)>>();
        foreach (var (id, handle) in accounts)
        {
            using var con = Con(id);
            foreach (var (species, score, best) in Bests(Candidates(con, seasons), season))
            {
                var c = range ? best : score;
                if ((range ? c.Mm : c.Score) > 0)
                    (all.TryGetValue(species, out var list) ? list : all[species] = []).Add((id, handle, c));
            }
        }
        var boards = new SortedDictionary<long, List<BoardRow>>();
        foreach (var (species, list) in all)
            boards[species] = [.. list.OrderByDescending(e => range ? e.Kill.Mm : e.Kill.Score).ThenBy(e => e.Kill.Ts).ThenBy(e => e.Id)
                .Select((e, i) => new BoardRow(i + 1, e.Id, e.Handle, range ? e.Kill.Mm / 1000 : e.Kill.Score, e.Kill.Rec["weapon_id"], e.Kill.Ts, e.Kill.Kill, (long)Num(e.Kill.Rec["animal_id"])))];
        return boards;
    }

    /// <summary>The Hall of Fame of an ended season: per species the best score and the best range; empty for season 0 and the current season.</summary>
    public List<(long Species, BoardRow? Score, BoardRow? Range)> HallOfFame(long season)
    {
        if (season == 0 || Store.Seasons().Last().No == season) return [];
        var (score, range) = (Boards(false, season), Boards(true, season));
        return [.. score.Keys.Union(range.Keys).Order().Select(s => (s, score.TryGetValue(s, out var a) ? a[0] : null, range.TryGetValue(s, out var b) ? b[0] : null))];
    }

    /// <summary>The name of a species by id for a board.</summary>
    public string SpeciesLabel(long species) => SpeciesName(species) ?? "#" + species.ToString(CultureInfo.InvariantCulture);

    /// <summary>The name of a weapon item ("" when unknown).</summary>
    public string WeaponName(JsonNode? weapon) => Lookup(items, weapon)?["name"] is { } w && Py.IsStr(w) ? (string)w! : "";

    // ---- the calls of the original's pages (Page_content/leaderboards_all, leaderboard_details, leaderboards_hall_of_fame, Leaderboard/current_personal), answered from the same boards

    /// <summary>A row of the original's boards: `{id, animal_id, rank, scoretype, score, scoreType, user: {id, handle, avatar}}`; `score` the score or the range in metres (the Hall of Fame's score a text); `scoretype` 1 score, 2 range, null in the Hall of Fame.</summary>
    JsonObject PageRow(BoardRow r, bool range, bool hall, string site) => new()
    {
        ["id"] = r.Animal, ["animal_id"] = r.Animal, ["rank"] = r.Rank, ["scoretype"] = hall ? null : range ? 2 : 1,
        ["score"] = !range && hall ? r.Value.ToString(CultureInfo.InvariantCulture) : r.Value, ["scoreType"] = null,
        ["user"] = new JsonObject { ["id"] = r.Account, ["handle"] = r.Handle, ["avatar"] = AvatarUrl(r.Account, site) },
    };

    JsonObject PageBoard(long species, List<BoardRow> rows, bool range, int top, string site) => new()
    {
        ["id"] = species, ["define"] = Py.Str(Lookup(this.species, (JsonNode)species)?["define"]).ToLowerInvariant(), ["name"] = Py.Str(Lookup(this.species, (JsonNode)species)?["name"] ?? SpeciesLabel(species)),
        ["scoretype"] = range ? 2 : 1, ["total"] = 1000, ["records"] = new JsonArray([.. rows.Take(top).Select(r => (JsonNode?)PageRow(r, range, false, site))]),
    };

    /// <summary>`Page_content/leaderboards_all` (`type` 1 score, 2 range): a block per species with a ranked hunter in the current season, its best 5.</summary>
    public byte[] BoardsAllReply(bool range, string site) =>
        Py.DumpBytes(new JsonArray([.. Boards(range, Store.Seasons().Last().No).Select(b => (JsonNode?)PageBoard(b.Key, b.Value, range, 5, site))]));

    /// <summary>`Page_content/leaderboard_details` (`type`, `species`): the species' best 100 of the current season; null for a species the catalogue lacks.</summary>
    public byte[]? BoardDetailsReply(bool range, long species, string site)
    {
        if (Lookup(this.species, (JsonNode)species) is null) return null;
        var board = PageBoard(species, Boards(range, Store.Seasons().Last().No).GetValueOrDefault(species) ?? [], range, 100, site);
        board["position"] = null;  // the page asks Leaderboard/current_personal for the player's own place
        return Py.DumpBytes(board);
    }

    /// <summary>`Page_content/leaderboards_hall_of_fame` (`season_no`, default the season before the current one): `[{speciesId, score, range}]` of an ended season, a species with no winner left out; `[]` for the current season, all time and a season that does not exist.</summary>
    public byte[] HallOfFameReply(long? season, string site)
    {
        var seasons = Store.Seasons();
        var wanted = season ?? (seasons.Count > 1 ? seasons[^2].No : 0);
        var rows = seasons.Any(s => s.No == wanted) ? HallOfFame(wanted) : [];
        return Py.DumpBytes(new JsonArray([.. rows.Select(h => (JsonNode?)new JsonObject
        {
            ["speciesId"] = h.Species, ["score"] = h.Score is { } s ? PageRow(s, false, true, site) : null, ["range"] = h.Range is { } r ? PageRow(r, true, true, site) : null,
        })]));
    }

    /// <summary>`Leaderboard/current_personal` (`user_id`): the account's places on the boards of the current season, `[{species_id, score: {total, rank, animal_id, score}, distance_min: {…}}]`
    /// (`total` and `rank` the place); `[]` for a user id that is none.</summary>
    public byte[] CurrentPersonalReply(long userId)
    {
        if (PublicAccount(userId) is not { } account) return "[]"u8.ToArray();
        var season = Store.Seasons().Last().No;
        var (score, range) = (Boards(false, season), Boards(true, season));
        static JsonObject? Place(SortedDictionary<long, List<BoardRow>> boards, long species, long account) =>
            boards.GetValueOrDefault(species)?.FirstOrDefault(r => r.Account == account) is { } r
                ? new JsonObject { ["total"] = r.Rank, ["rank"] = r.Rank, ["animal_id"] = r.Animal, ["score"] = r.Value } : null;
        return Py.DumpBytes(new JsonArray([.. score.Keys.Union(range.Keys).Order().Select(species => (species, Score: Place(score, species, account), Range: Place(range, species, account)))
            .Where(p => p.Score is not null || p.Range is not null)
            .Select(p =>
            {
                var row = new JsonObject { ["species_id"] = p.species };
                if (p.Score is not null) row["score"] = p.Score;
                if (p.Range is not null) row["distance_min"] = p.Range;
                return (JsonNode?)row;
            })]));
    }

    /// <summary>An application reply with `season` the current season (a community server); anything that is no object stays as it is.</summary>
    public byte[] WithSeason(byte[] body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject reply)
            {
                reply["season"] = Store.Seasons().Last().No;
                return Py.DumpBytes(reply);
            }
        }
        catch (System.Text.Json.JsonException) { }  // a body that is no JSON object stays as it is
        return body;
    }
}
