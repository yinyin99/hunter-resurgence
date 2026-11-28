using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class GameManagementPageTests : UiContext
{
    sealed class Fake(FeederList feeders) : IGameManagementService
    {
        public readonly List<(string Name, bool Locked)> Saved = [];
        public readonly List<(string Id, string Currency, int Max)> Refills = [];
        public Task<IReadOnlyList<LodgeRow>> LodgesAsync() => Task.FromResult<IReadOnlyList<LodgeRow>>([new("1", "Made-up Lodge", false, 1, "Cabin", 2, 1), new("2", "Made-up Hideaway", true, 2, null, 0, 0)]);
        public Task<Result> SaveLodgeAsync(LodgeRow lodge, string name, bool locked) { Saved.Add((name, locked)); return Task.FromResult(Result.Success); }
        public Task<FeederList> FeedersAsync() => Task.FromResult(feeders);
        public Task<Result<RefillReply>> RefillAsync(string id, string currency, int maxAmount) { Refills.Add((id, currency, maxAmount)); return Task.FromResult(Result<RefillReply>.Success(new(true, 0, maxAmount, null))); }
    }

    static FeederRow Feeder(string id, int amount, int owned) => new(id, 1, "Made-up Feeder " + id, 1, "Made-up Reserve", 1.5, 2, amount, 50, "active", null, owned);

    [Fact]
    public void LodgesAreListedAndRenameAndLockCallTheService()
    {
        var fake = new Fake(new([], 0, new(1, 2)));
        Services.AddSingleton<IGameManagementService>(fake);
        var cut = Render<GameManagement>();

        Assert.Equal(["Made-up Lodge", "Made-up Hideaway"], cut.FindAll("li.lodge span.name").Select(n => n.TextContent));
        cut.FindAll("button.lock")[0].Click();
        Assert.Equal([("Made-up Lodge", true)], fake.Saved);

        cut.FindAll("button.rename")[0].Click();
        cut.Find("input.name").Input("New name");
        cut.Find("input.name").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(("New name", false), fake.Saved[^1]);
    }

    [Fact]
    public void AFeederThatIsFullOrHasNoBaitCannotBeRefilledAndAPackPaysWhenThePlayerHasOne()
    {
        var fake = new Fake(new([Feeder("a", 50, 10), Feeder("b", 10, 0), Feeder("c", 10, 5)], 2, new(1, 2)));
        Services.AddSingleton<IGameManagementService>(fake);
        var cut = Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites"));

        var buttons = cut.FindAll("button.refill");
        Assert.Equal([true, true, false], buttons.Select(b => b.HasAttribute("disabled")));
        buttons[2].Click();
        Assert.Equal([("c", "pack", 50)], fake.Refills);
    }

    [Fact]
    public void WithoutAPackTheRefillIsPaidInEmOrGm()
    {
        var fake = new Fake(new([Feeder("c", 10, 5)], 0, new(1, 2)));
        Services.AddSingleton<IGameManagementService>(fake);
        var cut = Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites"));

        Assert.Contains("1 em$", cut.Find("button.em").TextContent);
        cut.Find("button.gm").Click();
        Assert.Equal([("c", "gm", 50)], fake.Refills);
    }

    [Fact]
    public void TheMockShowsBothTabsWithTheirEmptyTextsAbsent()
    {
        Assert.Equal(2, Render<GameManagement>().FindAll("li.lodge").Count);
        Assert.Equal(2, Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites")).FindAll("li.feeder").Count);
    }
}
