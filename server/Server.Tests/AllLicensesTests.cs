using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>`all_licenses`: every species is licensed on connect, every license item is owned without end and none is sold. Off: licenses are items like any other.</summary>
public class AllLicensesTests : StateTestBase
{
    const long License = 76;
    static readonly byte[] BuyIt = "item_id=76&amount=1&wallet=GOLD"u8.ToArray();

    // species 0 is licensed, 1 needs no license (a dog template), 2 is not available in the reserve, 3 is licensed
    State Make(bool all)
    {
        var connect = CONNECT;
        connect["spawn_licenses"] = JsonNode.Parse("[1, 0, 2, 1]");
        var app = APP;
        app["items"]!.AsArray().Add(O("""
            {"id": 76, "name": "Test Species License", "type": 13, "group": 12, "cat": 6, "price": 45, "silver_price": 765, "notbuyable": 0,
             "units": {"count": 1, "name": "day"}, "subcat": {"id": 9}, "inventory": {"slots": [0], "capacity": 0, "weight": 0}, "fit": {}}
            """));
        app["bundles"] = JsonNode.Parse("""[{"id": 9, "name": "Test Bundle", "price": 100, "items": [19, 76]}]""");
        return new State(Path.Combine(tmp, $"{all}.db"), Py.DumpBytes(connect), BASE, ICON, app, LOCALE, TEXTURES) { AllLicenses = all };
    }

    [Fact]
    public void ConnectLicensesEveryAvailableSpeciesAndNeverAnUnavailableOne()
    {
        Eq("[1, 1, 2, 1]", Reply(Make(true).ConnectReply(ME))["spawn_licenses"]);
        Eq("[1, 0, 2, 1]", Reply(Make(false).ConnectReply(ME))["spawn_licenses"]);
    }

    [Fact]
    public void TheConnectInventoryListsTheLicensesOnlyWhenOn()
    {
        foreach (var all in new[] { true, false })
        {
            var inv = Reply(Make(all).ConnectReply(ME))["inventory"]!;
            var rows = inv["items"]!.AsArray().Where(i => (long)i!["item_id"]! == License).ToList();
            Assert.Equal(all ? 1 : 0, rows.Count);
            Assert.Equal(all ? 1 : 0, inv["infos"]!.AsArray().Count(i => (long)i!["item_id"]! == License));
            if (all)
                Eq("[0, null]", new JsonArray(rows[0]!["slot"]!.DeepClone(), rows[0]!["units"]?.DeepClone()));
        }
    }

    [Fact]
    public void TheAccountInventoryShowsEachLicenseOnceWithoutAnEnd()
    {
        foreach (var all in new[] { true, false })
        {
            var s = Make(all);
            Assert.True(s.GrantItems(ME, License));
            var rows = s.WebInventory(ME)["items"]!.AsArray().Where(i => (long)i!["id"]! == License).ToList();
            var row = Assert.Single(rows)!;
            Eq(all ? "[0, null, 0]" : "[0, 1, null]", new JsonArray(row["slot"]!.DeepClone(), row["units"]?.DeepClone(), row["expires"]?.DeepClone()));
            Assert.Equal("Test Species License", (string)row["name"]!);
        }
    }

    [Fact]
    public void ALicenseIsRefusedOnBothRoutesAndNothingIsCharged()
    {
        var s = Make(true);
        Eq("""{"success": false}""", Reply(s.BuyReply(ME, BuyIt)));
        Assert.Null(s.WebBuy(ME, License, false, 1));
        Assert.Null(s.WebBuy(ME, License, false, 1, gms: true));
        Eq("182.0", s.Wallet(ME)["em"]);
        Eq("33.0", s.Wallet(ME)["gm"]);
        Assert.DoesNotContain(License, s.Inventory(ME).Select(i => (long)i!["item_id"]!));
    }

    [Fact]
    public void ABundleWithALicenseIsNotForSaleWhenOn()
    {
        foreach (var all in new[] { true, false })
        {
            var s = Make(all);
            var bundle = s.StoreCatalogue()["bundles"]!.AsArray().Single()!;
            Assert.Equal(!all, (bool)bundle["buyable"]!);
            Assert.Equal(!all, s.WebBuy(ME, 9, true, 1) is not null);
            Eq(all ? "182.0" : "82.0", s.Wallet(ME)["em"]);
        }
    }

    [Fact]
    public void OffALicenseIsSoldLikeAnyItem()
    {
        var s = Make(false);
        Eq("""{"price": 45.0, "success": true}""", Reply(s.BuyReply(ME, BuyIt)));
        Eq("137.0", s.Wallet(ME)["em"]);
        Assert.Contains(License, s.Inventory(ME).Select(i => (long)i!["item_id"]!));
    }
}
