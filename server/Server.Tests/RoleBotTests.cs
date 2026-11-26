using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The role bot (docs/server.md#discord-linked-roles, "The roles"): the server gives and takes away the Discord server's Verified hunter and Veteran roles itself, with Discord faked.</summary>
public class RoleBotTests : StateTestBase, IAsyncLifetime
{
    const string Guild = "900000000000000001", Verified = "900000000000000002", Veteran = "900000000000000003", Other = "900000000000000004";
    const string BotToken = "role-bot-token";
    const string A = "111111111111111111", B = "222222222222222222", C = "333333333333333333";
    readonly FakeDiscord discord = new();
    readonly List<string> log = [];
    Loopback server = null!;

    LinkedRoles Roles => server.Services.GetRequiredService<LinkedRoles>();

    static JsonObject Cfg() => new(Replay.HostKeys.Select(k => KeyValuePair.Create(k, (JsonNode?)(k[..^"_host".Length] + ".test"))))
    {
        ["discord_client_id"] = "123456789012345678", ["discord_client_secret"] = "test-secret",
        ["discord_role_bot_token"] = BotToken, ["discord_guild_id"] = Guild, ["discord_role_verified"] = Verified, ["discord_role_veteran"] = Veteran,
    };

    public async ValueTask InitializeAsync()
    {
        state = new State(Db, Py.DumpBytes(CONNECT), BASE, ICON, APP, LOCALE, TEXTURES, offline: false);
        state.CreateAccount(8, "second");
        state.CreateAccount(9, "third");
        server = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), Cfg(), state, tmp), tmp, discordHttp: new HttpClient(discord));
        Roles.Log = Keep;
    }

    protected override ValueTask StopServersAsync() => server.DisposeAsync();

    void Keep(string line)
    {
        lock (log) log.Add(line);
    }

    string Session(long account) => state.Store.CreateWebSession(account, TimeSpan.FromHours(1));

    /// <summary>The player of <paramref name="account"/> links the Discord account <paramref name="discordId"/> through <paramref name="on"/> (this test's server by default).</summary>
    async Task Link(long account, string discordId, Loopback? on = null)
    {
        on ??= server;
        discord.UserId = discordId;
        var cookie = Session(account);
        var given = Uri.EscapeDataString(on.Services.GetRequiredService<LinkedRoles>().StateFor(cookie));
        var reply = await on.SendAsync("GET", "web.test", $"/discord/callback?code=c&state={given}", [("Cookie", "hr_session=" + cookie)]);
        Assert.Matches("result=(linked|rewarded)$", reply.Headers.Single(h => h.Name == "Location").Value);
    }

    Task<Reply> Delete(long account) => server.SendAsync("POST", "web.test", "/account/delete",
        [("Content-Type", "application/x-www-form-urlencoded"), ("X-Requested-With", "x"), ("Cookie", "hr_session=" + Session(account))], "confirm=delete"u8.ToArray());

    void SetHunterscore(long account, long score)
    {
        using var con = state.Store.Open();
        con.Exec("UPDATE accounts SET account = json_set(account, '$.hunterscore', ?) WHERE id = ?", score, account);
    }

    static string Member(string discordId) => $"/api/v10/guilds/{Guild}/members/{discordId}";

    /// <summary>The calls to the guild since the last clear, in order.</summary>
    string[] RoleCalls() => [.. discord.Asked.Where(c => c.Path.StartsWith("/api/v10/guilds/", StringComparison.Ordinal)).Select(c => c.Line)];

    string[] RoleLog() => [.. log.Where(l => l.StartsWith("discord roles", StringComparison.Ordinal))];

    [Fact]
    public async Task TestALinkGivesVerifiedHunterAndVeteranOnlyFromTheThreshold()
    {
        discord.Members[A] = [Other];
        discord.Members[B] = [];
        SetHunterscore(8, 19999);
        SetHunterscore(9, 20000);
        await Link(8, A);
        Assert.Equal([$"GET {Member(A)}", $"PUT {Member(A)}/roles/{Verified}"], RoleCalls());
        Assert.All(discord.Asked.Where(c => c.Path.Contains("/guilds/")), c => Assert.Equal(("Bot", BotToken), (c.Scheme, c.Bearer)));  // the bot's token, never the player's
        Assert.True(discord.Members[A].SetEquals([Other, Verified]));  // the other role is untouched
        Assert.Single(discord.Asked, c => c.Line.EndsWith("/role-connection", StringComparison.Ordinal));  // the role connection is pushed as before
        discord.Asked.Clear();
        await Link(9, B);
        Assert.Equal([$"GET {Member(B)}", $"PUT {Member(B)}/roles/{Verified}", $"PUT {Member(B)}/roles/{Veteran}"], RoleCalls());
        Assert.Equal(["discord roles of account 8: gave Verified hunter", "discord roles of account 9: gave Verified hunter", "discord roles of account 9: gave Veteran"], RoleLog());
    }

    [Fact]
    public async Task TestAMemberNotInTheDiscordServerIsSkippedQuietlyAndCaughtOnceTheyJoin()
    {
        await Link(8, A);
        Assert.Equal([$"GET {Member(A)}"], RoleCalls());  // 404: nothing else
        discord.Asked.Clear();
        Assert.Equal(0, await Roles.SweepAsync());
        Assert.Equal([$"GET {Member(A)}"], RoleCalls());
        Assert.Empty(RoleLog());
        discord.Members[A] = [];  // joins after linking
        discord.Asked.Clear();
        await Roles.SweepAsync();
        Assert.Equal([$"GET {Member(A)}", $"PUT {Member(A)}/roles/{Verified}"], RoleCalls());
    }

    [Fact]
    public async Task TestTheSweepAddsAMissingVerifiedHunterAndVeteranBothWaysAndOtherwiseOnlyReads()
    {
        discord.Members[A] = [];
        await Link(8, A);
        discord.Members[A].Remove(Verified);  // taken away by hand on Discord
        discord.Members[A].Add(Other);
        discord.Asked.Clear();
        await Roles.SweepAsync();
        Assert.Equal([$"GET {Member(A)}", $"PUT {Member(A)}/roles/{Verified}"], RoleCalls());

        SetHunterscore(8, 20000);  // crosses the threshold upward
        discord.Asked.Clear();
        await Roles.SweepAsync();
        Assert.Equal([$"GET {Member(A)}", $"PUT {Member(A)}/roles/{Veteran}"], RoleCalls());

        discord.Asked.Clear();
        await Roles.SweepAsync();
        Assert.Equal([$"GET {Member(A)}"], RoleCalls());  // nothing differs: the read only

        SetHunterscore(8, 19999);  // and back below
        discord.Asked.Clear();
        await Roles.SweepAsync();
        Assert.Equal([$"GET {Member(A)}", $"DELETE {Member(A)}/roles/{Veteran}"], RoleCalls());
        Assert.True(discord.Members[A].SetEquals([Other, Verified]));
        Assert.Contains("discord roles of account 8: took away Veteran", RoleLog());
    }

    [Fact]
    public async Task TestARateLimitIsWaitedOutAndARefusedRoleIsLoggedOncePerSweep()
    {
        await Link(8, A);
        await Link(9, B);
        discord.Members[A] = [];
        discord.Members[B] = [];
        discord.Limited = 2;  // two 429s with retry_after, then the answer
        discord.Asked.Clear();
        await Roles.SweepAsync();
        Assert.Equal([$"GET {Member(A)}", $"GET {Member(A)}", $"GET {Member(A)}", $"PUT {Member(A)}/roles/{Verified}", $"GET {Member(B)}", $"PUT {Member(B)}/roles/{Verified}"], RoleCalls());

        discord.Members[A].Clear();
        discord.Members[B].Clear();
        discord.RoleStatus = 403;  // the bot's own role sits below Verified hunter
        log.Clear();
        await Roles.SweepAsync();
        Assert.Contains("Discord answered 403 to giving Verified hunter", Assert.Single(RoleLog()));  // two accounts, one line
        await Roles.SweepAsync();
        Assert.Equal(2, RoleLog().Length);  // once per sweep
    }

    [Fact]
    public async Task TestDeleteMyDataTakesBothRolesAwayFirstAndDeletesEvenWhenDiscordFails()
    {
        discord.Members[A] = [Other];
        discord.Members[B] = [];
        SetHunterscore(8, 25000);
        SetHunterscore(9, 25000);
        await Link(8, A);
        await Link(9, B);
        Assert.True(discord.Members[A].SetEquals([Other, Verified, Veteran]));
        discord.Asked.Clear();
        Assert.Equal(200, (await Delete(8)).Status);
        Assert.Equal([$"GET {Member(A)}", $"DELETE {Member(A)}/roles/{Verified}", $"DELETE {Member(A)}/roles/{Veteran}"], discord.Asked.Take(3).Select(c => c.Line));  // before the unlink
        Assert.Equal(Other, Assert.Single(discord.Members[A]));
        Assert.Contains(discord.Asked, c => c.Path == "/api/v10/oauth2/token/revoke");  // the unlink follows

        discord.RoleStatus = 500;  // Discord refuses
        Assert.Equal(200, (await Delete(9)).Status);
        Assert.Contains("discord roles of account 9: Discord answered 500 to taking away Verified hunter", RoleLog());
        state.CreateAccount(10, "fourth");
        discord.Members[C] = [];
        discord.RoleStatus = 204;
        await Link(10, C);
        discord.Down = true;  // Discord cannot be reached at all
        Assert.Equal(200, (await Delete(10)).Status);
        using var con = state.Store.Open();
        Assert.Equal((0L, 0L), ((long)con.Scalar("SELECT COUNT(*) FROM accounts WHERE id IN (8, 9, 10)")!, (long)con.Scalar("SELECT COUNT(*) FROM discord_links")!));
    }

    [Fact]
    public async Task TestTheRoleBotIsOffUnlessEverySettingIsSet()
    {
        var bot = LinkedRoles.RoleBot.From(Cfg())!;
        Assert.Equal((BotToken, Guild, Verified, Veteran, 20000L), (bot.Token, bot.Guild, bot.Verified, bot.Veteran, bot.VeteranAt));
        var at = Cfg();
        at["discord_veteran_at"] = 5L;  // as Settings stores it
        Assert.Equal(5L, LinkedRoles.RoleBot.From(at)!.VeteranAt);
        foreach (var key in new[] { "discord_role_bot_token", "discord_guild_id", "discord_role_verified", "discord_role_veteran" })
        {
            var cfg = Cfg();
            cfg.Remove(key);
            Assert.Null(LinkedRoles.RoleBot.From(cfg));
            cfg[key] = "";
            Assert.Null(LinkedRoles.RoleBot.From(cfg));
        }
        var bad = Cfg();
        bad["discord_role_veteran"] = "../../roles";  // an id goes into a URL: digits only
        Assert.Null(LinkedRoles.RoleBot.From(bad));

        // a server without the guild links and pushes as before and never asks the guild
        var plain = Cfg();
        plain.Remove("discord_guild_id");
        discord.Members[A] = [];
        await using var off = await Loopback.StartAsync(new Replay.Game(new Replay.HarTable(), plain, state, tmp), tmp, discordHttp: new HttpClient(discord));
        await Link(8, A, off);
        SetHunterscore(8, 30000);
        Assert.Equal(1, await off.Services.GetRequiredService<LinkedRoles>().SweepAsync());
        Assert.Empty(RoleCalls());
        Assert.Empty(discord.Members[A]);
    }

    [Fact]
    public async Task TestTheBotTokenIsNeverLogged()
    {
        Assert.DoesNotContain(BotToken, LinkedRoles.RoleBot.From(Cfg())!.ToString());
        discord.Members[A] = [];
        await Link(8, A);
        await Link(9, B);  // not a member
        foreach (var status in new[] { 403, 500, 401 })
        {
            discord.Members[A].Clear();
            discord.RoleStatus = status;
            await Roles.SweepAsync();
        }
        discord.Limited = 9;
        await Roles.SweepAsync();
        discord.Down = true;
        await Roles.SweepAsync();
        Assert.Equal(200, (await Delete(8)).Status);
        Assert.NotEmpty(RoleLog());
        Assert.All(log, l => Assert.DoesNotContain(BotToken, l));
    }
}
