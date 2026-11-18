using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui.Components;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

public sealed class TaxidermyTests : UiContext
{
    // the mock's own hunt 1 back: allowed, with a skull; the wallet has 100 em$ and 1,000 gm$ against 10 em$ or 2,000 gm$
    static readonly Kill Bear = new(100000201, "Brown Bear", "Rifle A", "Ammo Type 1", 120, 101, 85, new DateTime(2026, 10, 22, 11, 0, 0, DateTimeKind.Utc));
    static TaxidermyOffer Offer(DateTime end, string? done = null) => new(end, 10, 2000, true, done);

    string? bought;

    IRenderedComponent<TaxidermyModal> Dialog() => Render<TaxidermyModal>(p => p
        .Add(m => m.Kill, Bear).Add(m => m.Offer, Offer(DateTime.UtcNow.AddHours(71.5)))
        .Add(m => m.Bought, EventCallback.Factory.Create<string>(this, d => bought = d)));

    [Fact]
    public void ARefusalShowsUnderTheRowThatFailedOnly()
    {
        var cut = Dialog();
        Assert.Equal(2, cut.FindAll(".row").Count);
        cut.Find("#taxidermy-gms").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=alert]")));
        var products = cut.FindAll(".product");
        Assert.Contains("Not enough gm$", products[0].QuerySelector("[role=alert]")!.TextContent);
        Assert.Null(products[1].QuerySelector("[role=alert]"));
        Assert.Null(bought);
        Assert.Null(cut.Find("#taxidermy-ems").GetAttribute("disabled"));  // the other button may be tried
    }

    [Fact]
    public void ABoughtSkullRaisesTheEventAndShowsTheThanks()
    {
        var cut = Dialog();
        cut.Find("#skull-ems").Click();
        cut.WaitForAssertion(() => Assert.Equal("skull", bought));
        Assert.Contains("Skull mounted successfully", cut.Find(".result h2").TextContent);
        Assert.Empty(cut.FindAll(".row"));
    }

    [Fact]
    public void ASpeciesWithoutASkullHasOnlyTheTaxidermyRow()
    {
        var cut = Render<TaxidermyModal>(p => p.Add(m => m.Kill, Bear).Add(m => m.Offer, Offer(DateTime.UtcNow.AddHours(5)) with { Skull = false }));
        Assert.Single(cut.FindAll(".row"));
        Assert.Empty(cut.FindAll("#skull-ems"));
    }

    [Theory]
    [InlineData(71.5, null, "71")]
    [InlineData(0.4, null, "min")]
    public void TheButtonShowsTheTimeLeft(double hours, string? done, string text)
    {
        var cut = Render<TaxidermyButton>(p => p.Add(b => b.Kill, Bear).Add(b => b.Offer, Offer(DateTime.UtcNow.AddHours(hours), done)));
        Assert.Contains(text, cut.Find("button .left").TextContent);
    }

    [Fact]
    public void TheButtonIsGoneAfterTheEndAndABoughtOfferShowsItsBadge()
    {
        Assert.Empty(Render<TaxidermyButton>(p => p.Add(b => b.Kill, Bear).Add(b => b.Offer, Offer(DateTime.UtcNow.AddMinutes(-1)))).Markup.Trim());
        var done = Render<TaxidermyButton>(p => p.Add(b => b.Kill, Bear).Add(b => b.Offer, Offer(DateTime.UtcNow.AddHours(5), "taxidermy")));
        Assert.Empty(done.FindAll("button"));
        Assert.Contains("Taxidermized", done.Find(".done").TextContent);
    }
}
