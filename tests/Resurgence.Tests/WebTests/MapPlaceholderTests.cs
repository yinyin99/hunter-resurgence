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
}
