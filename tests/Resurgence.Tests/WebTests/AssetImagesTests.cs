using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

public class AssetImagesTests
{
    [Fact]
    public void PicturesAreFoundByNormalisedSlotName()
    {
        using var t = new TempDir("asset-images-");
        foreach (var f in new[] { "Logo Large.PNG", "logo-large.jpg", "bg-hero.webm", "x.svg", "notes.txt" })
            File.WriteAllText(Path.Combine(t.Path, f), "");
        var images = new AssetImages(AssetImages.Index(t.Path));
        Assert.StartsWith("/images/Logo%20Large.PNG?v=", images.Url("logo-large")); // first in ordinal order wins
        Assert.StartsWith("/images/bg-hero.webm?v=", images.Url("bg-hero"));        // the landing page's video
        Assert.Null(images.Url("x"));     // no SVG: it can run script
        Assert.Null(images.Url("notes")); // not a picture
    }

    [Theory]
    [InlineData("species-Alpine Ibex", "species-alpine-ibex")]
    [InlineData("news-news:featured-post", "news-news-featured-post")]
    public void KeyNormalises(string name, string key) => Assert.Equal(key, AssetImages.Key(name));

    [Fact]
    public void NoFolderMeansNoPictures()
    {
        Assert.Empty(AssetImages.Index(null));
        Assert.Empty(AssetImages.Index(Path.Combine(Path.GetTempPath(), "asset-images-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void AnIndexFromTheNetworkGivesOnlyEscapedPictures()
    {
        var images = new AssetImages(new Dictionary<string, string> { ["Logo Large.PNG"] = "1f", ["it's.png"] = "a')", ["x.svg"] = "1", [".hidden.png"] = "1" }, "https://example.test");
        Assert.Equal("https://example.test/images/Logo%20Large.PNG?v=1f", images.Url("logo-large"));
        Assert.Equal("https://example.test/images/it%27s.png?v=a%27%29", images.Url("it-s")); // a quote cannot leave MapPlaceholder's url('...')
        Assert.Null(images.Url("x"));      // no SVG
        Assert.Null(images.Url("hidden")); // no dot file
    }

    [Fact]
    public void MissingSlotsKeepTheirFirstPagePrivately()
    {
        var images = new AssetImages(new Dictionary<string, string>());
        images.NoteMissing("Item 12", "/store");
        images.NoteMissing("item-12", "/store/item/12");
        images.NoteMissing("avatar-default", "/messages/SomeHunter");
        Assert.Equal("/store", images.Missing["item-12"]);           // one key, the first page
        Assert.Equal("/messages", images.Missing["avatar-default"]); // a signed-in page loses its handle: the list is public

        for (var i = 0; i < 10_005; i++) images.NoteMissing("k" + i, "/feed");
        var missing = images.Missing;
        Assert.Equal(10_000, missing.Count);
        Assert.Contains("item-12", missing.Keys);
        Assert.Equal(missing.Keys.Order(StringComparer.Ordinal), missing.Keys);
    }
}
