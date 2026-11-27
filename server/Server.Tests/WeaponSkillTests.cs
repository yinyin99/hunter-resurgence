using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>One shooting skill per weapon type (docs/server.md, "Weapon families"): every item of a family reads and earns the same skill, whichever finish is fired or equipped.</summary>
public class WeaponSkillTests : StateTestBase
{
    const long B = 8;

    // 5, 706 and 900 are one family (706: the definitions' chain under another name, 900: the name of 5 in another finish), the representative is 5; 391 is a weapon alone
    State Make(bool online = false, Facts? facts = null, JsonObject? connect = null)
    {
        var app = APP;
        var items = app["items"]!.AsArray();
        items.First(i => (long)i!["id"]! == 5)!["cat"] = 1;
        items.Add(O("""{"id": 706, "name": "Test Rifle Variant", "variationname": "Winter", "cat": 1}"""));
        items.Add(O("""{"id": 900, "name": "Test Rifle", "variationname": "Camo", "cat": 1}"""));
        items.Add(O("""{"id": 391, "name": "Test Shotgun", "cat": 1}"""));
        return state = new State(Db, Py.DumpBytes(connect ?? CONNECT), BASE, ICON, app, LOCALE, TEXTURES, itemDefs: ITEM_DEFS, offline: !online, facts: facts);
    }

    /// <summary>The account's weapon skill rows, `key=xp` ascending by key.</summary>
    List<string> Rows(long account = ME)
    {
        using var con = state.Store.Open(account);
        return [.. con.Rows("SELECT key, xp FROM skills WHERE account_id = $a AND kind = 'weapon' ORDER BY key").Select(r => $"{r[0]}={Convert.ToDouble(r[1]).ToString("0.0", CultureInfo.InvariantCulture)}")];
    }

    /// <summary>Replace the account's weapon skill rows by these (item, xp): what an account holds before the merge.</summary>
    void Put(long account, params (long Item, double Xp)[] rows)
    {
        using var con = state.Store.Open(account);
        con.Exec("DELETE FROM skills WHERE account_id = $a AND kind = 'weapon'");
        foreach (var (item, xp) in rows)
            PlayerRows.SetSkill(con, "weapon", item, xp);
    }

    JsonNode Numbers(long account = ME) => Reply(state.SkillListReply(account, Encoding.ASCII.GetBytes($"user_id={account}"))!);

    static string[] Numbers(JsonNode weapon, params string[] ids) => [.. ids.Select(i => weapon[i]?.ToJsonString() ?? "none")];

    void Hit(long weapon, long individual = 9) =>
        Hb(O($$"""{"event": 7, "individual": {{individual}}, "species": 1}"""), O($$"""{"event": 1, "individual": {{individual}}, "weapon_id": {{weapon}}, "ammunition_id": 10, "animal_state": 3}"""));

    [Fact]
    public void TestHittingWithOneMemberRaisesTheSkillOfEveryMemberAndTheReverse()
    {
        Make();
        Put(ME);
        Hit(706);
        Assert.Equal(["5=2.1"], Rows());  // kept once, under the representative
        Assert.Equal(["1.14", "1.14", "1.14", "none"], Numbers(Numbers()["weapon"]!, "5", "706", "900", "391"));
        Hit(5);
        Hit(900);
        Assert.Equal(["5=6.1"], Rows());
        Assert.Equal(["1.407", "1.407", "1.407", "none"], Numbers(Numbers()["weapon"]!, "5", "706", "900", "391"));
        Assert.Equal("1", Reply(state.ConnectReply(ME))["skills"]!["weapon"]!["706"]!.ToJsonString());  // the connect carries levels
    }

    [Fact]
    public void TestAWeaponAloneAndTheOtherSkillsAreUnaffected()
    {
        Make();
        Put(ME);
        using (var con = state.Store.Open(ME))
            PlayerRows.SetSkill(con, "spotting", 1, Skills.FRESH_XP);
        Hit(391);
        Hit(706, 10);
        Assert.Equal(["5=2.1", "391=2.1"], Rows());
        Assert.Equal(["1.14", "1.14"], Numbers(Numbers()["weapon"]!, "5", "391"));
        Hb(O("""{"event": 7, "individual": 11, "species": 1}"""), O("""{"event": 11, "individual": 11}"""));
        Assert.Equal(["5=2.1", "391=2.1"], Rows());  // a spot earns no weapon XP
        Assert.Equal("1.14", Numbers()["spotting"]![0]!.ToJsonString());  // the one spotting row, 0.1 + 2
    }

    [Fact]
    public void TestALevelUpToastNamesTheHitWeaponWhicheverVariant()
    {
        Make();
        Put(ME, (5, 12.1));
        Hit(706);  // 14.1: still level 1
        Assert.Empty(Hb());
        Hit(706);  // 16.1: level 2
        Eq(new JsonArray(new JsonObject
        {
            ["text"] = "Test Rifle Variant: level 2 reached", ["colour"] = 4294967040,
            ["destination"] = ICON, ["update"] = 3, ["timer"] = 1000, ["type"] = 100,
        }), Hb());
        Assert.Equal("2", Reply(state.ConnectReply(ME))["skills"]!["weapon"]!["900"]!.ToJsonString());
    }

    [Fact]
    public void TestSkillListAndTheSkillsTabShowTheSameLevelOnEveryFinish()
    {
        Make();
        Put(ME);
        Hit(900);
        Hit(391);
        var numbers = Numbers()["weapon"]!;
        Assert.Equal(numbers["5"]!.ToJsonString(), numbers["706"]!.ToJsonString());
        var tab = state.PublicSkills(ME)["weapons"]!.AsArray();
        Eq("""
            [{"id": 5, "name": "Test Rifle", "variation": null, "group": null, "value": 1.14, "finishes": ["Test Rifle Variant (Winter)", "Camo"]},
             {"id": 391, "name": "Test Shotgun", "variation": null, "group": null, "value": 1.14, "finishes": []}]
            """, tab);
        Assert.Equal(numbers["900"]!.ToJsonString(), tab[0]!["value"]!.ToJsonString());
    }

    [Fact]
    public void TestRowsSavedPerItemReadAsTheHighestOfTheFamilyBeforeTheMerge()
    {
        Make();
        Put(ME, (5, 4.1), (706, 12.1), (391, 3.1));
        Assert.Equal(["1.807", "1.807", "1.807", "1.207"], Numbers(Numbers()["weapon"]!, "5", "706", "900", "391"));
        Hit(5);  // a hit folds the rows: the highest plus the hit
        Assert.Equal(["5=14.1", "391=3.1"], Rows());
    }

    [Fact]
    public void TestTheMergeTakesTheHighestOfEveryFamilyAndTheDryRunWritesNothing()
    {
        Make(online: true);
        state.CreateAccount(B, "second");
        Put(ME, (5, 4.1), (706, 12.1), (391, 3.1));
        Put(B, (706, 7.1), (900, 7.1));
        List<string> Spots(long a)
        {
            using var con = state.Store.Open(a);
            return [.. con.Rows("SELECT kind, key, xp FROM skills WHERE account_id = $a AND kind <> 'weapon' ORDER BY kind, key").Select(r => string.Join(":", r))];
        }
        var (spotsMe, spotsB) = (Spots(ME), Spots(B));

        var dry = state.MergeWeaponSkills(false);
        Assert.Equal(["5=4.1", "391=3.1", "706=12.1"], Rows());  // nothing written
        Assert.Equal(["706=7.1", "900=7.1"], Rows(B));
        Assert.Equal(
        [
            "account 7 family 5 (Test Rifle): before 5=4.1 xp (number 1.273), 706=12.1 xp (number 1.807); after 5=12.1 xp (number 1.807)",
            "account 8 family 5 (Test Rifle): before 706=7.1 xp (number 1.473), 900=7.1 xp (number 1.473); after 5=7.1 xp (number 1.473)",
            "family 5 (Test Rifle): 2 account(s) change, 1 with a row that rises, largest rise 8.0 xp, total 8.0 xp",
            "2 account(s) to change, 1 family(ies): dry run, nothing written; add --apply to write",
        ], dry);

        var applied = state.MergeWeaponSkills(true);
        Assert.Equal(dry.Take(3), applied.Take(3));
        Assert.Equal("2 account(s) changed, 1 family(ies)", applied[3]);
        Assert.Equal(["5=12.1", "391=3.1"], Rows());  // the highest, nothing lowered, the weapon alone untouched
        Assert.Equal(["5=7.1"], Rows(B));
        Assert.Equal(spotsMe, Spots(ME));  // the species skills are not touched
        Assert.Equal(spotsB, Spots(B));
        Assert.Equal(["0 account(s) changed, 0 family(ies)"], state.MergeWeaponSkills(true));  // idempotent
        Assert.Equal(["0 account(s) to change, 0 family(ies): dry run, nothing written; add --apply to write"], state.MergeWeaponSkills(false));
        Assert.Equal(["5=12.1", "391=3.1"], Rows());
        Assert.Equal(["1.807", "1.807", "1.807", "1.207"], Numbers(Numbers()["weapon"]!, "5", "706", "900", "391"));  // what the player read before the merge too
    }

    [Fact]
    public void TestTheMergeOfOneAccountLeavesTheOthersAlone()
    {
        Make(online: true);
        state.CreateAccount(B, "second");
        Put(ME, (5, 4.1), (706, 12.1));
        Put(B, (5, 4.1), (706, 12.1));
        state.MergeWeaponSkills(true, B);
        Assert.Equal(["5=4.1", "706=12.1"], Rows());
        Assert.Equal(["5=12.1"], Rows(B));
    }

    [Fact]
    public void TestARestoreWithDifferentLevelsOnTwoVariantsEndsWithTheHigherOnBoth()
    {
        Make(online: true);
        var zip = TestZip.Own().Replace("Skill_list.json", $$"""{"weapon":{"5":2.2,"706":3.5,"391":1.5},"tracking":[{{TestZip.Zeros(59, "1.5")}}],"spotting":[{{TestZip.Zeros(59, "2")}}]}""");
        var result = Restorer.Import(state, zip.Write(tmp, "variants.zip"), null, tmp);
        Assert.Equal(1001, result.Account);
        Assert.Equal(["5=67.5", "391=7.5"], Rows(1001));
        Assert.Equal(["3.5", "3.5", "3.5", "1.5"], Numbers(Numbers(1001)["weapon"]!, "5", "706", "900", "391"));
    }

    [Fact]
    public void TestAVariantsAchievementCountsOnlyThatVariant()
    {
        var connect = CONNECT;
        connect["achievements"] = JsonNode.Parse("""
            [{"id": 13, "category": 1, "group": 0, "group_title": "Rifle", "value": "weapon.TEST_RIFLE.*.ethical_kills", "triggers": [1]},
             {"id": 14, "category": 1, "group": 1, "group_title": "Variant", "value": "weapon.TEST_RIFLE_VARIANT.*.ethical_kills", "triggers": [1]}]
            """);
        Make(online: true, facts: Facts.Parse("""{"format": 1, "ranks": {"cap": 1000, "rows": [{"id": 3, "title": "Five", "category": "weapon", "items": [5, 706]}]}}"""), connect: connect);
        state.CreateAccount(B, "second");
        state.ConnectReply(B);
        state.HeartbeatReply(B, Events(
            O("""{"event": 7, "individual": 81, "species": 1, "gender": 0, "weight_grams": 1000, "texture": 0}"""),
            O("""{"event": 1, "individual": 81, "weapon_id": 706, "ammunition_id": 10}"""),
            O("""{"event": 3, "individual": 81, "extended_species": 1}""")));
        state.Disconnect(B, "{}"u8.ToArray());
        var milestones = state.PublicRanks(B)["milestones"]!.AsArray();
        Assert.Equal(new[] { ("Rifle", 0L), ("Variant", 1L) }, milestones.Select(m => ((string)m!["title"]!, (long)m["value"]!)));  // the variant's kill is the variant's only
        Assert.Equal(["5=2.1"], Rows(B));  // while its skill is the family's
        Assert.Equal(1.14, (double)state.PublicSkills(B)["weapons"]![0]!["value"]!, 3);
    }
}
