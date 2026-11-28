// Referrals (#929, docs/server.md#referrals): every account has a code; a new player enters a friend's code once and is paid the recruit reward at once; the recruiter is paid by tiers as his recruits qualify
// (hours played here and hunterscore gained since the code was entered) and by a Discord milestone as they link Discord. Every reward is a setting, empty until the operator fills it in.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

/// <summary>What a referral pays: gm$, em$ and items (the catalogue's ids, each `Count` copies). All zero and no items: nothing, which the settings read as no reward.</summary>
public sealed record Reward(long Gm, long Em, (long Id, long Count)[] Items)
{
    const long MaxMoney = 1_000_000_000;

    public static string Shape(string where) => $"{where}: null or {{\"gm\": 0, \"em\": 0, \"items\": [{{\"id\": 384, \"count\": 1}}]}}";

    /// <summary>The setting's reward, null for none (absent, null or all empty); InvalidDataException says what is wrong.</summary>
    public static Reward? Parse(JsonNode? node, string where)
    {
        if (node is null) return null;
        if (node is not JsonObject o || o.Any(kv => kv.Key is not ("gm" or "em" or "items"))) throw new InvalidDataException(Shape(where));
        long Money(string key) => o[key] switch
        {
            null => 0,
            JsonValue v when v.TryGetValue<long>(out var n) && n is >= 0 and <= MaxMoney => n,
            _ => throw new InvalidDataException($"{where}.{key}: a whole number from 0 to {MaxMoney}"),
        };
        var items = new List<(long, long)>();
        foreach (var row in o["items"] as JsonArray ?? (o["items"] is null ? new JsonArray() : throw new InvalidDataException($"{where}.items: a list of {{\"id\": an item id, \"count\": 1 or more}}")))
        {
            if (row is not JsonObject r || r.Any(kv => kv.Key is not ("id" or "count")) || r["id"] is not JsonValue id || !id.TryGetValue<long>(out var item) || item < 0
                || r["count"] is not JsonValue c || !c.TryGetValue<long>(out var count) || count is < 1 or > AdminApi.MaxStacks)
                throw new InvalidDataException($"{where}.items: a list of {{\"id\": an item id, \"count\": 1 to {AdminApi.MaxStacks}}}");
            items.Add((item, count));
        }
        var reward = new Reward(Money("gm"), Money("em"), [.. items]);
        return reward.Gm == 0 && reward.Em == 0 && reward.Items.Length == 0 ? null : reward;
    }
}

/// <summary>A milestone: reaching `Recruits` recruits pays `Reward` once; no reward: reached, nothing paid.</summary>
public sealed record ReferralTier(long Recruits, Reward? Reward);

/// <summary>settings.json `referral` (docs/server.md#referrals), read again when the file changes. Absent: the defaults, no reward anywhere.</summary>
public sealed record ReferralConfig(double QualifyHours, long QualifyScore, long WeeklyCap, double EnterHours, long ExistingDays, Reward? RecruitReward, ReferralTier[] Tiers, ReferralTier[] DiscordMilestones)
{
    static readonly string[] Keys = ["qualify_hours", "qualify_score", "weekly_cap", "enter_hours", "existing_days", "recruit_reward", "tiers", "discord_milestones"];

    public static readonly ReferralConfig Default = new(2, 10, 5, 72, 31, null, [.. new long[] { 1, 3, 5, 10, 20 }.Select(n => new ReferralTier(n, null))], [new(1, null)]);

    public IEnumerable<Reward> Rewards() => new[] { RecruitReward }.Concat(Tiers.Select(t => t.Reward)).Concat(DiscordMilestones.Select(t => t.Reward)).OfType<Reward>();

    /// <summary>The block as it is in the file; InvalidDataException says what is wrong. Item ids are checked against the catalogue by <see cref="State.LoadReferral"/>.</summary>
    public static ReferralConfig Parse(JsonNode? node)
    {
        if (node is null) return Default;
        if (node is not JsonObject o) throw new InvalidDataException("referral: an object");
        foreach (var (key, _) in o)
            if (!Keys.Contains(key)) throw new InvalidDataException($"referral.{key}: unknown; the settings are {string.Join(", ", Keys)}");
        double Hours(string key, double fallback) => o[key] switch
        {
            null => fallback,
            JsonValue v when v.TryGetValue<double>(out var n) && n >= 0 && double.IsFinite(n) => n,
            _ => throw new InvalidDataException($"referral.{key}: a number of hours, 0 or more"),
        };
        long Whole(string key, long fallback, long min) => o[key] switch
        {
            null => fallback,
            JsonValue v when v.TryGetValue<long>(out var n) && n >= min => n,
            _ => throw new InvalidDataException($"referral.{key}: a whole number, at least {min}"),
        };
        ReferralTier[] Tiers(string key, ReferralTier[] fallback)
        {
            if (o[key] is null) return fallback;
            var rows = o[key] as JsonArray ?? throw new InvalidDataException($"referral.{key}: a list of {{\"recruits\": 1 or more, \"reward\": null or a reward}}");
            var tiers = new List<ReferralTier>();
            foreach (var row in rows)
            {
                if (row is not JsonObject t || t.Any(kv => kv.Key is not ("recruits" or "reward")) || t["recruits"] is not JsonValue n || !n.TryGetValue<long>(out var recruits) || recruits < 1)
                    throw new InvalidDataException($"referral.{key}: a list of {{\"recruits\": 1 or more, \"reward\": null or a reward}}");
                tiers.Add(new(recruits, Reward.Parse(t["reward"], $"referral.{key}[{recruits}].reward")));
            }
            tiers.Sort((a, b) => a.Recruits.CompareTo(b.Recruits));
            for (var i = 1; i < tiers.Count; i++)
                if (tiers[i].Recruits == tiers[i - 1].Recruits) throw new InvalidDataException($"referral.{key}: {tiers[i].Recruits} recruits is listed twice");
            return [.. tiers];
        }
        return new(Hours("qualify_hours", Default.QualifyHours), Whole("qualify_score", Default.QualifyScore, 0), Whole("weekly_cap", Default.WeeklyCap, 1), Hours("enter_hours", Default.EnterHours),
                   Whole("existing_days", Default.ExistingDays, 0), Reward.Parse(o["recruit_reward"], "referral.recruit_reward"), Tiers("tiers", Default.Tiers), Tiers("discord_milestones", Default.DiscordMilestones));
    }
}

public sealed partial class State
{
    /// <summary>The `wallet_log` reason of every referral reward.</summary>
    public const string ReferralSummary = "Referral reward";

    /// <summary>The letters and digits of a code: A-Z and 2-9 without the look-alikes I, O, 0 and 1.</summary>
    const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    volatile ReferralConfig referral = ReferralConfig.Default;

    /// <summary>True on the serving server only (Program, after the settings are loaded): a recruit's progress then settles his recruiter's rewards as it changes. Off for the start pass of the constructor, which runs before the settings are loaded
    /// and with the default ones, and for the admin commands, so a command that only reads (or a start pass) never writes a milestone or pays.</summary>
    public bool LiveReferrals { get; set; }

    /// <summary>Sets the referral settings from settings.json `referral` (Settings.cs checked its shape); an item id that is not in the catalogue is refused (InvalidDataException) and what is in force stays.</summary>
    public void LoadReferral(JsonNode? cfg)
    {
        var read = ReferralConfig.Parse(cfg);
        foreach (var reward in read.Rewards())
            foreach (var (id, _) in reward.Items)
                if (!Py.Truthy(ItemInfo(id))) throw new InvalidDataException($"referral: item {id} is not in the catalogue");
        referral = read;
    }

    /// <summary>A code as typed: letters and digits only, upper case (spaces and dashes are ignored).</summary>
    public static string NormalizeCode(string? raw) => new((raw ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>`accounts a`: banned, or its Steam id is banned now. A recruit like that never counts; a code owner like that takes no recruit.</summary>
    const string BannedAccount = "(a.banned = 1 OR EXISTS (SELECT 1 FROM banned_steam b WHERE b.steam_id64 = a.steam_id64 AND b.lifted_ts IS NULL))";

    static bool IsBanned(Db con, long account) => con.Scalar($"SELECT 1 FROM accounts a WHERE a.id = ? AND {BannedAccount}", account) is not null;

    internal static double ScoreOf(Db con, long account) => Convert.ToDouble(con.Scalar("SELECT COALESCE(json_extract(account, '$.hunterscore'), 0) FROM accounts WHERE id = ?", account) ?? 0);

    /// <summary>The account's code, made (random, unique) the first time it is asked for.</summary>
    string ReferralCodeOf(Db con, long account)
    {
        while (true)
        {
            if (con.Scalar("SELECT code FROM referral_codes WHERE account_id = ?", account) is string code) return code;
            con.Exec("INSERT OR IGNORE INTO referral_codes (account_id, code) VALUES (?, ?)", account, RandomNumberGenerator.GetString(CodeAlphabet, 8));  // ignored by a taken code (try another) or a request that won the race (read it)
        }
    }

    /// <summary>The last second a code can be entered: 72 hours (`enter_hours`) after the account was made, and for an account made before the feature first started, the later of that and `existing_days` after it did.</summary>
    long ReferralEnterUntil(Db con, long account)
    {
        var cfg = referral;
        var created = (long)con.Scalar("SELECT created_ts FROM accounts WHERE id = ?", account)!;
        var until = created + (long)(cfg.EnterHours * 3600);
        var release = (long)con.Scalar("SELECT COALESCE(MIN(ts), 0) FROM referral_release")!;
        return created < release ? Math.Max(until, release + cfg.ExistingDays * 86400) : until;
    }

    /// <summary>The player enters a code (POST /account/referral/code): `ok` with the recruiter's handle and the reward paid at once (null: none is set), or why not: `already`, `expired`, `unknown`, `own`,
    /// `banned` (the code's owner) or `loop` (the code's owner is a recruit of his). His hunterscore and server playtime now are the start of what the qualification counts.</summary>
    public (string Status, string? Recruiter, Reward? Reward) EnterReferralCode(long account, string? raw)
    {
        using var con = Con(account);
        con.Begin();
        if (con.Scalar("SELECT 1 FROM referrals WHERE recruit_id = ?", account) is not null || EnteredBySteam(con, account)) return ("already", null, null);
        if (Clock() > ReferralEnterUntil(con, account)) return ("expired", null, null);
        if (con.Scalar("SELECT account_id FROM referral_codes WHERE code = ?", NormalizeCode(raw)) is not long owner) return ("unknown", null, null);
        if (owner == account) return ("own", null, null);
        if (IsBanned(con, owner)) return ("banned", null, null);
        if (con.Scalar("SELECT 1 FROM referrals WHERE recruit_id = ? AND recruiter_id = ?", owner, account) is not null) return ("loop", null, null);
        con.Exec("INSERT INTO referrals (recruit_id, recruiter_id, entered_ts, score_at_entry, seconds_at_entry) VALUES (?, ?, ?, ?, ?)",
                 account, owner, (long)Clock(), ScoreOf(con, account), (double)HuntSecondsHere(con));
        con.Exec("INSERT OR IGNORE INTO referral_steam (steam_id64, ts) SELECT steam_id64, ? FROM accounts WHERE id = ? AND steam_id64 IS NOT NULL", (long)Clock(), account);
        var reward = referral.RecruitReward;
        if (reward is not null) PayReferral(con, reward);
        con.Commit();
        return ("ok", (string)con.Scalar("SELECT handle FROM accounts WHERE id = ?", owner)!, reward);
    }

    /// <summary>Pays the reward to the account `con` is scoped to: a wallet row "Referral reward" per currency (the items listed on the first, or on a row of 0 gm$ when only items are paid) and the items in the
    /// inventory. An item the account may not own another copy of (<see cref="OverOwnLimit"/>) is left out, and said in the log. Call it inside a transaction.</summary>
    void PayReferral(Db con, Reward reward)
    {
        List<long> ids = [];
        foreach (var (id, count) in reward.Items)
            if (OverOwnLimit(con, id, (int)count)) Console.WriteLine($"referral reward: account {con.Account}: item {id} x{count} not given, the account may not own more of it");
            else ids.AddRange(Enumerable.Repeat(id, (int)count));
        var rows = new List<(string Currency, long Amount)>();
        if (reward.Em > 0) rows.Add(("em", reward.Em));
        if (reward.Gm > 0) rows.Add(("gm", reward.Gm));
        if (rows.Count == 0 && ids.Count > 0) rows.Add(("gm", 0));
        for (var i = 0; i < rows.Count; i++)
            if (AddCurrency(con, rows[i].Currency, rows[i].Amount, ReferralSummary, i == 0 ? ids : null) is { } balance)
                con.Exec("INSERT INTO pending (account_id, body) VALUES ($a, ?)", Py.Dumps(new JsonObject { ["amount"] = balance.DeepClone(), ["type"] = rows[i].Currency, ["update"] = 15 }));
        if (ids.Count > 0) Give(con, ids);
    }

    /// <summary>Hours played here and hunterscore gained since the recruit's entry.</summary>
    (double Hours, double Score) ReferralProgress(Db con, long recruit, double score0, double seconds0)
    {
        var mine = con.Sharing(recruit);
        return (Math.Max(0, (HuntSecondsHere(mine) - seconds0) / 3600), Math.Max(0, ScoreOf(con, recruit) - score0));
    }

    /// <summary>The Steam id of the account entered a code once: that is for good, deleting the account and signing up again does not give another entry (`referral_steam` outlives the account).</summary>
    static bool EnteredBySteam(Db con, long account) =>
        con.Scalar("SELECT 1 FROM referral_steam WHERE steam_id64 = (SELECT steam_id64 FROM accounts WHERE id = ?)", account) is not null;

    /// <summary>The recruits the recruiter's tiers count (counted: qualified and inside the weekly cap) and those the Discord milestone counts (counted and with Discord linked).</summary>
    static (long Counted, long Linked) ReferralCounts(Db con, long recruiter) =>
        ((long)con.Scalar("SELECT COUNT(*) FROM referrals WHERE recruiter_id = ? AND paid_ts IS NOT NULL", recruiter)!,
         (long)con.Scalar("SELECT COUNT(*) FROM referrals r WHERE r.recruiter_id = ? AND r.paid_ts IS NOT NULL AND EXISTS (SELECT 1 FROM discord_links l WHERE l.account_id = r.recruit_id)", recruiter)!);

    /// <summary>Brings a recruiter's recruits up to date, inside the caller's transaction: an unpaid recruit who is banned is dropped; one who now has the hours and the score qualifies; a qualified one is paid (counted)
    /// while fewer than `weekly_cap` were in the last 7 days; then each tier and Discord milestone the counts reach for the first time is written down and its reward, when one is set, paid. A milestone reached while its reward
    /// is unset is written down unpaid and never paid by this: a reward set later is for the next ones, as is a milestone listed later that the recruiter had already passed (`admin referral pay-qualified` pays the reached ones).</summary>
    void SettleReferrals(Db con, long recruiter)
    {
        var cfg = referral;
        var now = (long)Clock();
        var paidThisWeek = (long)con.Scalar("SELECT COUNT(*) FROM referrals WHERE recruiter_id = ? AND paid_ts > ?", recruiter, now - 7 * 86400)!;
        foreach (var r in con.Rows("SELECT recruit_id, score_at_entry, seconds_at_entry, qualified_ts FROM referrals WHERE recruiter_id = ? AND revoked = 0 AND paid_ts IS NULL ORDER BY COALESCE(qualified_ts, 1 << 62), recruit_id", recruiter))
        {
            var recruit = (long)r[0];
            if (IsBanned(con, recruit)) { con.Exec("UPDATE referrals SET revoked = 1 WHERE recruit_id = ?", recruit); continue; }
            if (r[3] is DBNull)
            {
                var (hours, score) = ReferralProgress(con, recruit, Convert.ToDouble(r[1]), Convert.ToDouble(r[2]));
                if (hours < cfg.QualifyHours || score < cfg.QualifyScore) continue;
                con.Exec("UPDATE referrals SET qualified_ts = ? WHERE recruit_id = ?", now, recruit);
            }
            if (paidThisWeek >= cfg.WeeklyCap) continue;
            con.Exec("UPDATE referrals SET paid_ts = ? WHERE recruit_id = ?", now, recruit);
            paidThisWeek++;
        }
        if (IsBanned(con, recruiter)) return;  // a banned recruiter is paid nothing (his recruits are still brought up to date above)
        var (counted, linked) = ReferralCounts(con, recruiter);
        foreach (var (kind, tiers, n) in new[] { ("tier", cfg.Tiers, counted), ("discord", cfg.DiscordMilestones, linked) })
        {
            var top = (long)con.Scalar("SELECT COALESCE(MAX(recruits), 0) FROM referral_payouts WHERE recruiter_id = ? AND kind = ?", recruiter, kind)!;  // the milestones up to it were reached before: only a higher one is reached by this check
            foreach (var tier in tiers.Where(t => t.Recruits <= n))
            {
                var pay = tier.Reward is not null && tier.Recruits > top;
                if (con.Exec("INSERT OR IGNORE INTO referral_payouts (recruiter_id, kind, recruits, paid_ts, paid) VALUES (?, ?, ?, ?, ?)", recruiter, kind, tier.Recruits, now, pay ? 1 : 0) > 0 && pay)
                    PayReferral(con.Sharing(recruiter), tier.Reward!);
            }
        }
    }

    /// <summary>A recruit's hunterscore or hunt time changed (<see cref="HunterscoreIn"/>), or he linked Discord: his recruiter's recruits are brought up to date. Inside the caller's transaction.</summary>
    internal void SettleReferralOf(Db con, long recruit)
    {
        if (LiveReferrals && con.Scalar("SELECT recruiter_id FROM referrals WHERE recruit_id = ? AND revoked = 0", recruit) is long recruiter)
            SettleReferrals(con, recruiter);
    }

    /// <summary>A restore or claim changed the account's hunterscore by what the backup held (<see cref="Restorer"/>): the same amount moves the score its entry counted from, so what the backup brought is not gained.</summary>
    internal void RebaseReferral(Db con, long recruit, double scoreBefore, double score)
    {
        var delta = score - scoreBefore;
        if (delta != 0) con.Exec("UPDATE referrals SET score_at_entry = score_at_entry + ? WHERE recruit_id = ?", delta, recruit);
    }

    public JsonNode? RewardJson(Reward? reward) => reward is null ? null : new JsonObject
    {
        ["gm"] = reward.Gm, ["em"] = reward.Em,
        ["items"] = new JsonArray([.. reward.Items.Select(i => (JsonNode?)new JsonObject { ["id"] = i.Id, ["name"] = ItemInfo(i.Id) is { } info ? Py.Str(info["name"]) : $"Item {i.Id}", ["count"] = i.Count })]),
    };

    /// <summary>`GET /account/referral`: the account's code and link, whether and until when it can enter one, the code it entered, the qualification, its recruits (not yet qualified first, the closest to qualifying
    /// first, then the qualified by when) and the tiers and Discord milestones with what they give. The recruits are brought up to date first.</summary>
    public JsonObject ReferralStatus(long account, string site)
    {
        var cfg = referral;
        using var con = Con(account);
        var code = ReferralCodeOf(con, account);
        if (con.Scalar("SELECT 1 FROM referrals WHERE recruiter_id = ? AND revoked = 0 LIMIT 1", account) is not null)
        {
            con.Begin();
            SettleReferrals(con, account);
            con.Commit();
        }
        var entry = con.Rows("SELECT a.handle, c.code FROM referrals r JOIN accounts a ON a.id = r.recruiter_id LEFT JOIN referral_codes c ON c.account_id = r.recruiter_id WHERE r.recruit_id = ?", account).FirstOrDefault();
        var until = ReferralEnterUntil(con, account);
        var canEnter = entry is null && !EnteredBySteam(con, account) && Clock() <= until;
        double Ratio(double have, double need) => need <= 0 ? 1 : Math.Min(have / need, 1);
        var recruits = con.Rows("SELECT r.recruit_id, a.handle, r.score_at_entry, r.seconds_at_entry, r.qualified_ts, r.paid_ts, EXISTS (SELECT 1 FROM discord_links l WHERE l.account_id = r.recruit_id)"
                                + " FROM referrals r JOIN accounts a ON a.id = r.recruit_id WHERE r.recruiter_id = ? AND r.revoked = 0", account)
            .Select(r =>
            {
                var (hours, score) = ReferralProgress(con, (long)r[0], Convert.ToDouble(r[2]), Convert.ToDouble(r[3]));
                return (Handle: (string)r[1], Hours: hours, Score: score, Qualified: r[4] as long?, Paid: r[5] is not DBNull, Linked: (long)r[6] == 1);
            })
            .OrderBy(r => r.Qualified is not null).ThenByDescending(r => Ratio(r.Hours, cfg.QualifyHours) + Ratio(r.Score, cfg.QualifyScore)).ThenByDescending(r => r.Qualified).ThenBy(r => r.Handle, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var (counted, linked) = ReferralCounts(con, account);
        JsonArray Tiers(ReferralTier[] tiers, long n) => new([.. tiers.Select(t => (JsonNode?)new JsonObject { ["recruits"] = t.Recruits, ["reward"] = RewardJson(t.Reward), ["reached"] = n >= t.Recruits })]);
        return new JsonObject
        {
            ["code"] = code,
            ["link"] = $"{site}/join/{code}",
            ["canEnter"] = canEnter,
            ["enterUntil"] = canEnter ? JsonValue.Create(until) : null,
            ["enteredCode"] = entry?[1] as string,
            ["recruiter"] = entry?[0] as string,
            ["qualification"] = new JsonObject { ["hours"] = cfg.QualifyHours, ["score"] = cfg.QualifyScore },
            ["recruits"] = new JsonArray([.. recruits.Select(r => (JsonNode?)new JsonObject
            {
                ["handle"] = r.Handle, ["hours"] = Math.Round(r.Hours, 2), ["score"] = (long)Math.Floor(r.Score), ["qualified"] = r.Qualified is not null, ["qualifiedAt"] = r.Qualified,
                ["discordLinked"] = r.Linked, ["paid"] = r.Paid,
            })]),
            ["qualifiedCount"] = recruits.Count(r => r.Qualified is not null),
            ["countedCount"] = (int)counted,
            ["tiers"] = Tiers(cfg.Tiers, counted),
            ["discord"] = new JsonObject { ["linkedCount"] = linked, ["milestones"] = Tiers(cfg.DiscordMilestones, linked) },
            ["recruitReward"] = RewardJson(cfg.RecruitReward),
        };
    }

    static string DescribeReward(Reward r) =>
        string.Join(", ", new[] { r.Gm > 0 ? $"{r.Gm} gm$" : null, r.Em > 0 ? $"{r.Em} em$" : null }.OfType<string>().Concat(r.Items.Select(i => $"{i.Count} x item {i.Id}")));

    /// <summary>`admin referral list`: for the recruiters (or the one account, as recruiter and as recruit) a line with the code and the counts, a line per recruit with his progress and state (`waiting`, `qualified` over the weekly cap
    /// and not yet counted, `paid` counted, `dropped`), and the milestones reached with whether their reward was paid.</summary>
    public List<string> ReferralReport(long? only)
    {
        var cfg = referral;
        var lines = new List<string>();
        using var con = Store.Open();
        string Name(long id) => $"{id} {con.Scalar("SELECT handle FROM accounts WHERE id = ?", id)}";
        if (only is { } one && con.Rows("SELECT recruiter_id, entered_ts FROM referrals WHERE recruit_id = ?", one).FirstOrDefault() is { } mine)
            lines.Add($"{Name(one)} entered the code of {Name((long)mine[0])} at {RewardEvents.Iso((long)mine[1])}");
        foreach (var recruiter in con.Rows("SELECT DISTINCT recruiter_id FROM referrals WHERE ?1 IS NULL OR recruiter_id = ?1 ORDER BY recruiter_id", only).Select(r => (long)r[0]))
        {
            var (counted, linked) = ReferralCounts(con, recruiter);
            lines.Add($"{Name(recruiter)}: code {con.Scalar("SELECT code FROM referral_codes WHERE account_id = ?", recruiter)}, {counted} counted, {linked} with Discord linked");
            foreach (var r in con.Rows("SELECT recruit_id, score_at_entry, seconds_at_entry, qualified_ts, paid_ts, revoked FROM referrals WHERE recruiter_id = ? ORDER BY entered_ts, recruit_id", recruiter))
            {
                var (hours, score) = ReferralProgress(con, (long)r[0], Convert.ToDouble(r[1]), Convert.ToDouble(r[2]));
                var state = (long)r[5] == 1 ? "dropped" : r[4] is not DBNull ? "paid" : r[3] is not DBNull ? "qualified (over the weekly cap)" : "waiting";
                lines.Add($"  {Name((long)r[0])}: {hours.ToString("0.##", CultureInfo.InvariantCulture)} of {cfg.QualifyHours.ToString(CultureInfo.InvariantCulture)} h, score +{score.ToString("0.##", CultureInfo.InvariantCulture)} of {cfg.QualifyScore}: {state}");
            }
            foreach (var p in con.Rows("SELECT kind, recruits, paid FROM referral_payouts WHERE recruiter_id = ? ORDER BY kind DESC, recruits", recruiter))
                lines.Add($"  {p[0]} {p[1]} reached: {((long)p[2] == 1 ? "reward paid" : "no reward paid")}");
        }
        if (lines.Count == 0) lines.Add("no referral");
        return lines;
    }

    /// <summary>`admin referral revoke`: drops the recruit's entry while the recruiter has not been paid for him (it stays on record, so he cannot enter another code). The message says what happened; Done is false when nothing was dropped.</summary>
    public (bool Done, string Message) RevokeReferral(long recruit)
    {
        using var con = Con(recruit);
        con.Begin();
        if (con.Rows("SELECT paid_ts, revoked FROM referrals WHERE recruit_id = ?", recruit).FirstOrDefault() is not { } row) return (false, $"account {recruit} entered no code");
        if ((long)row[1] == 1) return (false, $"account {recruit}: the entry is already dropped");
        if (row[0] is not DBNull) return (false, $"account {recruit}: the recruiter was already paid for him; paid rewards stay");
        con.Exec("UPDATE referrals SET revoked = 1 WHERE recruit_id = ?", recruit);
        con.Commit();
        return (true, $"account {recruit}: the entry is dropped");
    }

    /// <summary>`admin referral pay-qualified`: pays the tier and Discord milestone rewards that are set now to the recruiters who already reached them (while the reward was unset, or before the tier was listed), once each.
    /// Without `apply` only says what it would pay.</summary>
    public List<string> PayReferralMilestones(bool apply)
    {
        var cfg = referral;
        var lines = new List<string>();
        List<long> recruiters;
        using (var all = Store.Open())
            recruiters = [.. all.Rows("SELECT DISTINCT recruiter_id FROM referrals ORDER BY recruiter_id").Select(r => (long)r[0])];
        foreach (var recruiter in recruiters)
        {
            using var con = Con(recruiter);
            con.Begin();
            if (IsBanned(con, recruiter)) continue;  // a banned recruiter is paid nothing
            var (counted, linked) = ReferralCounts(con, recruiter);
            foreach (var (kind, tiers, n) in new[] { ("tier", cfg.Tiers, counted), ("discord", cfg.DiscordMilestones, linked) })
                foreach (var tier in tiers.Where(t => t.Recruits <= n && t.Reward is not null))
                {
                    if (con.Scalar("SELECT paid FROM referral_payouts WHERE recruiter_id = ? AND kind = ? AND recruits = ?", recruiter, kind, tier.Recruits) is 1L) continue;
                    lines.Add($"{recruiter} {con.Scalar("SELECT handle FROM accounts WHERE id = ?", recruiter)}: {kind} {tier.Recruits}: {(apply ? "paid" : "would pay")} {DescribeReward(tier.Reward!)}");
                    if (!apply) continue;
                    con.Exec("INSERT OR REPLACE INTO referral_payouts (recruiter_id, kind, recruits, paid_ts, paid) VALUES (?, ?, ?, ?, 1)", recruiter, kind, tier.Recruits, (long)Clock());
                    PayReferral(con, tier.Reward!);
                }
            con.Commit();
        }
        lines.Add(apply ? $"paid {lines.Count}" : $"dry run: {lines.Count} to pay; add --apply to pay");
        return lines;
    }
}
