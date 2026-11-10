// Daily missions on a community server (docs/server.md, "Daily missions"): the day's mission is picked from the facts file's templates by the date, the same for
// everyone; the account's streak is one JSON row of `daily_missions`; the objectives are the missions' (Missions.cs), and a completed day pays, tells the game
// (update 17) and adds a day to the statistics.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed partial class State
{
    sealed record Daily(Facts.DailyTemplate Template, MissionDef Def);

    const string DailyPath = "Daily_Mission_get_daily_missions_calendar.json";
    const string DateFormat = "yyyy-MM-dd";
    const int CalendarDays = 7, MaxBonusCycles = 10;

    readonly List<Daily> dailies = [];

    DateOnly Today() => DateOnly.FromDateTime(DateTime.UnixEpoch.AddSeconds(Clock()));

    static DateOnly DateOf(JsonObject state) => DateOnly.ParseExact(Py.Str(state["date"]), DateFormat, CultureInfo.InvariantCulture);

    /// <summary>The facts file's templates as missions whose objectives are read by the same rules as the catalogue's (their ids are 1, 2, ... in order). A template
    /// with an objective that is no harvest, spot or clue, or that names no species of the catalogue, would never complete or accept any animal: it is left out and logged.</summary>
    void LoadDailyMissions(Func<string, Rule> ruleOf)
    {
        if (facts.DailyCycle.Length == 0)
            return;
        List<long> skipped = [];
        foreach (var t in facts.DailyTemplates)
        {
            var objectives = t.Objectives.Select((title, i) => new Objective(i + 1, title, ruleOf(title))).ToArray();
            if (objectives.Length > 0 && objectives.All(o => o.Rule.Kind is Kind.Harvest or Kind.Spot or Kind.Track && o.Rule.Species is not null))
                dailies.Add(new(t, new MissionDef(t.Id, t.Title, false, [], 0, 0, objectives)));
            else
                skipped.Add(t.Id);
        }
        if (skipped.Count > 0)
            Console.WriteLine($"daily missions: templates {string.Join(", ", skipped)} are left out: an objective is no harvest, spot or clue, or names no species of the catalogue");
    }

    /// <summary>The difficulty of the streak's day (0 is the first), by the cycle.</summary>
    Facts.DailyDifficulty DifficultyAt(long day) => facts.DailyDifficulties.Single(d => d.Id == facts.DailyCycle[day % facts.DailyCycle.Length]);

    /// <summary>The gm$ of the streak's day: the difficulty's reward, 10 % more for each completed cycle, up to 10 cycles.</summary>
    long DailyReward(long day) => DifficultyAt(day).Reward * (10 + Math.Min(day / facts.DailyCycle.Length, MaxBonusCycles)) / 10;

    /// <summary>The mission of a date and difficulty: the templates of the difficulty in the file's order, one per day in turn; null when it has none.</summary>
    Daily? DailyOf(DateOnly date, long difficulty)
    {
        var pool = dailies.Where(d => d.Template.Difficulty == difficulty).ToList();
        return pool.Count == 0 ? null : pool[date.DayNumber % pool.Count];
    }

    /// <summary>The template the state's `mission` names; null when there is none.</summary>
    Daily? MissionOf(JsonObject state) => state["mission"] is { } id ? dailies.FirstOrDefault(d => d.Template.Id == Py.L(id)) : null;

    /// <summary>A new account's row, or a restored one's: `is_frozen` and `broken_streak_info` of the saved calendar (`extras`) as they were, and while frozen the
    /// date it showed (so the mission stays the one shown); a streak's length is not in a backup, so it starts at the first day, and without a saved calendar the
    /// account is frozen until it activates. `days` are the days already counted here (a restore keeps them).</summary>
    static JsonObject NewDaily(Db con, DateOnly today, JsonNode? days)
    {
        var saved = con.Scalar("SELECT body FROM extras WHERE account_id = $a AND path = ?", DailyPath) is string text ? JsonNode.Parse(text) as JsonObject : null;
        var info = saved?["broken_streak_info"] as JsonObject;
        var frozen = saved is null || Py.Truthy(saved["is_frozen"]);
        var shown = (saved?["calendar"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(e => Py.Truthy(e["today"]))?["date"];
        var date = frozen && Py.IsStr(shown) && DateOnly.TryParseExact((string)shown!, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : today;
        return new JsonObject
        {
            ["frozen"] = frozen, ["day"] = 0, ["date"] = date.ToString(DateFormat), ["done"] = false, ["objectives"] = new JsonArray(),
            ["has_broken"] = Py.Truthy(info?["has_broken_streak"]), ["larger"] = Py.IsNum(info?["streak_larger"]) ? Math.Max(0, Py.L(info!["streak_larger"])) : 0,
            ["days"] = days?.DeepClone() ?? new JsonObject(),
        };
    }

    static void SaveDaily(Db con, JsonObject state) =>
        con.Exec("INSERT INTO daily_missions (account_id, body) VALUES ($a, ?1) ON CONFLICT (account_id) DO UPDATE SET body = ?1", Py.Dumps(state));

    /// <summary>The account's daily state, brought to today and saved when that changed it: `frozen`, `day` (the streak's day today's mission belongs to, 0 first),
    /// `date` (that day's date; a frozen account's is the stale one), `mission` (the template of the day: picked by the date when the day begins, and kept while the
    /// account is frozen and when it activates), `done` and the finished `objectives`, `has_broken` and `larger` (the broken streak), `days` (the days completed
    /// here by difficulty id). A completed day is followed by the next one on the next date; a day that ends without its mission completed, or a date skipped,
    /// freezes the account, breaks the streak (its length is kept when it had a day) and ends the day count. A row without `frozen` (a restore keeps only `days`)
    /// is made again. Call inside a transaction.</summary>
    JsonObject DailyState(Db con)
    {
        var today = Today();
        var text = con.Scalar("SELECT body FROM daily_missions WHERE account_id = $a") as string;
        var kept = text is null ? null : Parse(text);
        var s = kept?["frozen"] is null ? NewDaily(con, today, kept?["days"]) : kept;
        if (!Py.Truthy(s["frozen"]) && DateOf(s) is var date && date < today)
        {
            var done = Py.Truthy(s["done"]);
            var day = Py.L(s["day"]);
            if (done && date.AddDays(1) == today)
            {
                s["day"] = day + 1;
                s["date"] = today.ToString(DateFormat);
            }
            else
            {
                var length = day + (done ? 1 : 0);
                if (length > 0)
                {
                    s["has_broken"] = true;
                    s["larger"] = length;
                }
                s["frozen"] = true;
                s["day"] = 0;
            }
            s["mission"] = null;
            s["done"] = false;
            s["objectives"] = new JsonArray();
        }
        if (MissionOf(s) is null)
            s["mission"] = DailyOf(DateOf(s), DifficultyAt(Py.L(s["day"])).Id)?.Template.Id;
        if (Py.Dumps(s) != text)
            SaveDaily(con, s);
        return s;
    }

    /// <summary>Today's daily mission meets an event (<see cref="Advance"/>): an objective of the kind that `meets` accepts is finished; the last one completes the
    /// day: its gm$ are paid (update 15 and a wallet row), the game is told (update 17) and the day is counted. A frozen account's mission does not count.</summary>
    void DailyAdvance(Db con, Kind kind, Func<Rule, bool> meets, Action<JsonObject> now)
    {
        if (dailies.Count == 0)
            return;
        var s = DailyState(con);
        var day = Py.L(s["day"]);
        var difficulty = DifficultyAt(day);
        if (Py.Truthy(s["frozen"]) || Py.Truthy(s["done"]) || MissionOf(s) is not { } d)
            return;
        var finished = Ids(s["objectives"]).ToList();
        if (Meet(d.Def, finished, kind, meets) is null)
            return;
        s["objectives"] = List(finished);
        if (d.Def.Objectives.All(o => finished.Contains(o.Id)))
        {
            s["done"] = true;
            var days = (JsonObject)s["days"]!;
            var key = difficulty.Id.ToString();
            days[key] = Py.IsNum(days[key]) ? Py.L(days[key]) + 1 : 1;
            if (AddCurrency(con, "gm", DailyReward(day), "Reward for Daily Mission") is { } balance)
                now(new JsonObject { ["amount"] = balance, ["type"] = "gm", ["update"] = 15 });
            now(new JsonObject { ["update"] = 17, ["mission_id"] = d.Template.Id, ["difficulty_id"] = difficulty.Id });
        }
        SaveDaily(con, s);
    }

    /// <summary>Daily_Mission/activate_user_progress: a frozen account starts a new streak at its first day, today (the broken streak is let go); any other
    /// is left as it is. True; null when the server has no daily missions.</summary>
    public bool? DailyActivate(long account)
    {
        if (dailies.Count == 0)
            return null;
        using var con = Con(account);
        con.Begin();
        var s = DailyState(con);
        if (Py.Truthy(s["frozen"]))
        {
            s["frozen"] = false;
            s["day"] = 0;
            s["date"] = Today().ToString(DateFormat);
            s["done"] = false;
            s["objectives"] = new JsonArray();
            s["has_broken"] = false;
            s["larger"] = 0;
            SaveDaily(con, s);
        }
        con.Commit();
        return true;
    }

    static string EventType(Kind kind) => kind switch { Kind.Harvest => "KILL", Kind.Spot => "SPOT", Kind.Track => "CLUE", _ => "" };

    /// <summary>The page's objective entry: its title and the condition the rule stands for. ponytail: a clue counts whatever its type (footsteps, droppings), as the
    /// catalogue's missions do; the condition names footsteps for the page only.</summary>
    JsonObject DailyObjective(Objective o, bool finished)
    {
        var rule = o.Rule;
        var parts = new List<string>();
        if (rule.Species is { } id)
            parts.Add($"a.species() == c.species.{Py.Str(species.GetValueOrDefault(id)?["define"])}");
        if (rule.Kind == Kind.Track)
            parts.Add("e.trackType() == c.clue.FOOTSTEPS");
        if (rule.Gender is { } gender)
            parts.Add($"a.gender() == c.gender.{(gender == 0 ? "MALE" : "FEMALE")}");
        var data = new JsonObject { ["id"] = o.Id, ["title"] = o.Title, ["update_message"] = "", ["conditions"] = string.Join(" and ", parts) };
        if (finished)
            data["completed"] = true;
        return new JsonObject { ["objective_data"] = data, ["event_type"] = EventType(rule.Kind) };
    }

    /// <summary>Daily_Mission/get_daily_missions_calendar: `{is_frozen, mission_end, broken_streak_info, calendar}`. The calendar is the 7 days of the streak's
    /// block (day 1 to 7, the block after the 7th day and so on) with each day's difficulty and reward; only today's entry holds a mission. Null when the server
    /// has no daily missions (the empty reply of ReplayScope stands).</summary>
    public byte[]? DailyCalendarReply(long account) => Calendar(account) is { } calendar ? Py.DumpBytes(calendar) : null;

    JsonObject? Calendar(long account)
    {
        if (dailies.Count == 0)
            return null;
        using var con = Con(account);
        con.Begin();
        var s = DailyState(con);
        con.Commit();
        var day = Py.L(s["day"]);
        var finished = Ids(s["objectives"]);
        var calendar = new JsonArray();
        for (var number = 1; number <= CalendarDays; number++)
        {
            var index = day - day % CalendarDays + number - 1;
            var difficulty = DifficultyAt(index);
            var entry = new JsonObject
            {
                ["day_number"] = number, ["today"] = index == day, ["protected"] = false, ["date"] = null, ["species"] = null, ["image"] = null,
                ["difficulty"] = new JsonObject
                {
                    ["id"] = difficulty.Id, ["title"] = difficulty.Title, ["alias"] = difficulty.Alias, ["mission_cooldown"] = difficulty.Cooldown, ["color"] = difficulty.Color,
                },
                ["rewards"] = new JsonArray(new JsonObject
                {
                    ["id"] = difficulty.RewardId, ["root_type"] = "difficulty", ["root_id"] = difficulty.Id, ["reward_type"] = "SILVER", ["amount"] = DailyReward(index),
                    ["item_id"] = null, ["bundle_id"] = null, ["trophy_define"] = null,
                }),
                ["rewards_translations"] = new JsonArray(), ["character"] = null, ["mission"] = null, ["objectives"] = null,
            };
            if (index == day && MissionOf(s) is { } d)
            {
                var objectives = Py.Dumps(new JsonArray([.. d.Def.Objectives.Select(o => (JsonNode?)DailyObjective(o, finished.Contains(o.Id)))]), compact: true);
                var animal = d.Def.Objectives[0].Rule.Species is { } id ? species.GetValueOrDefault(id) : null;
                var name = string.Join(' ', d.Template.GiverAlias.Split('_').Select(w => w[..1] + w[1..].ToLowerInvariant()));
                entry["date"] = s["date"]!.DeepClone();
                entry["species"] = animal is null ? null : Py.Str(animal["define"]);
                entry["rewards_translations"] = new JsonArray(new JsonObject { ["text"] = $"{DailyReward(index)} gm$" });
                entry["character"] = new JsonObject
                {
                    ["id"] = d.Template.Giver, ["name"] = name, ["alias"] = d.Template.GiverAlias, ["description"] = name, ["image"] = d.Template.GiverAlias.ToLowerInvariant() + ".jpg",
                };
                entry["mission"] = new JsonObject
                {
                    ["id"] = d.Template.Id, ["title"] = d.Template.Title, ["description"] = d.Template.Title + ".", ["gender"] = 0, ["species"] = entry["species"]?.DeepClone(),
                    ["image"] = null, ["update_message"] = "", ["reserves"] = d.Def.Objectives[0].Rule.Species is { } sp ? facts.ReserveNames(sp) : "", ["objectives"] = objectives,
                };
                entry["objectives"] = objectives;
            }
            calendar.Add(entry);
        }
        return new JsonObject
        {
            ["is_frozen"] = Py.Truthy(s["frozen"]) ? 1 : 0,
            ["mission_end"] = (Today().DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber + 1L) * 86400 - 1,
            ["broken_streak_info"] = new JsonObject { ["has_broken_streak"] = Py.Truthy(s["has_broken"]), ["streak_larger"] = s["larger"]!.DeepClone() },
            ["calendar"] = calendar,
        };
    }

    /// <summary>The website's daily missions page (GET /account/missions/daily): `today` (the day number), `ends_in` (seconds until the UTC day ends), `frozen`, `days`
    /// (`number`, `stars` the difficulty id, `gms`) and `missions` in the order of the days (`title`, `desc`, `objectives` titles, `reserve`, `rewards` texts, `done`; empty
    /// for a day without a mission). Null when the server has no daily missions.</summary>
    public JsonObject? WebDaily(long account)
    {
        if (Calendar(account) is not { } c)
            return null;
        var days = c["calendar"]!.AsArray().OfType<JsonObject>().ToList();
        var today = days.Single(d => (bool)d["today"]!);
        var objectives = today["objectives"] is { } text ? JsonNode.Parse((string)text!)!.AsArray().OfType<JsonObject>().ToList() : [];
        return new JsonObject
        {
            ["today"] = today["day_number"]!.DeepClone(), ["ends_in"] = Math.Max(0, (long)(Py.D(c["mission_end"]) + 1 - Clock())), ["frozen"] = Py.Truthy(c["is_frozen"]),
            ["days"] = new JsonArray([.. days.Select(d => (JsonNode?)new JsonObject
            {
                ["number"] = d["day_number"]!.DeepClone(), ["stars"] = d["difficulty"]!["id"]!.DeepClone(), ["gms"] = d["rewards"]![0]!["amount"]!.DeepClone(),
            })]),
            ["missions"] = new JsonArray([.. days.Select(d => (JsonNode?)(d["mission"] is JsonObject m ? new JsonObject
            {
                ["title"] = m["title"]!.DeepClone(), ["desc"] = m["description"]!.DeepClone(),
                ["objectives"] = new JsonArray([.. objectives.Select(o => (JsonNode?)Py.Str(o["objective_data"]!["title"]))]),
                ["reserve"] = m["reserves"]!.DeepClone(), ["rewards"] = new JsonArray([.. d["rewards_translations"]!.AsArray().Select(t => (JsonNode?)Py.Str(t!["text"]))]),
                ["done"] = objectives.Count > 0 && objectives.All(o => o["objective_data"]!["completed"] is not null),
            } : new JsonObject { ["title"] = "", ["desc"] = "", ["objectives"] = new JsonArray(), ["reserve"] = "", ["rewards"] = new JsonArray(), ["done"] = false }))]),
        };
    }
}
