using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Components;

namespace Resurgence.Tests.WebTests;

public sealed class MapPlaceholderTests : UiContext
{
    static readonly MapMarker[] Markers =
    [
        new(1, MarkerKind.Lodge, "Lodge 1", 10.25, 79.5), new(2, MarkerKind.Tower, "Tower 1", 50, 20), new(3, MarkerKind.Interest, "Old Mill", 90, 20), new(4, MarkerKind.Lodge, "Lodge 2", 30, 30),
    ];

    // each marker kind draws the slot marker-<kind>, the selected one marker-<kind>-active when there is such a picture; a kind without a picture is its icon, listed as missing
    [Fact]
    public void EachMarkerDrawsTheSlotOfItsKindAndAKindWithoutPictureIsItsIcon()
    {
        var images = new AssetImages(new Dictionary<string, string> { ["marker-lodge.png"] = "1", ["marker-lodge-active.png"] = "1", ["marker-tower.png"] = "1" });
        Services.AddSingleton(images);
        var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, Markers).Add(m => m.Selected, 4).Add(m => m.Reserve, 5));

        Assert.Equal(["marker-lodge", "marker-tower", "marker-lodge-active"], cut.FindAll("button.marker img").Select(i => i.GetAttribute("alt")));
        Assert.Empty(cut.Find("button.k-interest").QuerySelectorAll("img"));
        Assert.Contains("marker-interest", images.Missing.Keys);
        Assert.DoesNotContain("marker-tower", images.Missing.Keys);
    }

    // every kind of the player's placed items has a slot of its own: a tripod and a tower are not the stand's picture
    [Fact]
    public void EveryPlacedKindDrawsItsOwnSlot()
    {
        var kinds = new[] { MarkerKind.Tent, MarkerKind.TreeStand, MarkerKind.Tripod, MarkerKind.DeployableTower, MarkerKind.GroundBlind, MarkerKind.Climbing, MarkerKind.Caller };
        Services.AddSingleton(new AssetImages(kinds.ToDictionary(k => $"marker-{k.ToString().ToLowerInvariant()}.png", _ => "1")));
        var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, kinds.Select((k, i) => new MapMarker(-i, k, k.ToString(), 10 + i, 10)).ToArray()).Add(m => m.Reserve, 5));

        Assert.Equal(["marker-tent", "marker-treestand", "marker-tripod", "marker-deployabletower", "marker-groundblind", "marker-climbing", "marker-caller"],
            cut.FindAll("button.marker img").Select(i => i.GetAttribute("alt")));
    }

    // a decimal percent is written with a point in a culture that writes a comma
    [Fact]
    public void ThePositionIsAPercentWithAPointForTheDecimals()
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.GetCultureInfo("fr"));
        try
        {
            var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, Markers));
            Assert.Equal("left:10.25%;top:79.5%", cut.Find("button.k-lodge").GetAttribute("style"));
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui); }
    }

    static string Plane(IRenderedComponent<MapPlaceholder> cut) => cut.Find(".plane").GetAttribute("style")!.Split(';')[0];

    // the buttons and the wheel/keys share one view: + and − step by half, stop at ×1 and ×2, and a zoom out pulls the pan back inside the picture
    [Fact]
    public async Task TheZoomButtonsStepAndStopAtTheLimitsAndKeepThePanInsideThePicture()
    {
        var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, Markers));
        Assert.Equal("transform:scale(1) translate(0%, 0%)", Plane(cut));
        Assert.True(cut.Find(".zoom button:last-child").HasAttribute("disabled"));

        cut.Find(".zoom button:first-child").Click();
        cut.Find(".zoom button:first-child").Click();
        Assert.Equal("transform:scale(2) translate(0%, 0%)", Plane(cut));
        Assert.True(cut.Find(".zoom button:first-child").HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Instance.View(2, 25, -25));
        Assert.Equal("transform:scale(2) translate(25%, -25%)", Plane(cut));
        cut.Find(".zoom button:last-child").Click();
        Assert.Equal("transform:scale(1.5) translate(16.67%, -16.67%)", Plane(cut));
        cut.Find(".zoom button:last-child").Click();
        Assert.Equal("transform:scale(1) translate(0%, 0%)", Plane(cut));
    }

    // where a drag, the wheel or a key ended: the pan stops where the picture's edge meets the map's (|pan| <= 50 (scale - 1) / scale), the scale between ×1 and ×2
    [Theory]
    [InlineData(1, 30, 30, "scale(1) translate(0%, 0%)")]
    [InlineData(1.5, -99, 5, "scale(1.5) translate(-16.67%, 5%)")]
    [InlineData(2, 99, -99, "scale(2) translate(25%, -25%)")]
    [InlineData(7, 0, 0, "scale(2) translate(0%, 0%)")]
    [InlineData(.2, 3, 3, "scale(1) translate(0%, 0%)")]
    public async Task TheViewOfAGestureIsClampedToThePicture(double scale, double x, double y, string expected)
    {
        var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, Markers));
        await cut.InvokeAsync(() => cut.Instance.View(scale, x, y));
        Assert.Equal("transform:" + expected, Plane(cut));
        Assert.Equal(expected.Contains("scale(2)") ? "2" : expected.Contains("1.5") ? "1.5" : "1", cut.Find(".map").GetAttribute("data-s"));
    }

    // a marker is brought to the middle (zooming in once from ×1) while the picture still reaches the edges; a later drag is not undone by a render,
    // and CentreOn (the strip buttons) centres again after a drag
    [Fact]
    public async Task CentringAMarkerZoomsInOnceAndEveryCentreOnCentresAgainAfterADragAndAClickOnlySelects()
    {
        MapMarker[] markers = [new(1, MarkerKind.Lodge, "Near", 45, 55), new(2, MarkerKind.Lodge, "Edge", 5, 5)];
        MapMarker? picked = null;
        var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, markers).Add(m => m.OnPick, m => picked = m));
        await cut.InvokeAsync(() => cut.Instance.CentreOn(1));
        Assert.Equal("transform:scale(1.5) translate(5%, -5%)", Plane(cut));

        await cut.InvokeAsync(() => cut.Instance.View(1.5, -10, 10));
        cut.Render(p => p.Add(m => m.Markers, markers).Add(m => m.OnPick, m => picked = m).Add(m => m.Hover, 2));
        Assert.Equal("transform:scale(1.5) translate(-10%, 10%)", Plane(cut));

        await cut.InvokeAsync(() => cut.Instance.CentreOn(1));
        Assert.Equal("transform:scale(1.5) translate(5%, -5%)", Plane(cut));

        // a click on a marker only selects it: the view stays where the player put it
        await cut.InvokeAsync(() => cut.Instance.View(1.5, -10, 10));
        cut.Find("button.k-lodge").Click();
        Assert.Equal("transform:scale(1.5) translate(-10%, 10%)", Plane(cut));
        Assert.Equal(1, picked?.Id);

        await cut.InvokeAsync(() => cut.Instance.CentreOn(2));
        Assert.Equal("transform:scale(1.5) translate(16.67%, 16.67%)", Plane(cut));
    }

    // a map without the zoom buttons (join map, score sheet, server list) is no pan/zoom surface; the layer over the map moves with the picture
    [Fact]
    public void OnlyAMapWithZoomButtonsIsFocusableAndTheLayerOverTheMapSitsOnThePlane()
    {
        Assert.Null(Render<MapPlaceholder>(p => p.Add(m => m.Markers, Markers).Add(m => m.Zoom, false)).Find(".map").GetAttribute("tabindex"));

        var cut = Render<MapPlaceholder>(p => p.Add(m => m.Markers, Markers).AddChildContent("<span class=\"label\">here</span>"));
        Assert.Equal("0", cut.Find(".map").GetAttribute("tabindex"));
        Assert.False(string.IsNullOrWhiteSpace(cut.Find(".map").GetAttribute("aria-label")));
        Assert.Single(cut.FindAll(".plane .label"));
    }
}
