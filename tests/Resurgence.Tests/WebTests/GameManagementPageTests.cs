using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class GameManagementPageTests : UiContext
{
    sealed class Fake(FeederList feeders, bool refuse = false) : IGameManagementService
    {
        public readonly List<(string Name, bool Locked)> Saved = [];
        public readonly List<(string Id, string Currency, int Max)> Refills = [];
        public Task<IReadOnlyList<LodgeRow>> LodgesAsync() => Task.FromResult<IReadOnlyList<LodgeRow>>([new("1", "Made-up Lodge", false, "Cabin", 2, 1), new("2", "Made-up Hideaway", true, null, 0, 0)]);
        public Task<Result> SaveLodgeAsync(LodgeRow lodge, string name, bool locked) { Saved.Add((name, locked)); return Task.FromResult(Result.Success); }
        public Task<FeederList> FeedersAsync() => Task.FromResult(feeders);
        public Task<Result<RefillReply>> RefillAsync(string id, string currency, int maxAmount) { Refills.Add((id, currency, maxAmount)); return Task.FromResult(refuse ? Result<RefillReply>.Fail("GM.RefillFailed") : Result<RefillReply>.Success(new(true, 0, maxAmount, null))); }
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
    public void ARefusedRefillShowsTheErrorInline()
    {
        Services.AddSingleton<IGameManagementService>(new Fake(new([Feeder("c", 10, 5)], 1, new(1, 2)), refuse: true));
        var cut = Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites"));
        Assert.Empty(cut.FindAll("p.error"));

        cut.Find("button.refill").Click();

        Assert.Equal("The feeder could not be refilled. Try again.", cut.Find("p.error").TextContent);
    }

    [Fact]
    public void ThePriceButtonsAreDisabledWhileThePriceIsZero()
    {
        Services.AddSingleton<IGameManagementService>(new Fake(new([Feeder("c", 10, 5)], 0, new(0, 2))));
        var cut = Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites"));

        Assert.True(cut.Find("button.em").HasAttribute("disabled"));
        Assert.False(cut.Find("button.gm").HasAttribute("disabled"));
    }

    [Fact]
    public void LockingWorksWhateverTheName()
    {
        var fake = new Fake(new([], 0, new(1, 2)));
        Services.AddSingleton<IGameManagementService>(fake);
        var cut = Render<GameManagement>();
        cut.FindAll("button.lock")[0].Click();
        Assert.Single(fake.Saved);
    }

    [Fact]
    public void FeedersAreGroupedByTypeWithTheirPictureReserveCoordinatesBaitAndStatus()
    {
        var fake = new Fake(new([Feeder("a", 16, 10), Feeder("b", 10, 5) with { ItemId = 2, Name = "Made-up Other", Status = "penalty" }], 0, new(1, 2)));
        Services.AddSingleton<IGameManagementService>(fake);
        var cut = Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites"));

        Assert.Equal(["Made-up Feeder a", "Made-up Other"], cut.FindAll(".kind h5").Select(h => h.TextContent));
        var card = cut.FindAll("article.feeder")[0];
        Assert.Contains("item-1", card.InnerHtml);
        Assert.Equal(["Made-up Reserve", "x: 1.5", "y: 2"], card.QuerySelectorAll(".where > *").Select(e => e.TextContent));
        Assert.Equal(["Bait", "Status"], card.QuerySelectorAll("dt").Select(e => e.TextContent));
        Assert.Equal("16/50", card.QuerySelector(".num")!.TextContent);
        Assert.Equal("Active", card.QuerySelector(".status.active")!.TextContent);
        Assert.Equal("Penalty", cut.FindAll("article.feeder")[1].QuerySelector(".status.penalty")!.TextContent);
        Assert.Single(cut.FindAll("h4.bar input[type=number]"));
    }

    [Fact]
    public void TheMockShowsBothTabsWithTheirEmptyTextsAbsent()
    {
        Assert.Equal(2, Render<GameManagement>().FindAll("li.lodge").Count);
        Assert.Equal(2, Render<GameManagement>(p => p.Add(g => g.Tab, "bait-sites")).FindAll("article.feeder").Count);
    }
}
