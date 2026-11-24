// Multiplayer: a host's session and the players who joined it. The game plays peer to peer around the host; the server only
// holds who is in which session (in memory: a session ends with its host), tells each player in the connect reply who the
// host is and how to reach it, learns the host's port and network id from heartbeat event 29, and lists the open sessions.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Resurgence.Server;

public sealed class Games
{
    /// <summary>Seconds a launched player may take to send the first heartbeat (the game starts first); after it, State.HEARTBEAT_TIMEOUT of silence ends the player.</summary>
    public const int Grace = 600;

    public sealed class Member(long account, string session, double now)
    {
        public readonly long Account = account;
        public readonly string Session = session;
        public string? Ip;  // as the server saw the player's connect
        public int Port;  // event 29
        public string Guid = "", Color = "";  // event 29: the network id as decimal text, the colour as rrggbb
        public double X, Y, Z, Joined = now;
        public double? Seen;  // the last heartbeat
        public Member Copy() => (Member)MemberwiseClone();
    }

    public sealed class Game
    {
        public long Id, Host, Reserve;
        public string Name = "", Description = "", Password = "", Time = "";
        public bool Public = true, Free = true;
        public long? HsMin, HsMax;
        public int Max = 8;
        public double Start;
        public List<Member> Members = [];  // the host first
        public bool Ready => Members[0].Port > 0 && Members[0].Guid != "";  // the host's event 29 came
        public Game Copy() => new()
        {
            Id = Id, Host = Host, Reserve = Reserve, Name = Name, Description = Description, Password = Password, Time = Time, Public = Public, Free = Free,
            HsMin = HsMin, HsMax = HsMax, Max = Max, Start = Start, Members = [.. Members.Select(m => m.Copy())]
        };
    }

    readonly object gate = new();
    readonly Dictionary<long, Game> games = [];

    static bool Gone(Member m, double now) => now - (m.Seen ?? m.Joined) >= (m.Seen is null ? Grace : State.HEARTBEAT_TIMEOUT);

    /// <summary>Silent players go; a silent host takes its session along. Inside the lock.</summary>
    void Prune(double now)
    {
        foreach (var g in games.Values.ToList())
            if (Gone(g.Members[0], now)) games.Remove(g.Id);
            else g.Members.RemoveAll(m => m != g.Members[0] && Gone(m, now));
    }

    Game? Of(long account) => games.Values.FirstOrDefault(g => g.Members.Any(m => m.Account == account));

    void LeaveLocked(long account)
    {
        if (Of(account) is not { } g) return;
        if (g.Host == account) games.Remove(g.Id);
        else g.Members.RemoveAll(m => m.Account == account);
    }

    /// <summary>The account is in no session any more; its own ends with it.</summary>
    public void Leave(long account)
    {
        lock (gate) LeaveLocked(account);
    }

    /// <summary>A new session with its host (`g.Host`) as the only member; the network id is made here.</summary>
    public void Create(double now, Game g, string session)
    {
        lock (gate)
        {
            LeaveLocked(g.Host);
            do g.Id = 7_000_000_000L + RandomNumberGenerator.GetInt32(1_000_000_000); while (games.ContainsKey(g.Id));
            g.Start = now;
            g.Members = [new(g.Host, session, now)];
            games[g.Id] = g;
        }
    }

    /// <summary>Add the player to session `id`, or say why not: `gone` (no such session), `own_game` (the player hosts it), `not_allowed` (a friends-only session of a stranger, or a hunterscore out of
    /// its range), `host_not_ready` (the host's port and network id are not known yet), `password`, `full`. `session` makes the player's session token once it is let in.</summary>
    public (Game? Game, string? Error, string? Session) Join(double now, long id, long account, string password, long score, Func<long, bool> friendOf, Func<string> session)
    {
        lock (gate)
        {
            Prune(now);
            if (!games.TryGetValue(id, out var g)) return (null, "gone", null);
            if (g.Host == account) return (null, "own_game", null);
            if ((!g.Public && !friendOf(g.Host)) || score < g.HsMin || score > g.HsMax) return (null, "not_allowed", null);
            if (!g.Ready) return (null, "host_not_ready", null);
            if (g.Password != "" && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(g.Password))) return (null, "password", null);
            if (g.Members.Count(m => m.Account != account) >= g.Max) return (null, "full", null);
            LeaveLocked(account);
            var token = session();
            g.Members.Add(new(account, token, now));
            return (g.Copy(), null, token);
        }
    }

    /// <summary>The player of this session token connects from `ip`: the session and the player (copies), or null when the token belongs to none.</summary>
    public (Game Game, Member Me)? Connect(double now, string session, string? ip)
    {
        lock (gate)
        {
            Prune(now);
            foreach (var g in games.Values)
                if (g.Members.Find(m => m.Session == session) is { } me)
                {
                    me.Ip = ip;
                    return (g.Copy(), me.Copy());
                }
            return null;
        }
    }

    /// <summary>Change the account's session and player (the real ones, inside the lock); nothing when it is in none.</summary>
    public void Update(double now, long account, Action<Game, Member> change)
    {
        lock (gate)
        {
            Prune(now);
            if (Of(account) is { } g && g.Members.Find(m => m.Account == account) is { } me)
                change(g, me);
        }
    }

    public List<Game> Open(double now)
    {
        lock (gate)
        {
            Prune(now);
            return [.. games.Values.OrderBy(g => g.Start).Select(g => g.Copy())];
        }
    }

    public Game? Find(double now, long id) => Open(now).Find(g => g.Id == id);
}

public sealed partial class State
{
    readonly Games games = new();

    static long? Num(string s) => Py.IsDigit(s) && long.TryParse(s, out var n) ? n : null;

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>A hunterscore limit of the form: whole, at most 99999 (the website's slider ends at 3000).</summary>
    static long? Hs(string s) => Num(s) is { } n ? Math.Min(n, 99999) : null;

    /// <summary>A world coordinate as the game sends it: whole numbers without a fraction.</summary>
    static JsonNode Coord(double d) => d == Math.Floor(d) ? (JsonNode)(long)d : d;

    /// <summary>Launch/start_mp: the account opens a session as its host and a hunt at the form's start (as <see cref="NoteLaunch(long, byte[])"/>); `session` is the token the game
    /// will connect with. The form: `reserve`, `start` and `loctype` (or `lodge_id`), `time`, `loadout_id`, and the session's `name` (up to 25 characters, else "&lt;handle&gt;'s game"),
    /// `description` (255), `public_game` (0: friends only), `free_players`, `hs_min`, `hs_max`, `password` (20) and `max_players` (2 to 8, else 8).</summary>
    public void HostGame(long account, string session, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        games.Create(Clock(), new Games.Game
        {
            Host = account, Reserve = Num(Form1(form, "reserve")) ?? 0, Time = Form1(form, "time"),
            Name = Form1(form, "name") is { Length: > 0 } name ? Cut(name, 25) : $"{Store.HandleOf(account)}'s game",
            Description = Cut(Form1(form, "description"), 255), Public = Form1(form, "public_game") != "0", Free = Form1(form, "free_players") != "0",
            HsMin = Hs(Form1(form, "hs_min")), HsMax = Hs(Form1(form, "hs_max")), Password = Cut(Form1(form, "password"), 20),
            Max = (int)Math.Clamp(Num(Form1(form, "max_players")) ?? 8, 2, 8),
        }, session);
        NoteLaunch(account, form);
    }

    /// <summary>Launch/join: the account joins session `network_id` (form as at <see cref="HostGame"/>: `start` and `loctype` or `lodge_id`, `loadout_id`, `password`) and a hunt opens in the
    /// session's reserve, at its time unless the form has one. Error: see <see cref="Games.Join"/>; else the session token the game will connect with.</summary>
    public (string? Error, string? Session) JoinGame(long account, byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        if (Num(Form1(form, "network_id")) is not { } id) return ("gone", null);
        var score = Store.AccountJson(account)["hunterscore"] is { } h && Py.IsNum(h) ? Py.L(h) : 0;
        var friends = Store.FriendsOf(account, LocalAccount).Friends;  // read before the lock: every heartbeat waits on it
        var (joined, error, session) = games.Join(Clock(), id, account, Form1(form, "password"), score,
            friends.Contains, () => Store.CreateToken(account, Lifetimes.Session));  // ponytail: the token is written under the lock; make it after the checks if joins ever queue
        if (joined is null) return (error, null);
        form["reserve"] = [joined.Reserve.ToString()];
        if (Form1(form, "time") == "") form["time"] = [joined.Time];
        NoteLaunch(account, form);
        return (null, session);
    }

    /// <summary>The connect reply's `network` for a player of a session: the host's own, or a joiner's with the host as its one client (its address as the server saw its connect, its port and network id from event 29).</summary>
    static JsonObject Network(Games.Game g, Games.Member me, string? handle, string? hostHandle)
    {
        var host = g.Members[0];
        return new JsonObject
        {
            ["singleplayer"] = false, ["host"] = g.Host, ["user_id"] = me.Account, ["handle"] = handle, ["ip"] = me.Ip, ["max_players"] = g.Max,
            ["clients"] = me.Account == g.Host ? null : new JsonArray(new JsonObject
            {
                ["user_id"] = g.Host, ["handle"] = hostHandle, ["ip"] = host.Ip ?? "", ["port"] = host.Port, ["guid"] = host.Guid,
            }),
        };
    }

    /// <summary>What a heartbeat tells of the session: event 29 `{port, guid, color}` stores the player's port, network id and colour; event 30 `{guid}` (the connection to the host failed)
    /// drops a joiner when the guid is the host's; the host's `player_positions` give where each player is. Every heartbeat keeps its player alive.</summary>
    void NoteNetwork(long account, JsonArray events, JsonNode? positions)
    {
        var now = Clock();
        games.Update(now, account, (g, me) =>
        {
            me.Seen = now;
            foreach (var e in events.OfType<JsonObject>())
                if (Is(e["event"], 29) && Py.IsInt(e["port"]) && Py.L(e["port"]) is > 0 and < 65536 && ulong.TryParse(Py.Str(e["guid"]), out var guid))
                    (me.Port, me.Guid, me.Color) = ((int)Py.L(e["port"]), guid.ToString(), Py.Str(e["color"]) is { Length: 6 } c && c.All(Uri.IsHexDigit) ? c.ToLowerInvariant() : "");
                else if (Is(e["event"], 30) && me != g.Members[0] && ulong.TryParse(Py.Str(e["guid"]), out var failed) && failed.ToString() == g.Members[0].Guid)
                    g.Members.Remove(me);
            if (me == g.Members[0] && positions is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                    if (Py.IsNum(row["user_id"]) && g.Members.Find(m => m.Account == Py.L(row["user_id"])) is { } at && Py.IsNum(row["x"]) && Py.IsNum(row["y"]) && Py.IsNum(row["z"]))
                        (at.X, at.Y, at.Z) = (Py.D(row["x"]), Py.D(row["y"]), Py.D(row["z"]));
        });
    }

    /// <summary>Network/list: the open sessions, oldest first, as the original lists them (no country or continent).</summary>
    public byte[] NetworkListReply() => Py.DumpBytes(new JsonArray([.. games.Open(Clock()).Select(g =>
    {
        var reserve = Arr(G(application, "reserves", null)).OfType<JsonObject>().FirstOrDefault(r => TryKey(r["id"], out var k) && k == g.Reserve);
        return (JsonNode?)new JsonObject
        {
            ["id"] = g.Id, ["name"] = g.Name, ["handle"] = Store.HandleOf(g.Host), ["host"] = g.Host, ["description"] = g.Description == "" ? null : g.Description,
            ["password"] = g.Password != "", ["public"] = g.Public, ["singleplayer"] = false, ["freeplayers"] = g.Free, ["hs_min"] = g.HsMin, ["hs_max"] = g.HsMax,
            ["max_players"] = g.Max, ["start_ts"] = (long)g.Start, ["clients_len"] = g.Members.Count,
            ["clients"] = new JsonArray([.. g.Members.Select(m => (JsonNode?)new JsonObject
            {
                ["user_id"] = m.Account, ["handle"] = Store.HandleOf(m.Account), ["guid"] = m.Guid, ["color"] = m.Color, ["avatar"] = Avatar(m.Account),
                ["x"] = Coord(m.X), ["y"] = Coord(m.Y), ["z"] = Coord(m.Z), ["hunterscore"] = Store.AccountJson(m.Account)["hunterscore"] is { } h && Py.IsNum(h) ? Py.L(h) : 0,
            })]),
            ["reserve"] = new JsonObject
            {
                ["id"] = g.Reserve, ["define"] = C(reserve?["define"]) ?? "", ["name"] = C(reserve?["name"]) ?? "", ["species"] = new JsonArray(), ["border"] = new JsonArray(),
            },
        };
    })]));

    /// <summary>Map/locations: the catalogue's locations of the reserve of session `network_id` (else of `reserve`), `[]` for neither.</summary>
    public byte[] MapLocationsReply(byte[] formBody)
    {
        var form = Py.ParseQs(Encoding.UTF8.GetString(formBody));
        var reserve = Num(Form1(form, "network_id")) is { } id ? games.Find(Clock(), id)?.Reserve : Num(Form1(form, "reserve"));
        return Py.DumpBytes(new JsonArray([.. Arr(G(application, "locations", null)).OfType<JsonObject>()
            .Where(l => reserve is not null && TryKey(l["reserve"], out var r) && r == reserve)
            .Select(l => (JsonNode?)new JsonObject
            {
                ["id"] = C(l["id"]), ["define"] = C(l["define"]) ?? "", ["name"] = PlaceName(l) ?? "", ["description"] = "", ["reserve"] = C(l["reserve"]),
                ["type"] = C(l["type"]), ["pos"] = C(l["pos"]), ["metadata"] = new JsonObject(),
            })]));
    }
}
